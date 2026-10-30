using System;
using System.Collections.Generic;
using System.Linq;
using StigGpoManager.Core;

namespace StigGpoManager
{
    /// <summary>
    /// Command line: StigGpoManager.exe cli &lt;command&gt; [options]. Run from an elevated console.
    /// Commands that change AD print their plan; they act only with --yes (or --confirm WORD for
    /// promote/delete). --whatif prints the plan and never acts.
    /// </summary>
    public static class Cli
    {
        private const string Usage = @"STIG GPO Manager - command line
Usage: StigGpoManager.exe cli <command> [options] [--config path]

  detect [--save] [--force]            Detect domain settings and print a suggested config.json
                                       (--save writes it to the config path; --force overwrites)
  preflight [--dcdiag]                 Pre-flight checks
  runs                                 List runs
  load <zip> [--hash <sha256>]         Create a run from a DISA package (no AD changes)
  central-store --run <id> [--allow-downgrade] [--whatif | --yes]
  import --run <id> [--whatif | --yes] Import all matched backups as new GPOs
  review --run <id>                    Diff + exception cross-check; writes HTML/CSV reports
  pilot --run <id> [--confirm-dc-backup] [--whatif | --yes]
  import-scan --run <id> --stage Pilot|Baseline <file> [<file>...]
  gate --run <id>                      Evaluate the scan-results promotion gate
  promote --run <id> [--confirm-dc-backup] [--whatif | --confirm PROMOTE]
  refresh --run <id> [--whatif | --yes]  Invoke-GPUpdate on computers under the prod targets
  rollback --run <id> [--gpo <name>] [--confirm-dc-backup] [--whatif | --yes]
  cleanup [--delete <name> ...] [--whatif | --confirm DELETE]
  report --run <id>                    Write the run report
  close --run <id>                     Mark a run closed
  sample-config                        Print a sample config.json

Exit codes: 0 success, 1 error, 2 blocked / confirmation required.";

        private static int Detect(string[] rest, string savePath)
        {
            var domain = SetupWindow.DetectComputerDomain();
            if (string.IsNullOrEmpty(domain)) { Console.WriteLine("This computer is not joined to a domain."); return 1; }
            foreach (var r in HostChecks.Modules().Where(r => r.Status == CheckStatus.Fail)) { Console.WriteLine(r); return 1; }
            using (var dir = new PowerShellDirectory(new AppConfig { Domain = domain, TargetDC = "auto-pdc" }))
            {
                var det = ConfigDetector.Detect(dir, SetupWindow.DefaultWorkingFolder(), System.Globalization.CultureInfo.InstalledUICulture.Name, "DoD ", m => Console.Error.WriteLine(m));
                foreach (var n in det.Notes) Console.Error.WriteLine("NOTE: " + n);
                foreach (var c in det.ExceptionPrefixCandidates.Take(5)) Console.Error.WriteLine("Exception prefix candidate: " + c);
                foreach (var m in det.Mappings) Console.Error.WriteLine($"Mapping: {m.Mapping.NameTemplate}  <- {m.Reason}");
                foreach (var m in det.UnlinkedFamilies) Console.Error.WriteLine($"Not linked (not included): {m.LatestGpo}");
                var json = ConfigWriter.ToJson(det.Suggested);
                Console.WriteLine(json);
                if (rest.Any(a => a == "--save"))
                {
                    if (System.IO.File.Exists(savePath) && !rest.Any(a => a == "--force")) { Console.WriteLine($"{savePath} exists; add --force to overwrite."); return 2; }
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(savePath));
                    System.IO.File.WriteAllText(savePath, json);
                    Console.WriteLine("Saved " + savePath + " - review it (or run StigGpoManager.exe /setup) before use.");
                }
                return 0;
            }
        }

