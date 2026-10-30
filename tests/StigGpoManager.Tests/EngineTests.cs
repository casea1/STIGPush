using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StigGpoManager.Core;
using Xunit;

namespace StigGpoManager.Tests
{
    /// <summary>End-to-end quarterly workflow against the in-memory FakeDirectory.</summary>
    public sealed class EngineTests : IDisposable
    {
        private const string Ex = "SITE - Server Exceptions";
        private const string Old = "DoD WinSvr2025 MS STIG Comp v1r3";
        private const string Older = "DoD WinSvr2025 MS STIG Comp v1r2";
        private const string New = "DoD WinSvr2025 MS STIG Comp v1r4";
        private const string Other = "Site Baseline";

        private readonly TempDir _tmp = new TempDir();
        private readonly FakeDirectory _ad = new FakeDirectory();
        private readonly AppConfig _cfg;
        private readonly StigEngine _engine;
        private readonly List<string> _log = new List<string>();
        private readonly string _zip;

        public EngineTests()
        {
            _cfg = TestData.Config(_tmp.Sub("work"), _tmp.Sub("store"));
            foreach (var dn in new[] { TestData.ProdOu, TestData.PilotOu, TestData.AdminOu, TestData.DcOu }) _ad.Containers.Add(dn);

            var oldReport = TestData.Report(Old,
                TestData.Account("MinimumPasswordLength", 14),
                TestData.UserRight("SeDenyNetworkLogonRight", "BUILTIN\\Guests"),
                TestData.Policy("Windows Components/AutoPlay", "Turn off Autoplay", "Enabled"));
            var newReport = TestData.Report(New,
                TestData.Account("MinimumPasswordLength", 15),
                TestData.UserRight("SeDenyNetworkLogonRight", "BUILTIN\\Guests", "NT AUTHORITY\\Local account"),
                TestData.Policy("Windows Components/AutoPlay", "Turn off Autoplay", "Enabled"),
                TestData.Policy("Windows Components/Remote Desktop", "Do not allow drive redirection", "Enabled"));
            var exReport = TestData.Report(Ex,
                TestData.UserRight("SeDenyNetworkLogonRight", "BUILTIN\\Guests", "LAB\\svc-scan"),
                TestData.Account("MinimumPasswordLength", 15));

            var ex = _ad.AddGpo(Ex, exReport);
            var old = _ad.AddGpo(Old, oldReport);
            var older = _ad.AddGpo(Older);
            var other = _ad.AddGpo(Other);
            _ad.Link(TestData.ProdOu, ex);
            _ad.Link(TestData.ProdOu, old);
            _ad.Link(TestData.ProdOu, other);
            _ad.Link(TestData.ProdOu, older, enabled: false);
            _ad.Link(TestData.AdminOu, old);
            _ad.Link(TestData.AdminOu, ex);   // violation: exception below DISA; must be fixed
            _ad.Computers[TestData.ProdOu] = new List<string> { "srv1.lab.test", "offline1.lab.test" };

            _engine = new StigEngine(_cfg, _ad, new AuditLog(_cfg.AuditLogPath, "LAB\\admin"), new RunStore(_cfg.RunsFolder), "LAB\\admin");
            _engine.Log += (l, m) => _log.Add($"{l}: {m}");
            _zip = TestData.BuildPackage(_tmp, newReport);
        }

        public void Dispose() => _tmp.Dispose();

        private RunState ImportMemberServer()
        {
            var run = _engine.CreateRun(_zip, null, out var scan);
            var candidates = MappingResolver.Resolve(scan.Backups, _cfg);
            foreach (var c in candidates) c.Skip = c.Mapping?.Key != "DoD WinSvr2025 MS STIG Comp {Version}";
            var plan = _engine.PlanImport(run, candidates);
            Assert.Empty(plan.Blockers);
            _engine.ExecuteImport(run, candidates);
            return run;
        }

