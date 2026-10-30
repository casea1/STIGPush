using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml;

namespace StigGpoManager.Core
{
    /// <summary>A domain account or group from DISA's own build domain that a backup's settings refer to.</summary>
    public sealed class ForeignPrincipal
    {
        public string Sid { get; set; }
        public string SamAccountName { get; set; }
        public string Type { get; set; }
        public string NetBiosDomain { get; set; }
        public string DnsDomain { get; set; }
        public string Upn { get; set; }

        public int Rid => int.TryParse((Sid ?? "").Split('-').LastOrDefault(), out var r) ? r : -1;
        public string DomainSid => Sid != null && Sid.LastIndexOf('-') > 0 ? Sid.Substring(0, Sid.LastIndexOf('-')) : null;
        /// <summary>How GPMC names the source in a migration table (UPN form, as "Populate from backup" writes it).</summary>
        public string Source => !string.IsNullOrEmpty(Upn) ? Upn
                              : !string.IsNullOrEmpty(DnsDomain) ? SamAccountName + "@" + DnsDomain
                              : !string.IsNullOrEmpty(NetBiosDomain) ? NetBiosDomain + "\\" + SamAccountName : Sid;
        public string Display => (string.IsNullOrEmpty(NetBiosDomain) ? DnsDomain : NetBiosDomain) + "\\" + SamAccountName + " (" + Sid + ")";
        public string Key => (Sid ?? Source ?? "").ToUpperInvariant();
    }

    public sealed class MigrationMapping
    {
        public ForeignPrincipal Source { get; set; }
        public string Destination { get; set; }
        public string MigrationType { get; set; }
    }

    public sealed class MigrationPlan
    {
        public List<MigrationMapping> Mapped { get; } = new List<MigrationMapping>();
        /// <summary>Principals with no equivalent here: imported unchanged, so their SID will not resolve in this domain.</summary>
        public List<ForeignPrincipal> Unmapped { get; } = new List<ForeignPrincipal>();
        public bool Needed => Mapped.Count + Unmapped.Count > 0;
    }

    /// <summary>
    /// DISA's GPO backups are made in DISA's own domain. A setting that names a domain group (for example
    /// "Deny log on through Remote Desktop: Domain Admins") carries that domain's SID; imported as-is it
    /// points at a group that does not exist here, so the STIG setting silently stops covering anyone.
    /// Import-GPO -MigrationTable translates them. This reads the principals from Backup.xml and builds a
    /// table that maps the domain's well-known groups (by RID) to the same groups in this domain.
    /// </summary>
    public static class MigrationTables
    {
        private const string Ns = "http://www.microsoft.com/GroupPolicy/GPOOperations/MigrationTable";

        /// <summary>Domain-relative well-known RIDs: these groups exist in every domain.</summary>
        public static readonly Dictionary<int, string> WellKnownRids = new Dictionary<int, string>
        {
            { 498, "Enterprise Read-only Domain Controllers" }, { 500, "Administrator" }, { 501, "Guest" },
            { 512, "Domain Admins" }, { 513, "Domain Users" }, { 514, "Domain Guests" }, { 515, "Domain Computers" },
            { 516, "Domain Controllers" }, { 517, "Cert Publishers" }, { 518, "Schema Admins" }, { 519, "Enterprise Admins" },
            { 520, "Group Policy Creator Owners" }, { 521, "Read-only Domain Controllers" }, { 522, "Cloneable Domain Controllers" },
            { 525, "Protected Users" }, { 526, "Key Admins" }, { 527, "Enterprise Key Admins" }, { 553, "RAS and IAS Servers" },
        };

