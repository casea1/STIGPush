using System;
using System.Collections.Generic;
using System.Linq;
using StigGpoManager.Core;
using Xunit;

namespace StigGpoManager.Tests
{
    public class TimelineTests
    {
        private static RunState Run(string id, string package, DateTime created, DateTime? pilot = null, DateTime? promoted = null, params RunPhase[] phases)
        {
            var r = new RunState { RunId = id, CreatedUtc = created.ToUniversalTime(), Package = new PackageRecord { Name = package } };
            r.Items.Add(new RunItem { NewGpoId = "{A}", NewGpoName = "DoD X v1r1", Status = ItemStatus.Imported, PilotMode = PilotMode.OU });
            foreach (var p in phases) r.SetPhase(p, "u");
            if (pilot.HasValue) { r.PilotStartedUtc = pilot.Value.ToUniversalTime(); r.Items[0].Status = ItemStatus.Piloted; r.Phase = RunPhase.Piloted; }
            if (promoted.HasValue) { r.PromotedUtc = promoted.Value.ToUniversalTime(); r.Items[0].Status = ItemStatus.Promoted; r.Phase = RunPhase.Promoted; }
            return r;
        }

        [Fact]
        public void StagesFollowTheRun()
        {
            var r = new RunState { RunId = "r" };
            Assert.Equal(Stage.Prepare, Timeline.StageOf(r));
            r.Items.Add(new RunItem { NewGpoId = "{A}", Status = ItemStatus.Imported, PilotMode = PilotMode.OU });
            Assert.Equal(Stage.Review, Timeline.StageOf(r));
            r.SetPhase(RunPhase.Reviewed, "u");
            Assert.Equal(Stage.Pilot, Timeline.StageOf(r));
            r.Items[0].Status = ItemStatus.Piloted;
            Assert.Equal(Stage.Promote, Timeline.StageOf(r));
            r.Items[0].Status = ItemStatus.Promoted;
            Assert.Equal(Stage.Done, Timeline.StageOf(r));
        }

        [Fact]
        public void NoPilotMappingGoesStraightToPromoteAfterReview()
        {
            var r = new RunState { RunId = "r" };
            r.Items.Add(new RunItem { NewGpoId = "{A}", Status = ItemStatus.Imported, PilotMode = PilotMode.None });
            r.SetPhase(RunPhase.Reviewed, "u");
            Assert.Equal(Stage.Promote, Timeline.StageOf(r));
        }

        [Theory]
        [InlineData("U_STIG_GPO_Package_October_2026.zip", 2026, 10)]
        [InlineData("U_STIG_GPO_Package_Jan_2027.zip", 2027, 1)]
        [InlineData("DISA GPO Package July 2026.zip", 2026, 7)]
        public void ReleaseFromPackageName(string name, int year, int month)
        {
            var r = new RunState { Package = new PackageRecord { Name = name }, CreatedUtc = new DateTime(2026, 3, 3) };
            Assert.Equal(new DateTime(year, month, 1), Timeline.ReleaseDateOf(r));
        }

        [Fact]
        public void ReleaseFallsBackToQuarterOfRunDate()
        {
            var r = new RunState { Package = new PackageRecord { Name = "gpos.zip" }, CreatedUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc) };
            Assert.Equal(new DateTime(2026, 4, 1), Timeline.ReleaseDateOf(r));
            Assert.Equal(new DateTime(2027, 1, 1), Timeline.NextReleaseAfter(new DateTime(2026, 10, 1)));
            Assert.Equal(new DateTime(2025, 10, 1), Timeline.ReleaseAtOrBefore(new DateTime(2025, 12, 31)));
        }

        [Fact]
        public void CalendarShowsPastCurrentAndNextRelease()
        {
            var today = new DateTime(2026, 10, 30);
            var july = Run("r1", "U_STIG_GPO_Package_July_2026.zip", new DateTime(2026, 7, 20), new DateTime(2026, 7, 22), new DateTime(2026, 8, 5));
            var oct = Run("r2", "U_STIG_GPO_Package_October_2026.zip", new DateTime(2026, 10, 18), new DateTime(2026, 10, 20));
            var tl = Timeline.Build(new[] { july, oct }, 14, today);

            Assert.Equal(new DateTime(2027, 1, 1), tl.NextExpectedRelease);
            Assert.Contains(tl.Markers, m => m.Kind == MarkerKind.ExpectedRelease && m.Date == new DateTime(2027, 1, 1));
            Assert.Contains(tl.Markers, m => m.Kind == MarkerKind.DisaRelease && m.Date == new DateTime(2026, 7, 1));
            Assert.Equal(new[] { "r2", "r1" }, tl.Rows.Select(r => r.RunId));

            var current = tl.Rows[0];
            Assert.Equal("October 2026 release", current.Title);
            var pilot = current.Segments.Single(s => s.Kind == SegmentKind.Pilot);
            Assert.True(pilot.Open);
            Assert.Equal(today, pilot.End);
            Assert.Contains(current.Markers, m => m.Kind == MarkerKind.SoakEnds && m.Date == new DateTime(2026, 11, 3));

            var prod = tl.Rows[1].Segments.Single(s => s.Kind == SegmentKind.Production);
            Assert.True(prod.Open); // October not promoted yet, so July is still in production
            Assert.Equal("In production", prod.Label);
        }

        [Fact]
        public void NewerPromotionEndsOlderProductionPeriod()
        {
            var today = new DateTime(2026, 11, 20);
            var july = Run("r1", "July_2026.zip", new DateTime(2026, 7, 20), new DateTime(2026, 7, 22), new DateTime(2026, 8, 5));
            var oct = Run("r2", "October_2026.zip", new DateTime(2026, 10, 18), new DateTime(2026, 10, 20), new DateTime(2026, 11, 4));
            var tl = Timeline.Build(new[] { july, oct }, 14, today);
            var old = tl.Rows.Single(r => r.RunId == "r1").Segments.Single(s => s.Kind == SegmentKind.Production);
            Assert.False(old.Open);
            Assert.Equal(new DateTime(2026, 11, 4), old.End);
            Assert.StartsWith("Replaced by October 2026", old.Label);
        }
    }
}