        [Fact]
        public void FullQuarterlyCycle_ImportPilotPromoteRollback()
        {
            var run = ImportMemberServer();
            var item = run.Items.Single();
            Assert.Equal(New, item.NewGpoName);
            Assert.Equal(Old, item.PreviousGpoName);
            Assert.Equal(2, item.OlderVersionIds.Count);
            Assert.Contains("BackupAllGpos", _ad.Calls);
            Assert.Contains("Version v1r4", _ad.Gpos[item.NewGpoId].Description);
            Assert.DoesNotContain(_ad.Calls, c => c.StartsWith("NewLink")); // import never links

            // Review
            var review = _engine.Review(run);
            Assert.True(File.Exists(review.HtmlPath));
            Assert.True(File.Exists(review.CsvPath));
            Assert.Contains(review.Rows, r => r.Change == "Added" && r.Setting.Contains("drive redirection"));
            Assert.Contains(review.Rows, r => r.Severity == 2 && r.Setting == "SeDenyNetworkLogonRight" && r.Detail.Contains("NT AUTHORITY\\Local account"));
            Assert.Contains(review.Rows, r => r.Flag == "Exception may be unnecessary" && r.Setting == "Password/MinimumPasswordLength");
            Assert.Contains(review.Warnings, w => w.Contains("linked on parent") && w.Contains(Ex)); // pilot OU inherits the exception

            // Pilot (OU-based): only the pilot OU gets a link
            var pilotPlan = _engine.PlanPilot(run);
            Assert.Empty(pilotPlan.Blockers);
            Assert.Empty(pilotPlan.RequiredAcknowledgements);
            _engine.ExecutePilot(run);
            Assert.Equal(new[] { New }, _ad.OrderNames(TestData.PilotOu));
            Assert.Equal(new[] { Ex, Old, Other, Older + " (off)" }, _ad.OrderNames(TestData.ProdOu));
            Assert.Equal(RunPhase.Piloted, run.Phase);
            Assert.Equal(ItemStatus.Piloted, item.Status);

            // Run survives a restart: reload from disk and continue
            run = _engine.Store.Load(run.RunId);
            Assert.Contains("Pilot - day 1 of 14", run.StatusText(14, DateTime.UtcNow));
            Assert.Contains("ready to promote", run.StatusText(14, DateTime.UtcNow.AddDays(20)));

            // Promote requires the typed word
            Assert.Throws<InvalidOperationException>(() => _engine.ExecutePromote(run, "promote"));
            var promotePlan = _engine.PlanPromote(run);
            Assert.Equal("PROMOTE", promotePlan.ConfirmationWord);
            Assert.Contains(promotePlan.Changes, c => c.Kind == "Disable" && c.Description.Contains(Old));
            _engine.ExecutePromote(run, "PROMOTE");

            Assert.Equal(new[] { Ex, New, Old + " (off)", Other, Older + " (off)" }, _ad.OrderNames(TestData.ProdOu));
            Assert.Equal(new[] { Ex, New, Old + " (off)" }, _ad.OrderNames(TestData.AdminOu)); // exception moved above DISA
            Assert.Empty(_ad.OrderNames(TestData.PilotOu));                                    // pilot-only link removed
            Assert.True(_ad.Gpos.ContainsKey(_ad.IdOf(Old)));                                   // disabled, not deleted
            Assert.Equal(RunPhase.Promoted, run.Phase);

            // Policy refresh reports per machine
            var results = _engine.ExecuteRefresh(run, _engine.RefreshTargets(run));
            Assert.Single(results, r => r.Success);
            Assert.Single(results, r => !r.Success && r.Computer.StartsWith("offline"));

            // Rollback from persisted state
            run = _engine.Store.Load(run.RunId);
            var rbPlan = _engine.PlanRollback(run);
            Assert.Empty(rbPlan.Blockers);
            _engine.ExecuteRollback(run);
            Assert.Equal(new[] { Ex, Old, Other, Older + " (off)", New + " (off)" }, _ad.OrderNames(TestData.ProdOu));
            Assert.Equal(new[] { Ex, Old, New + " (off)" }, _ad.OrderNames(TestData.AdminOu));
            Assert.Equal(RunPhase.RolledBack, run.Phase);

            // Every change is in the append-only audit log
            var audit = _engine.Audit.ReadAll(run.RunId);
            Assert.Contains(audit, a => Json.GetString(a, "action") == "ImportGpo" && Json.GetString(a, "result") == "Success");
            Assert.Contains(audit, a => Json.GetString(a, "action") == "DisableLink");
            Assert.All(audit, a => Assert.Equal("LAB\\admin", Json.GetString(a, "user")));
            var report = _engine.WriteRunReport(run);
            Assert.Contains("Link changes", File.ReadAllText(report));
        }

