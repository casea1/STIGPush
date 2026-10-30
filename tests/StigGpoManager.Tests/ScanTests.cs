using System;
using System.IO;
using System.Linq;
using StigGpoManager.Core;
using Xunit;

namespace StigGpoManager.Tests
{
    public class ScanTests
    {
        [Fact]
        public void ParsesCkl()
        {
            var doc = PackageScanner.LoadXmlString(TestData.Ckl("web01", "Microsoft Windows Server 2025 STIG", ("V-1", "Open"), ("V-2", "NotAFinding"), ("V-3", "Not_Applicable"), ("V-4", "Not_Reviewed")));
            var r = ScanParser.ParseCkl(doc, "web01.ckl");
            Assert.Equal(new[] { "web01" }, r.Hosts);
            Assert.Equal(new[] { ScanStatus.Open, ScanStatus.NotAFinding, ScanStatus.NotApplicable, ScanStatus.NotReviewed }, r.Findings.Select(f => f.Status).ToArray());
            Assert.Equal("Microsoft Windows Server 2025 STIG", r.Findings[0].StigName);
        }

        [Fact]
        public void ParsesCklb()
        {
            const string json = @"{ ""title"": ""x"", ""target_data"": { ""host_name"": ""ws01"" },
              ""stigs"": [ { ""stig_name"": ""Windows_11_STIG"", ""display_name"": ""Windows 11 STIG"",
                ""rules"": [ { ""group_id"": ""V-253254"", ""rule_title"": ""t1"", ""status"": ""open"" },
                             { ""group_id"": ""V-253255"", ""status"": ""not_a_finding"" },
                             { ""group_id"": ""V-253256"", ""status"": ""not_applicable"" } ] } ] }";
            var r = ScanParser.ParseCklb(json, "ws01.cklb");
            Assert.Equal(3, r.Findings.Count);
            Assert.Equal("ws01", r.Findings[0].Host);
            Assert.Equal("Windows 11 STIG", r.Findings[0].StigName);
            Assert.Equal(ScanStatus.Open, r.Findings[0].Status);
            Assert.Equal(ScanStatus.NotApplicable, r.Findings[2].Status);
        }

        [Fact]
        public void ParsesXccdfWithEmbeddedBenchmarkAndIdents()
        {
            const string xml = @"<?xml version=""1.0""?>
<cdf:Benchmark xmlns:cdf=""http://checklists.nist.gov/xccdf/1.2"" id=""xccdf_mil.disa.stig_benchmark_Windows_Server_2025"">
  <cdf:title>Microsoft Windows Server 2025 STIG SCAP Benchmark</cdf:title>
  <cdf:Group id=""xccdf_mil.disa.stig_group_V-254239""><cdf:Rule id=""xccdf_mil.disa.stig_rule_SV-254239r1_rule""><cdf:title>SMBv1 disabled</cdf:title></cdf:Rule></cdf:Group>
  <cdf:TestResult id=""r"">
    <cdf:target>SRV01</cdf:target>
    <cdf:rule-result idref=""xccdf_mil.disa.stig_rule_SV-254239r1_rule""><cdf:result>fail</cdf:result></cdf:rule-result>
    <cdf:rule-result idref=""xccdf_mil.disa.stig_rule_SV-254300r1_rule""><cdf:result>pass</cdf:result><cdf:ident system=""http://cyber.mil/legacy"">V-254300</cdf:ident></cdf:rule-result>
    <cdf:rule-result idref=""xccdf_mil.disa.stig_rule_SV-254301r1_rule""><cdf:result>notapplicable</cdf:result></cdf:rule-result>
  </cdf:TestResult>
</cdf:Benchmark>";
            var r = ScanParser.ParseXccdf(PackageScanner.LoadXmlString(xml), "SRV01_XCCDF-Results.xml");
            Assert.Equal("V-254239", r.Findings[0].VulnId);
            Assert.Equal(ScanStatus.Open, r.Findings[0].Status);
            Assert.Equal("SMBv1 disabled", r.Findings[0].Title);
            Assert.Equal("V-254300", r.Findings[1].VulnId);
            Assert.Equal("SV-254301", r.Findings[2].VulnId);
            Assert.All(r.Findings, f => Assert.Equal("SRV01", f.Host));
        }

