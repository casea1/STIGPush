using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StigGpoManager.Core
{
    /// <summary>
    /// In-memory Active Directory / Group Policy with GPMC link-order semantics. Used by the unit tests
    /// and by the exe's /demo mode; it never touches a real domain.
    /// </summary>
    public class InMemoryDirectory : IGpoDirectory
    {
        public string DomainName { get; }
        public string Server => "dc1." + DomainName;
        public List<string> DomainControllers { get; }

        public InMemoryDirectory(string domain = "lab.test")
        {
            DomainName = domain;
            DomainControllers = new List<string> { "dc1." + domain, "dc2." + domain };
        }
        public HashSet<string> Containers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Groups { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GPO-Pilot" };
        public Dictionary<string, GpoInfo> Gpos { get; } = new Dictionary<string, GpoInfo>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Reports { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<LinkInfo>> Links { get; } = new Dictionary<string, List<LinkInfo>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> Permissions { get; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Computers { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = new List<string>();
        public List<string> BackupFolders { get; } = new List<string>();
        public int ChangeCount => Calls.Count(c => !c.StartsWith("Get"));

        public static string EmptyReport(string name) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><GPO xmlns=\"http://www.microsoft.com/GroupPolicy/Settings\"><Name>" +
            System.Net.WebUtility.HtmlEncode(name) + "</Name><Computer><Enabled>true</Enabled></Computer><User><Enabled>true</Enabled></User></GPO>";

        public string AddGpo(string name, string reportXml = null)
        {
            var id = Guid.NewGuid().ToString("B").ToUpperInvariant();
            Gpos[id] = new GpoInfo { Id = id, DisplayName = name, CreationTime = DateTime.Now, ModificationTime = DateTime.Now };
            Reports[id] = reportXml ?? EmptyReport(name);
            Permissions[id] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "Authenticated Users", "GpoApply" } };
            return id;
        }

        /// <summary>Appends a link at the bottom (lowest precedence).</summary>
        public void Link(string dn, string gpoId, bool enabled = true)
        {
            var list = L(dn);
            list.Add(new LinkInfo { GpoId = gpoId, GpoName = Gpos[gpoId].DisplayName, Enabled = enabled, Order = list.Count + 1 });
        }

        private List<LinkInfo> L(string dn)
        {
            if (!Links.TryGetValue(AppConfig.NormalizeDn(dn), out var l)) Links[AppConfig.NormalizeDn(dn)] = l = new List<LinkInfo>();
            return l;
        }

        private static void Renumber(List<LinkInfo> l) { for (int i = 0; i < l.Count; i++) l[i].Order = i + 1; }

        public List<string> OrderNames(string dn) => L(dn).Select(x => x.GpoName + (x.Enabled ? "" : " (off)")).ToList();

        public string IdOf(string name) => Gpos.Values.First(g => g.DisplayName == name).Id;

        // ---------------------------------------------------------------- IGpoDirectory

        public DomainInfo GetDomain() => new DomainInfo
        {
            DnsRoot = DomainName, NetBiosName = DomainName.Split('.')[0].ToUpperInvariant(), DistinguishedName = AppConfig.DomainToDn(DomainName), PdcEmulator = Server,
            DomainControllers = DomainControllers.ToList(), DomainSid = "S-1-5-21-1-2-3",
        };

        public bool ContainerExists(string dn) => Containers.Contains(AppConfig.NormalizeDn(dn));
        public bool GroupExists(string name) => Groups.Contains(name);
        public List<GpoInfo> GetAllGpos() => Gpos.Values.ToList();
        public GpoInfo GetGpoByName(string name) => Gpos.Values.FirstOrDefault(g => string.Equals(g.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        public string GetGpoReportXml(string gpoId) => Reports[LinkOrdering.NormalizeGuid(gpoId)];

        public List<GpoLinkLocation> GetGpoLinkLocations(string gpoId) =>
            Links.SelectMany(kv => kv.Value.Where(l => LinkOrdering.SameGpo(l.GpoId, gpoId)).Select(l => new GpoLinkLocation { SomPath = kv.Key, Enabled = l.Enabled })).ToList();

        public List<LinkInfo> GetLinks(string targetDn) => L(targetDn).Select(l => l.Clone()).ToList();

        public void NewLink(string gpoId, string targetDn, int order, bool enabled)
        {
            Calls.Add($"NewLink {Gpos[gpoId].DisplayName} {targetDn} {order}");
            var l = L(targetDn);
            if (l.Any(x => LinkOrdering.SameGpo(x.GpoId, gpoId))) throw new InvalidOperationException("already linked");
            l.Insert(Math.Min(Math.Max(order, 1) - 1, l.Count), new LinkInfo { GpoId = gpoId, GpoName = Gpos[gpoId].DisplayName, Enabled = enabled });
            Renumber(l);
        }

        public void SetLinkEnabled(string gpoId, string targetDn, bool enabled)
        {
            Calls.Add($"SetLinkEnabled {Gpos[gpoId].DisplayName} {targetDn} {enabled}");
            L(targetDn).First(x => LinkOrdering.SameGpo(x.GpoId, gpoId)).Enabled = enabled;
        }

        public void SetLinkOrder(string gpoId, string targetDn, int order)
        {
            Calls.Add($"SetLinkOrder {Gpos[gpoId].DisplayName} {targetDn} {order}");
            var l = L(targetDn);
            var link = l.First(x => LinkOrdering.SameGpo(x.GpoId, gpoId));
            l.Remove(link);
            l.Insert(Math.Min(order - 1, l.Count), link);
            Renumber(l);
        }

        public void RemoveLink(string gpoId, string targetDn)
        {
            Calls.Add($"RemoveLink {Gpos[gpoId].DisplayName} {targetDn}");
            var l = L(targetDn);
            l.RemoveAll(x => LinkOrdering.SameGpo(x.GpoId, gpoId));
            Renumber(l);
        }

        public GpoInfo ImportNewGpo(string backupRoot, string backupId, string targetName, string migrationTable = null)
        {
            Calls.Add($"ImportNewGpo {targetName}" + (migrationTable == null ? "" : " migtable=" + migrationTable));
            if (GetGpoByName(targetName) != null) throw new InvalidOperationException("exists");
            var report = Path.Combine(backupRoot, backupId, "gpreport.xml");
            var id = AddGpo(targetName, File.Exists(report) ? File.ReadAllText(report) : null);
            return Gpos[id];
        }

        public void SetGpoDescription(string gpoId, string description) { Calls.Add("SetGpoDescription"); Gpos[gpoId].Description = description; }
        public void BackupAllGpos(string folder) { Calls.Add("BackupAllGpos"); BackupFolders.Add(folder); }
        public void BackupGpo(string gpoId, string folder) { Calls.Add($"BackupGpo {Gpos[gpoId].DisplayName}"); BackupFolders.Add(folder); }

        public void RemoveGpo(string gpoId)
        {
            Calls.Add($"RemoveGpo {Gpos[gpoId].DisplayName}");
            Gpos.Remove(gpoId);
            foreach (var l in Links.Values) { l.RemoveAll(x => LinkOrdering.SameGpo(x.GpoId, gpoId)); Renumber(l); }
        }

        public List<GpoPermission> GetPermissions(string gpoId) =>
            Permissions[gpoId].Select(kv => new GpoPermission { Trustee = kv.Key, Permission = kv.Value }).ToList();

        public void SetPermission(string gpoId, string trustee, string trusteeType, string level)
        {
            Calls.Add($"SetPermission {Gpos[gpoId].DisplayName} {trustee}={level}");
            if (level == "None") Permissions[gpoId].Remove(trustee);
            else Permissions[gpoId][trustee] = level;
        }

        public List<string> GetComputersUnder(string targetDn) =>
            Computers.Where(kv => kv.Key.EndsWith(AppConfig.NormalizeDn(targetDn), StringComparison.OrdinalIgnoreCase)).SelectMany(kv => kv.Value).ToList();

        public List<string> GetOrganizationalUnits() => Containers.OrderBy(c => c.Length).ThenBy(c => c).ToList();

        public List<string> FindGroups(string nameContains) =>
            Groups.Where(g => g.IndexOf(nameContains ?? "", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(g => g).ToList();

        public GpUpdateResult InvokeGpUpdate(string computer)
        {
            Calls.Add($"GpUpdate {computer}");
            return new GpUpdateResult { Computer = computer, Success = !computer.StartsWith("offline"), Message = computer.StartsWith("offline") ? "RPC server unavailable" : "ok" };
        }
    }
}