        [Fact]
        public void ImportStopsWhenNameExists()
        {
            _ad.AddGpo(New);
            var run = _engine.CreateRun(_zip, null, out var scan);
            var candidates = MappingResolver.Resolve(scan.Backups, _cfg);
            foreach (var c in candidates) c.Skip = c.Mapping?.Key != "DoD WinSvr2025 MS STIG Comp {Version}";
            Assert.Contains(_engine.PlanImport(run, candidates).Blockers, b => b.Contains("already exists"));
            var ex = Assert.Throws<InvalidOperationException>(() => _engine.ExecuteImport(run, candidates));
            Assert.Contains("STOPPED", ex.Message);
            Assert.DoesNotContain(_ad.Calls, c => c.StartsWith("ImportNewGpo"));
        }

        [Fact]
        public void HashMismatchIsRecorded()
        {
            var run = _engine.CreateRun(_zip, "00 11 22", out _);
            Assert.False(run.Package.HashMatches);
            Assert.Contains(_engine.Audit.ReadAll(run.RunId), a => Json.GetString(a, "result") == "HashMismatch");
            var good = _engine.CreateRun(_zip, PackageScanner.ComputeSha256(_zip).ToLowerInvariant(), out _);
            Assert.True(good.Package.HashMatches);
        }

        [Fact]
        public void GroupPilotOnSingleDcRequiresBackupConfirmation()
        {
            _ad.DomainControllers.RemoveAt(1);
            var oldDc = _ad.AddGpo("DoD WinSvr2025 DC STIG Comp v1r3");
            var exDc = _ad.AddGpo("SITE - DC Exceptions");
            _ad.Link(TestData.DcOu, exDc);
            _ad.Link(TestData.DcOu, oldDc);

            var run = _engine.CreateRun(_zip, null, out var scan);
            var candidates = MappingResolver.Resolve(scan.Backups, _cfg);
            foreach (var c in candidates) c.Skip = c.Mapping?.Key != "DoD WinSvr2025 DC STIG Comp {Version}";
            _engine.ExecuteImport(run, candidates);
            var item = run.Items.Single();
            Assert.Equal(PilotMode.Group, item.PilotMode);

            var plan = _engine.PlanPilot(run);
            Assert.Single(plan.RequiredAcknowledgements);
            Assert.Contains("system-state", plan.RequiredAcknowledgements[0]);
            Assert.Throws<InvalidOperationException>(() => _engine.ExecutePilot(run));
            Assert.Equal(new[] { "SITE - DC Exceptions", "DoD WinSvr2025 DC STIG Comp v1r3" }, _ad.OrderNames(TestData.DcOu));

            _engine.ExecutePilot(run, null, dcBackupConfirmed: true);
            Assert.Single(run.Confirmations, c => c.Kind == "SystemStateBackup");
            Assert.Equal("GpoApply", _ad.Permissions[item.NewGpoId]["GPO-Pilot"]);
            Assert.Equal("GpoRead", _ad.Permissions[item.NewGpoId]["Authenticated Users"]);
            Assert.Equal(new[] { "SITE - DC Exceptions", "DoD WinSvr2025 DC STIG Comp v1r4", "DoD WinSvr2025 DC STIG Comp v1r3" }, _ad.OrderNames(TestData.DcOu));

            _engine.ExecutePromote(run, "PROMOTE", null, dcBackupConfirmed: true);
            Assert.Equal("GpoApply", _ad.Permissions[item.NewGpoId]["Authenticated Users"]);
            Assert.False(_ad.Permissions[item.NewGpoId].ContainsKey("GPO-Pilot"));
            Assert.Equal(new[] { "SITE - DC Exceptions", "DoD WinSvr2025 DC STIG Comp v1r4", "DoD WinSvr2025 DC STIG Comp v1r3 (off)" }, _ad.OrderNames(TestData.DcOu));
        }