        [Fact]
        public void ParseDetectsFormatByContent()
        {
            using (var tmp = new TempDir())
            {
                var p = Path.Combine(tmp.Path, "evaluate-stig-output.xml");
                File.WriteAllText(p, TestData.Ckl("h", "s", ("V-1", "Open")));
                Assert.Equal("CKL", ScanParser.Parse(p).Format);
                var bad = Path.Combine(tmp.Path, "x.txt");
                File.WriteAllText(bad, "hello");
                Assert.Throws<InvalidDataException>(() => ScanParser.Parse(bad));
            }
        }

        [Fact]
        public void GateSeparatesExpectedFromBlockingAndShowsChanges()
        {
            var pilot = ScanParser.ParseCkl(PackageScanner.LoadXmlString(TestData.Ckl("h1", "Windows Server 2025 STIG", ("V-1", "Open"), ("V-2", "Open"), ("V-3", "NotAFinding"))), "p.ckl").Findings;
            var baseline = ScanParser.ParseCkl(PackageScanner.LoadXmlString(TestData.Ckl("h1", "Windows Server 2025 STIG", ("V-1", "Open"), ("V-2", "NotAFinding"), ("V-3", "Open"))), "b.ckl").Findings;
            var reg = new ExceptionsRegister();
            reg.Entries.Add(new ExceptionEntry { VulnId = "V-1", StigName = "Server 2025", Justification = "j" });
            reg.Entries.Add(new ExceptionEntry { VulnId = "V-2", StigName = "Windows 11", Justification = "other STIG" });

            var g = ScanGate.Evaluate(pilot, baseline, reg);
            Assert.False(g.Passed);
            Assert.Equal("V-2", g.Blocking.Single().VulnId);
            Assert.Equal("V-1", g.Expected.Single().Finding.VulnId);
            Assert.Contains(g.StatusChanges, c => c.VulnId == "V-2" && c.Before == ScanStatus.NotAFinding && c.After == ScanStatus.Open);
            Assert.Contains(g.StatusChanges, c => c.VulnId == "V-3" && c.After == ScanStatus.NotAFinding);

            Assert.False(ScanGate.Evaluate(Enumerable.Empty<ScanFinding>(), null, reg).Passed);
            Assert.True(ScanGate.Evaluate(Enumerable.Empty<ScanFinding>(), null, reg, requireResults: false).Passed);
        }

        [Fact]
        public void WorstStatusWinsAcrossFiles()
        {
            var a = new ScanFinding { Host = "h", VulnId = "V-1", Status = ScanStatus.NotAFinding };
            var b = new ScanFinding { Host = "H", VulnId = "V-1", Status = ScanStatus.Open };
            Assert.Equal(ScanStatus.Open, ScanParser.Consolidate(new[] { a, b }).Single().Value.Status);
        }

        [Fact]
        public void RegisterRequiresJustificationAndKeepsHistory()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Path, "exceptions.json");
                var reg = new ExceptionsRegister();
                reg.Entries.Add(new ExceptionEntry { VulnId = "V-1" });
                Assert.Throws<InvalidDataException>(() => reg.Save(path));
                reg.Entries[0].Justification = "ok";
                reg.Save(path);
                reg.Entries.Add(new ExceptionEntry { VulnId = "V-2", Justification = "j2" });
                reg.Save(path);
                Assert.Equal(2, ExceptionsRegister.Load(path).Entries.Count);
                Assert.Single(Directory.GetFiles(Path.Combine(tmp.Path, "exceptions-history")));
            }
        }
    }
}
