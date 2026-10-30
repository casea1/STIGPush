using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StigGpoManager.Core
{
    public enum LogLevel { Info, Warning, Error, Success }

    public sealed class PlannedChange
    {
        public string Kind { get; set; }
        public string Target { get; set; }
        public string Description { get; set; }
        public override string ToString() => $"[{Kind}] {Target}: {Description}";
    }

    /// <summary>
    /// What a Push will do, shown to the user before confirmation (and printed by -WhatIf in the CLI).
    /// Blockers prevent execution; acknowledgements must be explicitly confirmed.
    /// </summary>
    public sealed class OperationPlan
    {
        public string Title { get; set; }
        public List<PlannedChange> Changes { get; } = new List<PlannedChange>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Blockers { get; } = new List<string>();
        /// <summary>Word the user must type (PROMOTE / DELETE), or null for a simple Yes/No.</summary>
        public string ConfirmationWord { get; set; }
        /// <summary>Statements the user must tick before execution (e.g. system-state backup on a single-DC domain).</summary>
        public List<string> RequiredAcknowledgements { get; } = new List<string>();

        public bool CanExecute => Blockers.Count == 0 && Changes.Count > 0;

        public void Add(string kind, string target, string description) =>
            Changes.Add(new PlannedChange { Kind = kind, Target = target, Description = description });

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Title);
            sb.AppendLine(new string('=', Math.Max(10, (Title ?? "").Length)));
            foreach (var b in Blockers) sb.AppendLine("BLOCKED: " + b);
            foreach (var w in Warnings) sb.AppendLine("WARNING: " + w);
            foreach (var a in RequiredAcknowledgements) sb.AppendLine("REQUIRES CONFIRMATION: " + a);
            if (Blockers.Count + Warnings.Count + RequiredAcknowledgements.Count > 0) sb.AppendLine();
            if (Changes.Count == 0) sb.AppendLine("(no changes)");
            foreach (var g in Changes.GroupBy(c => c.Target))
            {
                sb.AppendLine(g.Key);
                foreach (var c in g) sb.AppendLine($"   - [{c.Kind}] {c.Description.Replace("\n", "\n       ")}");
            }
            if (ConfirmationWord != null) sb.AppendLine().AppendLine($"Type {ConfirmationWord} to confirm.");
            return sb.ToString();
        }
    }

    public enum CheckStatus { Pass, Warning, Fail }

    public sealed class CheckResult
    {
        public string Name { get; set; }
        public CheckStatus Status { get; set; }
        public string Detail { get; set; }

        public static CheckResult Pass(string name, string detail = null) => new CheckResult { Name = name, Status = CheckStatus.Pass, Detail = detail };
        public static CheckResult Warn(string name, string detail) => new CheckResult { Name = name, Status = CheckStatus.Warning, Detail = detail };
        public static CheckResult Fail(string name, string detail) => new CheckResult { Name = name, Status = CheckStatus.Fail, Detail = detail };
        public override string ToString() => $"[{Status}] {Name}{(string.IsNullOrEmpty(Detail) ? "" : ": " + Detail)}";
    }

    /// <summary>
    /// A blocking pre-check evaluated before Promote. Phase 2's scan-results gate is one of these;
    /// more can be added without changing the engine.
    /// </summary>
    public interface IPromotionCheck
    {
        string Name { get; }
        CheckResult Evaluate(RunState run);
    }
}