        [Fact]
        public void ScanGateBlocksPromoteUntilFindingIsRegistered()
        {
            _cfg.RequireScanGate = true;
            _engine.PromotionChecks.Add(new ScanGateCheck(_engine));
            var run = ImportMemberServer();
            _engine.ExecutePilot(run);

            Assert.Contains(_engine.PlanPromote(run).Blockers, b => b.Contains("No pilot scan results"));

            var ckl = Path.Combine(_tmp.Path, "srv-pilot1.ckl");
            File.WriteAllText(ckl, TestData.Ckl("srv-pilot1", "Windows Server 2025 STIG", ("V-254239", "Open"), ("V-254240", "NotAFinding"), ("V-254241", "Not_Reviewed")));
            _engine.ImportScanResult(run, ckl, StigEngine.StagePilot);
            var plan = _engine.PlanPromote(run);
            Assert.Contains(plan.Blockers, b => b.Contains("V-254239"));
            Assert.Throws<InvalidOperationException>(() => _engine.ExecutePromote(run, "PROMOTE"));

            var reg = _engine.LoadRegister();
            reg.Entries.Add(new ExceptionEntry { VulnId = "V-254239", StigName = "Windows Server 2025", ExceptionGpo = Ex, Justification = "Mission app needs SMBv1 until Q2", PoamReference = "POAM-0042", DateAdded = DateTime.UtcNow });
            _engine.SaveRegister(reg);
            Assert.Empty(_engine.PlanPromote(run).Blockers);
            _engine.ExecutePromote(run, "PROMOTE");
            Assert.True(run.Gate.Passed);
            Assert.True(File.Exists(run.ScanImports[0].StoredPath));
        }

        [Fact]
        public void CleanupKeepsNewestTwoAndLinkedVersions()
        {
            var v11 = _ad.AddGpo("DoD WinSvr2025 MS STIG Comp v1r1");
            var v10 = _ad.AddGpo("DoD WinSvr2025 MS STIG Comp v1r0");
            _ad.Link(TestData.AdminOu, v10); // enabled link: must be kept
            var candidates = _engine.GetCleanupCandidates();
            // versions present: v1r3 (newest), v1r2 (kept), v1r1, v1r0. v1r2 has a disabled link only but is the rollback target.
            Assert.Equal(new[] { "DoD WinSvr2025 MS STIG Comp v1r1" }, candidates.Select(c => c.Gpo.DisplayName).ToArray());

            Assert.Throws<InvalidOperationException>(() => _engine.ExecuteCleanup(candidates, "delete"));
            Assert.Equal("DELETE", _engine.PlanCleanup(candidates).ConfirmationWord);
            _engine.ExecuteCleanup(candidates, "DELETE");
            Assert.False(_ad.Gpos.ContainsKey(v11));
            Assert.Contains("BackupGpo DoD WinSvr2025 MS STIG Comp v1r1", _ad.Calls);
            Assert.True(_ad.Calls.IndexOf("BackupGpo DoD WinSvr2025 MS STIG Comp v1r1") < _ad.Calls.IndexOf("RemoveGpo DoD WinSvr2025 MS STIG Comp v1r1"));
        }

        /// <summary>.NET Framework rejects these in paths; plans must never Path.Combine placeholders (regression).</summary>
        [Fact]
        public void PlanTextHasNoCharactersInvalidInWindowsPaths()
        {
            var run = _engine.CreateRun(_zip, null, out var scan);
            var candidates = MappingResolver.Resolve(scan.Backups, _cfg);
            var plans = new[] { _engine.PlanImport(run, candidates), _engine.PlanCleanup(new CleanupCandidate[0]) };
            foreach (var c in plans.SelectMany(p => p.Changes).Where(c => c.Kind == "Backup"))
                Assert.DoesNotContain(c.Target + c.Description, ch => ch == '<' || ch == '>' || ch == '|');
        }

        [Fact]
        public void PlansNeverChangeAnything()
        {
            var run = ImportMemberServer();
            var before = _ad.ChangeCount;
            _engine.PlanPilot(run);
            _engine.PlanPromote(run);
            _engine.PlanRollback(run);
            _engine.GetCleanupCandidates();
            _engine.Review(run);
            Assert.Equal(before, _ad.ChangeCount);
        }

        [Fact]
        public void AuditLogIsAppendOnly()
        {
            var path = Path.Combine(_tmp.Path, "a", "audit.log");
            var log = new AuditLog(path, "LAB\\u");
            log.Write("r1", "A", "t", "Success");
            var first = File.ReadAllText(path);
            log.Write("r1", "B", "t", "Failure", "boom");
            var all = File.ReadAllText(path);
            Assert.StartsWith(first, all);
            Assert.Equal(2, log.ReadAll().Count);
            Assert.Equal("boom", Json.GetString(log.ReadAll()[1], "error"));
        }
    }
}
