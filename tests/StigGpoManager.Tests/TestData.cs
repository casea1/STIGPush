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

        // ---------------------------------------------------------------- report XML

        public static string UserRight(string right, params string[] members) =>
            $"<q1:UserRightsAssignment><q1:Name>{right}</q1:Name>" +
            string.Concat(members.Select(m => $"<q1:Member><Name xmlns=\"http://www.microsoft.com/GroupPolicy/Types\">{m}</Name></q1:Member>")) +
            "</q1:UserRightsAssignment>";

        public static string Account(string name, int value) =>
            $"<q1:Account><q1:Name>{name}</q1:Name><q1:SettingNumber>{value}</q1:SettingNumber><q1:Type>Password</q1:Type></q1:Account>";

        public static string SecOption(string key, int value, string display) =>
            $"<q1:SecurityOptions><q1:KeyName>{key}</q1:KeyName><q1:SettingNumber>{value}</q1:SettingNumber><q1:Display><q1:Name>{display}</q1:Name><q1:DisplayBoolean>{(value == 1 ? "true" : "false")}</q1:DisplayBoolean></q1:Display></q1:SecurityOptions>";

        public static string Policy(string category, string name, string state) =>
            $"<q2:Policy><q2:Name>{name}</q2:Name><q2:State>{state}</q2:State><q2:Explain>Long help text</q2:Explain><q2:Supported>At least Windows 10</q2:Supported><q2:Category>{category}</q2:Category></q2:Policy>";

        public static string Audit(string sub, string guid, int value) =>
            $"<q3:AuditSetting><q3:PolicyTarget>System</q3:PolicyTarget><q3:SubcategoryName>{sub}</q3:SubcategoryName><q3:SubcategoryGuid>{guid}</q3:SubcategoryGuid><q3:SettingValue>{value}</q3:SettingValue></q3:AuditSetting>";

        /// <summary>A Get-GPOReport style document. Items are routed to the right extension by prefix.</summary>
        public static string Report(string name, params string[] items)
        {
            var sec = string.Concat(items.Where(i => i.StartsWith("<q1:")));
            var reg = string.Concat(items.Where(i => i.StartsWith("<q2:")));
            var aud = string.Concat(items.Where(i => i.StartsWith("<q3:")));
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<GPO xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"http://www.microsoft.com/GroupPolicy/Settings\">");
            sb.Append($"<Identifier><Identifier xmlns=\"http://www.microsoft.com/GroupPolicy/Types\">{{00000000-0000-0000-0000-000000000000}}</Identifier></Identifier><Name>{name}</Name>");
            sb.Append("<Computer><VersionDirectory>1</VersionDirectory><VersionSysvol>1</VersionSysvol><Enabled>true</Enabled>");
            if (sec.Length > 0) sb.Append($"<ExtensionData><Extension xmlns:q1=\"http://www.microsoft.com/GroupPolicy/Settings/Security\" xsi:type=\"q1:SecuritySettings\">{sec}</Extension><Name>Security</Name></ExtensionData>");
            if (reg.Length > 0) sb.Append($"<ExtensionData><Extension xmlns:q2=\"http://www.microsoft.com/GroupPolicy/Settings/Registry\" xsi:type=\"q2:RegistrySettings\">{reg}</Extension><Name>Registry</Name></ExtensionData>");
            if (aud.Length > 0) sb.Append($"<ExtensionData><Extension xmlns:q3=\"http://www.microsoft.com/GroupPolicy/Settings/Auditing\" xsi:type=\"q3:AuditSettings\">{aud}</Extension><Name>Advanced Audit Configuration</Name></ExtensionData>");
            sb.Append("</Computer><User><VersionDirectory>0</VersionDirectory><VersionSysvol>0</VersionSysvol><Enabled>true</Enabled></User>");
            sb.Append("<LinksTo/></GPO>");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- package

        public static string BkupInfo(string backupId, string displayName) =>
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><BackupInst xmlns=\"http://www.microsoft.com/GroupPolicy/GPOOperations/Manifest\">" +
            $"<GPOGuid><![CDATA[{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}]]></GPOGuid><GPODomain><![CDATA[disa.mil]]></GPODomain>" +
            $"<GPODomainGuid><![CDATA[{{{Guid.NewGuid()}}}]]></GPODomainGuid><GPODomainController><![CDATA[dc.disa.mil]]></GPODomainController>" +
            $"<BackupTime><![CDATA[2026-09-15T10:00:00]]></BackupTime><ID><![CDATA[{backupId}]]></ID><Comment><![CDATA[]]></Comment>" +
            $"<GPODisplayName><![CDATA[{displayName}]]></GPODisplayName></BackupInst>";

        public static void AddBackup(string gposFolder, string displayName, string reportXml, out string backupId)
        {
            backupId = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            var dir = Path.Combine(gposFolder, backupId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bkupInfo.xml"), BkupInfo(backupId, displayName));
            File.WriteAllText(Path.Combine(dir, "gpreport.xml"), reportXml);
            File.WriteAllText(Path.Combine(dir, "Backup.xml"), "<GroupPolicyBackupScheme/>");
        }

        /// <summary>DISA-like package: product folders with GPOs\{GUID} backups and ADMX templates (with a duplicate).</summary>
        public static string BuildPackage(TempDir tmp, string memberServerReport, string dcReport = null)
        {
            var src = tmp.Sub("pkgsrc", "October 2026 DISA STIG GPO Package");
            var ms = Path.Combine(src, "DoD WinSvr 2025 MS and DC v1r4", "GPOs");
            Directory.CreateDirectory(ms);
            AddBackup(ms, "DoD WinSvr 2025 Member Server STIG Computer v1r4", memberServerReport, out _);
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
