using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using StigGpoManager.Core;

namespace StigGpoManager.Tests
{
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stiggpo-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public TempDir() { Directory.CreateDirectory(Path); }
        public string Sub(params string[] parts) { var p = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray()); Directory.CreateDirectory(p); return p; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    public static class TestData
    {
        public const string Domain = "lab.test";
        public const string ProdOu = "OU=Server2025,OU=Servers,DC=lab,DC=test";
        public const string PilotOu = "OU=Pilot,OU=Server2025,OU=Servers,DC=lab,DC=test";
        public const string AdminOu = "OU=Servers,OU=Admin,DC=lab,DC=test";
        public const string DcOu = "OU=Domain Controllers,DC=lab,DC=test";

        public static string ConfigJson(string workingFolder, string centralStore, bool requireGate = false) => $@"{{
  ""Domain"": ""lab.test"",
  ""TargetDC"": ""auto-pdc"",
  ""ExceptionPrefix"": ""SITE - "",
  ""DisaGpoPrefix"": ""DoD "",
  ""WorkingFolder"": {Q(workingFolder)},
  ""CentralStorePath"": {Q(centralStore)},
  ""Language"": ""en-US"",
  ""PilotGroup"": ""GPO-Pilot"",
  ""RequireScanGate"": {(requireGate ? "true" : "false")},
  ""Mappings"": [
    {{ ""Match"": ""WinSvr 2025.*Member Server.*Computer"", ""NameTemplate"": ""DoD WinSvr2025 MS STIG Comp {{Version}}"",
       ""PilotTargets"": [""{PilotOu}""], ""ProdTargets"": [""{ProdOu}"", ""{AdminOu}""] }},
    {{ ""Match"": ""WinSvr 2025.*Domain Controller.*Computer"", ""NameTemplate"": ""DoD WinSvr2025 DC STIG Comp {{Version}}"",
       ""PilotTargets"": [], ""ProdTargets"": [""{DcOu}""] }}
  ]
}}";

        private static string Q(string s) => "\"" + s.Replace("\\", "\\\\") + "\"";

        public static AppConfig Config(string workingFolder, string centralStore, bool requireGate = false)
        {
            var issues = new List<ConfigIssue>();
            var cfg = AppConfig.FromJson(ConfigJson(workingFolder, centralStore, requireGate), issues);
            if (issues.Any(i => i.IsError)) throw new InvalidOperationException(string.Join("; ", issues));
            return cfg;
        }

        // ---------------------------------------------------------------- report XML (shared with /demo)

        public static string UserRight(string right, params string[] members) => GpoReportXml.UserRight(right, members);
        public static string Account(string name, int value) => GpoReportXml.Account(name, value);
        public static string SecOption(string key, int value, string display) => GpoReportXml.SecOption(key, value, display);
        public static string Policy(string category, string name, string state) => GpoReportXml.Policy(category, name, state);
        public static string Audit(string sub, string guid, int value) => GpoReportXml.Audit(sub, guid, value);
        public static string Report(string name, params string[] items) => GpoReportXml.Report(name, items);

        // ---------------------------------------------------------------- package

        public static string BkupInfo(string backupId, string displayName) =>
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><BackupInst xmlns=\"http://www.microsoft.com/GroupPolicy/GPOOperations/Manifest\">" +
            $"<GPOGuid><![CDATA[{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}]]></GPOGuid><GPODomain><![CDATA[disa.mil]]></GPODomain>" +
            $"<GPODomainGuid><![CDATA[{{{Guid.NewGuid()}}}]]></GPODomainGuid><GPODomainController><![CDATA[dc.disa.mil]]></GPODomainController>" +
            $"<BackupTime><![CDATA[2026-09-15T10:00:00]]></BackupTime><ID><![CDATA[{backupId}]]></ID><Comment><![CDATA[]]></Comment>" +
            $"<GPODisplayName><![CDATA[{displayName}]]></GPODisplayName></BackupInst>";

        /// <summary>Backup.xml listing domain principals the way GPMC records them for migration.</summary>
        public static string BackupXml(params (string sam, string sid, string type)[] principals) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><GroupPolicyBackupScheme bkp:version=\"2.0\" bkp:type=\"GroupPolicyBackupTemplate\" xmlns:bkp=\"http://www.microsoft.com/GroupPolicy/GPOOperations\" xmlns=\"http://www.microsoft.com/GroupPolicy/GPOOperations\"><GroupPolicyObject>" +
            string.Concat(principals.Select(p => $"<SecurityGroup><Sid><![CDATA[{p.sid}]]></Sid><SamAccountName><![CDATA[{p.sam}]]></SamAccountName><Type><![CDATA[{p.type}]]></Type><NetBIOSDomainName><![CDATA[DISATEST]]></NetBIOSDomainName><DnsDomainName><![CDATA[disa.test]]></DnsDomainName><UPN><![CDATA[{p.sam}@disa.test]]></UPN></SecurityGroup>")) +
            "</GroupPolicyObject></GroupPolicyBackupScheme>";

        public static void AddBackup(string gposFolder, string displayName, string reportXml, out string backupId, string backupXml = null)
        {
            backupId = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            var dir = Path.Combine(gposFolder, backupId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bkupInfo.xml"), BkupInfo(backupId, displayName));
            File.WriteAllText(Path.Combine(dir, "gpreport.xml"), reportXml);
            File.WriteAllText(Path.Combine(dir, "Backup.xml"), backupXml ?? "<GroupPolicyBackupScheme/>");
        }

        /// <summary>DISA-like package: product folders with GPOs\{GUID} backups and ADMX templates (with a duplicate).</summary>
        public static string BuildPackage(TempDir tmp, string memberServerReport, string dcReport = null, string memberServerBackupXml = null)
        {
            var src = tmp.Sub("pkgsrc", "October 2026 DISA STIG GPO Package");
            var ms = Path.Combine(src, "DoD WinSvr 2025 MS and DC v1r4", "GPOs");
            Directory.CreateDirectory(ms);
            AddBackup(ms, "DoD WinSvr 2025 Member Server STIG Computer v1r4", memberServerReport, out _, memberServerBackupXml);
            AddBackup(ms, "DoD WinSvr 2025 Domain Controller STIG Computer v1r4", dcReport ?? Report("dc"), out _);
            var other = Path.Combine(src, "DoD Google Chrome v2r11", "GPOs");
            Directory.CreateDirectory(other);
            AddBackup(other, "DoD Google Chrome STIG Computer v2r11", Report("chrome", Policy("Google/Chrome", "Block third party cookies", "Enabled")), out _);

            var admx1 = Path.Combine(src, "DoD WinSvr 2025 MS and DC v1r4", "ADMX Templates", "Microsoft");
            Directory.CreateDirectory(Path.Combine(admx1, "en-US"));
            Directory.CreateDirectory(Path.Combine(admx1, "de-DE"));
            File.WriteAllText(Path.Combine(admx1, "SecGuide.admx"), "<policyDefinitions v='new'/>");
            File.WriteAllText(Path.Combine(admx1, "en-US", "SecGuide.adml"), "<policyDefinitionResources v='new'/>");
            File.WriteAllText(Path.Combine(admx1, "de-DE", "SecGuide.adml"), "<policyDefinitionResources lang='de'/>");
            File.SetLastWriteTimeUtc(Path.Combine(admx1, "SecGuide.admx"), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
            var admx2 = Path.Combine(src, "DoD Google Chrome v2r11", "ADMX");
            Directory.CreateDirectory(admx2);
            File.WriteAllText(Path.Combine(admx2, "SecGuide.admx"), "<policyDefinitions v='old'/>");
            File.SetLastWriteTimeUtc(Path.Combine(admx2, "SecGuide.admx"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(Path.Combine(admx2, "chrome.admx"), "<policyDefinitions chrome='1'/>");

            var zip = Path.Combine(tmp.Path, "U_STIG_GPO_Package_October_2026.zip");
            ZipFile.CreateFromDirectory(Path.Combine(tmp.Path, "pkgsrc"), zip);
            return zip;
        }

        // ---------------------------------------------------------------- scan results

        public static string Ckl(string host, string stig, params (string vid, string status)[] vulns)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CHECKLIST><ASSET><ROLE>Member Server</ROLE><HOST_NAME>").Append(host).Append("</HOST_NAME></ASSET><STIGS><iSTIG><STIG_INFO>");
            sb.Append("<SI_DATA><SID_NAME>version</SID_NAME><SID_DATA>1</SID_DATA></SI_DATA><SI_DATA><SID_NAME>title</SID_NAME><SID_DATA>").Append(stig).Append("</SID_DATA></SI_DATA></STIG_INFO>");
            foreach (var v in vulns)
                sb.Append("<VULN><STIG_DATA><VULN_ATTRIBUTE>Vuln_Num</VULN_ATTRIBUTE><ATTRIBUTE_DATA>").Append(v.vid)
                  .Append("</ATTRIBUTE_DATA></STIG_DATA><STIG_DATA><VULN_ATTRIBUTE>Rule_Title</VULN_ATTRIBUTE><ATTRIBUTE_DATA>Rule ").Append(v.vid)
                  .Append("</ATTRIBUTE_DATA></STIG_DATA><STATUS>").Append(v.status).Append("</STATUS><FINDING_DETAILS/><COMMENTS/></VULN>");
            sb.Append("</iSTIG></STIGS></CHECKLIST>");
            return sb.ToString();
        }
    }
}
