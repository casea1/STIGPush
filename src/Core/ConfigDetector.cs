using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace StigGpoManager.Core
{
    public sealed class PrefixCandidate
    {
        public string Prefix { get; set; }
        public int GpoCount { get; set; }
        public int Score { get; set; }
        public List<string> Examples { get; set; } = new List<string>();
        public override string ToString() => $"\"{Prefix}\"  ({GpoCount} GPOs, e.g. {string.Join(", ", Examples.Take(2))})";
    }

    public sealed class DetectedMapping
    {
        public GpoMapping Mapping { get; set; }
        public string LatestGpo { get; set; }
        public List<string> Versions { get; set; } = new List<string>();
        public string Reason { get; set; }
    }

    public sealed class DetectionResult
    {
        public AppConfig Suggested { get; set; }
        public DomainInfo Domain { get; set; }
        public List<string> OrganizationalUnits { get; set; } = new List<string>();
        public List<PrefixCandidate> ExceptionPrefixCandidates { get; } = new List<PrefixCandidate>();
        public List<string> PilotGroupCandidates { get; } = new List<string>();
        public List<DetectedMapping> Mappings { get; } = new List<DetectedMapping>();
        /// <summary>DISA GPO families found in AD that are not linked anywhere (offered, not pre-selected).</summary>
        public List<DetectedMapping> UnlinkedFamilies { get; } = new List<DetectedMapping>();
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Proposes config.json values from what is already in the domain, so setup asks the admin to
    /// confirm rather than type: domain and PDC, OU tree, exception-GPO prefix (from GPO names that sit
    /// above DISA GPOs), pilot group, and one mapping per DISA GPO family that is currently linked
    /// (name template from the GPO name, production OUs from its links, pilot OUs from "Pilot" child OUs).
    /// Reads only.
    /// </summary>
    public static class ConfigDetector
    {
        public static DetectionResult Detect(IGpoDirectory dir, string workingFolder, string language,
                                             string disaPrefix = "DoD ", Action<string> progress = null)
        {
            var r = new DetectionResult();
            progress?.Invoke("Reading domain ...");
            r.Domain = dir.GetDomain();
            var cfg = new AppConfig
            {
                Domain = r.Domain.DnsRoot, TargetDC = "auto-pdc", DisaGpoPrefix = disaPrefix,
                WorkingFolder = workingFolder, Language = string.IsNullOrEmpty(language) ? "en-US" : language,
            };
            r.Suggested = cfg;

            progress?.Invoke("Reading OUs ...");
            r.OrganizationalUnits = dir.GetOrganizationalUnits();
            var containers = new List<string> { r.Domain.DistinguishedName };
            containers.AddRange(r.OrganizationalUnits);

            progress?.Invoke("Reading GPOs ...");
            var gpos = dir.GetAllGpos();
            var links = new Dictionary<string, List<LinkInfo>>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var c in containers)
            {
                if (++n % 25 == 0) progress?.Invoke($"Reading GPO links ({n}/{containers.Count}) ...");
                try { links[c] = dir.GetLinks(c); }
                catch (Exception ex) { r.Notes.Add($"Could not read links on {c}: {ex.Message}"); }
            }

            // ---- exception prefix
            r.ExceptionPrefixCandidates.AddRange(RankExceptionPrefixes(gpos.Select(g => g.DisplayName), links, disaPrefix));
            cfg.ExceptionPrefix = r.ExceptionPrefixCandidates.FirstOrDefault()?.Prefix;
            if (cfg.ExceptionPrefix == null) r.Notes.Add("No exception-GPO naming prefix was found; enter the prefix your site uses.");

            // ---- pilot group
            try { r.PilotGroupCandidates.AddRange(dir.FindGroups("pilot")); }
            catch (Exception ex) { r.Notes.Add("Could not search groups: " + ex.Message); }
            cfg.PilotGroup = r.PilotGroupCandidates.FirstOrDefault();

            // ---- DISA GPO families
            var families = gpos
                .Where(g => g.DisplayName.StartsWith(disaPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(g => new { Gpo = g, Template = TemplateOf(g.DisplayName), Version = StigVersion.TryParse(g.DisplayName) })
                .Where(x => x.Template != null)
                .GroupBy(x => x.Template, StringComparer.OrdinalIgnoreCase);
            foreach (var fam in families.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                var ids = new HashSet<string>(fam.Select(x => LinkOrdering.NormalizeGuid(x.Gpo.Id)), StringComparer.OrdinalIgnoreCase);
                var prod = links.Where(kv => kv.Value.Any(l => l.Enabled && ids.Contains(LinkOrdering.NormalizeGuid(l.GpoId))))
                                .Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                var latest = fam.OrderByDescending(x => x.Version).First();
                var mapping = new GpoMapping { NameTemplate = fam.Key, Match = SuggestMatch(fam.Key, disaPrefix), ProdTargets = prod };
                var pilots = prod.SelectMany(p => r.OrganizationalUnits.Where(o => IsPilotChild(o, p))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // a "pilot" OU already linked to this family is a pilot target, not production
                prod.RemoveAll(p => pilots.Any(x => AppConfig.DnEquals(x, p)) || IsPilotName(p));
                mapping.ProdTargets = prod;
                mapping.PilotTargets = pilots;
                var dm = new DetectedMapping
                {
                    Mapping = mapping, LatestGpo = latest.Gpo.DisplayName,
                    Versions = fam.Select(x => x.Version?.ToString()).Where(v => v != null).Distinct().OrderBy(v => v).ToList(),
                };
                if (prod.Count > 0)
                {
                    dm.Reason = $"'{latest.Gpo.DisplayName}' is linked to {prod.Count} OU(s)" + (pilots.Count > 0 ? $"; pilot OU found under it" : "; no Pilot child OU found");
                    r.Mappings.Add(dm);
                }
                else
                {
                    dm.Reason = $"'{latest.Gpo.DisplayName}' exists but is not linked (enabled) anywhere";
                    r.UnlinkedFamilies.Add(dm);
                }
            }
            cfg.Mappings = r.Mappings.Select(m => m.Mapping).ToList();
            if (r.Mappings.Count == 0)
                r.Notes.Add($"No linked DISA GPOs ('{disaPrefix}... v#r#') were found; add mappings by hand or load a DISA package to suggest them.");
            if (r.Domain.DomainControllers.Count == 1)
                r.Notes.Add("Single-DC domain: changes to the Domain Controllers OU will ask for a fresh system-state backup confirmation.");
            return r;
        }

        /// <summary>Name with its version token replaced by {Version}; null if the name has no version.</summary>
        public static string TemplateOf(string gpoName)
        {
            var matches = StigVersion.Pattern.Matches(gpoName ?? "");
            if (matches.Count == 0) return null;
            var m = matches[matches.Count - 1];
            return gpoName.Substring(0, m.Index) + "{Version}" + gpoName.Substring(m.Index + m.Length);
        }

        private static bool IsPilotName(string dn)
        {
            var rdn = AppConfig.NormalizeDn(dn).Split(',')[0];
            return rdn.IndexOf("pilot", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsPilotChild(string ou, string parent)
        {
            var parts = AppConfig.NormalizeDn(ou).Split(new[] { ',' }, 2);
            return parts.Length == 2 && AppConfig.DnEquals(parts[1], parent) && IsPilotName(ou);
        }

        /// <summary>
        /// Ranks prefixes like "SITE - " from GPO names that are not DISA GPOs. Prefixes whose GPOs are
        /// linked above DISA GPOs, or whose names mention "exception", score higher.
        /// </summary>
        public static List<PrefixCandidate> RankExceptionPrefixes(IEnumerable<string> gpoNames, IDictionary<string, List<LinkInfo>> links, string disaPrefix)
        {
            var re = new Regex(@"^(?<p>[^\s\-_:]{1,24}(\s*-\s*|_|:\s*|\s+-\s+))\S", RegexOptions.CultureInvariant);
            var byPrefix = new Dictionary<string, PrefixCandidate>(StringComparer.Ordinal);
            foreach (var name in gpoNames.Where(g => !g.StartsWith(disaPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                var m = re.Match(name);
                if (!m.Success) continue;
                var p = m.Groups["p"].Value;
                if (!byPrefix.TryGetValue(p, out var c)) byPrefix[p] = c = new PrefixCandidate { Prefix = p };
                c.GpoCount++;
                if (c.Examples.Count < 3) c.Examples.Add(name);
                c.Score += name.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : 1;
            }
            foreach (var kv in links ?? new Dictionary<string, List<LinkInfo>>())
            {
                var seq = kv.Value.OrderBy(l => l.Order).ToList();
                int firstDisa = seq.FindIndex(l => (l.GpoName ?? "").StartsWith(disaPrefix, StringComparison.OrdinalIgnoreCase));
                if (firstDisa <= 0) continue;
                foreach (var l in seq.Take(firstDisa))
                    foreach (var c in byPrefix.Values.Where(c => (l.GpoName ?? "").StartsWith(c.Prefix, StringComparison.Ordinal)))
                        c.Score += 2;
            }
            return byPrefix.Values.OrderByDescending(c => c.Score).ThenByDescending(c => c.GpoCount).ThenBy(c => c.Prefix).ToList();
        }

        private static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "MS", "(MS|Microsoft|Member Server)" }, { "DC", "(DC|Domain Controller)" },
            { "Comp", "Comp(uter)?" }, { "Computer", "Comp(uter)?" }, { "Usr", "(Usr|User)" }, { "User", "User" },
            { "Svr", "(Svr|Server)" }, { "Srv", "(Srv|Server)" }, { "Server", "(Server|Svr)" },
            { "Win", "(Win|Windows)" }, { "Windows", "(Win|Windows)" }, { "AV", "(AV|Antivirus)" },
            { "FW", "(FW|Firewall)" }, { "Office", "Office" }, { "Edge", "Edge" },
        };

        private static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "STIG", "GPO", "-", "Baseline" };

        /// <summary>
        /// Suggests a Match regex for DISA backup names from a local GPO name template, e.g.
        /// "DoD WinSvr2025 MS STIG Comp {Version}" -> "(Win|Windows) ?(Svr|Server) ?2025.*(MS|Member Server).*Comp(uter)?".
        /// The wizard previews it against a DISA package so the admin can confirm or edit it.
        /// </summary>
        public static string SuggestMatch(string template, string disaPrefix)
        {
            var t = template.Replace("{Version}", " ");
            if (!string.IsNullOrEmpty(disaPrefix) && t.StartsWith(disaPrefix, StringComparison.OrdinalIgnoreCase)) t = t.Substring(disaPrefix.Length);
            var parts = new List<string>();
            foreach (var token in t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Noise.Contains(token)) continue;
                parts.Add(TokenPattern(token));
            }
            return parts.Count == 0 ? Regex.Escape(template.Replace("{Version}", "").Trim()) : string.Join(".*", parts);
        }

        private static string TokenPattern(string token)
        {
            if (Synonyms.TryGetValue(token, out var syn)) return syn;
            // split compound tokens like WinSvr2025 / Win11 into pieces with optional spaces
            var pieces = Regex.Matches(token, @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+").Cast<Match>().Select(m => m.Value).ToList();
            if (pieces.Count > 1 && string.Concat(pieces) == token)
                return string.Join(" ?", pieces.Select(p => Synonyms.TryGetValue(p, out var s) ? s : Regex.Escape(p)));
            return Regex.Escape(token);
        }

        /// <summary>For each mapping, the package backups its Match regex selects (setup preview).</summary>
        public static Dictionary<GpoMapping, List<string>> PreviewMatches(IEnumerable<GpoMapping> mappings, IEnumerable<string> backupNames)
        {
            var names = backupNames.ToList();
            var result = new Dictionary<GpoMapping, List<string>>();
            foreach (var m in mappings)
            {
                try { result[m] = names.Where(m.IsMatch).ToList(); }
                catch (ArgumentException) { result[m] = new List<string>(); }
            }
            return result;
        }

        /// <summary>Suggests a mapping for a DISA backup that no mapping matches (from its display name).</summary>
        public static GpoMapping SuggestFromBackup(string backupDisplayName, string disaPrefix)
        {
            var template = TemplateOf(backupDisplayName) ?? backupDisplayName + " {Version}";
            var core = template.Replace("{Version}", "").Trim();
            if (!string.IsNullOrEmpty(disaPrefix) && core.StartsWith(disaPrefix, StringComparison.OrdinalIgnoreCase)) core = core.Substring(disaPrefix.Length);
            var match = string.Join(".*", core.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(t => !Noise.Contains(t)).Select(Regex.Escape));
            var name = template.StartsWith(disaPrefix ?? "", StringComparison.OrdinalIgnoreCase) ? template : disaPrefix + template;
            return new GpoMapping { Match = match, NameTemplate = Regex.Replace(name, @"\s+", " ").Trim() };
        }
    }

    public static class ConfigWriter
    {
        /// <summary>Serializes a config in the documented key order (comments/unknown keys are not kept).</summary>
        public static string ToJson(AppConfig c)
        {
            var o = Json.NewObject();
            o["_readme"] = "STIG GPO Manager configuration (written by the setup wizard). Backslashes must be doubled. See README.md.";
            o["Domain"] = c.Domain;
            o["TargetDC"] = c.TargetDC;
            o["ExceptionPrefix"] = c.ExceptionPrefix;
            o["DisaGpoPrefix"] = c.DisaGpoPrefix;
            o["WorkingFolder"] = c.WorkingFolder;
            o["Language"] = c.Language;
            if (!string.IsNullOrWhiteSpace(c.PilotGroup)) o["PilotGroup"] = c.PilotGroup;
            if (!string.IsNullOrWhiteSpace(c.CentralStorePath)) o["CentralStorePath"] = c.CentralStorePath;
            o["WriteGpoComment"] = c.WriteGpoComment;
            o["SoakDays"] = c.SoakDays;
            o["RequireScanGate"] = c.RequireScanGate;
            var maps = new List<object>();
            foreach (var m in c.Mappings)
            {
                var mo = Json.NewObject();
                mo["Match"] = m.Match;
                mo["NameTemplate"] = m.NameTemplate;
                mo["PilotTargets"] = m.PilotTargets;
                mo["ProdTargets"] = m.ProdTargets;
                if (m.PilotModeOverride.HasValue) mo["PilotMode"] = m.PilotModeOverride.Value.ToString();
                if (!string.IsNullOrWhiteSpace(m.Notes)) mo["Notes"] = m.Notes;
                maps.Add(mo);
            }
            o["Mappings"] = maps;
            return Json.Serialize(o);
        }
    }
}
