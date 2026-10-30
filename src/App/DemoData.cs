using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using StigGpoManager.Core;
using R = StigGpoManager.Core.GpoReportXml;

namespace StigGpoManager
{
    /// <summary>
    /// /demo mode: a simulated domain (in memory) with a realistic quarterly run already in pilot.
    /// Used for training, for trying the tool without a lab, and to generate the README screenshots.
    /// Nothing here touches Active Directory or SYSVOL; all files go to %TEMP%\StigGpoManager-demo.
    /// </summary>
    public static class DemoData
    {
        public const string Domain = "lab.example.mil";
        private const string Dn = "DC=lab,DC=example,DC=mil";
        private const string OrgRoot = "OU=ORG," + Dn;
        private const string SrvProd = "OU=Server2025,OU=Servers,OU=Computers," + OrgRoot;
        private const string SrvPilot = "OU=Pilot," + SrvProd;
        private const string AdminSrv = "OU=Servers,OU=Admin," + OrgRoot;
        private const string Win11Prod = "OU=Win11,OU=Workstations,OU=Computers," + OrgRoot;
        private const string Win11Pilot = "OU=Pilot," + Win11Prod;
        private const string DcOu = "OU=Domain Controllers," + Dn;

        public static Session Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "StigGpoManager-demo");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            var work = Path.Combine(root, "WorkingFolder");
            var store = Path.Combine(root, "SYSVOL", "PolicyDefinitions");
            Directory.CreateDirectory(Path.Combine(store, "en-US"));

            var configPath = Path.Combine(root, "config.json");
            File.WriteAllText(configPath, Config(work, store));
            var session = new Session(configPath);
            if (!session.ConfigValid) throw new InvalidOperationException("Demo config invalid: " + string.Join("; ", session.Issues));

            var ad = Seed();
            session.ConnectDemo(ad, null);
            var e = session.Engine;

            // Central Store: one current, one older, one newer than the package.
            WriteFile(Path.Combine(store, "MSS-legacy.admx"), "<policyDefinitions/>", new DateTime(2024, 3, 1));
            WriteFile(Path.Combine(store, "SecGuide.admx"), "<policyDefinitions rev='2025'/>", new DateTime(2025, 6, 1));
            WriteFile(Path.Combine(store, "en-US", "SecGuide.adml"), "<policyDefinitionResources rev='2025'/>", new DateTime(2025, 6, 1));
            WriteFile(Path.Combine(store, "chrome.admx"), "<policyDefinitions rev='2026-10'/>", new DateTime(2026, 10, 20));

            var zip = BuildPackage(root);
            var run = e.CreateRun(zip, null, out var scan);
            var candidates = MappingResolver.Resolve(scan.Backups, session.Config);
            e.ExecuteImport(run, candidates);
            e.Review(run);
            e.ExecutePilot(run);
            run.PilotStartedUtc = DateTime.UtcNow.AddDays(-9).AddHours(-2);

            var register = new ExceptionsRegister();
            register.Entries.Add(new ExceptionEntry
            {
                VulnId = "V-254269", StigName = "Windows Server 2025", ExceptionGpo = "SITE - Server Exceptions",
                Justification = "Backup agent service account needs network logon on member servers; compensating control: account restricted to backup VLAN.",
                PoamReference = "POAM-2026-014", DateAdded = new DateTime(2026, 2, 3), AddedBy = "LAB\\isso",
            });
            register.Entries.Add(new ExceptionEntry
            {
                VulnId = "V-253301", StigName = "Windows 11", ExceptionGpo = "SITE - Workstation Exceptions",
                Justification = "Legacy CAD plotter driver requires Point and Print without elevation prompt.",
                PoamReference = "RA-2025-07", DateAdded = new DateTime(2025, 11, 12), AddedBy = "LAB\\isso",
            });
            e.SaveRegister(register);

