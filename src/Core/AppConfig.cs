using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace StigGpoManager.Core
{
    public enum PilotMode
    {
        /// <summary>Link the new GPO to the mapping's PilotTargets OUs.</summary>
        OU,
        /// <summary>Link the new GPO at the ProdTargets, security-filtered to PilotGroup.</summary>
        Group,
        /// <summary>No pilot for this mapping; it is linked directly at Promote.</summary>
        None
    }

    /// <summary>One mapping from DISA backups (matched by display-name regex) to a GPO name and link targets.</summary>
    public sealed class GpoMapping
    {
        public string Match { get; set; }
        public string NameTemplate { get; set; }
        public List<string> PilotTargets { get; set; } = new List<string>();
        public List<string> ProdTargets { get; set; } = new List<string>();
        public string Notes { get; set; }
        /// <summary>Explicit pilot mode; null means "OU" when PilotTargets is non-empty, else "Group" when PilotGroup is configured, else "None".</summary>
        public PilotMode? PilotModeOverride { get; set; }

        /// <summary>Stable key used in run state to refer to this mapping.</summary>
        public string Key => NameTemplate;

        private Regex _regex;
        public Regex MatchRegex => _regex ?? (_regex = new Regex(Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        /// <summary>True if Match fits the backup name, or a spelled-out / abbreviated form of it (see NameVariants).</summary>
        public bool IsMatch(string displayName) => !string.IsNullOrEmpty(displayName) && NameVariants.Of(displayName).Any(v => MatchRegex.IsMatch(v));

        public string RenderName(StigVersion version)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            return NameTemplate.Replace("{Version}", version.ToString());
        }

        /// <summary>Regex matching any version of this mapping's GPO name, capturing the version token.</summary>
        public Regex NameFamilyRegex()
        {
            var parts = NameTemplate.Split(new[] { "{Version}" }, StringSplitOptions.None);
            var pattern = "^" + string.Join("(?<ver>" + StigVersion.AnyVersionRegex + ")", parts.Select(Regex.Escape)) + "$";
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>If <paramref name="gpoName"/> is a version of this mapping's GPO, returns its version; otherwise null.</summary>
        public StigVersion VersionOfName(string gpoName)
        {
            if (string.IsNullOrEmpty(gpoName)) return null;
            var m = NameFamilyRegex().Match(gpoName);
            return m.Success ? StigVersion.TryParse(m.Groups["ver"].Value) : null;
        }

        public PilotMode EffectivePilotMode(AppConfig config)
        {
            if (PilotModeOverride.HasValue) return PilotModeOverride.Value;
            if (PilotTargets.Count > 0) return PilotMode.OU;
            if (!string.IsNullOrWhiteSpace(config?.PilotGroup)) return PilotMode.Group;
            return PilotMode.None;
        }

        public override string ToString() => NameTemplate;
    }

    public sealed class ConfigIssue
    {
        public bool IsError { get; set; }
        public string Message { get; set; }
        public override string ToString() => (IsError ? "ERROR: " : "WARNING: ") + Message;
    }

    /// <summary>
    /// The admin-maintained config.json. All domain-specific values live here so the same
    /// build runs in lab and production domains.
    /// </summary>
    public sealed class AppConfig
    {
        public string Domain { get; set; }
        public string TargetDC { get; set; } = "auto-pdc";
        public string ExceptionPrefix { get; set; }
        public string DisaGpoPrefix { get; set; } = "DoD ";
        public string WorkingFolder { get; set; }
        public string Language { get; set; } = "en-US";
        public string PilotGroup { get; set; }
        /// <summary>Optional override of the Central Store path; default is \\domain\SYSVOL\domain\Policies\PolicyDefinitions.</summary>
        public string CentralStorePath { get; set; }
        /// <summary>Write source package, version and import date into the new GPO's comment.</summary>
        public bool WriteGpoComment { get; set; } = true;
        /// <summary>
        /// "auto" (default): when DISA's backups refer to groups of DISA's own domain (Domain Admins, Enterprise
        /// Admins...), build a migration table that maps them to this domain's groups. "none": import unchanged.
        /// Or the path of a .migtable made in the GPMC Migration Table Editor.
        /// </summary>
        public string MigrationTable { get; set; } = "auto";
        public bool MigrationAuto => string.IsNullOrWhiteSpace(MigrationTable) || MigrationTable.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
        public bool MigrationNone => (MigrationTable ?? "").Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
        /// <summary>Planned soak length in days, used for the tray status ("Pilot - day 9 of 14").</summary>
        public int SoakDays { get; set; } = 14;
        /// <summary>Phase 2: Promote is blocked unless pilot scan results are imported and every open finding is in the exceptions register.</summary>
        public bool RequireScanGate { get; set; } = true;
        public List<GpoMapping> Mappings { get; set; } = new List<GpoMapping>();

        /// <summary>Path the config was loaded from (not serialized).</summary>
        public string SourcePath { get; set; }

        public string DomainDn => DomainToDn(Domain);

        public string EffectiveCentralStorePath =>
            !string.IsNullOrWhiteSpace(CentralStorePath)
                ? CentralStorePath
                : $@"\\{Domain}\SYSVOL\{Domain}\Policies\PolicyDefinitions";

        public string DomainControllersOuDn => "OU=Domain Controllers," + DomainDn;

        public string PackagesFolder => Path.Combine(WorkingFolder, "packages");
        public string BackupsFolder => Path.Combine(WorkingFolder, "backups");
        public string ReportsFolder => Path.Combine(WorkingFolder, "reports");
        public string RunsFolder => Path.Combine(WorkingFolder, "runs");
        public string AuditLogPath => Path.Combine(WorkingFolder, "audit.log");
        public string ExceptionsRegisterPath => Path.Combine(WorkingFolder, "exceptions.json");

        public bool IsExceptionGpo(string name) =>
            !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(ExceptionPrefix) && name.StartsWith(ExceptionPrefix, StringComparison.OrdinalIgnoreCase);

        public bool IsDisaGpo(string name) =>
            !string.IsNullOrEmpty(name) && !IsExceptionGpo(name) &&
            ((!string.IsNullOrEmpty(DisaGpoPrefix) && name.StartsWith(DisaGpoPrefix, StringComparison.OrdinalIgnoreCase))
             || Mappings.Any(m => m.VersionOfName(name) != null));

        public GpoMapping FindMapping(string key) =>
            Mappings.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

        public static string DomainToDn(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return "";
            return string.Join(",", domain.Trim().Split('.').Where(p => p.Length > 0).Select(p => "DC=" + p));
        }

        // ------------------------------------------------------------------ load / validate

        /// <summary>Loads and validates config.json. Returns the config (possibly partially filled) and all issues found.</summary>
        public static AppConfig Load(string path, out List<ConfigIssue> issues)
        {
            issues = new List<ConfigIssue>();
            if (!File.Exists(path))
            {
                issues.Add(Err($"Config file not found: {path}"));
                return null;
            }
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) { issues.Add(Err($"Cannot read config file {path}: {ex.Message}")); return null; }
            var cfg = FromJson(text, issues);
            if (cfg != null) cfg.SourcePath = path;
            return cfg;
        }

        public static AppConfig FromJson(string text, List<ConfigIssue> issues)
        {
            object root;
            try { root = Json.Parse(text); }
            catch (FormatException ex) { issues.Add(Err("config.json is not valid JSON. " + ex.Message)); return null; }
            var o = Json.AsObject(root);
            if (o == null) { issues.Add(Err("config.json must contain a JSON object at the top level.")); return null; }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Domain", "TargetDC", "ExceptionPrefix", "DisaGpoPrefix", "WorkingFolder", "Language", "PilotGroup",
                "CentralStorePath", "WriteGpoComment", "MigrationTable", "SoakDays", "RequireScanGate", "Mappings"
            };
            foreach (var k in o.Keys.Where(k => !known.Contains(k) && !k.StartsWith("_")))
                issues.Add(Warn($"Unknown setting '{k}' is ignored (check spelling)."));

            var cfg = new AppConfig
            {
                Domain = Json.GetString(o, "Domain"),
                TargetDC = Json.GetString(o, "TargetDC", "auto-pdc"),
                ExceptionPrefix = Json.GetString(o, "ExceptionPrefix"),
                DisaGpoPrefix = Json.GetString(o, "DisaGpoPrefix", "DoD "),
                WorkingFolder = Json.GetString(o, "WorkingFolder"),
                Language = Json.GetString(o, "Language", "en-US"),
                PilotGroup = Json.GetString(o, "PilotGroup"),
                CentralStorePath = Json.GetString(o, "CentralStorePath"),
                WriteGpoComment = Json.GetBool(o, "WriteGpoComment", true),
                MigrationTable = Json.GetString(o, "MigrationTable") ?? "auto",
                SoakDays = (int)Json.GetLong(o, "SoakDays", 14),
                RequireScanGate = Json.GetBool(o, "RequireScanGate", true),
            };

            int index = 0;
            foreach (var item in Json.GetArray(o, "Mappings"))
            {
                index++;
                var mo = Json.AsObject(item);
                if (mo == null) { issues.Add(Err($"Mappings[{index}] must be an object.")); continue; }
                var m = new GpoMapping
                {
                    Match = Json.GetString(mo, "Match"),
                    NameTemplate = Json.GetString(mo, "NameTemplate"),
                    PilotTargets = Json.GetStringArray(mo, "PilotTargets"),
                    ProdTargets = Json.GetStringArray(mo, "ProdTargets"),
                    Notes = Json.GetString(mo, "Notes"),
                };
                var pm = Json.GetString(mo, "PilotMode");
                if (!string.IsNullOrWhiteSpace(pm))
                {
                    if (Enum.TryParse<PilotMode>(pm, true, out var parsed)) m.PilotModeOverride = parsed;
                    else issues.Add(Err($"Mappings[{index}].PilotMode '{pm}' must be OU, Group or None."));
                }
                cfg.Mappings.Add(m);
            }

            issues.AddRange(cfg.Validate());
            return cfg;
        }

        /// <summary>Static validation (no AD access). OU existence is checked separately at pre-flight.</summary>
        public List<ConfigIssue> Validate()
        {
            var issues = new List<ConfigIssue>();
            if (string.IsNullOrWhiteSpace(Domain) || !Regex.IsMatch(Domain, @"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$"))
                issues.Add(Err("Domain must be the DNS name of the domain, e.g. lab.example.mil."));
            if (string.IsNullOrWhiteSpace(TargetDC))
                issues.Add(Err("TargetDC must be 'auto-pdc' or the FQDN of a domain controller."));
            if (string.IsNullOrWhiteSpace(ExceptionPrefix))
                issues.Add(Err("ExceptionPrefix is required (e.g. \"SITE - \"); it identifies site exception GPOs, which must always win."));
            if (string.IsNullOrWhiteSpace(DisaGpoPrefix))
                issues.Add(Err("DisaGpoPrefix is required (e.g. \"DoD \")."));
            if (!string.IsNullOrWhiteSpace(ExceptionPrefix) && !string.IsNullOrWhiteSpace(DisaGpoPrefix) &&
                (ExceptionPrefix.StartsWith(DisaGpoPrefix, StringComparison.OrdinalIgnoreCase) || DisaGpoPrefix.StartsWith(ExceptionPrefix, StringComparison.OrdinalIgnoreCase)))
                issues.Add(Err("ExceptionPrefix and DisaGpoPrefix overlap; they must distinguish exception GPOs from DISA GPOs."));
            if (string.IsNullOrWhiteSpace(WorkingFolder))
                issues.Add(Err("WorkingFolder is required (local path or UNC). Remember to double backslashes in JSON."));
            else if (!IsRootedPath(WorkingFolder))
                issues.Add(Err($"WorkingFolder '{WorkingFolder}' must be an absolute path."));
            if (string.IsNullOrWhiteSpace(Language) || !Regex.IsMatch(Language, @"^[a-zA-Z]{2,3}(-[a-zA-Z0-9]+)*$"))
                issues.Add(Err("Language must be a culture name such as en-US."));
            if (SoakDays < 0 || SoakDays > 365)
                issues.Add(Err("SoakDays must be between 0 and 365."));
            if (Mappings.Count == 0)
                issues.Add(Err("Mappings is empty; at least one mapping is required."));

            var domainDn = DomainDn;
            var seenTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Mappings.Count; i++)
            {
                var m = Mappings[i];
                var label = $"Mappings[{i + 1}]";
                if (string.IsNullOrWhiteSpace(m.Match))
                    issues.Add(Err($"{label}.Match is required (regex against the backup's GPO display name)."));
                else
                {
                    try { _ = new Regex(m.Match); }
                    catch (ArgumentException ex) { issues.Add(Err($"{label}.Match is not a valid regex: {ex.Message}")); }
                }
                if (string.IsNullOrWhiteSpace(m.NameTemplate))
                    issues.Add(Err($"{label}.NameTemplate is required."));
                else
                {
                    label = $"{label} ({m.NameTemplate})";
                    int count = Regex.Matches(m.NameTemplate, Regex.Escape("{Version}")).Count;
                    if (count != 1)
                        issues.Add(Err($"{label}: NameTemplate must contain {{Version}} exactly once, so each release gets a new GPO name."));
                    if (!seenTemplates.Add(m.NameTemplate))
                        issues.Add(Err($"{label}: NameTemplate is used by more than one mapping."));
                    if (IsExceptionGpo(m.NameTemplate))
                        issues.Add(Err($"{label}: NameTemplate starts with the ExceptionPrefix; DISA GPOs must not look like exception GPOs."));
                    if (!string.IsNullOrEmpty(DisaGpoPrefix) && !m.NameTemplate.StartsWith(DisaGpoPrefix, StringComparison.OrdinalIgnoreCase))
                        issues.Add(Warn($"{label}: NameTemplate does not start with DisaGpoPrefix '{DisaGpoPrefix}'."));
                    if (m.NameTemplate.Length > 250)
                        issues.Add(Err($"{label}: NameTemplate is too long."));
                }
                if (m.ProdTargets.Count == 0)
                    issues.Add(Err($"{label}: ProdTargets must list at least one OU."));
                foreach (var dn in m.PilotTargets.Concat(m.ProdTargets))
                {
                    if (!IsValidDn(dn))
                        issues.Add(Err($"{label}: '{dn}' is not a valid distinguished name."));
                    else if (domainDn.Length > 0 && !dn.EndsWith(domainDn, StringComparison.OrdinalIgnoreCase))
                        issues.Add(Err($"{label}: '{dn}' is not in domain {Domain} ({domainDn}). Each domain needs its own config."));
                }
                foreach (var dn in m.PilotTargets.Where(p => m.ProdTargets.Any(q => DnEquals(p, q))))
                    issues.Add(Err($"{label}: '{dn}' is both a pilot and a production target."));
                var mode = m.EffectivePilotMode(this);
                if (mode == PilotMode.Group && string.IsNullOrWhiteSpace(PilotGroup))
                    issues.Add(Err($"{label}: PilotMode is Group but PilotGroup is not set."));
                if (mode == PilotMode.OU && m.PilotTargets.Count == 0)
                    issues.Add(Err($"{label}: PilotMode is OU but PilotTargets is empty."));
                if (mode == PilotMode.None)
                    issues.Add(Warn($"{label}: no pilot (no PilotTargets and no PilotGroup); this GPO goes straight to production at Promote."));
            }
            return issues;
        }

        public static bool IsValidDn(string dn)
        {
            if (string.IsNullOrWhiteSpace(dn)) return false;
            return Regex.IsMatch(dn.Trim(), @"^((OU|CN|DC)=[^,=]+)(,\s*(OU|CN|DC)=[^,=]+)*$", RegexOptions.IgnoreCase);
        }

        public static bool DnEquals(string a, string b) =>
            string.Equals(NormalizeDn(a), NormalizeDn(b), StringComparison.OrdinalIgnoreCase);

        public static string NormalizeDn(string dn) =>
            string.Join(",", (dn ?? "").Split(',').Select(p => p.Trim()));

        /// <summary>Parent containers of an OU, nearest first, ending with the domain root (which can also carry GPO links).</summary>
        public static IEnumerable<string> AncestorDns(string dn)
        {
            var parts = NormalizeDn(dn).Split(',');
            for (int i = 1; i < parts.Length; i++)
            {
                var parent = string.Join(",", parts.Skip(i));
                yield return parent;
                if (parent.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)) yield break;
            }
        }

        private static bool IsRootedPath(string p) =>
            p.StartsWith(@"\\") || Regex.IsMatch(p, @"^[A-Za-z]:\\") || p.StartsWith("/");

        private static ConfigIssue Err(string m) => new ConfigIssue { IsError = true, Message = m };
        private static ConfigIssue Warn(string m) => new ConfigIssue { IsError = false, Message = m };
    }
}
