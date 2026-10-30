using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StigGpoManager.Core;
using Xunit;

namespace StigGpoManager.Tests
{
    public class JsonTests
    {
        [Fact]
        public void RoundTripsObjectsArraysAndEscapes()
        {
            var o = Json.AsObject(Json.Parse("{\"a\": \"D:\\\\Stig\", \"b\": [1, 2.5, true, null], \"c\": {\"d\": \"\\u00e9\"}}"));
            Assert.Equal(@"D:\Stig", o["A"]); // case-insensitive keys
            var arr = (List<object>)o["b"];
            Assert.Equal(1L, arr[0]);
            Assert.Equal(2.5, arr[1]);
            Assert.Equal(true, arr[2]);
            Assert.Null(arr[3]);
            var again = Json.AsObject(Json.Parse(Json.Serialize(o)));
            Assert.Equal("\u00e9", Json.AsObject(again["c"])["d"]);
        }

        [Fact]
        public void SingleBackslashGivesHelpfulError()
        {
            var ex = Assert.Throws<FormatException>(() => Json.Parse("{\"WorkingFolder\": \"D:\\StigGpoManager\"}"));
            Assert.Contains("line 1", ex.Message);
            Assert.Contains("doubled", ex.Message);
        }

        [Fact]
        public void MissingCommaReportsPosition()
        {
            var ex = Assert.Throws<FormatException>(() => Json.Parse("{\n\"a\": 1\n\"b\": 2}"));
            Assert.Contains("line 3", ex.Message);
        }

