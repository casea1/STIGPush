using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StigGpoManager.Core
{
    public sealed class ReviewResult
    {
        public List<ReviewRow> Rows { get; } = new List<ReviewRow>();
        public List<string> Warnings { get; } = new List<string>();
        public string HtmlPath { get; set; }
        public string CsvPath { get; set; }
    }

    /// <summary>
    /// The quarterly workflow. Every method that changes AD has a Plan* counterpart that only reads,
    /// so the GUI can show exact changes and the CLI can honour -WhatIf. Execute* methods re-read AD,
    /// log every change to the audit log and record it in the run state (saved after each change).
    /// </summary>
    public sealed partial class StigEngine
    {
        public AppConfig Config { get; }
        public IGpoDirectory Directory { get; }
        public AuditLog Audit { get; }
        public RunStore Store { get; }
        public string User { get; }
        public List<IPromotionCheck> PromotionChecks { get; } = new List<IPromotionCheck>();

        /// <summary>Progress / log lines for the UI log pane.</summary>
        public event Action<LogLevel, string> Log;

        private DomainInfo _domain;

        public StigEngine(AppConfig config, IGpoDirectory directory, AuditLog audit, RunStore store, string user)
        {
            Config = config;
            Directory = directory;
            Audit = audit;
            Store = store;
            User = user;
        }

        public DomainInfo Domain => _domain ?? (_domain = Directory.GetDomain());

        internal void Info(string m) => Log?.Invoke(LogLevel.Info, m);
        internal void Warn(string m) => Log?.Invoke(LogLevel.Warning, m);
        internal void Error(string m) => Log?.Invoke(LogLevel.Error, m);
        internal void Ok(string m) => Log?.Invoke(LogLevel.Success, m);

        internal void Save(RunState run) => Store.Save(run);

        /// <summary>Runs an AD change, writing success/failure to the audit log. Exceptions propagate after logging.</summary>
        internal T Do<T>(RunState run, string action, string target, Func<T> op, string detail = null)
        {
            try
            {
                var r = op();
                Audit.Write(run?.RunId, action, target, "Success", null, detail);
                return r;
            }
            catch (Exception ex)
            {
                Audit.Write(run?.RunId, action, target, "Failure", ex.Message, detail);
                Error($"{action} on {target} failed: {ex.Message}");
                throw;
            }
        }

        internal void Do(RunState run, string action, string target, Action op, string detail = null) =>
            Do<object>(run, action, target, () => { op(); return null; }, detail);

        // ================================================================ pre-flight

        /// <summary>AD-side pre-flight checks. Module and privilege checks are added by the host.</summary>
        public List<CheckResult> Preflight()
        {
            var results = new List<CheckResult>();
            DomainInfo d;
            try
            {
                d = Domain;
                results.Add(CheckResult.Pass("Domain reachable", $"{d.DnsRoot} via {Directory.Server}"));
                results.Add(CheckResult.Pass("PDC emulator", d.PdcEmulator));
                if (!string.Equals(d.DnsRoot, Config.Domain, StringComparison.OrdinalIgnoreCase))
                    results.Add(CheckResult.Fail("Config domain", $"config.json is for {Config.Domain} but the domain is {d.DnsRoot}."));
                if (d.DomainControllers.Count == 1)
                    results.Add(CheckResult.Warn("Domain controllers", $"Single-DC domain ({d.DomainControllers[0]}). Linking to the Domain Controllers OU will require a fresh system-state backup confirmation."));
                else
                    results.Add(CheckResult.Pass("Domain controllers", $"{d.DomainControllers.Count} DCs"));
            }
            catch (Exception ex)
            {
                results.Add(CheckResult.Fail("Domain reachable", ex.Message));
                return results;
            }

            try
            {
                var store = Config.EffectiveCentralStorePath;
                results.Add(System.IO.Directory.Exists(store)
                    ? CheckResult.Pass("Central Store", store)
                    : CheckResult.Fail("Central Store", $"{store} does not exist. Create it (copy C:\\Windows\\PolicyDefinitions) before using the tool."));
            }
            catch (Exception ex) { results.Add(CheckResult.Fail("Central Store", ex.Message)); }

            var missing = new List<string>();
            foreach (var dn in Config.Mappings.SelectMany(m => m.PilotTargets.Concat(m.ProdTargets)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { if (!Directory.ContainerExists(dn)) missing.Add(dn); }
                catch (Exception ex) { missing.Add($"{dn} ({ex.Message})"); }
            }
            results.Add(missing.Count == 0
                ? CheckResult.Pass("Target OUs exist", $"{Config.Mappings.SelectMany(m => m.PilotTargets.Concat(m.ProdTargets)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} OUs")
                : CheckResult.Fail("Target OUs exist", "Not found: " + string.Join("; ", missing)));

            if (Config.Mappings.Any(m => m.EffectivePilotMode(Config) == PilotMode.Group))
            {
                try
                {
                    results.Add(Directory.GroupExists(Config.PilotGroup)
                        ? CheckResult.Pass("Pilot group", Config.PilotGroup)
                        : CheckResult.Fail("Pilot group", $"Group '{Config.PilotGroup}' not found."));
                }
                catch (Exception ex) { results.Add(CheckResult.Fail("Pilot group", ex.Message)); }
            }

            try
            {
                System.IO.Directory.CreateDirectory(Config.WorkingFolder);
                var probe = Path.Combine(Config.WorkingFolder, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                results.Add(CheckResult.Pass("Working folder writable", Config.WorkingFolder));
            }
            catch (Exception ex) { results.Add(CheckResult.Fail("Working folder writable", ex.Message)); }

            return results;
        }

        // ================================================================ 1. load package

        /// <summary>Hashes, extracts and scans a DISA package and starts a new run (saved immediately).</summary>
        public RunState CreateRun(string zipPath, string expectedHash, out PackageScanResult scan)
        {
            if (!File.Exists(zipPath)) throw new FileNotFoundException("Package not found.", zipPath);
            Info($"Computing SHA-256 of {Path.GetFileName(zipPath)} ...");
            var hash = PackageScanner.ComputeSha256(zipPath);
            Info("SHA-256: " + hash);
            bool? matches = null;
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                matches = string.Equals(PackageScanner.NormalizeHash(expectedHash), hash, StringComparison.OrdinalIgnoreCase);
                if (matches == true) Ok("Hash matches the expected value.");
                else Error("HASH MISMATCH: the package does not match the expected SHA-256.");
            }

            var extracted = PackageScanner.Extract(zipPath, Config.PackagesFolder, hash, Info);
            scan = PackageScanner.Scan(extracted, Config.Language, Info);
            foreach (var w in scan.Warnings) Warn(w);

            var now = DateTime.Now;
            var run = new RunState
            {
                RunId = RunState.NewRunId(now),
                CreatedUtc = DateTime.UtcNow,
                CreatedBy = User,
                Domain = Config.Domain,
                TargetDc = Directory.Server,
                Package = new PackageRecord
                {
                    Name = Path.GetFileName(zipPath), ZipPath = zipPath, Sha256 = hash,
                    ExpectedSha256 = string.IsNullOrWhiteSpace(expectedHash) ? null : PackageScanner.NormalizeHash(expectedHash),
                    HashMatches = matches, ExtractedPath = extracted, LoadedUtc = DateTime.UtcNow,
                },
            };
            run.SetPhase(RunPhase.PackageLoaded, User, $"{scan.Backups.Count} backups, {scan.Templates.Count} templates");
            Save(run);
            Audit.Write(run.RunId, "LoadPackage", zipPath, matches == false ? "HashMismatch" : "Success", null, $"sha256={hash}; extracted={extracted}");
            Ok($"Run {run.RunId} created.");
            return run;
        }

        /// <summary>Re-scans the extracted package of a resumed run.</summary>
        public PackageScanResult RescanPackage(RunState run)
        {
            if (string.IsNullOrEmpty(run.Package?.ExtractedPath) || !System.IO.Directory.Exists(run.Package.ExtractedPath))
                throw new DirectoryNotFoundException($"Extracted package folder for run {run.RunId} is missing: {run.Package?.ExtractedPath}");
            return PackageScanner.Scan(run.Package.ExtractedPath, Config.Language, Info);
        }

        // ================================================================ 2. central store

        public List<CentralStoreItem> CompareCentralStore(PackageScanResult scan) =>
            CentralStore.Compare(scan.Templates, Config.EffectiveCentralStorePath);

        public OperationPlan PlanCentralStore(IEnumerable<CentralStoreItem> items)
        {
            var plan = new OperationPlan { Title = "Update Central Store" };
            var store = Config.EffectiveCentralStorePath;
            var copies = items.Where(i => i.WillCopy).ToList();
            if (!System.IO.Directory.Exists(store)) plan.Blockers.Add($"Central Store {store} does not exist.");
            if (copies.Count == 0) return plan;
            plan.Add("Backup", Config.BackupsFolder, $"Zip the entire PolicyDefinitions folder ({store}) first");
            foreach (var i in copies)
                plan.Add(i.Status == CentralStoreStatus.SkippedStoreNewer ? "DOWNGRADE" : i.StatusText, store, i.Source.RelativeTarget);
            if (copies.Any(c => c.Status == CentralStoreStatus.SkippedStoreNewer))
                plan.Warnings.Add("Some files will replace NEWER copies in the store (override ticked).");
            return plan;
        }

        public void ExecuteCentralStore(RunState run, IEnumerable<CentralStoreItem> items)
        {
            var copies = items.Where(i => i.WillCopy).ToList();
            var store = Config.EffectiveCentralStorePath;
            if (copies.Count == 0) { Info("Central Store: nothing to copy."); return; }
            var zip = Do(run, "BackupCentralStore", store, () => CentralStore.BackupStore(store, Config.BackupsFolder, DateTime.Now));
            run.Backups.Add(zip);
            Save(run);
            Ok($"Central Store backed up to {zip}");
            int done = 0;
            foreach (var i in copies)
            {
                Do(run, "CentralStore" + (i.Status == CentralStoreStatus.SkippedStoreNewer ? "Downgrade" : i.Status.ToString()), i.TargetPath,
                   () => CentralStore.CopyItem(i), $"source={i.Source.FullPath}; sha256={i.Source.Sha256}");
                done++;
            }
            run.SetPhase(RunPhase.CentralStoreUpdated, User, $"{done} files copied");
            Save(run);
            Ok($"Central Store updated: {done} file(s).");
        }

        // ================================================================ 3. import

        public OperationPlan PlanImport(RunState run, IEnumerable<ImportCandidate> candidates)
        {
            var list = candidates.ToList();
            var plan = new OperationPlan { Title = "Import DISA GPOs (no links are created in this step)" };
            plan.Blockers.AddRange(MappingResolver.ValidateSelection(list));
            var selected = list.Where(c => !c.Skip && c.Mapping != null && c.Version != null).ToList();
            if (selected.Count == 0) return plan;
            var backupDir = Path.Combine(Config.BackupsFolder, "gpo-<timestamp>");
            plan.Add("Backup", backupDir, "Backup-GPO -All (pre-flight backup of every GPO in the domain)");
            foreach (var c in selected)
            {
                var name = c.ProposedName;
                GpoInfo existing = null;
                try { existing = Directory.GetGpoByName(name); }
                catch (Exception ex) { plan.Blockers.Add($"Cannot check whether '{name}' exists: {ex.Message}"); }
                if (existing != null)
                    plan.Blockers.Add($"A GPO named '{name}' already exists ({existing.Id}). DISA GPOs are never imported over an existing GPO. Rename/remove it in GPMC or fix the mapping.");
                var existingItem = run.Items.FirstOrDefault(i => string.Equals(i.NewGpoName, name, StringComparison.OrdinalIgnoreCase) && i.NewGpoId != null);
                if (existingItem != null)
                    plan.Blockers.Add($"'{name}' was already imported in this run.");
                plan.Add("Create GPO", name, $"Import-GPO -CreateIfNeeded from backup {c.Backup.BackupId} ('{c.Backup.DisplayName}', {c.Backup.Scope})");
                if (Config.WriteGpoComment) plan.Add("Comment", name, CommentFor(run, c));
            }
            return plan;
        }

        private string CommentFor(RunState run, ImportCandidate c) =>
            $"Imported by StigGpoManager. Source: {run.Package?.Name} ({c.Backup.ProductFolder}); DISA backup '{c.Backup.DisplayName}' {c.Backup.BackupId}; Version {c.Version}; Imported {DateTime.Now:yyyy-MM-dd}; Run {run.RunId}.";

        /// <summary>Backs up all GPOs, then creates one new GPO per selected backup. Stops on any name conflict.</summary>
        public void ExecuteImport(RunState run, IEnumerable<ImportCandidate> candidates)
        {
            var selected = candidates.Where(c => !c.Skip).ToList();
            var problems = MappingResolver.ValidateSelection(selected);
            if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

            var backupDir = Path.Combine(Config.BackupsFolder, $"gpo-{DateTime.Now:yyyyMMdd-HHmmss}");
            System.IO.Directory.CreateDirectory(backupDir);
            Info($"Backing up all GPOs to {backupDir} ...");
            Do(run, "BackupAllGpos", backupDir, () => Directory.BackupAllGpos(backupDir));
            run.Backups.Add(backupDir);
            Save(run);
            Ok("Pre-flight GPO backup complete.");

            foreach (var c in selected)
            {
                var name = c.ProposedName;
                var existing = Directory.GetGpoByName(name);
                if (existing != null)
                {
                    Audit.Write(run.RunId, "ImportGpo", name, "Blocked", "GPO with this name already exists", existing.Id);
                    throw new InvalidOperationException($"STOPPED: a GPO named '{name}' already exists. Nothing was imported over it. Remaining backups were not imported.");
                }

                var item = run.Items.FirstOrDefault(i => i.BackupId == c.Backup.BackupId && i.NewGpoId == null);
                if (item == null)
                {
                    item = new RunItem();
                    run.Items.Add(item);
                }
                item.BackupId = c.Backup.BackupId;
                item.BackupDisplayName = c.Backup.DisplayName;
                item.BackupRoot = c.Backup.BackupRoot;
                item.ProductFolder = c.Backup.ProductFolder;
                item.MappingKey = c.Mapping.Key;
                item.Version = c.Version.ToString();
                item.NewGpoName = name;
                item.PilotMode = c.Mapping.EffectivePilotMode(Config);
                item.PilotTargets = c.Mapping.PilotTargets.ToList();
                item.ProdTargets = c.Mapping.ProdTargets.ToList();
                item.PilotGroup = item.PilotMode == PilotMode.Group ? Config.PilotGroup : null;
                item.Status = ItemStatus.Selected;
                Save(run);

                Info($"Importing '{c.Backup.DisplayName}' as '{name}' ...");
                GpoInfo gpo;
                try
                {
                    gpo = Do(run, "ImportGpo", name, () => Directory.ImportNewGpo(c.Backup.BackupRoot, c.Backup.BackupId, name),
                             $"backupId={c.Backup.BackupId}; source='{c.Backup.DisplayName}'");
                }
                catch (Exception ex)
                {
                    item.Status = ItemStatus.Failed;
                    item.LastError = ex.Message;
                    Save(run);
                    throw;
                }
                item.NewGpoId = LinkOrdering.NormalizeGuid(gpo.Id);
                item.ImportedUtc = DateTime.UtcNow;
                item.Status = ItemStatus.Imported;
                Save(run);

                if (Config.WriteGpoComment)
                {
                    try { Do(run, "SetGpoComment", name, () => Directory.SetGpoDescription(gpo.Id, CommentFor(run, c))); }
                    catch (Exception ex) { Warn($"Comment not written to '{name}': {ex.Message}"); }
                }

                ResolvePreviousVersion(item, c.Mapping);
                Save(run);
                Ok($"Created '{name}' {item.NewGpoId}" + (item.PreviousGpoName != null ? $"; previous version: '{item.PreviousGpoName}'" : "; no previous version found"));
            }
            run.SetPhase(RunPhase.Imported, User, $"{run.Items.Count(i => i.Status == ItemStatus.Imported)} GPOs imported");
            Save(run);
        }

        /// <summary>
        /// Previous version = same mapping, older version, currently linked (enabled) on one of the
        /// mapping's targets; if none is linked, the newest older version (flagged as not linked).
        /// </summary>
        public void ResolvePreviousVersion(RunItem item, GpoMapping mapping)
        {
            var newVersion = StigVersion.TryParse(item.Version);
            var older = Directory.GetAllGpos()
                .Select(g => new { Gpo = g, Version = mapping.VersionOfName(g.DisplayName) })
                .Where(x => x.Version != null && x.Version < newVersion)
                .OrderByDescending(x => x.Version)
                .ToList();
            item.OlderVersionIds = older.Select(x => LinkOrdering.NormalizeGuid(x.Gpo.Id)).ToList();
            item.PreviousGpoId = null;
            item.PreviousGpoName = null;
            item.PreviousVersion = null;
            if (older.Count == 0) return;

            var targets = item.ProdTargets.Concat(item.PilotTargets).ToList();
            var linkedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in targets)
            {
                try
                {
                    foreach (var l in Directory.GetLinks(t).Where(l => l.Enabled)) linkedIds.Add(LinkOrdering.NormalizeGuid(l.GpoId));
                }
                catch (Exception ex) { Warn($"Could not read links on {t}: {ex.Message}"); }
            }
            var linked = older.FirstOrDefault(x => linkedIds.Contains(LinkOrdering.NormalizeGuid(x.Gpo.Id)));
            var chosen = linked ?? older[0];
            item.PreviousGpoId = LinkOrdering.NormalizeGuid(chosen.Gpo.Id);
            item.PreviousGpoName = chosen.Gpo.DisplayName;
            item.PreviousVersion = chosen.Version.ToString();
            if (linked == null) Warn($"'{chosen.Gpo.DisplayName}' is the newest older version of {mapping.Key} but is not linked to any target.");
        }

        // ================================================================ 4. review / diff

        public ReviewResult Review(RunState run)
        {
            var result = new ReviewResult();
            var reportCache = new Dictionary<string, List<GpoSetting>>(StringComparer.OrdinalIgnoreCase);
            List<GpoSetting> SettingsOf(string id)
            {
                id = LinkOrdering.NormalizeGuid(id);
                if (!reportCache.TryGetValue(id, out var s))
                {
                    s = GpoSettingsParser.Parse(Directory.GetGpoReportXml(id));
                    reportCache[id] = s;
                }
                return s;
            }

            foreach (var item in run.Items.Where(i => i.NewGpoId != null && i.Status != ItemStatus.Failed))
            {
                Info($"Diffing '{item.NewGpoName}' against '{item.PreviousGpoName ?? "(no previous version)"}' ...");
                var mapping = Config.FindMapping(item.MappingKey);
                if (mapping != null && item.Status == ItemStatus.Imported) ResolvePreviousVersion(item, mapping);

                var newSettings = SettingsOf(item.NewGpoId);
                var oldSettings = item.PreviousGpoId != null ? SettingsOf(item.PreviousGpoId) : new List<GpoSetting>();
                var changes = SettingDiff.Compare(oldSettings, newSettings);

                var targets = item.ProdTargets.Concat(item.PilotTargets).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var exceptionGpos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> id
                foreach (var t in targets)
                {
                    try
                    {
                        foreach (var l in Directory.GetLinks(t).Where(l => Config.IsExceptionGpo(l.GpoName)))
                            exceptionGpos[l.GpoName] = l.GpoId;
                    }
                    catch (Exception ex) { result.Warnings.Add($"Could not read links on {t}: {ex.Message}"); }
                }
                var exSettings = new Dictionary<string, List<GpoSetting>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in exceptionGpos)
                {
                    try { exSettings[kv.Key] = SettingsOf(kv.Value); }
                    catch (Exception ex) { result.Warnings.Add($"Could not read report of exception GPO '{kv.Key}': {ex.Message}"); }
                }

                var findings = SettingDiff.CrossCheck(changes, newSettings, exSettings);
                var rows = SettingDiff.BuildRows(item.MappingKey, item.NewGpoName, item.PreviousGpoName, changes, findings);
                result.Rows.AddRange(rows);
                item.DiffAdded = changes.Count(c => c.Kind == ChangeKind.Added);
                item.DiffRemoved = changes.Count(c => c.Kind == ChangeKind.Removed);
                item.DiffChanged = changes.Count(c => c.Kind == ChangeKind.Changed);
                item.ExceptionFlags = findings.Count;
                item.ReplacementWarnings = findings.Count(f => f.Kind == ExceptionFindingKind.ReplacementTypeConflict);

                foreach (var w in PilotInheritanceWarnings(item)) { result.Warnings.Add(w); }
                Info($"  {item.DiffAdded} added, {item.DiffRemoved} removed, {item.DiffChanged} changed; {findings.Count} exception finding(s).");
                if (item.ReplacementWarnings > 0)
                    Warn($"  {item.ReplacementWarnings} REPLACEMENT-TYPE warning(s) (User Rights / Restricted Groups) for '{item.NewGpoName}'.");
            }

            System.IO.Directory.CreateDirectory(Config.ReportsFolder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            result.CsvPath = Path.Combine(Config.ReportsFolder, $"review-{run.RunId}-{stamp}.csv");
            result.HtmlPath = Path.Combine(Config.ReportsFolder, $"review-{run.RunId}-{stamp}.html");
            Reports.WriteReviewCsv(result.CsvPath, result.Rows);
            Reports.WriteReviewHtml(result.HtmlPath, run, result);
            run.Reports.Add(result.HtmlPath);
            run.Reports.Add(result.CsvPath);
            if (run.Phase < RunPhase.Reviewed) run.SetPhase(RunPhase.Reviewed, User);
            Save(run);
            Audit.Write(run.RunId, "Review", result.HtmlPath, "Success", null, $"{result.Rows.Count} rows");
            Ok($"Review report written: {result.HtmlPath}");
            return result;
        }

        /// <summary>
        /// For OU-based pilot: exception GPOs linked only on a parent OU are inherited with LOWER
        /// precedence than a GPO linked directly on the pilot OU, so during pilot the new DISA GPO
        /// would beat them.
        /// </summary>
        public List<string> PilotInheritanceWarnings(RunItem item)
        {
            var warnings = new List<string>();
            if (item.PilotMode != PilotMode.OU) return warnings;
            foreach (var pilot in item.PilotTargets)
            {
                List<LinkInfo> onPilot;
                try { onPilot = Directory.GetLinks(pilot); } catch { continue; }
                var direct = new HashSet<string>(onPilot.Select(l => LinkOrdering.NormalizeGuid(l.GpoId)), StringComparer.OrdinalIgnoreCase);
                foreach (var parent in AppConfig.AncestorDns(pilot))
                {
                    List<LinkInfo> links;
                    try { links = Directory.GetLinks(parent); } catch { continue; }
                    foreach (var l in links.Where(l => l.Enabled && Config.IsExceptionGpo(l.GpoName) && !direct.Contains(LinkOrdering.NormalizeGuid(l.GpoId))))
                        warnings.Add($"Exception GPO '{l.GpoName}' is linked on parent {parent} but not on pilot OU {pilot}. " +
                                     $"During pilot, '{item.NewGpoName}' (linked directly on the pilot OU) takes precedence over it on pilot machines. " +
                                     "Link the exception GPO to the pilot OU too, or use group-based pilot for this mapping.");
                }
            }
            return warnings;
        }
    }
}
