using System;
using System.Collections.Generic;
using System.Linq;
using StigGpoManager.Core;
using Xunit;

namespace StigGpoManager.Tests
{
    public class LinkOrderingTests
    {
        private static readonly AppConfig Cfg = TestData.Config("/tmp/w", "/tmp/cs");

        private static LinkInfo L(string name, int order, bool enabled = true) =>
            new LinkInfo { GpoId = "{" + Guid.NewGuid() + "}", GpoName = name, Order = order, Enabled = enabled };

        private static string[] Names(IEnumerable<LinkInfo> s) => s.OrderBy(l => l.Order).Select(l => l.GpoName).ToArray();

        [Fact]
        public void NewVersionGoesDirectlyAbovePreviousAndBelowExceptions()
        {
            var ex = L("SITE - Server Exceptions", 1);
            var other = L("Site Baseline", 2);
            var prev = L("DoD WinSvr2025 MS STIG Comp v1r3", 3);
            var defender = L("DoD Defender STIG v2r4", 4);
            var neu = L("DoD WinSvr2025 MS STIG Comp v1r4", 0);
            var result = LinkOrdering.PlaceNewLink(new[] { ex, other, prev, defender }, neu, new[] { prev.GpoId }, Cfg);
            Assert.Equal(new[] { "SITE - Server Exceptions", "Site Baseline", "DoD WinSvr2025 MS STIG Comp v1r4", "DoD WinSvr2025 MS STIG Comp v1r3", "DoD Defender STIG v2r4" }, Names(result));
            Assert.Empty(LinkOrdering.Verify(result, Cfg, neu.GpoId, new[] { prev.GpoId }));
        }

        [Fact]
        public void WithoutPreviousGoesDirectlyBelowLastException()
        {
            var e1 = L("SITE - A", 1);
            var e2 = L("SITE - B", 2);
            var d = L("DoD Edge v2r1", 3);
            var result = LinkOrdering.PlaceNewLink(new[] { e1, e2, d }, L("DoD WinSvr2025 MS STIG Comp v1r4", 0), new string[0], Cfg);
            Assert.Equal(new[] { "SITE - A", "SITE - B", "DoD WinSvr2025 MS STIG Comp v1r4", "DoD Edge v2r1" }, Names(result));
            Assert.Equal(new[] { "DoD WinSvr2025 MS STIG Comp v1r4" }, Names(LinkOrdering.PlaceNewLink(new LinkInfo[0], L("DoD WinSvr2025 MS STIG Comp v1r4", 0), null, Cfg)));
        }

        [Fact]
        public void ExceptionBelowDisaIsMovedUp()
        {
            var prev = L("DoD WinSvr2025 MS STIG Comp v1r3", 1);
            var other = L("Site Baseline", 2);
            var ex = L("SITE - Server Exceptions", 3);
            Assert.NotEmpty(LinkOrdering.Verify(new[] { prev, other, ex }, Cfg));
            var result = LinkOrdering.PlaceNewLink(new[] { prev, other, ex }, L("DoD WinSvr2025 MS STIG Comp v1r4", 0), new[] { prev.GpoId }, Cfg);
            Assert.Equal(new[] { "SITE - Server Exceptions", "DoD WinSvr2025 MS STIG Comp v1r4", "DoD WinSvr2025 MS STIG Comp v1r3", "Site Baseline" }, Names(result));
            Assert.Empty(LinkOrdering.Verify(result, Cfg));
        }

        [Fact]
        public void ExistingDisabledLinkIsMovedNotDuplicated()
        {
            var ex = L("SITE - X", 1);
            var prev = L("DoD WinSvr2025 MS STIG Comp v1r3", 2);
            var neu = L("DoD WinSvr2025 MS STIG Comp v1r4", 3, enabled: false);
            var result = LinkOrdering.PlaceNewLink(new[] { ex, prev, neu }, new LinkInfo { GpoId = neu.GpoId, GpoName = neu.GpoName }, new[] { prev.GpoId }, Cfg);
            Assert.Equal(3, result.Count);
            Assert.Equal(2, result.Single(l => l.GpoId == neu.GpoId).Order);
        }

        [Fact]
        public void VerifyFlagsEnforcedDisaAndWrongPlacement()
        {
            var prev = L("DoD WinSvr2025 MS STIG Comp v1r3", 1);
            var neu = L("DoD WinSvr2025 MS STIG Comp v1r4", 2);
            prev.Enforced = true;
            var problems = LinkOrdering.Verify(new[] { prev, neu }, Cfg, neu.GpoId, new[] { prev.GpoId });
            Assert.Contains(problems, p => p.Contains("Enforced"));
            Assert.Contains(problems, p => p.Contains("below previous version"));
        }

        [Fact]
        public void PlanMovesReachesDesiredOrderWithGpmcSemantics()
        {
            var rnd = new Random(42);
            for (int trial = 0; trial < 200; trial++)
            {
                var links = Enumerable.Range(1, rnd.Next(1, 9)).Select(i => L("G" + i, i)).ToList();
                var desired = links.OrderBy(_ => rnd.Next()).Select((l, i) => { var c = l.Clone(); c.Order = i + 1; return c; }).ToList();
                var moves = LinkOrdering.PlanMoves(links, desired);
                var after = LinkOrdering.ApplyMoves(links, moves);
                Assert.Equal(Names(desired), Names(after));
                Assert.True(moves.Count <= links.Count);
            }
            var same = new[] { L("A", 1), L("B", 2) };
            Assert.Empty(LinkOrdering.PlanMoves(same, same));
        }

        [Fact]
        public void RestoreOrderKeepsOtherNewGposNextToTheirAnchors()
        {
            var ex = L("SITE - X", 1);
            var msNew = L("DoD WinSvr2025 MS STIG Comp v1r4", 2, false);   // being rolled back
            var ms = L("DoD WinSvr2025 MS STIG Comp v1r3", 3);
            var edgeNew = L("DoD Edge v2r2", 4);                           // another mapping, stays
            var edge = L("DoD Edge v2r1", 5, false);
            var other = L("Site Baseline", 6);
            var snapshot = new[] { ex.GpoId, ms.GpoId, edge.GpoId, other.GpoId };
            var result = LinkOrdering.RestoreOrder(new[] { ex, msNew, ms, edgeNew, edge, other }, snapshot, new[] { msNew.GpoId }, Cfg);
            Assert.Equal(new[] { "SITE - X", "DoD WinSvr2025 MS STIG Comp v1r3", "DoD Edge v2r2", "DoD Edge v2r1", "Site Baseline", "DoD WinSvr2025 MS STIG Comp v1r4" }, Names(result));
        }
    }
}
