using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Xml;
using StigGpoManager.Core;

namespace StigGpoManager
{
    /// <summary>
    /// IGpoDirectory implemented with the in-box GroupPolicy and ActiveDirectory PowerShell modules,
    /// hosted in-process (Windows PowerShell 5.1). Every cmdlet gets -Server pinned to one DC (the PDC
    /// emulator by default) so all changes land on the same DC. Calls are serialized on one runspace.
    /// </summary>
    public sealed class PowerShellDirectory : IGpoDirectory, IDisposable
    {
        private readonly Runspace _runspace;
        private readonly object _lock = new object();
        private readonly string _domain;

        public string Server { get; }

        /// <summary>Each cmdlet invocation, for the log pane ("PS&gt; New-GPLink ...").</summary>
        public event Action<string> CommandLog;

        public PowerShellDirectory(AppConfig config)
        {
            _domain = config.Domain;
            var iss = InitialSessionState.CreateDefault2();
            iss.ImportPSModule(new[] { "GroupPolicy", "ActiveDirectory" });
            _runspace = RunspaceFactory.CreateRunspace(iss);
            _runspace.Open();

            if (string.Equals(config.TargetDC, "auto-pdc", StringComparison.OrdinalIgnoreCase))
            {
                var d = Invoke("Get-ADDomain", new Dictionary<string, object> { { "Identity", _domain }, { "Server", _domain } }).FirstOrDefault()
                        ?? throw new InvalidOperationException($"Get-ADDomain returned nothing for {_domain}.");
                Server = Str(d, "PDCEmulator");
            }
            else
            {
                Server = config.TargetDC;
            }
        }

        public void Dispose() => _runspace?.Dispose();

        // ------------------------------------------------------------------ invocation helpers

        private List<PSObject> Invoke(string command, IDictionary<string, object> parameters)
        {
            lock (_lock)
            {
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    ps.AddCommand(command);
                    foreach (var kv in parameters) ps.AddParameter(kv.Key, kv.Value);
                    ps.AddParameter("ErrorAction", "Stop");
                    CommandLog?.Invoke("PS> " + command + " " + string.Join(" ", parameters.Select(kv => "-" + kv.Key + " " + Quote(kv.Value))));
                    try
                    {
                        var result = ps.Invoke().Where(o => o != null).ToList();
                        if (ps.HadErrors && ps.Streams.Error.Count > 0)
                            throw new InvalidOperationException(string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
                        return result;
                    }
                    catch (RuntimeException ex)
                    {
                        throw new InvalidOperationException(ex.Message, ex);
                    }
                }
            }
        }

        /// <summary>Runs a script block with named parameters (for things a single cmdlet cannot do).</summary>
        private List<PSObject> Script(string script, IDictionary<string, object> parameters)
        {
            lock (_lock)
            {
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    ps.AddScript(script);
                    foreach (var kv in parameters) ps.AddParameter(kv.Key, kv.Value);
                    try
                    {
                        var result = ps.Invoke().Where(o => o != null).ToList();
                        if (ps.HadErrors && ps.Streams.Error.Count > 0)
                            throw new InvalidOperationException(string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
                        return result;
                    }
                    catch (RuntimeException ex)
                    {
                        throw new InvalidOperationException(ex.Message, ex);
                    }
                }
            }
        }

        private static string Quote(object v) =>
            v is string s ? "'" + s.Replace("'", "''") + "'" : v is SwitchParameter ? "" : Convert.ToString(v);

