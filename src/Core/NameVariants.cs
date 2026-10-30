using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StigGpoManager.Core
{
    /// <summary>
    /// DISA abbreviates inconsistently between products and releases ("WinSvr 2025 MS STIG Comp" vs
    /// "Windows Server 2019 Member Server STIG Computer"). Mapping regexes are tested against the raw
    /// backup name and against spelled-out and abbreviated forms of it, so one mapping keeps working
    /// whichever style a release uses.
    /// </summary>
    public static class NameVariants
    {
        private static readonly (Regex from, string to)[] RoleExpand =
        {
            (new Regex(@"\bMS\b"), "Member Server"), (new Regex(@"\bDC\b"), "Domain Controller"),
            (new Regex(@"\bComp\b", RegexOptions.IgnoreCase), "Computer"), (new Regex(@"\bUsr\b", RegexOptions.IgnoreCase), "User"),
        };

        private static readonly (Regex from, string to)[] ProductExpand =
        {
            (new Regex(@"\bWinSvr\s*(?=\d)", RegexOptions.IgnoreCase), "Windows Server "), (new Regex(@"\bWinSvr\b", RegexOptions.IgnoreCase), "Windows Server"),
            (new Regex(@"\bWin\s*(?=\d)", RegexOptions.IgnoreCase), "Windows "), (new Regex(@"\bSvr\b", RegexOptions.IgnoreCase), "Server"),
        };

        private static readonly (Regex from, string to)[] RoleContract =
        {
            (new Regex(@"\bMember Server\b", RegexOptions.IgnoreCase), "MS"), (new Regex(@"\bDomain Controller\b", RegexOptions.IgnoreCase), "DC"),
            (new Regex(@"\bComputer\b", RegexOptions.IgnoreCase), "Comp"),
        };

        private static readonly (Regex from, string to)[] ProductContract =
        {
            (new Regex(@"\bWindows Server\b", RegexOptions.IgnoreCase), "WinSvr"), (new Regex(@"\bWindows\s+(?=\d)", RegexOptions.IgnoreCase), "Win"),
        };

        private static string Apply(string s, IEnumerable<(Regex from, string to)> rules)
        {
            foreach (var r in rules) s = r.from.Replace(s, r.to);
            return Regex.Replace(s, @"\s{2,}", " ");
        }

        /// <summary>The raw name plus spelled-out / abbreviated variants (distinct).</summary>
        public static List<string> Of(string name)
        {
            var list = new List<string> { name ?? "" };
            if (string.IsNullOrEmpty(name)) return list;
            list.Add(Apply(name, RoleExpand));
            list.Add(Apply(name, RoleExpand.Concat(ProductExpand)));
            list.Add(Apply(name, ProductExpand));
            list.Add(Apply(name, RoleContract));
            list.Add(Apply(name, RoleContract.Concat(ProductContract)));
            list.Add(Apply(name, ProductContract));
            return list.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>Lower-case spelled-out words, for similarity scoring.</summary>
        public static HashSet<string> Tokens(string name)
        {
            var expanded = Apply(name ?? "", RoleExpand.Concat(ProductExpand)).ToLowerInvariant();
            expanded = StigVersion.Pattern.Replace(expanded, " ").Replace("{version}", " ");
            return new HashSet<string>(Regex.Split(expanded, @"[^a-z0-9]+").Where(t => t.Length > 0 && t != "dod" && t != "stig" && t != "and"));
        }
    }
}
