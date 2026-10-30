using System.Collections.Generic;
using System.Linq;
using StigGpoManager.Core;
using Xunit;

namespace StigGpoManager.Tests
{
    public class DetectorTests
    {
        private const string Domain = "corp.example.com";
        private const string Dn = "DC=corp,DC=example,DC=com";
        private const string Srv = "OU=Server2025,OU=Servers," + Dn;
        private const string SrvPilot = "OU=Pilot," + Srv;
        private const string Ws = "OU=Win11,OU=Workstations," + Dn;
        private const string DcOu = "OU=Domain Controllers," + Dn;

        private static InMemoryDirectory Seed()
        {
            var ad = new InMemoryDirectory(Domain);
            foreach (var ou in new[] { "OU=Servers," + Dn, Srv, SrvPilot, "OU=Workstations," + Dn, Ws, DcOu }) ad.Containers.Add(ou);
            ad.Groups.Clear();
            ad.Groups.Add("Workstation Pilot");
            ad.Groups.Add("Helpdesk");
            var ex = ad.AddGpo("ACME - Server Exceptions");
            var ex2 = ad.AddGpo("ACME - WS Exceptions");
            ad.AddGpo("ACME - Printers");
            ad.AddGpo("IT_Drive Maps");
            var ms3 = ad.AddGpo("DoD WinSvr2025 MS STIG Comp v1r3");
            ad.AddGpo("DoD WinSvr2025 MS STIG Comp v1r2");
            var w11 = ad.AddGpo("DoD Win11 STIG User v2r8");
            var dc = ad.AddGpo("DoD WinSvr2025 DC STIG Comp v1r3");
            ad.AddGpo("DoD Office 2024 STIG v1r1"); // not linked
            ad.Link(Srv, ex); ad.Link(Srv, ms3);
            ad.Link(SrvPilot, ms3);
            ad.Link(Ws, ex2); ad.Link(Ws, w11);
            ad.Link(DcOu, dc);
            return ad;
        }

        [Fact]
        public void DetectsDomainPrefixesGroupsAndMappings()
        {
            var r = ConfigDetector.Detect(Seed(), @"D:\StigGpoManager", "en-US");
            var c = r.Suggested;
            Assert.Equal(Domain, c.Domain);
            Assert.Equal("ACME - ", c.ExceptionPrefix);
            Assert.Equal("Workstation Pilot", c.PilotGroup);
            Assert.Equal(3, r.Mappings.Count);

            var ms = c.Mappings.Single(m => m.NameTemplate == "DoD WinSvr2025 MS STIG Comp {Version}");
            Assert.Equal(new[] { Srv }, ms.ProdTargets);
            Assert.Equal(new[] { SrvPilot }, ms.PilotTargets);
            Assert.True(ms.IsMatch("DoD WinSvr 2025 Member Server STIG Computer v1r4"));
            Assert.False(ms.IsMatch("DoD WinSvr 2025 Domain Controller STIG Computer v1r4"));

            var dc = c.Mappings.Single(m => m.NameTemplate.Contains("DC STIG"));
            Assert.True(dc.IsMatch("DoD WinSvr 2025 Domain Controller STIG Computer v1r4"));
            Assert.Empty(dc.PilotTargets);

            var w = c.Mappings.Single(m => m.NameTemplate == "DoD Win11 STIG User {Version}");
            Assert.True(w.IsMatch("DoD Windows 11 STIG User v2r9"));
            Assert.False(w.IsMatch("DoD Windows 11 STIG Computer v2r9"));

            Assert.Single(r.UnlinkedFamilies, f => f.LatestGpo.StartsWith("DoD Office"));
            Assert.DoesNotContain(c.Validate(), i => i.IsError);
        }

        [Fact]
        public void WrittenConfigRoundTrips()
        {
            var r = ConfigDetector.Detect(Seed(), @"D:\StigGpoManager", "en-US");
            var json = ConfigWriter.ToJson(r.Suggested);
            var issues = new List<ConfigIssue>();
            var back = AppConfig.FromJson(json, issues);
            Assert.DoesNotContain(issues, i => i.IsError);
            Assert.Equal(@"D:\StigGpoManager", back.WorkingFolder);
            Assert.Equal(3, back.Mappings.Count);
            Assert.Equal(r.Suggested.Mappings[0].Match, back.Mappings[0].Match);
        }

        [Fact]
        public void PrefixRankingPrefersPrefixesAboveDisa()
        {
            var links = new Dictionary<string, List<LinkInfo>>
            {
                { "OU=A", new List<LinkInfo> { new LinkInfo { GpoName = "LOC_Settings", Order = 1 }, new LinkInfo { GpoName = "DoD X v1r1", Order = 2 } } },
            };
            var ranked = ConfigDetector.RankExceptionPrefixes(new[] { "LOC_Settings", "OPS - Maps", "OPS - Drives" }, links, "DoD ");
            Assert.Equal("LOC_", ranked[0].Prefix);
            Assert.Contains(ranked, p => p.Prefix == "OPS - " && p.GpoCount == 2);
        }

        [Fact]
        public void SuggestsMappingFromUnmatchedBackup()
        {
            var m = ConfigDetector.SuggestFromBackup("DoD Microsoft Edge STIG Computer v2r3", "DoD ");
            Assert.Equal("DoD Microsoft Edge STIG Computer {Version}", m.NameTemplate);
            Assert.True(m.IsMatch("DoD Microsoft Edge STIG Computer v2r4"));
        }
    }
}