        private Dictionary<string, object> P(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private Dictionary<string, object> Gp(params object[] kv)
        {
            var d = P(kv);
            d["Domain"] = _domain;
            d["Server"] = Server;
            return d;
        }

        private static object Prop(PSObject o, string name) => o?.Properties[name]?.Value;

        private static string Str(PSObject o, string name)
        {
            var v = Prop(o, name);
            if (v is PSObject p) v = p.BaseObject;
            return v == null ? null : Convert.ToString(v);
        }

        private static string GuidStr(object v)
        {
            if (v is PSObject p) v = p.BaseObject;
            if (v is Guid g) return g.ToString("B").ToUpperInvariant();
            return LinkOrdering.NormalizeGuid(Convert.ToString(v));
        }

        private static Guid ToGuid(string id) => Guid.Parse(id);

        private static bool Bool(PSObject o, string name)
        {
            var v = Prop(o, name);
            if (v is PSObject p) v = p.BaseObject;
            return v is bool b ? b : string.Equals(Convert.ToString(v), "True", StringComparison.OrdinalIgnoreCase);
        }

        private static GpoInfo ToGpo(PSObject o) => new GpoInfo
        {
            Id = GuidStr(Prop(o, "Id")),
            DisplayName = Str(o, "DisplayName"),
            Description = Str(o, "Description"),
            CreationTime = Prop(o, "CreationTime") is DateTime c ? c : DateTime.MinValue,
            ModificationTime = Prop(o, "ModificationTime") is DateTime m ? m : DateTime.MinValue,
            GpoStatus = Str(o, "GpoStatus"),
        };

        // ------------------------------------------------------------------ IGpoDirectory

        public DomainInfo GetDomain()
        {
            var d = Invoke("Get-ADDomain", P("Identity", _domain, "Server", Server)).First();
            var info = new DomainInfo
            {
                DnsRoot = Str(d, "DNSRoot"),
                NetBiosName = Str(d, "NetBIOSName"),
                DistinguishedName = Str(d, "DistinguishedName"),
                PdcEmulator = Str(d, "PDCEmulator"),
                DomainSid = Convert.ToString(Prop(PSObject.AsPSObject(Prop(d, "DomainSID")), "Value")),
            };
            foreach (var dc in Invoke("Get-ADDomainController", P("Filter", "*", "Server", Server)))
                info.DomainControllers.Add(Str(dc, "HostName"));
            return info;
        }

        public bool ContainerExists(string dn)
        {
            var r = Script("param($dn, $s) [bool](Get-ADObject -Filter * -SearchBase $dn -SearchScope Base -Server $s -ErrorAction SilentlyContinue)",
                           P("dn", dn, "s", Server));
            return r.Count > 0 && r[0].BaseObject is bool b && b;
        }

        public bool GroupExists(string name)
        {
            var r = Script("param($n, $s) [bool](Get-ADGroup -LDAPFilter \"(|(sAMAccountName=$n)(name=$n))\" -Server $s)",
                           P("n", EscapeLdap(name), "s", Server));
            return r.Count > 0 && r[0].BaseObject is bool b && b;
        }

        private static string EscapeLdap(string v) =>
            (v ?? "").Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

        public List<GpoInfo> GetAllGpos() => Invoke("Get-GPO", Gp("All", new SwitchParameter(true))).Select(ToGpo).ToList();

        public GpoInfo GetGpoByName(string name) =>
            GetAllGpos().FirstOrDefault(g => string.Equals(g.DisplayName, name, StringComparison.OrdinalIgnoreCase));

        public string GetGpoReportXml(string gpoId) =>
            Convert.ToString(Invoke("Get-GPOReport", Gp("Guid", ToGuid(gpoId), "ReportType", "Xml")).First().BaseObject);

        public List<GpoLinkLocation> GetGpoLinkLocations(string gpoId)
        {
            var doc = PackageScanner.LoadXmlString(GetGpoReportXml(gpoId));
            var list = new List<GpoLinkLocation>();
            foreach (XmlNode n in doc.SelectNodes("/*[local-name()='GPO']/*[local-name()='LinksTo']"))
            {
                string T(string l) => n.SelectSingleNode($"*[local-name()='{l}']")?.InnerText;
                list.Add(new GpoLinkLocation
                {
                    SomPath = T("SOMPath"),
                    Enabled = string.Equals(T("Enabled"), "true", StringComparison.OrdinalIgnoreCase),
                    NoOverride = string.Equals(T("NoOverride"), "true", StringComparison.OrdinalIgnoreCase),
                });
            }
            return list;
        }

        public List<LinkInfo> GetLinks(string targetDn)
        {
            var inh = Invoke("Get-GPInheritance", Gp("Target", targetDn)).First();
            var links = new List<LinkInfo>();
            if (Prop(inh, "GpoLinks") is IEnumerable e)
            {
                foreach (var raw in e)
                {
                    var l = PSObject.AsPSObject(raw);
                    links.Add(new LinkInfo
                    {
                        GpoId = GuidStr(Prop(l, "GpoId")),
                        GpoName = Str(l, "DisplayName"),
                        Enabled = Bool(l, "Enabled"),
                        Enforced = Bool(l, "Enforced"),
                        Order = Convert.ToInt32(Prop(l, "Order")),
                    });
                }
            }
            return links.OrderBy(l => l.Order).ToList();
        }

        public void NewLink(string gpoId, string targetDn, int order, bool enabled) =>
            Invoke("New-GPLink", Gp("Guid", ToGuid(gpoId), "Target", targetDn, "LinkEnabled", enabled ? "Yes" : "No", "Order", order));

        public void SetLinkEnabled(string gpoId, string targetDn, bool enabled) =>
            Invoke("Set-GPLink", Gp("Guid", ToGuid(gpoId), "Target", targetDn, "LinkEnabled", enabled ? "Yes" : "No"));

        public void SetLinkOrder(string gpoId, string targetDn, int order) =>
            Invoke("Set-GPLink", Gp("Guid", ToGuid(gpoId), "Target", targetDn, "Order", order));

        public void RemoveLink(string gpoId, string targetDn) =>
            Invoke("Remove-GPLink", Gp("Guid", ToGuid(gpoId), "Target", targetDn));

        public GpoInfo ImportNewGpo(string backupRoot, string backupId, string targetName)
        {
            // Rule 1: never import over an existing GPO. -CreateIfNeeded would silently overwrite one.
            if (GetGpoByName(targetName) != null)
                throw new InvalidOperationException($"A GPO named '{targetName}' already exists; refusing to import over it.");
            var r = Invoke("Import-GPO", Gp("BackupId", ToGuid(backupId), "Path", backupRoot, "TargetName", targetName, "CreateIfNeeded", new SwitchParameter(true)));
            return ToGpo(r.First());
        }

        public void SetGpoDescription(string gpoId, string description) =>
            Script("param($id, $d, $s, $text) $g = Get-GPO -Guid $id -Domain $d -Server $s -ErrorAction Stop; $g.Description = $text",
                   P("id", ToGuid(gpoId), "d", _domain, "s", Server, "text", description));

        public void BackupAllGpos(string folder) =>
            Invoke("Backup-GPO", Gp("All", new SwitchParameter(true), "Path", folder, "Comment", "StigGpoManager pre-import backup"));

        public void BackupGpo(string gpoId, string folder) =>
            Invoke("Backup-GPO", Gp("Guid", ToGuid(gpoId), "Path", folder, "Comment", "StigGpoManager backup before delete"));

        public void RemoveGpo(string gpoId) => Invoke("Remove-GPO", Gp("Guid", ToGuid(gpoId)));

        public List<GpoPermission> GetPermissions(string gpoId) =>
            Invoke("Get-GPPermission", Gp("Guid", ToGuid(gpoId), "All", new SwitchParameter(true)))
                .Select(p => new GpoPermission
                {
                    Trustee = Str(PSObject.AsPSObject(Prop(p, "Trustee")), "Name"),
                    TrusteeType = Str(PSObject.AsPSObject(Prop(p, "Trustee")), "SidType"),
                    Permission = Str(p, "Permission"),
                    Denied = Bool(p, "Denied"),
                }).ToList();

        public void SetPermission(string gpoId, string trustee, string trusteeType, string level) =>
            Invoke("Set-GPPermission", Gp("Guid", ToGuid(gpoId), "TargetName", trustee, "TargetType", trusteeType, "PermissionLevel", level, "Replace", new SwitchParameter(true)));

        public List<string> GetComputersUnder(string targetDn) =>
            Invoke("Get-ADComputer", P("Filter", "*", "SearchBase", targetDn, "SearchScope", "Subtree", "Server", Server, "Properties", new[] { "DNSHostName" }))
                .Select(c => Str(c, "DNSHostName") ?? Str(c, "Name")).Where(n => !string.IsNullOrEmpty(n)).ToList();

        public GpUpdateResult InvokeGpUpdate(string computer)
        {
            try
            {
                Invoke("Invoke-GPUpdate", P("Computer", computer, "Force", new SwitchParameter(true), "RandomDelayInMinutes", 0));
                return new GpUpdateResult { Computer = computer, Success = true, Message = "Refresh scheduled" };
            }
            catch (Exception ex)
            {
                return new GpUpdateResult { Computer = computer, Success = false, Message = ex.Message };
            }
        }

        // ------------------------------------------------------------------ host checks

        public bool ModuleAvailable(string name)
        {
            var r = Script("param($n) [bool](Get-Module -ListAvailable -Name $n)", P("n", name));
            return r.Count > 0 && r[0].BaseObject is bool b && b;
        }
    }
}
