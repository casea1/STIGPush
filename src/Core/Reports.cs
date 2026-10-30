using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace StigGpoManager.Core
{
    /// <summary>HTML and CSV reports (self-contained; no external CSS or scripts).</summary>
    public static class Reports
    {
        private const string Style =
            "body{font-family:Segoe UI,Arial,sans-serif;font-size:13px;margin:20px;color:#111}" +
            "h1{font-size:20px}h2{font-size:16px;margin-top:24px;border-bottom:1px solid #ccc}" +
            "table{border-collapse:collapse;width:100%;margin:8px 0}th,td{border:1px solid #bbb;padding:3px 6px;vertical-align:top;text-align:left}" +
            "th{background:#e8e8e8}tr.s2 td{background:#ffd6d6;font-weight:bold}tr.s1 td{background:#fff3cd}" +
            ".pass{color:#060;font-weight:bold}.fail{color:#b00;font-weight:bold}.warn{color:#a60;font-weight:bold}" +
            "pre{background:#f4f4f4;padding:6px;white-space:pre-wrap}.small{color:#555;font-size:11px}";

        public static string H(string s) => WebUtility.HtmlEncode(s ?? "");

        public static string Csv(string s)
        {
            s = s ?? "";
            // Neutralize spreadsheet formula injection from GPO content.
            if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        public static void WriteReviewCsv(string path, IEnumerable<ReviewRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Mapping,NewGpo,PreviousGpo,Change,Scope,Category,Setting,OldValue,NewValue,Flag,ExceptionGpo,ExceptionValue,Detail,Severity");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",", new[] { r.Mapping, r.NewGpo, r.PreviousGpo, r.Change, r.Scope, r.Category, r.Setting, r.OldValue, r.NewValue, r.Flag, r.ExceptionGpo, r.ExceptionValue, r.Detail, r.Severity.ToString() }.Select(Csv)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static void WriteReviewHtml(string path, RunState run, ReviewResult review)
        {
            var sb = new StringBuilder();
            Header(sb, $"STIG GPO review - run {run.RunId}");
            sb.Append($"<p>Package: <b>{H(run.Package?.Name)}</b> &nbsp; SHA-256: <code>{H(run.Package?.Sha256)}</code> &nbsp; Generated {DateTime.Now:yyyy-MM-dd HH:mm} by {H(run.CreatedBy)}</p>");
            sb.Append("<h2>Summary</h2><table><tr><th>Mapping</th><th>New GPO</th><th>Previous</th><th>Added</th><th>Removed</th><th>Changed</th><th>Exception findings</th><th>Replacement-type warnings</th></tr>");
            foreach (var i in run.Items.Where(i => i.NewGpoId != null))
                sb.Append($"<tr class=\"{(i.ReplacementWarnings > 0 ? "s2" : "")}\"><td>{H(i.MappingKey)}</td><td>{H(i.NewGpoName)}</td><td>{H(i.PreviousGpoName ?? "(none)")}</td><td>{i.DiffAdded}</td><td>{i.DiffRemoved}</td><td>{i.DiffChanged}</td><td>{i.ExceptionFlags}</td><td>{i.ReplacementWarnings}</td></tr>");
            sb.Append("</table>");
            if (review.Warnings.Count > 0)
            {
                sb.Append("<h2>Warnings</h2><ul>");
                foreach (var w in review.Warnings) sb.Append($"<li class=\"warn\">{H(w)}</li>");
                sb.Append("</ul>");
            }
            var flagged = review.Rows.Where(r => r.Severity > 0).ToList();
            if (flagged.Count > 0)
            {
                sb.Append("<h2>Needs attention</h2>");
                RowsTable(sb, flagged.OrderByDescending(r => r.Severity));
            }
            foreach (var g in review.Rows.GroupBy(r => r.NewGpo))
            {
                sb.Append($"<h2>{H(g.Key)} (previous: {H(g.First().PreviousGpo)})</h2>");
                RowsTable(sb, g);
            }
            Footer(sb);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void RowsTable(StringBuilder sb, IEnumerable<ReviewRow> rows)
        {
            sb.Append("<table><tr><th>Change</th><th>Scope</th><th>Setting</th><th>Old</th><th>New</th><th>Flag</th><th>Exception GPO</th><th>Exception value</th><th>Detail</th></tr>");
            foreach (var r in rows)
                sb.Append($"<tr class=\"s{r.Severity}\"><td>{H(r.Change)}</td><td>{H(r.Scope)}</td><td>{H(r.Setting)}</td><td>{H(r.OldValue)}</td><td>{H(r.NewValue)}</td><td>{H(r.Flag)}</td><td>{H(r.ExceptionGpo)}</td><td>{H(r.ExceptionValue)}</td><td>{H(r.Detail)}</td></tr>");
            sb.Append("</table>");
        }

        /// <summary>Run report for SSP / POA&amp;M evidence: everything the run did, plus gate outcome and audit entries.</summary>
        public static string WriteRunReport(string reportsFolder, RunState run, AuditLog audit, ScanGateResult gate = null)
        {
            Directory.CreateDirectory(reportsFolder);
            var path = Path.Combine(reportsFolder, $"run-{run.RunId}-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            var sb = new StringBuilder();
            Header(sb, $"STIG GPO run report - {run.RunId}");
            sb.Append("<table>");
            void Row(string k, string v) => sb.Append($"<tr><th style=\"width:220px\">{H(k)}</th><td>{H(v)}</td></tr>");
            Row("Domain", run.Domain);
            Row("Target DC", run.TargetDc);
            Row("Created", run.CreatedUtc.ToLocalTime() + " by " + run.CreatedBy);
            Row("Current phase", run.Phase.ToString());
            Row("Package", run.Package?.Name);
            Row("SHA-256", run.Package?.Sha256);
            Row("Expected SHA-256", run.Package?.ExpectedSha256 ?? "(not provided)");
            Row("Hash check", run.Package?.HashMatches == null ? "not performed" : run.Package.HashMatches == true ? "MATCH" : "MISMATCH");
            Row("Pilot started", run.PilotStartedUtc?.ToLocalTime().ToString() ?? "");
            Row("Promoted", run.PromotedUtc?.ToLocalTime().ToString() ?? "");
            sb.Append("</table>");

            sb.Append("<h2>GPOs</h2><table><tr><th>New GPO</th><th>GUID</th><th>Source backup</th><th>Version</th><th>Previous</th><th>Pilot mode</th><th>Status</th><th>Diff (+/-/~)</th></tr>");
            foreach (var i in run.Items)
                sb.Append($"<tr><td>{H(i.NewGpoName)}</td><td>{H(i.NewGpoId)}</td><td>{H(i.BackupDisplayName)}</td><td>{H(i.Version)}</td><td>{H(i.PreviousGpoName)}</td><td>{i.PilotMode}</td><td>{i.Status}{(i.LastError != null ? "<br><span class=fail>" + H(i.LastError) + "</span>" : "")}</td><td>{i.DiffAdded}/{i.DiffRemoved}/{i.DiffChanged}</td></tr>");
            sb.Append("</table>");

            sb.Append("<h2>Phase history</h2><table><tr><th>Time</th><th>Phase</th><th>User</th><th>Note</th></tr>");
            foreach (var h in run.History) sb.Append($"<tr><td>{h.Utc.ToLocalTime()}</td><td>{H(h.Phase)}</td><td>{H(h.User)}</td><td>{H(h.Note)}</td></tr>");
            sb.Append("</table>");

            sb.Append("<h2>Link changes</h2><table><tr><th>Time</th><th>Phase</th><th>Action</th><th>GPO</th><th>Target</th><th>Detail</th><th>Order after</th></tr>");
            foreach (var a in run.LinkActions)
                sb.Append($"<tr><td>{a.Utc.ToLocalTime()}</td><td>{H(a.Phase)}</td><td>{H(a.Action)}</td><td>{H(a.GpoName)}</td><td>{H(a.Target)}</td><td>{H(a.Detail)}</td><td><pre>{H(string.Join("\n", a.OrderAfter.OrderBy(l => l.Order).Select(l => $"{l.Order}. {l.GpoName}{(l.Enabled ? "" : " (disabled)")}")))}</pre></td></tr>");
            sb.Append("</table>");

            if (run.Confirmations.Count > 0)
            {
                sb.Append("<h2>Confirmations</h2><table><tr><th>Time</th><th>User</th><th>Kind</th><th>Statement</th></tr>");
                foreach (var c in run.Confirmations) sb.Append($"<tr><td>{c.Utc.ToLocalTime()}</td><td>{H(c.User)}</td><td>{H(c.Kind)}</td><td>{H(c.Detail)}</td></tr>");
                sb.Append("</table>");
            }

            sb.Append("<h2>Backups</h2><ul>");
            foreach (var b in run.Backups) sb.Append($"<li>{H(b)}</li>");
            sb.Append("</ul>");

            sb.Append("<h2>Scan results (promotion gate evidence)</h2>");
            if (run.ScanImports.Count == 0) sb.Append("<p>No scan results imported.</p>");
            else
            {
                sb.Append("<table><tr><th>Stage</th><th>File</th><th>Format</th><th>SHA-256</th><th>Hosts</th><th>Open / total</th><th>Imported</th></tr>");
                foreach (var s in run.ScanImports)
                    sb.Append($"<tr><td>{H(s.Stage)}</td><td>{H(s.OriginalFileName)}</td><td>{H(s.Format)}</td><td><code>{H(s.Sha256)}</code></td><td>{H(string.Join(", ", s.Hosts))}</td><td>{s.OpenCount} / {s.TotalCount}</td><td>{s.ImportedUtc.ToLocalTime()}</td></tr>");
                sb.Append("</table>");
            }
            if (run.Gate != null)
                sb.Append($"<p>Gate evaluated {run.Gate.EvaluatedUtc.ToLocalTime()}: <span class=\"{(run.Gate.Passed ? "pass" : "fail")}\">{(run.Gate.Passed ? "PASS" : "FAIL")}</span> - {H(run.Gate.Summary)}</p>");
            if (gate != null) WriteGateTables(sb, gate);

            sb.Append("<h2>Audit log entries for this run</h2><table><tr><th>Time (UTC)</th><th>User</th><th>Action</th><th>Target</th><th>Result</th><th>Error</th></tr>");
            foreach (var e in audit.ReadAll(run.RunId))
                sb.Append($"<tr><td>{H(Json.GetString(e, "timestamp"))}</td><td>{H(Json.GetString(e, "user"))}</td><td>{H(Json.GetString(e, "action"))}</td><td>{H(Json.GetString(e, "target"))}</td><td>{H(Json.GetString(e, "result"))}</td><td>{H(Json.GetString(e, "error"))}</td></tr>");
            sb.Append("</table>");
            Footer(sb);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        public static void WriteGateTables(StringBuilder sb, ScanGateResult gate)
        {
            sb.Append($"<p>Gate: <span class=\"{(gate.Passed ? "pass" : "fail")}\">{(gate.Passed ? "PASS" : "FAIL")}</span> - {H(gate.Summary)}</p>");
            if (gate.Blocking.Count > 0)
            {
                sb.Append("<h2>Blocking findings: open or not evaluated (error), not in exceptions register</h2><table><tr><th>Host</th><th>V-ID</th><th>STIG</th><th>Title</th><th>Source</th></tr>");
                foreach (var f in gate.Blocking) sb.Append($"<tr class=s2><td>{H(f.Host)}</td><td>{H(f.DisplayId)}{(f.Status == ScanStatus.Error ? " (error)" : "")}</td><td>{H(f.StigName)}</td><td>{H(f.Title)}</td><td>{H(f.SourceFile)}</td></tr>");
                sb.Append("</table>");
            }
            if (gate.Expected.Count > 0)
            {
                sb.Append("<h2>Expected open findings (in exceptions register)</h2><table><tr><th>Host</th><th>V-ID</th><th>STIG</th><th>Exception GPO</th><th>POA&amp;M</th></tr>");
                foreach (var e in gate.Expected) sb.Append($"<tr><td>{H(e.Finding.Host)}</td><td>{H(e.Finding.VulnId)}</td><td>{H(e.Finding.StigName)}</td><td>{H(e.Entry.ExceptionGpo)}</td><td>{H(e.Entry.PoamReference)}</td></tr>");
                sb.Append("</table>");
            }
            if (gate.StatusChanges.Count > 0)
            {
                sb.Append("<h2>Status changes: baseline vs pilot</h2><table><tr><th>Host</th><th>V-ID</th><th>Before</th><th>After</th></tr>");
                foreach (var c in gate.StatusChanges) sb.Append($"<tr class=\"{(c.After == ScanStatus.Open ? "s1" : "")}\"><td>{H(c.Host)}</td><td>{H(c.VulnId)}</td><td>{c.Before}</td><td>{c.After}</td></tr>");
                sb.Append("</table>");
            }
        }

        private static void Header(StringBuilder sb, string title)
        {
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(H(title)).Append("</title><style>").Append(Style).Append("</style></head><body>");
            sb.Append("<h1>").Append(H(title)).Append("</h1>");
        }

        private static void Footer(StringBuilder sb) =>
            sb.Append($"<p class=small>Generated by StigGpoManager on {H(Environment.MachineName)} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.</p></body></html>");
    }
}