        [Fact]
        public void MapperRoundTripsRunState()
        {
            var run = new RunState { RunId = "r1", CreatedUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), Phase = RunPhase.Piloted };
            run.Items.Add(new RunItem { NewGpoName = "x", PilotMode = PilotMode.Group, OlderVersionIds = { "{A}" }, ImportedUtc = DateTime.UtcNow });
            run.Package.HashMatches = true;
            var back = JsonMapper.Deserialize<RunState>(JsonMapper.Serialize(run));
            Assert.Equal("r1", back.RunId);
            Assert.Equal(RunPhase.Piloted, back.Phase);
            Assert.Equal(PilotMode.Group, back.Items[0].PilotMode);
            Assert.Equal("{A}", back.Items[0].OlderVersionIds[0]);
            Assert.True(back.Package.HashMatches);
            Assert.Null(back.Gate);
            Assert.Equal(run.CreatedUtc, back.CreatedUtc);
        }
    }

    public class VersionTests
    {
        [Theory]
        [InlineData("DoD Windows 11 STIG Computer v2r9", 2, 9)]
        [InlineData("DoD WinSvr 2025 MS and DC V1R4", 1, 4)]
        [InlineData("Something v1r2 then v3r10", 3, 10)]
        public void ParsesLastVersionToken(string text, int v, int r)
        {
            var ver = StigVersion.TryParse(text);
            Assert.Equal(v, ver.Version);
            Assert.Equal(r, ver.Release);
        }

        [Theory]
        [InlineData("DoD Office 2019")]
        [InlineData("server2025r2")]
        [InlineData("")]
        public void ReturnsNullWhenNoVersion(string text) => Assert.Null(StigVersion.TryParse(text));

        [Fact]
        public void FallsBackToProductFolder() =>
            Assert.Equal("v1r4", StigVersion.Detect("DoD Defender Antivirus STIG Computer", "DoD Microsoft Defender Antivirus v1r4").ToString());

        [Fact]
        public void ComparesNumerically()
        {
            Assert.True(StigVersion.TryParse("v1r10") > StigVersion.TryParse("v1r9"));
            Assert.True(StigVersion.TryParse("v2r1") > StigVersion.TryParse("v1r99"));
            Assert.Equal(StigVersion.TryParse("V1R4"), StigVersion.ParseUserInput("1r4"));
            Assert.Throws<FormatException>(() => StigVersion.ParseUserInput("1.4"));
        }
    }

    public class ConfigTests
    {
        [Fact]
        public void SampleConfigIsValid()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.sample.json");
            var cfg = AppConfig.Load(path, out var issues);
            Assert.DoesNotContain(issues, i => i.IsError);
            Assert.Equal(3, cfg.Mappings.Count);
            Assert.Equal(PilotMode.OU, cfg.Mappings[0].EffectivePilotMode(cfg));
            Assert.Equal(PilotMode.Group, cfg.Mappings[1].EffectivePilotMode(cfg));
            Assert.Equal(@"\\corp.example.com\SYSVOL\corp.example.com\Policies\PolicyDefinitions", cfg.EffectiveCentralStorePath);
        }

        [Fact]
        public void ReportsClearErrors()
        {
            var issues = new List<ConfigIssue>();
            AppConfig.FromJson(@"{ ""Domain"": ""lab.test"", ""ExceptionPrefix"": ""DoD X"", ""DisaGpoPrefix"": ""DoD "", ""WorkingFolder"": ""relative\\path"",
                ""Mappings"": [ { ""Match"": ""(unclosed"", ""NameTemplate"": ""DoD Thing"", ""ProdTargets"": [""OU=X,DC=other,DC=com""] } ], ""Typo"": 1 }", issues);
            var text = string.Join("\n", issues);
            Assert.Contains("overlap", text);
            Assert.Contains("absolute path", text);
            Assert.Contains("not a valid regex", text);
            Assert.Contains("{Version} exactly once", text);
            Assert.Contains("not in domain lab.test", text);
            Assert.Contains("Unknown setting 'Typo'", text);
        }

        [Fact]
        public void TemplateFamilyRegexFindsVersions()
        {
            var m = new GpoMapping { Match = "x", NameTemplate = "DoD WinSvr2025 MS STIG Comp {Version}" };
            Assert.Equal("v1r3", m.VersionOfName("DoD WinSvr2025 MS STIG Comp v1r3").ToString());
            Assert.Null(m.VersionOfName("DoD WinSvr2025 DC STIG Comp v1r3"));
            Assert.Null(m.VersionOfName("SITE - DoD WinSvr2025 MS STIG Comp v1r3"));
            Assert.Equal("DoD WinSvr2025 MS STIG Comp v2r1", m.RenderName(new StigVersion(2, 1)));
        }

        [Fact]
        public void AncestorsIncludeDomainRoot() =>
            Assert.Equal(new[] { "OU=Server2025,OU=Servers,DC=lab,DC=test", "OU=Servers,DC=lab,DC=test", "DC=lab,DC=test" },
                         AppConfig.AncestorDns(TestData.PilotOu).ToArray());
    }

    public class PackageTests
    {
        [Fact]
        public void DiscoversBackupsAndTemplatesByContent()
        {
            using (var tmp = new TempDir())
            {
                var zip = TestData.BuildPackage(tmp, TestData.Report("ms", TestData.Account("MinimumPasswordLength", 14)));
                var hash = PackageScanner.ComputeSha256(zip);
                var dest = PackageScanner.Extract(zip, tmp.Sub("packages"), hash);
                var scan = PackageScanner.Scan(dest, "en-US");

                Assert.Equal(3, scan.Backups.Count);
                var ms = scan.Backups.Single(b => b.DisplayName.Contains("Member Server"));
                Assert.Equal("DoD WinSvr 2025 MS and DC v1r4", ms.ProductFolder);
                Assert.Equal("v1r4", ms.DetectedVersion.ToString());
                Assert.True(ms.HasComputerSettings);
                Assert.False(ms.HasUserSettings);
                Assert.Equal(Path.GetFileName(ms.BackupFolder), ms.BackupId);

                // newest SecGuide.admx wins; de-DE ADML is ignored for en-US
                var sec = scan.Templates.Single(t => t.RelativeTarget == "SecGuide.admx");
                Assert.Contains("new", File.ReadAllText(sec.FullPath));
                Assert.Equal(1, sec.DuplicatesIgnored);
                Assert.Contains(scan.Templates, t => t.RelativeTarget == @"en-US\SecGuide.adml");
                Assert.DoesNotContain(scan.Templates, t => t.FullPath.Contains("de-DE"));

                // re-extracting the same zip reuses the folder
                Assert.Equal(dest, PackageScanner.Extract(zip, tmp.Sub("packages"), hash));
            }
        }

        [Fact]
        public void RejectsZipSlip()
        {
            using (var tmp = new TempDir())
            {
                var zip = Path.Combine(tmp.Path, "evil.zip");
                using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                using (var w = new StreamWriter(z.CreateEntry("../../escape.txt").Open())) w.Write("x");
                Assert.Throws<InvalidDataException>(() => PackageScanner.Extract(zip, tmp.Sub("packages"), PackageScanner.ComputeSha256(zip)));
            }
        }

        [Fact]
        public void NormalizesPastedHash() =>
            Assert.Equal("ABCDEF01", PackageScanner.NormalizeHash(" ab:cd ef-01 \n"));
    }

    public class MappingTests
    {
        private static AppConfig Cfg() => TestData.Config("/tmp/w", "/tmp/cs");

        private static GpoBackupInfo B(string name, string folder = "x") =>
            new GpoBackupInfo { DisplayName = name, BackupId = Guid.NewGuid().ToString("B"), ProductFolder = folder, DetectedVersion = StigVersion.Detect(name, folder) };

        [Fact]
        public void MatchesAndProposesNames()
        {
            var c = MappingResolver.Resolve(new[] { B("DoD WinSvr 2025 Member Server STIG Computer v1r4"), B("DoD Google Chrome STIG Computer v2r11") }, Cfg());
            Assert.Equal("DoD WinSvr2025 MS STIG Comp v1r4", c[0].ProposedName);
            Assert.False(c[0].Skip);
            Assert.False(c[1].IsMatched);
            Assert.True(c[1].Skip);
        }

        [Theory]
        [InlineData("WinSvr 2025.*Member Server.*Computer", "DoD WinSvr 2025 MS STIG Comp v1r1", true)]
        [InlineData("WinSvr 2025.*Member Server.*Computer", "DoD Windows Server 2025 Member Server STIG Computer v1r1", true)]
        [InlineData("WinSvr 2025.*Member Server.*Computer", "DoD WinSvr 2025 DC STIG Comp v1r1", false)]
        [InlineData("WinSvr 2025.*Member Server.*Computer", "DoD WinSvr 2025 MS and DC STIG User v1r1", false)]
        [InlineData("WinSvr 2025.*Domain Controller.*Computer", "DoD WinSvr 2025 DC STIG Comp v1r1", true)]
        [InlineData("Windows 11.*User", "DoD Windows 11 STIG User v2r3", true)]
        [InlineData("Windows 11.*User", "DoD Win11 STIG User v2r3", true)]
        [InlineData("Windows 11.*User", "DoD WinSvr 2025 MS and DC STIG User v1r1", false)]
        [InlineData("Windows 11.*Computer", "DoD Windows 11 STIG Computer v2r3", true)]
        public void MatchesAbbreviatedAndSpelledOutDisaNames(string regex, string backup, bool expected) =>
            Assert.Equal(expected, new GpoMapping { Match = regex, NameTemplate = "x {Version}" }.IsMatch(backup));

        [Fact]
        public void UnmatchedBackupGetsClosestMappingHint()
        {
            var cfg = Cfg();
            cfg.Mappings[0].Match = "this matches nothing";
            var c = MappingResolver.Resolve(new[] { B("DoD WinSvr 2025 MS STIG Comp v1r4") }, cfg);
            Assert.Null(c[0].Mapping);
            Assert.Equal("DoD WinSvr2025 MS STIG Comp {Version}", c[0].SuggestedMapping.NameTemplate);
            Assert.Contains("Closest", c[0].Problem);
        }

        [Fact]
        public void MissingVersionDefaultsToSkip()
        {
            var c = MappingResolver.Resolve(new[] { B("DoD WinSvr 2025 Member Server STIG Computer", "folder without version") }, Cfg());
            Assert.True(c[0].Skip);
            Assert.Contains("Version", c[0].Problem);
        }

        [Fact]
        public void DuplicateProposedNamesBlockImport()
        {
            var c = MappingResolver.Resolve(new[] { B("DoD WinSvr 2025 Member Server STIG Computer v1r4"), B("DoD WinSvr 2025 Member Server Computer v1r4 copy") }, Cfg());
            Assert.Contains(MappingResolver.ValidateSelection(c), p => p.Contains("More than one"));
        }
    }

    public class CentralStoreTests
    {
        private static readonly DateTime T = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ClassifiesNeverDowngrading()
        {
            Assert.Equal(CentralStoreStatus.Unchanged, CentralStore.Classify("AA", T, "aa", T.AddDays(5)));
            Assert.Equal(CentralStoreStatus.Updated, CentralStore.Classify("AA", T, "BB", T.AddDays(-5)));
            Assert.Equal(CentralStoreStatus.Updated, CentralStore.Classify("AA", T, "BB", T.AddSeconds(1)));
            Assert.Equal(CentralStoreStatus.SkippedStoreNewer, CentralStore.Classify("AA", T, "BB", T.AddDays(1)));
        }

        [Fact]
        public void ComparesAndCopiesWithOverride()
        {
            using (var tmp = new TempDir())
            {
                var pkg = tmp.Sub("pkg");
                var store = tmp.Sub("store");
                Directory.CreateDirectory(Path.Combine(store, "en-US"));
                void W(string p, string c, DateTime t) { File.WriteAllText(p, c); File.SetLastWriteTimeUtc(p, t); }
                W(Path.Combine(pkg, "new.admx"), "n", T);
                W(Path.Combine(pkg, "same.admx"), "s", T);
                W(Path.Combine(store, "same.admx"), "s", T.AddDays(-30));
                W(Path.Combine(pkg, "upd.admx"), "u2", T);
                W(Path.Combine(store, "upd.admx"), "u1", T.AddDays(-30));
                W(Path.Combine(pkg, "old.admx"), "o1", T);
                W(Path.Combine(store, "old.admx"), "o2", T.AddDays(30));
                var templates = PackageScanner.FindTemplates(pkg, "en-US", new List<string>());
                var items = CentralStore.Compare(templates, store).ToDictionary(i => i.Source.RelativeTarget);

                Assert.Equal(CentralStoreStatus.New, items["new.admx"].Status);
                Assert.Equal(CentralStoreStatus.Unchanged, items["same.admx"].Status);
                Assert.Equal(CentralStoreStatus.Updated, items["upd.admx"].Status);
                Assert.Equal(CentralStoreStatus.SkippedStoreNewer, items["old.admx"].Status);
                Assert.False(items["old.admx"].WillCopy);
                items["old.admx"].OverrideDowngrade = true;
                Assert.True(items["old.admx"].WillCopy);

                var zip = CentralStore.BackupStore(store, tmp.Sub("backups"), DateTime.Now);
                Assert.True(File.Exists(zip));
                foreach (var i in items.Values.Where(i => i.WillCopy)) CentralStore.CopyItem(i);
                Assert.Equal("u2", File.ReadAllText(Path.Combine(store, "upd.admx")));
                Assert.Equal("o1", File.ReadAllText(Path.Combine(store, "old.admx")));
                Assert.Equal(T, File.GetLastWriteTimeUtc(Path.Combine(store, "new.admx")));
            }
        }
    }

    public class SettingsAndDiffTests
    {
        private const string Ex = "SITE - Server Exceptions";

        [Fact]
        public void ParsesKnownShapes()
        {
            var s = GpoSettingsParser.Parse(TestData.Report("x",
                TestData.Account("MinimumPasswordLength", 14),
                TestData.UserRight("SeDenyNetworkLogonRight", "BUILTIN\\Guests", "LAB\\Domain Admins"),
                TestData.SecOption(@"MACHINE\System\CurrentControlSet\Control\Lsa\LimitBlankPasswordUse", 1, "Accounts: Limit local account use of blank passwords"),
                TestData.Policy("Windows Components/AutoPlay Policies", "Turn off Autoplay", "Enabled"),
                TestData.Audit("Audit Credential Validation", "{0cce923f-69ae-11d9-bed3-505054503030}", 3)));
            Assert.Equal(5, s.Count);
            var right = s.Single(x => x.Category == "UserRightsAssignment");
            Assert.True(right.IsReplacementType);
            Assert.Equal("BUILTIN\\Guests; LAB\\Domain Admins", right.Value);
            Assert.Equal("Accounts: Limit local account use of blank passwords", s.Single(x => x.Category == "SecurityOptions").DisplayName);
            var pol = s.Single(x => x.Category == "AdministrativeTemplate");
            Assert.Equal("Windows Components/AutoPlay Policies/Turn off Autoplay", pol.DisplayName);
            Assert.DoesNotContain("help", pol.Value); // Explain text is noise
            Assert.Equal("Success and Failure", s.Single(x => x.Category == "AuditSetting").Value);
        }

        [Fact]
        public void UnknownExtensionsAreFlattenedNotLost()
        {
            var xml = TestData.Report("x").Replace("</Computer>",
                "<ExtensionData><Extension xmlns:q9=\"urn:fw\" xsi:type=\"q9:WindowsFirewallSettings\"><q9:DomainProfile><q9:EnableFirewall><q9:Value>true</q9:Value></q9:EnableFirewall></q9:DomainProfile></Extension><Name>Firewall</Name></ExtensionData></Computer>");
            var s = GpoSettingsParser.Parse(xml);
            Assert.Contains(s, x => x.Key.Contains("DomainProfile") && x.Value == "true");
        }

        [Fact]
        public void DiffFindsAddedRemovedChanged()
        {
            var oldS = GpoSettingsParser.Parse(TestData.Report("old", TestData.Account("MinimumPasswordLength", 14), TestData.Account("LockoutBadCount", 3), TestData.Policy("C", "Removed one", "Enabled")));
            var newS = GpoSettingsParser.Parse(TestData.Report("new", TestData.Account("MinimumPasswordLength", 15), TestData.Account("LockoutBadCount", 3), TestData.Policy("C", "Added one", "Disabled")));
            var d = SettingDiff.Compare(oldS, newS);
            Assert.Equal(3, d.Count);
            var changed = d.Single(c => c.Kind == ChangeKind.Changed);
            Assert.Contains("14", changed.OldValue);
            Assert.Contains("15", changed.NewValue);
            Assert.Single(d, c => c.Kind == ChangeKind.Added && c.DisplayName == "C/Added one");
            Assert.Single(d, c => c.Kind == ChangeKind.Removed && c.DisplayName == "C/Removed one");
        }

        [Fact]
        public void CrossCheckFlagsExceptions()
        {
            var oldS = GpoSettingsParser.Parse(TestData.Report("old",
                TestData.Account("MinimumPasswordLength", 14),
                TestData.Policy("C", "Turn off Autoplay", "Enabled"),
                TestData.UserRight("SeDenyNetworkLogonRight", "BUILTIN\\Guests")));
            var newS = GpoSettingsParser.Parse(TestData.Report("new",
                TestData.Account("MinimumPasswordLength", 15),
                TestData.Policy("C", "Turn off Autoplay", "Disabled"),
                TestData.UserRight("SeDenyNetworkLogonRight", "BUILTIN\\Guests", "NT AUTHORITY\\Local account")));
            var exS = GpoSettingsParser.Parse(TestData.Report(Ex,
                TestData.Account("MinimumPasswordLength", 12),
                TestData.Policy("C", "Turn off Autoplay", "Disabled"),
                TestData.UserRight("SeDenyNetworkLogonRight", "BUILTIN\\Guests", "LAB\\svc-backup")));
            var diff = SettingDiff.Compare(oldS, newS);
            var f = SettingDiff.CrossCheck(diff, newS, new Dictionary<string, List<GpoSetting>> { { Ex, exS } });

            Assert.Contains(f, x => x.Kind == ExceptionFindingKind.ExceptionMayNeedReview && x.DisplayName == "Password/MinimumPasswordLength");
            Assert.Contains(f, x => x.Kind == ExceptionFindingKind.ExceptionMayBeUnnecessary && x.DisplayName == "C/Turn off Autoplay");
            var repl = f.Single(x => x.Kind == ExceptionFindingKind.ReplacementTypeConflict);
            Assert.Equal(new[] { "NT AUTHORITY\\Local account" }, repl.MissingEntries);
            Assert.Contains("missing STIG entries", repl.Message);

            var rows = SettingDiff.BuildRows("m", "new", "old", diff, f);
            Assert.Contains(rows, r => r.Severity == 2 && r.Setting == "SeDenyNetworkLogonRight");
        }
    }
}