        public static int Run(string[] args, string configPath)
        {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help" || args[0] == "/?")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var cmd = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            if (cmd == "sample-config") { Console.WriteLine(Installer.SampleConfig()); return 0; }
            if (cmd == "detect") return Detect(rest, Program.ResolveConfigPath(configPath ?? Program.ArgValue(rest, "--config")));

            bool Flag(string f) => rest.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase));
            string Opt(string f) => Program.ArgValue(rest, f);
            List<string> Positional()
            {
                var list = new List<string>();
                for (int i = 0; i < rest.Length; i++)
                {
                    if (rest[i].StartsWith("--"))
                    {
                        if (!new[] { "--whatif", "--yes", "--dcdiag", "--confirm-dc-backup", "--allow-downgrade" }.Contains(rest[i].ToLowerInvariant())) i++;
                        continue;
                    }
                    list.Add(rest[i]);
                }
                return list;
            }
            bool whatIf = Flag("--whatif");

            using (var session = new Session(Program.ResolveConfigPath(configPath ?? Opt("--config"))))
            {
                foreach (var i in session.Issues) Console.WriteLine(i);
                if (!session.ConfigValid) { Console.WriteLine($"Config {session.ConfigPath} is not valid."); return 1; }
                try
                {
                    session.Connect((lvl, m) => Console.WriteLine($"[{lvl}] {m}"), whatIf ? null : (Action<string>)(c => Console.WriteLine("    " + c)));
                }
                catch (Exception ex) { Console.WriteLine("Cannot connect: " + ex.Message); return 1; }
                var e = session.Engine;

                RunState LoadRun()
                {
                    var id = Opt("--run") ?? throw new ArgumentException("--run <id> is required.");
                    return e.Store.Load(id);
                }

                int Act(OperationPlan plan, Action execute, string word = null)
                {
                    Console.WriteLine(plan.ToText());
                    if (plan.Blockers.Count > 0) return 2;
                    if (plan.Changes.Count == 0) { Console.WriteLine("Nothing to do."); return 0; }
                    if (whatIf) { Console.WriteLine("WhatIf: no changes made."); return 0; }
                    if (plan.RequiredAcknowledgements.Count > 0 && !Flag("--confirm-dc-backup"))
                    { Console.WriteLine("Add --confirm-dc-backup to confirm the statement above."); return 2; }
                    if (word != null ? Opt("--confirm") != word : !Flag("--yes"))
                    { Console.WriteLine(word != null ? $"Add --confirm {word} to proceed." : "Add --yes to proceed."); return 2; }
                    execute();
                    return 0;
                }

                try
                {
                    switch (cmd)
                    {
                        case "preflight":
                        {
                            var results = session.Preflight(Flag("--dcdiag"));
                            foreach (var r in results) Console.WriteLine(r);
                            return results.Any(r => r.Status == CheckStatus.Fail) ? 1 : 0;
                        }
                        case "runs":
                            foreach (var r in e.Store.LoadAll())
                                Console.WriteLine($"{r.RunId}  {r.Package?.Name,-45} {r.StatusText(session.Config.SoakDays, DateTime.UtcNow)}");
                            return 0;
                        case "load":
                        {
                            var zip = Positional().FirstOrDefault() ?? throw new ArgumentException("load <zip> is required.");
                            var run = e.CreateRun(zip, Opt("--hash"), out var scan);
                            foreach (var c in MappingResolver.Resolve(scan.Backups, session.Config))
                                Console.WriteLine($"{(c.Skip ? "SKIP  " : "IMPORT")} {c.Backup.DisplayName,-60} {c.Version?.ToString() ?? "?",-6} {c.Backup.Scope,-14} -> {c.ProposedName ?? "(no mapping)"} {c.Problem}");
                            Console.WriteLine($"Run ID: {run.RunId}");
                            return run.Package.HashMatches == false ? 2 : 0;
                        }
                        case "central-store":
                        {
                            var run = LoadRun();
                            var items = e.CompareCentralStore(e.RescanPackage(run));
                            if (Flag("--allow-downgrade")) items.ForEach(i => i.OverrideDowngrade = true);
                            foreach (var i in items) Console.WriteLine($"{i.StatusText,-30} {i.Source.RelativeTarget}");
                            return Act(e.PlanCentralStore(items), () => e.ExecuteCentralStore(run, items));
                        }
                        case "import":
                        {
                            var run = LoadRun();
                            var candidates = MappingResolver.Resolve(e.RescanPackage(run).Backups, session.Config);
                            return Act(e.PlanImport(run, candidates), () => e.ExecuteImport(run, candidates));
                        }
                        case "review":
                        {
                            var result = e.Review(LoadRun());
                            foreach (var w in result.Warnings) Console.WriteLine("WARNING: " + w);
                            foreach (var r in result.Rows.Where(r => r.Severity > 0)) Console.WriteLine($"{r.Flag}: {r.Setting} [{r.ExceptionGpo}] {r.Detail}");
                            Console.WriteLine($"Report: {result.HtmlPath}");
                            return 0;
                        }
                        case "pilot":
                        {
                            var run = LoadRun();
                            var code = Act(e.PlanPilot(run), () => e.ExecutePilot(run, null, Flag("--confirm-dc-backup")));
                            if (code == 0 && !whatIf && Flag("--yes")) foreach (var s in StigEngine.PilotChecklist) Console.WriteLine(" [ ] " + s);
                            return code;
                        }
                        case "import-scan":
                        {
                            var run = LoadRun();
                            var stage = Opt("--stage") ?? StigEngine.StagePilot;
                            foreach (var f in Positional()) e.ImportScanResult(run, f, stage);
                            return 0;
                        }
                        case "gate":
                        {
                            var g = e.EvaluateGate(LoadRun());
                            Console.WriteLine((g.Passed ? "PASS: " : "FAIL: ") + g.Summary);
                            foreach (var f in g.Blocking) Console.WriteLine($"  BLOCKING {f.Host} {f.VulnId} {f.Title}");
                            return g.Passed ? 0 : 2;
                        }
                        case "promote":
                        {
                            var run = LoadRun();
                            return Act(e.PlanPromote(run), () => e.ExecutePromote(run, Opt("--confirm"), null, Flag("--confirm-dc-backup")), "PROMOTE");
                        }
                        case "refresh":
                        {
                            var run = LoadRun();
                            var computers = e.RefreshTargets(run);
                            var plan = new OperationPlan { Title = "Policy refresh (Invoke-GPUpdate -Force -RandomDelayInMinutes 0)" };
                            foreach (var c in computers) plan.Add("GPUpdate", c, "Force policy refresh");
                            return Act(plan, () => e.ExecuteRefresh(run, computers, r => Console.WriteLine($"{(r.Success ? "OK  " : "FAIL")} {r.Computer} {r.Message}")));
                        }
                        case "rollback":
                        {
                            var run = LoadRun();
                            var name = Opt("--gpo");
                            var items = e.RollbackableItems(run).Where(i => name == null || string.Equals(i.NewGpoName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                            return Act(e.PlanRollback(run, items), () => e.ExecuteRollback(run, items, Flag("--confirm-dc-backup")));
                        }
                        case "cleanup":
                        {
                            var candidates = e.GetCleanupCandidates();
                            foreach (var c in candidates) Console.WriteLine($"{c.Gpo.DisplayName,-50} {c.Reason} (newest {c.LatestVersion})");
                            var names = new List<string>();
                            for (int i = 0; i < rest.Length - 1; i++) if (rest[i] == "--delete") names.Add(rest[i + 1]);
                            if (names.Count == 0) return 0;
                            var selected = candidates.Where(c => names.Contains(c.Gpo.DisplayName, StringComparer.OrdinalIgnoreCase)).ToList();
                            foreach (var n in names.Where(n => !selected.Any(c => string.Equals(c.Gpo.DisplayName, n, StringComparison.OrdinalIgnoreCase))))
                                Console.WriteLine($"'{n}' is not a cleanup candidate; ignored.");
                            return Act(e.PlanCleanup(selected), () => e.ExecuteCleanup(selected, Opt("--confirm")), "DELETE");
                        }
                        case "report":
                            Console.WriteLine(e.WriteRunReport(LoadRun()));
                            return 0;
                        case "close":
                            e.CloseRun(LoadRun());
                            return 0;
                        default:
                            Console.WriteLine($"Unknown command '{cmd}'.\n");
                            Console.WriteLine(Usage);
                            return 1;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR: " + ex.Message);
                    return 1;
                }
            }
        }
    }
}
