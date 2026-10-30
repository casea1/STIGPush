using System;
using System.Collections.Generic;
using System.Linq;

namespace StigGpoManager.Core
{
    /// <summary>A GPO link on an OU (or domain root). Order 1 = highest precedence.</summary>
    public sealed class LinkInfo
    {
        public string GpoId { get; set; }
        public string GpoName { get; set; }
        public bool Enabled { get; set; }
        public bool Enforced { get; set; }
        public int Order { get; set; }

        public LinkInfo Clone() => new LinkInfo { GpoId = GpoId, GpoName = GpoName, Enabled = Enabled, Enforced = Enforced, Order = Order };
        public override string ToString() => $"{Order}. {GpoName}{(Enabled ? "" : " (disabled)")}{(Enforced ? " (enforced)" : "")}";
    }

    public enum LinkClass { Exception, Disa, Other }

    /// <summary>One Set-GPLink -Order operation.</summary>
    public sealed class OrderMove
    {
        public string GpoId { get; set; }
        public string GpoName { get; set; }
        public int NewOrder { get; set; }
        public override string ToString() => $"Move '{GpoName}' to order {NewOrder}";
    }

    /// <summary>
    /// Pure link-order logic. Rules:
    ///  - Every exception GPO (ExceptionPrefix) is above every DISA GPO on the same OU.
    ///  - A new DISA version goes directly above the previous version of the same mapping;
    ///    with no previous version on the OU it goes directly below the last exception GPO.
    ///  - Other links keep their relative order.
    /// </summary>
    public static class LinkOrdering
    {
        public static string NormalizeGuid(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            return Guid.TryParse(id.Trim(), out var g) ? g.ToString("B").ToUpperInvariant() : id.Trim().ToUpperInvariant();
        }

        public static bool SameGpo(string a, string b) => string.Equals(NormalizeGuid(a), NormalizeGuid(b), StringComparison.OrdinalIgnoreCase);

        public static LinkClass Classify(LinkInfo link, AppConfig config)
        {
            if (config.IsExceptionGpo(link.GpoName)) return LinkClass.Exception;
            if (config.IsDisaGpo(link.GpoName)) return LinkClass.Disa;
            return LinkClass.Other;
        }

        public static List<LinkInfo> Sorted(IEnumerable<LinkInfo> links) => links.OrderBy(l => l.Order).Select(l => l.Clone()).ToList();

        /// <summary>Renumbers Order 1..n to match list position.</summary>
        public static List<LinkInfo> Renumber(List<LinkInfo> seq)
        {
            for (int i = 0; i < seq.Count; i++) seq[i].Order = i + 1;
            return seq;
        }

        /// <summary>
        /// Desired link sequence after placing <paramref name="newLink"/> on an OU.
        /// <paramref name="previousVersionIds"/> are the GPO IDs of older versions of the same mapping.
        /// If the new GPO is already linked its current link is moved, not duplicated.
        /// </summary>
        public static List<LinkInfo> PlaceNewLink(IEnumerable<LinkInfo> current, LinkInfo newLink, IEnumerable<string> previousVersionIds, AppConfig config)
        {
            var seq = Sorted(current);
            var existing = seq.FirstOrDefault(l => SameGpo(l.GpoId, newLink.GpoId));
            var toInsert = existing ?? newLink.Clone();
            if (existing != null) seq.Remove(existing);

            var prevIds = new HashSet<string>((previousVersionIds ?? Enumerable.Empty<string>()).Select(NormalizeGuid), StringComparer.OrdinalIgnoreCase);
            int prevIndex = seq.FindIndex(l => prevIds.Contains(NormalizeGuid(l.GpoId)));
            int index;
            if (prevIndex >= 0)
            {
                index = prevIndex;
            }
            else
            {
                int lastException = seq.FindLastIndex(l => Classify(l, config) == LinkClass.Exception);
                index = lastException + 1;
            }
            seq.Insert(index, toInsert);
            return Renumber(MakeCompliant(seq, config));
        }

        /// <summary>
        /// Returns a sequence in which every exception GPO is above every DISA GPO. Exceptions found
        /// below the first DISA GPO are moved up to sit directly above it (keeping their relative order).
        /// Everything else keeps its relative order. Input order is preserved if already compliant.
        /// </summary>
        public static List<LinkInfo> MakeCompliant(IEnumerable<LinkInfo> sequence, AppConfig config)
        {
            var seq = sequence.Select(l => l.Clone()).ToList();
            int firstDisa = seq.FindIndex(l => Classify(l, config) == LinkClass.Disa);
            if (firstDisa < 0) return Renumber(seq);
            var lateExceptions = seq.Skip(firstDisa).Where(l => Classify(l, config) == LinkClass.Exception).ToList();
            if (lateExceptions.Count == 0) return Renumber(seq);
            foreach (var e in lateExceptions) seq.Remove(e);
            seq.InsertRange(firstDisa, lateExceptions);
            return Renumber(seq);
        }