            var scans = Path.Combine(root, "scans");
            Directory.CreateDirectory(scans);
            var baseline = Path.Combine(scans, "srv-pilot01_baseline.ckl");
            File.WriteAllText(baseline, Ckl("srv-pilot01", "Microsoft Windows Server 2025 STIG",
                ("V-254269", "Open", "Deny access from the network: Local account"), ("V-254247", "NotAFinding", "SMBv1 client must be disabled"),
                ("V-254301", "Open", "LAN Manager authentication level must be NTLMv2 only"), ("V-254350", "NotAFinding", "Audit Credential Validation")));
            e.ImportScanResult(run, baseline, StigEngine.StageBaseline);
            var pilot1 = Path.Combine(scans, "srv-pilot01.ckl");
            File.WriteAllText(pilot1, Ckl("srv-pilot01", "Microsoft Windows Server 2025 STIG",
                ("V-254269", "Open", "Deny access from the network: Local account"), ("V-254247", "Open", "SMBv1 client must be disabled"),
                ("V-254301", "NotAFinding", "LAN Manager authentication level must be NTLMv2 only"), ("V-254350", "NotAFinding", "Audit Credential Validation")));
            e.ImportScanResult(run, pilot1, StigEngine.StagePilot);
            var pilot2 = Path.Combine(scans, "ws-pilot01.cklb");
            File.WriteAllText(pilot2, Cklb("ws-pilot01", "Windows 11 STIG",
                ("V-253301", "open", "Point and Print must prompt for elevation"), ("V-253256", "not_a_finding", "Autoplay must be turned off"),
                ("V-253290", "not_applicable", "Bluetooth must be turned off")));
            e.ImportScanResult(run, pilot2, StigEngine.StagePilot);
            e.EvaluateGate(run);
            e.Store.Save(run);
            return session;
        }

        private static string Config(string work, string store) => $@"{{
  ""_readme"": ""DEMO configuration - simulated domain, no AD access."",
  ""Domain"": ""{Domain}"",
  ""TargetDC"": ""auto-pdc"",
  ""ExceptionPrefix"": ""SITE - "",
  ""DisaGpoPrefix"": ""DoD "",
  ""WorkingFolder"": {Q(work)},
  ""CentralStorePath"": {Q(store)},
  ""Language"": ""en-US"",
  ""PilotGroup"": ""GPO-Pilot"",
  ""SoakDays"": 14,
  ""RequireScanGate"": true,
  ""Mappings"": [
    {{ ""Match"": ""WinSvr 2025.*Member Server.*Computer"", ""NameTemplate"": ""DoD WinSvr2025 MS STIG Comp {{Version}}"",
       ""PilotTargets"": [""{SrvPilot}""], ""ProdTargets"": [""{SrvProd}"", ""{AdminSrv}""] }},
    {{ ""Match"": ""WinSvr 2025.*Domain Controller.*Computer"", ""NameTemplate"": ""DoD WinSvr2025 DC STIG Comp {{Version}}"",
       ""PilotTargets"": [], ""ProdTargets"": [""{DcOu}""] }},
    {{ ""Match"": ""Windows 11.*User"", ""NameTemplate"": ""DoD Win11 STIG User {{Version}}"",
       ""PilotTargets"": [""{Win11Pilot}""], ""ProdTargets"": [""{Win11Prod}""] }}
  ]
}}";

        private static string Q(string s) => "\"" + s.Replace("\\", "\\\\") + "\"";

        private static InMemoryDirectory Seed()
        {
            var ad = new InMemoryDirectory(Domain);
            foreach (var dn in new[] { SrvProd, SrvPilot, AdminSrv, Win11Prod, Win11Pilot, DcOu }) ad.Containers.Add(dn);

            var srvEx = ad.AddGpo("SITE - Server Exceptions", R.Report("SITE - Server Exceptions",
                R.UserRight("SeDenyNetworkLogonRight", "BUILTIN\\Guests", "LAB\\svc-backup-deny"),
                R.Account("MinimumPasswordLength", 15),
                R.Policy("Network/Lanman Workstation", "Enable insecure guest logons", "Disabled")));
            var baseline = ad.AddGpo("ORG Server Baseline");
            var defender = ad.AddGpo("DoD MS Defender Antivirus STIG Comp v2r4");
            var ms3 = ad.AddGpo("DoD WinSvr2025 MS STIG Comp v1r3", MemberServer(false));
            var ms2 = ad.AddGpo("DoD WinSvr2025 MS STIG Comp v1r2", MemberServer(false));
            ad.AddGpo("DoD WinSvr2025 MS STIG Comp v1r1", MemberServer(false));
            ad.AddGpo("DoD WinSvr2025 MS STIG Comp v1r0", MemberServer(false));
            ad.Link(SrvProd, srvEx);
            ad.Link(SrvProd, baseline);
            ad.Link(SrvProd, ms3);
            ad.Link(SrvProd, defender);
            ad.Link(SrvProd, ms2, enabled: false);
            ad.Link(AdminSrv, ms3);
            ad.Link(AdminSrv, srvEx); // out of order on purpose: the tool moves the exception up at promote

            var dcEx = ad.AddGpo("SITE - DC Exceptions");
            var dc3 = ad.AddGpo("DoD WinSvr2025 DC STIG Comp v1r3", R.Report("dc", R.Account("MinimumPasswordLength", 14)));
            ad.Link(DcOu, dcEx);
            ad.Link(DcOu, dc3);

            var wsEx = ad.AddGpo("SITE - Workstation Exceptions", R.Report("ws-ex", R.Policy("Printers", "Point and Print Restrictions", "Disabled")));
            var w8 = ad.AddGpo("DoD Win11 STIG User v2r8", R.Report("w11", R.Policy("Windows Components/AutoPlay Policies", "Turn off Autoplay", "Enabled")));
            ad.Link(Win11Prod, wsEx);
            ad.Link(Win11Prod, w8);

            ad.Computers[SrvProd] = new List<string> { "srv-app01." + Domain, "srv-app02." + Domain, "srv-sql01." + Domain, "srv-pilot01." + Domain };
            ad.Computers[AdminSrv] = new List<string> { "adm-jump01." + Domain };
            return ad;
        }

        private static string MemberServer(bool v4)
        {
            var items = new List<string>
            {
                R.Account("MinimumPasswordLength", v4 ? 15 : 14),
                R.Account("LockoutBadCount", 3),
                R.UserRight("SeDenyNetworkLogonRight", v4 ? new[] { "BUILTIN\\Guests", "NT AUTHORITY\\Local account" } : new[] { "BUILTIN\\Guests" }),
                R.UserRight("SeRemoteInteractiveLogonRight", "BUILTIN\\Administrators"),
                R.SecOption(@"MACHINE\System\CurrentControlSet\Control\Lsa\LmCompatibilityLevel", 5, "Network security: LAN Manager authentication level"),
                R.Policy("Windows Components/AutoPlay Policies", "Turn off Autoplay", "Enabled"),
                R.Policy("Network/Lanman Workstation", "Enable insecure guest logons", "Disabled"),
                R.Audit("Audit Credential Validation", "{0cce923f-69ae-11d9-bed3-505054503030}", 3),
                R.Audit("Audit Logon", "{0cce9215-69ae-11d9-bed3-505054503030}", v4 ? 3 : 1),
            };
            if (v4)
            {
                items.Add(R.Policy("Windows Components/Remote Desktop Services/Device and Resource Redirection", "Do not allow drive redirection", "Enabled"));
                items.Add(R.SecOption(@"MACHINE\System\CurrentControlSet\Services\LanmanWorkstation\Parameters\RequireSecuritySignature", 1, "Microsoft network client: Digitally sign communications (always)"));
            }
            else
            {
                items.Add(R.Policy("System/Internet Communication Management", "Turn off downloading of print drivers over HTTP", "Enabled"));
            }
            return R.Report(v4 ? "DoD WinSvr 2025 MS v1r4" : "DoD WinSvr 2025 MS v1r3", items.ToArray());
        }

        private static string BuildPackage(string root)
        {
            var src = Path.Combine(root, "pkgsrc", "October 2026 DISA STIG GPO Package");
            Backup(Path.Combine(src, "DoD WinSvr 2025 MS and DC v1r4", "GPOs"), "DoD WinSvr 2025 Member Server STIG Computer v1r4", MemberServer(true));
            Backup(Path.Combine(src, "DoD WinSvr 2025 MS and DC v1r4", "GPOs"), "DoD WinSvr 2025 Domain Controller STIG Computer v1r4", R.Report("dc", R.Account("MinimumPasswordLength", 15)));
            Backup(Path.Combine(src, "DoD Windows 11 v2r9", "GPOs"), "DoD Windows 11 STIG User v2r9",
                R.Report("w11u", R.Policy("Windows Components/AutoPlay Policies", "Turn off Autoplay", "Enabled"), R.Policy("Printers", "Point and Print Restrictions", "Enabled")));
            Backup(Path.Combine(src, "DoD Google Chrome v2r11", "GPOs"), "DoD Google Chrome STIG Computer v2r11", R.Report("chrome"));
            Backup(Path.Combine(src, "DoD Microsoft Edge v2r3", "GPOs"), "DoD Microsoft Edge STIG Computer v2r3", R.Report("edge"));

            var admx = Path.Combine(src, "ADMX Templates", "Microsoft");
            WriteFile(Path.Combine(admx, "SecGuide.admx"), "<policyDefinitions rev='2026-10'/>", new DateTime(2026, 9, 15));
            WriteFile(Path.Combine(admx, "en-US", "SecGuide.adml"), "<policyDefinitionResources rev='2026-10'/>", new DateTime(2026, 9, 15));
            WriteFile(Path.Combine(admx, "MSS-legacy.admx"), "<policyDefinitions/>", new DateTime(2024, 3, 1));
            WriteFile(Path.Combine(admx, "en-US", "MSS-legacy.adml"), "<policyDefinitionResources/>", new DateTime(2024, 3, 1));
            var chrome = Path.Combine(src, "DoD Google Chrome v2r11", "ADMX");
            WriteFile(Path.Combine(chrome, "chrome.admx"), "<policyDefinitions rev='2026-09'/>", new DateTime(2026, 9, 1));
            WriteFile(Path.Combine(chrome, "en-US", "chrome.adml"), "<policyDefinitionResources rev='2026-09'/>", new DateTime(2026, 9, 1));
            WriteFile(Path.Combine(src, "DoD Microsoft Edge v2r3", "ADMX", "msedge.admx"), "<policyDefinitions/>", new DateTime(2026, 9, 1));

            var zip = Path.Combine(root, "U_STIG_GPO_Package_October_2026.zip");
            ZipFile.CreateFromDirectory(Path.Combine(root, "pkgsrc"), zip);
            return zip;
        }

        private static void Backup(string gposFolder, string displayName, string report)
        {
            var id = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            var dir = Path.Combine(gposFolder, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bkupInfo.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><BackupInst xmlns=\"http://www.microsoft.com/GroupPolicy/GPOOperations/Manifest\">" +
                $"<GPOGuid><![CDATA[{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}]]></GPOGuid><GPODomain><![CDATA[disa.mil]]></GPODomain>" +
                $"<BackupTime><![CDATA[2026-09-20T10:00:00]]></BackupTime><ID><![CDATA[{id}]]></ID><GPODisplayName><![CDATA[{displayName}]]></GPODisplayName></BackupInst>");
            File.WriteAllText(Path.Combine(dir, "gpreport.xml"), report);
            File.WriteAllText(Path.Combine(dir, "Backup.xml"), "<GroupPolicyBackupScheme/>");
        }

        private static void WriteFile(string path, string content, DateTime localTime)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            File.SetLastWriteTime(path, localTime);
        }

        private static string Ckl(string host, string stig, params (string vid, string status, string title)[] vulns)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CHECKLIST><ASSET><HOST_NAME>" + host + "</HOST_NAME></ASSET><STIGS><iSTIG><STIG_INFO>");
            sb.Append("<SI_DATA><SID_NAME>title</SID_NAME><SID_DATA>" + stig + "</SID_DATA></SI_DATA></STIG_INFO>");
            foreach (var v in vulns)
                sb.Append($"<VULN><STIG_DATA><VULN_ATTRIBUTE>Vuln_Num</VULN_ATTRIBUTE><ATTRIBUTE_DATA>{v.vid}</ATTRIBUTE_DATA></STIG_DATA>" +
                          $"<STIG_DATA><VULN_ATTRIBUTE>Rule_Title</VULN_ATTRIBUTE><ATTRIBUTE_DATA>{v.title}</ATTRIBUTE_DATA></STIG_DATA><STATUS>{v.status}</STATUS></VULN>");
            return sb.Append("</iSTIG></STIGS></CHECKLIST>").ToString();
        }

        private static string Cklb(string host, string stig, params (string vid, string status, string title)[] rules) =>
            "{\"target_data\":{\"host_name\":\"" + host + "\"},\"stigs\":[{\"display_name\":\"" + stig + "\",\"rules\":[" +
            string.Join(",", rules.Select(r => $"{{\"group_id\":\"{r.vid}\",\"rule_title\":\"{r.title}\",\"status\":\"{r.status}\"}}")) + "]}]}";
    }
}
