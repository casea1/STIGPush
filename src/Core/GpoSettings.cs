using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace StigGpoManager.Core
{
    /// <summary>One setting flattened out of a Get-GPOReport XML document.</summary>
    public sealed class GpoSetting
    {
        /// <summary>Computer or User.</summary>
        public string Scope { get; set; }
        /// <summary>Client-side extension, e.g. SecuritySettings, RegistrySettings, AuditSettings.</summary>
        public string Extension { get; set; }
        /// <summary>Element kind, e.g. UserRightsAssignment, Policy, SecurityOptions.</summary>
        public string Category { get; set; }
        /// <summary>Unique identity used to compare settings between GPOs.</summary>
        public string Key { get; set; }
        public string DisplayName { get; set; }
        /// <summary>Canonical value text (members sorted, noise removed).</summary>
        public string Value { get; set; }
        /// <summary>For list-type settings (user rights, restricted groups): member identities (SID if known, else name).</summary>
        public List<string> MemberIds { get; set; }
        /// <summary>Display names matching MemberIds.</summary>
        public List<string> MemberNames { get; set; }
        /// <summary>User Rights Assignment and Restricted Groups are replaced, not merged, by the winning GPO.</summary>
        public bool IsReplacementType { get; set; }

        public override string ToString() => $"{DisplayName} = {Value}";
    }

    /// <summary>
    /// Flattens Get-GPOReport -ReportType Xml (and backup gpreport.xml) into comparable settings.
    /// Known shapes (security settings, administrative templates, advanced audit, registry) get a
    /// meaningful identity; unknown extension content is flattened leaf by leaf so nothing is missed.
    /// </summary>
    public static class GpoSettingsParser
    {
        private static readonly string[] IdentityChildren =
        {
            "KeyName", "SystemAccessPolicyName", "KeyPath", "SubcategoryGuid", "Path", "Id", "Name"
        };

        private static readonly HashSet<string> NoiseElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Explain", "Supported", "GPO", "Precedence", "Display", "Category", "SubcategoryName"
        };

        private static readonly HashSet<string> ReplacementCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UserRightsAssignment", "RestrictedGroups"
        };

        public static List<GpoSetting> Parse(XmlDocument report)
        {
            var settings = new List<GpoSetting>();
            var root = report.DocumentElement;
            if (root == null) return settings;
            foreach (var scope in new[] { "Computer", "User" })
            {
                var scopeNode = Child(root, scope);
                if (scopeNode == null) continue;
                foreach (var extData in Children(scopeNode, "ExtensionData"))
                {
                    foreach (var ext in Children(extData, "Extension"))
                    {
                        var extType = ext.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
                        if (string.IsNullOrEmpty(extType)) extType = Child(extData, "Name")?.InnerText ?? "Extension";
                        var colon = extType.IndexOf(':');
                        if (colon >= 0) extType = extType.Substring(colon + 1);
                        foreach (var el in ext.ChildNodes.OfType<XmlElement>())
                            ParseElement(scope, extType, el, settings);
                    }
                }
            }
            // Keep keys unique (duplicates can occur for repeated generic leaves); suffix later duplicates.
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in settings)
            {
                if (seen.TryGetValue(s.Key, out var n)) { seen[s.Key] = n + 1; s.Key = s.Key + "#" + (n + 1); }
                else seen[s.Key] = 1;
            }
            return settings;
        }

        public static List<GpoSetting> Parse(string xml) => Parse(PackageScanner.LoadXmlString(xml));

        private static void ParseElement(string scope, string ext, XmlElement el, List<GpoSetting> output)
        {
            var category = el.LocalName;
            var prefix = scope + "|" + ext + "|" + category + "|";

            if (category.Equals("UserRightsAssignment", StringComparison.OrdinalIgnoreCase))
            {
                var name = Text(Child(el, "Name"));
                var members = Children(el, "Member").Select(ReadPrincipal).ToList();
                output.Add(ListSetting(scope, ext, category, prefix + name, name, members));
                return;
            }
            if (category.Equals("RestrictedGroups", StringComparison.OrdinalIgnoreCase))
            {
                var group = ReadPrincipal(Child(el, "GroupName"));
                var members = Children(el, "Member").Select(ReadPrincipal).ToList();
                var s = ListSetting(scope, ext, category, prefix + group.Id, "Restricted group " + group.Name, members);
                var memberOf = Children(el, "Memberof").Concat(Children(el, "MemberOf")).Select(ReadPrincipal).Select(p => p.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                if (memberOf.Count > 0) s.Value += " | MemberOf: " + string.Join("; ", memberOf);
                output.Add(s);
                return;
            }
            if (category.Equals("Policy", StringComparison.OrdinalIgnoreCase))
            {
                var name = Text(Child(el, "Name"));
                var cat = Text(Child(el, "Category"));
                var display = string.IsNullOrEmpty(cat) ? name : cat + "/" + name;
                output.Add(new GpoSetting
                {
                    Scope = scope, Extension = ext, Category = "AdministrativeTemplate",
                    Key = prefix + display, DisplayName = display,
                    Value = CanonicalValue(el, "Name"),
                });
                return;
            }
            if (category.Equals("RegistrySetting", StringComparison.OrdinalIgnoreCase))
            {
                var keyPath = Text(Child(el, "KeyPath"));
                var valueName = Text(Child(Child(el, "Value"), "Name"));
                var display = string.IsNullOrEmpty(valueName) ? keyPath : keyPath + "\\" + valueName;
                output.Add(new GpoSetting
                {
                    Scope = scope, Extension = ext, Category = category,
                    Key = prefix + display, DisplayName = display,
                    Value = CanonicalValue(el, "KeyPath"),
                });
                return;
            }
            if (category.Equals("AuditSetting", StringComparison.OrdinalIgnoreCase))
            {
                var guid = Text(Child(el, "SubcategoryGuid"));
                var name = Text(Child(el, "SubcategoryName"));
                output.Add(new GpoSetting
                {
                    Scope = scope, Extension = ext, Category = category,
                    Key = prefix + (string.IsNullOrEmpty(guid) ? name : guid.ToUpperInvariant()),
                    DisplayName = "Advanced audit: " + name,
                    Value = Text(Child(el, "SettingValue")) is string v && v.Length > 0 ? AuditValueText(v) : CanonicalValue(el, "SubcategoryGuid", "PolicyTarget"),
                });
                return;
            }
            if (category.Equals("EventLog", StringComparison.OrdinalIgnoreCase) || category.Equals("Account", StringComparison.OrdinalIgnoreCase))
            {
                var qualifier = Text(Child(el, category.Equals("EventLog", StringComparison.OrdinalIgnoreCase) ? "Log" : "Type"));
                var name = Text(Child(el, "Name"));
                var display = string.IsNullOrEmpty(qualifier) ? name : qualifier + "/" + name;
                output.Add(new GpoSetting
                {
                    Scope = scope, Extension = ext, Category = category,
                    Key = prefix + display, DisplayName = display,
                    Value = CanonicalValue(el, "Name", "Log", "Type"),
                });
                return;
            }

            // Generic: identified element (has an identity child) -> one setting; otherwise flatten leaves.
            foreach (var idName in IdentityChildren)
            {
                var idNode = Child(el, idName);
                if (idNode != null && !idNode.ChildNodes.OfType<XmlElement>().Any())
                {
                    var id = Text(idNode);
                    var display = id;
                    var displayName = Text(Child(Child(el, "Display"), "Name"));
                    if (!string.IsNullOrEmpty(displayName)) display = displayName;
                    output.Add(new GpoSetting
                    {
                        Scope = scope, Extension = ext, Category = category,
                        Key = prefix + id, DisplayName = display,
                        Value = CanonicalValue(el, idName),
                    });
                    return;
                }
            }

            if (!el.ChildNodes.OfType<XmlElement>().Any())
            {
                output.Add(new GpoSetting
                {
                    Scope = scope, Extension = ext, Category = category,
                    Key = prefix.TrimEnd('|'), DisplayName = ext + ": " + category, Value = Text(el),
                });
                return;
            }
            foreach (var leaf in Leaves(el, ""))
            {
                output.Add(new GpoSetting
                {
                    Scope = scope, Extension = ext, Category = category,
                    Key = prefix + leaf.Key, DisplayName = ext + ": " + category + "/" + leaf.Key, Value = leaf.Value,
                });
            }
        }

        private static string AuditValueText(string v)
        {
            switch (v.Trim())
            {
                case "0": return "No Auditing";
                case "1": return "Success";
                case "2": return "Failure";
                case "3": return "Success and Failure";
                default: return v;
            }
        }

        private struct Principal { public string Id; public string Name; }

        /// <summary>
        /// Well-known principals by name (as GPMC prints them, with or without BUILTIN\ / NT AUTHORITY\), so a
        /// member written as a name in one package and as a SID in the next compares as the same member.
        /// </summary>
        private static readonly Dictionary<string, string> WellKnown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Everyone", "S-1-1-0" }, { "Local", "S-1-2-0" }, { "Creator Owner", "S-1-3-0" },
            { "Dialup", "S-1-5-1" }, { "Network", "S-1-5-2" }, { "Batch", "S-1-5-3" }, { "Interactive", "S-1-5-4" },
            { "Service", "S-1-5-6" }, { "Anonymous Logon", "S-1-5-7" }, { "Enterprise Domain Controllers", "S-1-5-9" },
            { "Self", "S-1-5-10" }, { "Authenticated Users", "S-1-5-11" }, { "Restricted", "S-1-5-12" },
            { "Terminal Server User", "S-1-5-13" }, { "Remote Interactive Logon", "S-1-5-14" }, { "This Organization", "S-1-5-15" },
            { "System", "S-1-5-18" }, { "Local System", "S-1-5-18" }, { "Local Service", "S-1-5-19" }, { "Network Service", "S-1-5-20" },
            { "Local account", "S-1-5-113" }, { "Local account and member of Administrators group", "S-1-5-114" },
            { "Administrators", "S-1-5-32-544" }, { "Users", "S-1-5-32-545" }, { "Guests", "S-1-5-32-546" },
            { "Power Users", "S-1-5-32-547" }, { "Account Operators", "S-1-5-32-548" }, { "Server Operators", "S-1-5-32-549" },
            { "Print Operators", "S-1-5-32-550" }, { "Backup Operators", "S-1-5-32-551" }, { "Replicator", "S-1-5-32-552" },
            { "Pre-Windows 2000 Compatible Access", "S-1-5-32-554" }, { "Remote Desktop Users", "S-1-5-32-555" },
            { "Network Configuration Operators", "S-1-5-32-556" }, { "Performance Monitor Users", "S-1-5-32-558" },
            { "Performance Log Users", "S-1-5-32-559" }, { "Distributed COM Users", "S-1-5-32-562" }, { "IIS_IUSRS", "S-1-5-32-568" },
            { "Cryptographic Operators", "S-1-5-32-569" }, { "Event Log Readers", "S-1-5-32-573" }, { "Hyper-V Administrators", "S-1-5-32-578" },
            { "Remote Management Users", "S-1-5-32-580" },
            { "Window Manager Group", "S-1-5-90-0" }, { "Virtual Machines", "S-1-5-83-0" }, { "All Services", "S-1-5-80-0" },
            { "ALL APPLICATION PACKAGES", "S-1-15-2-1" }, { "ALL RESTRICTED APPLICATION PACKAGES", "S-1-15-2-2" },
        };

        private static string NormalizeName(string name)
        {
            var n = (name ?? "").Trim().TrimStart('*');
            if (n.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return n.ToUpperInvariant();
            var bare = n;
            var slash = n.LastIndexOf('\\');
            if (slash >= 0)
            {
                var domain = n.Substring(0, slash);
                // Only built-in qualifiers are dropped; LAB\svc-scan stays a domain account.
                if (domain.Equals("BUILTIN", StringComparison.OrdinalIgnoreCase) || domain.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase)
                    || domain.Equals("Window Manager", StringComparison.OrdinalIgnoreCase) || domain.Equals("NT VIRTUAL MACHINE", StringComparison.OrdinalIgnoreCase)
                    || domain.Equals("APPLICATION PACKAGE AUTHORITY", StringComparison.OrdinalIgnoreCase) || domain.Equals("NT SERVICE", StringComparison.OrdinalIgnoreCase))
                    bare = n.Substring(slash + 1);
            }
            if (slash < 0 || bare != n) { if (WellKnown.TryGetValue(bare, out var known)) return known; }
            return n.ToUpperInvariant();
        }

        private static Principal ReadPrincipal(XmlElement el)
        {
            var sid = Text(Child(el, "SID"));
            var name = Text(Child(el, "Name"));
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(sid) ? Text(el) : sid;
            var id = !string.IsNullOrEmpty(sid) ? sid.TrimStart('*').ToUpperInvariant() : NormalizeName(name);
            return new Principal { Id = id, Name = name };
        }

        private static GpoSetting ListSetting(string scope, string ext, string category, string key, string display, List<Principal> members)
        {
            // Sorted for display; SettingDiff compares MemberIds as a set, so the order DISA lists members in is never a change.
            var sorted = members.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
            return new GpoSetting
            {
                Scope = scope, Extension = ext, Category = category, Key = key, DisplayName = display,
                Value = sorted.Count == 0 ? "(no one)" : string.Join("; ", sorted.Select(m => m.Name)),
                MemberIds = sorted.Select(m => m.Id).ToList(),
                MemberNames = sorted.Select(m => m.Name).ToList(),
                IsReplacementType = ReplacementCategories.Contains(category),
            };
        }

        /// <summary>Leaf values of an element (excluding identity and noise children) as "path=value" pairs, sorted.</summary>
        private static string CanonicalValue(XmlElement el, params string[] exclude)
        {
            var ex = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
            var parts = new List<string>();
            foreach (var child in el.ChildNodes.OfType<XmlElement>())
            {
                if (ex.Contains(child.LocalName) || NoiseElements.Contains(child.LocalName)) continue;
                foreach (var leaf in Leaves(child, child.LocalName))
                    parts.Add(leaf.Key + "=" + leaf.Value);
            }
            if (parts.Count == 0) return "(set)";
            parts.Sort(StringComparer.Ordinal);
            return string.Join("; ", parts);
        }

        private static IEnumerable<KeyValuePair<string, string>> Leaves(XmlElement el, string path)
        {
            var children = el.ChildNodes.OfType<XmlElement>().Where(c => !NoiseElements.Contains(c.LocalName)).ToList();
            if (children.Count == 0)
            {
                yield return new KeyValuePair<string, string>(path.Length == 0 ? el.LocalName : path, Text(el));
                yield break;
            }
            foreach (var c in children)
            {
                var p = path.Length == 0 ? c.LocalName : path + "/" + c.LocalName;
                foreach (var leaf in Leaves(c, p)) yield return leaf;
            }
        }

        private static XmlElement Child(XmlNode parent, string localName) =>
            parent?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<XmlElement> Children(XmlNode parent, string localName) =>
            parent == null ? Enumerable.Empty<XmlElement>() :
            parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

        private static string Text(XmlNode n) => n == null ? "" : (n.InnerText ?? "").Trim();
    }
}