        /// <summary>Human-readable violations of the ordering rules on one OU (empty = compliant).</summary>
        public static List<string> Verify(IEnumerable<LinkInfo> links, AppConfig config, string newGpoId = null, IEnumerable<string> previousVersionIds = null)
        {
            var seq = Sorted(links);
            var problems = new List<string>();
            foreach (var ex in seq.Where(l => Classify(l, config) == LinkClass.Exception))
            {
                foreach (var d in seq.Where(l => Classify(l, config) == LinkClass.Disa && l.Order < ex.Order))
                    problems.Add($"Exception GPO '{ex.GpoName}' (order {ex.Order}) is below DISA GPO '{d.GpoName}' (order {d.Order}).");
            }
            foreach (var d in seq.Where(l => l.Enforced && Classify(l, config) == LinkClass.Disa))
                problems.Add($"DISA GPO '{d.GpoName}' link is Enforced; enforced links override exception GPOs on child OUs.");
            if (newGpoId != null)
            {
                var newLink = seq.FirstOrDefault(l => SameGpo(l.GpoId, newGpoId));
                var prevIds = new HashSet<string>((previousVersionIds ?? Enumerable.Empty<string>()).Select(NormalizeGuid), StringComparer.OrdinalIgnoreCase);
                var prev = seq.FirstOrDefault(l => prevIds.Contains(NormalizeGuid(l.GpoId)));
                if (newLink == null) problems.Add("The new GPO is not linked.");
                else if (prev != null && newLink.Order > prev.Order)
                    problems.Add($"New GPO '{newLink.GpoName}' (order {newLink.Order}) is below previous version '{prev.GpoName}' (order {prev.Order}).");
            }
            return problems;
        }

        /// <summary>
        /// Minimal sequence of Set-GPLink -Order operations that turns <paramref name="current"/> into
        /// <paramref name="desired"/>. Setting a link's order removes it from its position and inserts it
        /// at the new position, shifting the others (GPMC semantics). Links in desired that are not in
        /// current are ignored (they must be created first).
        /// </summary>
        public static List<OrderMove> PlanMoves(IEnumerable<LinkInfo> current, IEnumerable<LinkInfo> desired)
        {
            var cur = Sorted(current).Select(l => NormalizeGuid(l.GpoId)).ToList();
            var names = Sorted(current).ToDictionary(l => NormalizeGuid(l.GpoId), l => l.GpoName, StringComparer.OrdinalIgnoreCase);
            var want = desired.OrderBy(l => l.Order).Select(l => NormalizeGuid(l.GpoId)).Where(id => cur.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            // links present now but absent from desired stay at the bottom in current relative order
            want.AddRange(cur.Where(id => !want.Contains(id, StringComparer.OrdinalIgnoreCase)));

            var moves = new List<OrderMove>();
            for (int i = 0; i < want.Count; i++)
            {
                if (string.Equals(cur[i], want[i], StringComparison.OrdinalIgnoreCase)) continue;
                int from = cur.FindIndex(x => string.Equals(x, want[i], StringComparison.OrdinalIgnoreCase));
                var id = cur[from];
                cur.RemoveAt(from);
                cur.Insert(i, id);
                moves.Add(new OrderMove { GpoId = id, GpoName = names[id], NewOrder = i + 1 });
            }
            return moves;
        }

        /// <summary>Applies moves to a sequence (used to simulate and to test PlanMoves).</summary>
        public static List<LinkInfo> ApplyMoves(IEnumerable<LinkInfo> current, IEnumerable<OrderMove> moves)
        {
            var seq = Sorted(current);
            foreach (var m in moves)
            {
                var link = seq.First(l => SameGpo(l.GpoId, m.GpoId));
                seq.Remove(link);
                seq.Insert(Math.Min(m.NewOrder - 1, seq.Count), link);
            }
            return Renumber(seq);
        }

        /// <summary>
        /// Desired sequence to restore a recorded prior order:
        ///  - GPOs from the snapshot, in their recorded order;
        ///  - links not in the snapshot that are NOT being rolled back (e.g. another mapping's new GPO)
        ///    stay directly above the snapshot GPO they currently sit above;
        ///  - rolled-back links go to the bottom (they are disabled).
        /// The exception rule is then re-applied, because exceptions always win.
        /// </summary>
        public static List<LinkInfo> RestoreOrder(IEnumerable<LinkInfo> current, IEnumerable<string> priorOrderIds,
                                                  IEnumerable<string> rolledBackIds, AppConfig config)
        {
            var seq = Sorted(current);
            var prior = priorOrderIds.Select(NormalizeGuid).ToList();
            var rolledBack = new HashSet<string>((rolledBackIds ?? Enumerable.Empty<string>()).Select(NormalizeGuid), StringComparer.OrdinalIgnoreCase);
            bool InSnapshot(LinkInfo l) => prior.Contains(NormalizeGuid(l.GpoId), StringComparer.OrdinalIgnoreCase);

            var result = new List<LinkInfo>();
            foreach (var id in prior)
            {
                var l = seq.FirstOrDefault(x => SameGpo(x.GpoId, id));
                if (l != null && !result.Contains(l)) result.Add(l);
            }
            var unanchored = new List<LinkInfo>();
            var tail = new List<LinkInfo>();
            for (int i = 0; i < seq.Count; i++)
            {
                var l = seq[i];
                if (InSnapshot(l)) continue;
                if (rolledBack.Contains(NormalizeGuid(l.GpoId))) { tail.Add(l); continue; }
                var anchor = seq.Skip(i + 1).FirstOrDefault(InSnapshot);
                if (anchor == null) unanchored.Add(l);
                else result.Insert(result.IndexOf(anchor), l);
            }
            // non-rolled-back unanchored links first, then rolled-back ones at the very bottom
            result.AddRange(unanchored);
            result.AddRange(tail);
            return MakeCompliant(result, config);
        }

        public static string Describe(IEnumerable<LinkInfo> links) =>
            string.Join(Environment.NewLine, Sorted(links).Select(l => "  " + l));
    }
}