        /// <summary>Domain principals (S-1-5-21-...) recorded in a backup's Backup.xml.</summary>
        public static List<ForeignPrincipal> ReadBackupXml(string backupFolder)
        {
            var list = new List<ForeignPrincipal>();
            var path = Path.Combine(backupFolder ?? "", "Backup.xml");
            if (!File.Exists(path)) return list;
            XmlDocument doc;
            try { doc = PackageScanner.LoadXml(path); } catch { return list; }
            foreach (XmlElement g in doc.SelectNodes("//*[local-name()='SecurityGroup']"))
            {
                string T(string n) => g.SelectSingleNode($"*[local-name()='{n}']")?.InnerText?.Trim();
                var sid = T("Sid");
                if (string.IsNullOrEmpty(sid) || !sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new ForeignPrincipal
                {
                    Sid = sid.ToUpperInvariant(), SamAccountName = T("SamAccountName"), Type = T("Type"),
                    NetBiosDomain = T("NetBIOSDomainName"), DnsDomain = T("DnsDomainName"), Upn = T("UPN"),
                });
            }
            return list.GroupBy(p => p.Key).Select(x => x.First()).ToList();
        }

        /// <summary>Principals from other domains across the backups (this domain's own SIDs are left alone).</summary>
        public static List<ForeignPrincipal> Foreign(IEnumerable<GpoBackupInfo> backups, DomainInfo domain) =>
            backups.SelectMany(b => b.ForeignPrincipals ?? new List<ForeignPrincipal>())
                   .Where(p => !string.Equals(p.DomainSid, domain?.DomainSid, StringComparison.OrdinalIgnoreCase))
                   .Where(p => !string.Equals(p.DnsDomain, domain?.DnsRoot, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(p.DnsDomain))
                   .GroupBy(p => p.Key).Select(g => g.First())
                   .OrderBy(p => p.Rid).ThenBy(p => p.SamAccountName, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Maps each well-known group to the group of the same name here, when it exists.</summary>
        public static MigrationPlan Plan(IEnumerable<ForeignPrincipal> principals, DomainInfo domain, Func<string, bool> groupExists)
        {
            var plan = new MigrationPlan();
            foreach (var p in principals)
            {
                bool known = WellKnownRids.ContainsKey(p.Rid) && !string.IsNullOrEmpty(p.SamAccountName);
                bool exists = false;
                if (known) { try { exists = p.Rid == 500 || p.Rid == 501 || groupExists(p.SamAccountName); } catch { exists = false; } }
                if (known && exists)
                    plan.Mapped.Add(new MigrationMapping { Source = p, Destination = p.SamAccountName + "@" + domain.DnsRoot, MigrationType = TypeFor(p) });
                else
                    plan.Unmapped.Add(p);
            }
            return plan;
        }

        public static string TypeFor(ForeignPrincipal p)
        {
            switch ((p.Type ?? "").Replace(" ", "").ToLowerInvariant())
            {
                case "user": return "User";
                case "computer": return "Computer";
                case "globalgroup": return "GlobalGroup";
                case "universalgroup": case "domainuniversalgroup": return "DomainUniversalGroup";
                case "localgroup": case "domainlocalgroup": return "DomainLocalGroup";
                default:
                    if (p.Rid == 500 || p.Rid == 501) return "User";
                    if (p.Rid == 518 || p.Rid == 519 || p.Rid == 498 || p.Rid == 527) return "DomainUniversalGroup";
                    if (p.Rid == 553) return "DomainLocalGroup";
                    return "GlobalGroup";
            }
        }

        /// <summary>A GPMC migration table (.migtable): open it in the Migration Table Editor to review or validate it.</summary>
        public static string ToXml(MigrationPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-16\"?>");
            sb.AppendLine($"<MigrationTable xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"{Ns}\">");
            foreach (var m in plan.Mapped)
            {
                sb.AppendLine("  <Mapping>");
                sb.AppendLine($"    <Type>{m.MigrationType}</Type>");
                sb.AppendLine($"    <Source>{SecurityElement.Escape(m.Source.Source)}</Source>");
                sb.AppendLine($"    <Destination>{SecurityElement.Escape(m.Destination)}</Destination>");
                sb.AppendLine("  </Mapping>");
            }
            sb.AppendLine("</MigrationTable>");
            return sb.ToString();
        }

        public static void Write(string path, MigrationPlan plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, ToXml(plan), Encoding.Unicode);
        }
    }
}
