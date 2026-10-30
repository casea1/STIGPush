using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace StigGpoManager.Core
{
    /// <summary>Ordered from best to worst (Consolidate keeps the worst). Error = the scanner could not evaluate the rule.</summary>
    public enum ScanStatus { NotAFinding, NotApplicable, NotReviewed, Error, Open }

    public sealed class ScanFinding
    {
        public string Host { get; set; }
        public string VulnId { get; set; }
        public string RuleId { get; set; }
        public string StigName { get; set; }
        public string Title { get; set; }
        public ScanStatus Status { get; set; }
        public string SourceFile { get; set; }
        /// <summary>False when the result carried only a rule ID (SV-...): register entries must then use that rule ID.</summary>
        public bool HasVulnId => VulnId != null && VulnId.StartsWith("V-", StringComparison.OrdinalIgnoreCase);
        /// <summary>The scanner reached a verdict on this rule (pass, fail or not applicable).</summary>
        public bool Evaluated => Status == ScanStatus.NotAFinding || Status == ScanStatus.NotApplicable || Status == ScanStatus.Open;
        public string DisplayId => HasVulnId || string.IsNullOrEmpty(RuleId) ? VulnId : VulnId + " (rule " + RuleId + ")";
    }

    public sealed class ScanFileResult
    {
        public string Format { get; set; }
        public string Path { get; set; }
        public List<ScanFinding> Findings { get; } = new List<ScanFinding>();
        public List<string> Hosts => Findings.Select(f => f.Host).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => h).ToList();
    }

    /// <summary>Reads SCC / Evaluate-STIG output: CKL, CKLB and XCCDF results. Never runs scans.</summary>
    public static class ScanParser
    {
        private static readonly Regex VidRegex = new Regex(@"(?<![A-Za-z0-9])V-\d+(?![0-9])", RegexOptions.IgnoreCase);

        public static ScanFileResult Parse(string path)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".cklb") return ParseCklb(File.ReadAllText(path), path);
            if (ext == ".ckl") return ParseCkl(PackageScanner.LoadXml(path), path);
            if (ext == ".xml")
            {
                var doc = PackageScanner.LoadXml(path);
                var root = doc.DocumentElement?.LocalName;
                if (root == "CHECKLIST") return ParseCkl(doc, path);
                if (root == "Benchmark" || root == "TestResult" || doc.SelectSingleNode("//*[local-name()='TestResult']") != null)
                    return ParseXccdf(doc, path);
                throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a CKL or XCCDF results file (root element <{root}>).");
            }
            throw new InvalidDataException($"Unsupported scan result file type '{ext}'. Use .ckl, .cklb or XCCDF .xml.");
        }

        public static ScanStatus MapCklStatus(string s)
        {
            switch ((s ?? "").Trim().Replace("_", "").Replace(" ", "").ToLowerInvariant())
            {
                case "open": return ScanStatus.Open;
                case "notafinding": return ScanStatus.NotAFinding;
                case "notapplicable": return ScanStatus.NotApplicable;
                default: return ScanStatus.NotReviewed;
            }
        }

        public static ScanStatus MapXccdfResult(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "pass": case "fixed": return ScanStatus.NotAFinding;
                case "fail": return ScanStatus.Open;
                case "notapplicable": return ScanStatus.NotApplicable;
                case "error": case "unknown": return ScanStatus.Error;
                default: return ScanStatus.NotReviewed; // notchecked, notselected, informational
            }
        }

        public static ScanFileResult ParseCkl(XmlDocument doc, string path)
        {
            var r = new ScanFileResult { Format = "CKL", Path = path };
            var asset = doc.SelectSingleNode("/CHECKLIST/ASSET");
            var host = asset?.SelectSingleNode("HOST_NAME")?.InnerText?.Trim();
            if (string.IsNullOrEmpty(host)) host = asset?.SelectSingleNode("HOST_FQDN")?.InnerText?.Trim();
            if (string.IsNullOrEmpty(host)) host = System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (XmlNode istig in doc.SelectNodes("/CHECKLIST/STIGS/iSTIG"))
            {
                string stig = null;
                foreach (XmlNode si in istig.SelectNodes("STIG_INFO/SI_DATA"))
                    if (si.SelectSingleNode("SID_NAME")?.InnerText == "title") stig = si.SelectSingleNode("SID_DATA")?.InnerText?.Trim();
                foreach (XmlNode vuln in istig.SelectNodes("VULN"))
                {
                    string Attr(string name)
                    {
                        foreach (XmlNode sd in vuln.SelectNodes("STIG_DATA"))
                            if (sd.SelectSingleNode("VULN_ATTRIBUTE")?.InnerText == name) return sd.SelectSingleNode("ATTRIBUTE_DATA")?.InnerText?.Trim();
                        return null;
                    }
                    var vid = Attr("Vuln_Num");
                    if (string.IsNullOrEmpty(vid)) continue;
                    r.Findings.Add(new ScanFinding
                    {
                        Host = host, VulnId = vid.ToUpperInvariant(), RuleId = Attr("Rule_ID"), StigName = stig, Title = Attr("Rule_Title"),
                        Status = MapCklStatus(vuln.SelectSingleNode("STATUS")?.InnerText), SourceFile = System.IO.Path.GetFileName(path),
                    });
                }
            }
            return r;
        }

        public static ScanFileResult ParseCklb(string json, string path)
        {
            var r = new ScanFileResult { Format = "CKLB", Path = path };
            var root = Json.AsObject(Json.Parse(json)) ?? throw new InvalidDataException("CKLB root is not an object.");
            var target = Json.AsObject(root.TryGetValue("target_data", out var td) ? td : null);
            var host = Json.GetString(target, "host_name");
            if (string.IsNullOrEmpty(host)) host = Json.GetString(target, "fqdn");
            if (string.IsNullOrEmpty(host)) host = System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (var s in Json.GetArray(root, "stigs").Select(Json.AsObject).Where(s => s != null))
            {
                var stig = Json.GetString(s, "display_name") ?? Json.GetString(s, "stig_name");
                foreach (var rule in Json.GetArray(s, "rules").Select(Json.AsObject).Where(x => x != null))
                {
                    var vid = Json.GetString(rule, "group_id");
                    if (string.IsNullOrEmpty(vid)) continue;
                    r.Findings.Add(new ScanFinding
                    {
                        Host = host, VulnId = vid.ToUpperInvariant(), RuleId = Json.GetString(rule, "rule_id"), StigName = stig,
                        Title = Json.GetString(rule, "rule_title"), Status = MapCklStatus(Json.GetString(rule, "status")),
                        SourceFile = System.IO.Path.GetFileName(path),
                    });
                }
            }
            return r;
        }

        public static ScanFileResult ParseXccdf(XmlDocument doc, string path)
        {
            var r = new ScanFileResult { Format = "XCCDF", Path = path };
            var benchTitle = doc.SelectSingleNode("/*[local-name()='Benchmark']/*[local-name()='title']")?.InnerText?.Trim();
            // Rule id -> V-ID (from the parent Group id) and title, when the benchmark is embedded.
            var ruleMap = new Dictionary<string, (string vid, string title)>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement rule in doc.SelectNodes("//*[local-name()='Group']/*[local-name()='Rule']"))
            {
                var group = rule.ParentNode as XmlElement;
                var m = VidRegex.Match(group?.GetAttribute("id") ?? "");
                string vid = m.Success ? m.Value.ToUpperInvariant() : null;
                if (vid == null) vid = VidFromIdents(rule);
                var title = rule.SelectSingleNode("*[local-name()='title']")?.InnerText?.Trim();
                ruleMap[rule.GetAttribute("id")] = (vid, title);
            }
            foreach (XmlElement tr in doc.SelectNodes("//*[local-name()='TestResult']"))
            {
                var host = tr.SelectSingleNode("*[local-name()='target']")?.InnerText?.Trim();
                if (string.IsNullOrEmpty(host)) host = System.IO.Path.GetFileNameWithoutExtension(path);
                foreach (XmlElement rr in tr.SelectNodes("*[local-name()='rule-result']"))
                {
                    var idref = rr.GetAttribute("idref");
                    ruleMap.TryGetValue(idref, out var info);
                    var vid = info.vid ?? VidFromIdents(rr);
                    if (vid == null)
                    {
                        // Results without the benchmark: only the rule ID is known. Keep it (SV-123456) so the
                        // finding is still counted; the gate says it has no V-ID and register entries can use it.
                        vid = RuleStem(idref) ?? idref;
                    }
                    r.Findings.Add(new ScanFinding
                    {
                        Host = host, VulnId = vid, RuleId = idref, StigName = benchTitle, Title = info.title,
                        Status = MapXccdfResult(rr.SelectSingleNode("*[local-name()='result']")?.InnerText),
                        SourceFile = System.IO.Path.GetFileName(path),
                    });
                }
            }
            return r;
        }

        private static string VidFromIdents(XmlNode node)
        {
            foreach (XmlNode ident in node.SelectNodes("*[local-name()='ident']"))
            {
                var t = (ident.InnerText ?? "").Trim();
                if (Regex.IsMatch(t, @"^V-\d+$", RegexOptions.IgnoreCase)) return t.ToUpperInvariant();
            }
            return null;
        }

        /// <summary>"xccdf_mil.disa.stig_rule_SV-253256r828891_rule" -&gt; "SV-253256" (the rule, without its revision).</summary>
        public static string RuleStem(string ruleId)
        {
            var m = Regex.Match(ruleId ?? "", @"(?<![A-Za-z0-9])SV-(\d+)", RegexOptions.IgnoreCase);
            return m.Success ? "SV-" + m.Groups[1].Value : null;
        }

        /// <summary>Per host and V-ID, the worst status across all files (Open beats NotReviewed beats the rest).</summary>
        public static Dictionary<string, ScanFinding> Consolidate(IEnumerable<ScanFinding> findings)
        {
            var map = new Dictionary<string, ScanFinding>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in findings)
            {
                var key = f.Host + "|" + f.VulnId;
                if (!map.TryGetValue(key, out var existing) || f.Status > existing.Status) map[key] = f;
            }
            return map;
        }
    }

    // ==================================================================== exceptions register

    public sealed class ExceptionEntry
    {
        public string VulnId { get; set; }
        /// <summary>STIG / benchmark name; empty matches the V-ID in any STIG.</summary>
        public string StigName { get; set; }
        public string ExceptionGpo { get; set; }
        public string Justification { get; set; }
        public string PoamReference { get; set; }
        public DateTime DateAdded { get; set; }
        public string AddedBy { get; set; }

        /// <summary>
        /// Same V-ID (any case), or - for an entry written as a rule ID (SV-123456 or SV-123456r1_rule) - the same
        /// rule regardless of its revision, which is how results that carry no V-ID can still be accepted.
        /// </summary>
        public bool Matches(ScanFinding f)
        {
            var id = (VulnId ?? "").Trim();
            bool same = string.Equals(id, (f.VulnId ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            if (!same)
            {
                var stem = ScanParser.RuleStem(id);
                same = stem != null && (string.Equals(stem, ScanParser.RuleStem(f.RuleId), StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(stem, ScanParser.RuleStem(f.VulnId), StringComparison.OrdinalIgnoreCase));
            }
            if (!same) return false;
            if (string.IsNullOrWhiteSpace(StigName)) return true;
            return (f.StigName ?? "").IndexOf(StigName.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public sealed class ExceptionsRegister
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ExceptionEntry> Entries { get; set; } = new List<ExceptionEntry>();

        public static ExceptionsRegister Load(string path)
        {
            if (!File.Exists(path)) return new ExceptionsRegister();
            return JsonMapper.Deserialize<ExceptionsRegister>(File.ReadAllText(path));
        }

        /// <summary>Saves atomically and keeps a timestamped copy of the previous version.</summary>
        public void Save(string path)
        {
            var problems = Validate();
            if (problems.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, problems));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonMapper.Serialize(this));
            if (File.Exists(path))
            {
                var history = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), "exceptions-history");
                Directory.CreateDirectory(history);
                File.Copy(path, System.IO.Path.Combine(history, $"exceptions-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        public List<string> Validate()
        {
            var problems = new List<string>();
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                if (string.IsNullOrWhiteSpace(e.VulnId) || !Regex.IsMatch(e.VulnId.Trim(), @"^(V|SV)-\d+", RegexOptions.IgnoreCase))
                    problems.Add($"Entry {i + 1}: V-ID '{e.VulnId}' must look like V-123456 (or a rule ID like SV-123456).");
                if (string.IsNullOrWhiteSpace(e.Justification))
                    problems.Add($"Entry {i + 1} ({e.VulnId}): justification is required.");
            }
            return problems;
        }

        public ExceptionEntry Find(ScanFinding f) => Entries.FirstOrDefault(e => e.Matches(f));
    }

    // ==================================================================== gate

    public sealed class ExpectedFinding
    {
        public ScanFinding Finding { get; set; }
        public ExceptionEntry Entry { get; set; }
    }

    public sealed class StatusChange
    {
        public string Host { get; set; }
        public string VulnId { get; set; }
        public ScanStatus Before { get; set; }
        public ScanStatus After { get; set; }
    }

    public sealed class ScanGateResult
    {
        public bool Passed { get; set; }
        public List<ScanFinding> Blocking { get; } = new List<ScanFinding>();
        public List<ExpectedFinding> Expected { get; } = new List<ExpectedFinding>();
        public List<ScanFinding> NotReviewed { get; } = new List<ScanFinding>();
        /// <summary>Hosts where the scanner reached no verdict at all (every rule error / not checked): the scan did not really run.</summary>
        public List<string> UnevaluatedHosts { get; } = new List<string>();
        /// <summary>Results that carried only a rule ID (no V-ID).</summary>
        public int WithoutVulnId { get; set; }
        public List<StatusChange> StatusChanges { get; } = new List<StatusChange>();
        public List<string> Hosts { get; } = new List<string>();
        public string Summary { get; set; }
    }

    public static class ScanGate
    {
        /// <summary>
        /// Open findings in the register are expected; any other open finding blocks Promote.
        /// With baseline results, also reports which host/V-ID statuses changed.
        /// </summary>
        public static ScanGateResult Evaluate(IEnumerable<ScanFinding> pilot, IEnumerable<ScanFinding> baseline, ExceptionsRegister register, bool requireResults = true)
        {
            var result = new ScanGateResult();
            var pilotMap = ScanParser.Consolidate(pilot);
            result.Hosts.AddRange(pilotMap.Values.Select(f => f.Host).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => h));
            foreach (var host in pilotMap.Values.GroupBy(f => f.Host, StringComparer.OrdinalIgnoreCase))
                if (!host.Any(f => f.Evaluated)) result.UnevaluatedHosts.Add(host.Key);
            result.WithoutVulnId = pilotMap.Values.Count(f => !f.HasVulnId);
            foreach (var f in pilotMap.Values.OrderBy(f => f.Host).ThenBy(f => f.VulnId))
            {
                if (f.Status == ScanStatus.Open || f.Status == ScanStatus.Error)
                {
                    var entry = register.Find(f);
                    if (entry != null) result.Expected.Add(new ExpectedFinding { Finding = f, Entry = entry });
                    else result.Blocking.Add(f);
                }
                else if (f.Status == ScanStatus.NotReviewed) result.NotReviewed.Add(f);
            }
            if (baseline != null)
            {
                var baseMap = ScanParser.Consolidate(baseline);
                foreach (var kv in pilotMap)
                    if (baseMap.TryGetValue(kv.Key, out var b) && b.Status != kv.Value.Status)
                        result.StatusChanges.Add(new StatusChange { Host = kv.Value.Host, VulnId = kv.Value.VulnId, Before = b.Status, After = kv.Value.Status });
            }
            bool noResults = pilotMap.Count == 0;
            result.Passed = result.Blocking.Count == 0 && result.UnevaluatedHosts.Count == 0 && !(requireResults && noResults);
            if (noResults) result.Summary = "No pilot scan results imported.";
            else
            {
                int errors = result.Blocking.Count(f => f.Status == ScanStatus.Error);
                var sb = new System.Text.StringBuilder($"{result.Hosts.Count} host(s); {result.Blocking.Count - errors} blocking open finding(s)");
                if (errors > 0) sb.Append($", {errors} rule(s) the scanner could not evaluate (error)");
                sb.Append($", {result.Expected.Count} expected (in register), {result.NotReviewed.Count} not reviewed.");
                if (result.UnevaluatedHosts.Count > 0)
                    sb.Append($" No rule was evaluated on {string.Join(", ", result.UnevaluatedHosts)} (every result is error / not checked): the scan did not run properly there; re-scan before promoting.");
                if (result.WithoutVulnId > 0)
                    sb.Append($" {result.WithoutVulnId} result(s) carry only a rule ID (no V-ID; the results file has no benchmark): an exception for one of them must be entered with its SV- rule ID.");
                result.Summary = sb.ToString();
            }
            return result;
        }
    }
}
