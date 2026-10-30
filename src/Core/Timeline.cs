using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace StigGpoManager.Core
{
    /// <summary>The four stages an admin sees for one quarterly update.</summary>
    public enum Stage { Prepare = 0, Review = 1, Pilot = 2, Promote = 3, Done = 4 }

    public enum SegmentKind { Prepare, Pilot, Production, RolledBack }
    public enum MarkerKind { DisaRelease, ExpectedRelease, SoakEnds, Promoted, Today }

    public sealed class TimelineSegment
    {
        public SegmentKind Kind { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        /// <summary>Still running (ends today).</summary>
        public bool Open { get; set; }
        public string Label { get; set; }
    }

    public sealed class TimelineMarker
    {
        public MarkerKind Kind { get; set; }
        public DateTime Date { get; set; }
        public string Label { get; set; }
    }

    /// <summary>One quarterly update (run) on the calendar.</summary>
    public sealed class TimelineRow
    {
        public string RunId { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public bool IsActive { get; set; }
        public List<TimelineSegment> Segments { get; } = new List<TimelineSegment>();
        public List<TimelineMarker> Markers { get; } = new List<TimelineMarker>();
    }

    public sealed class QuarterTimeline
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public DateTime Today { get; set; }
        public List<TimelineRow> Rows { get; } = new List<TimelineRow>();
        /// <summary>DISA's quarterly release months in range, plus the next expected one.</summary>
        public List<TimelineMarker> Markers { get; } = new List<TimelineMarker>();
        public DateTime NextExpectedRelease { get; set; }
    }

    /// <summary>
    /// Quarter-aware view of all runs: which stage the current update is in, and a calendar of past,
    /// current and upcoming quarterly GPO updates. Pure logic (no AD), so it is unit-tested.
    /// </summary>
    public static class Timeline
    {
        /// <summary>DISA publishes the GPO package quarterly.</summary>
        public static readonly int[] ReleaseMonths = { 1, 4, 7, 10 };

        private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12).ToArray();

        /// <summary>The stage an update is in, from what the run has done so far.</summary>
        public static Stage StageOf(RunState run)
        {
            if (run == null) return Stage.Prepare;
            var items = run.Items.Where(i => i.NewGpoId != null && i.Status != ItemStatus.Failed).ToList();
            if (items.Any(i => i.Status == ItemStatus.Promoted)) return Stage.Done;
            if (items.Any(i => i.Status == ItemStatus.Piloted)) return Stage.Promote;
            bool reviewed = run.Phase == RunPhase.Reviewed || run.History.Any(h => h.Phase == RunPhase.Reviewed.ToString());
            if (reviewed && items.Count > 0 && items.All(i => i.PilotMode == PilotMode.None)) return Stage.Promote;
            if (reviewed) return Stage.Pilot;
            if (items.Count > 0) return Stage.Review;
            return Stage.Prepare;
        }

        /// <summary>"October 2026" from the package name if it names a month and year, else from the run date.</summary>
        public static string QuarterTitle(RunState run)
        {
            var d = ReleaseDateOf(run);
            return MonthNames[d.Month - 1] + " " + d.Year.ToString(CultureInfo.InvariantCulture) + " release";
        }

        /// <summary>The DISA release the run's package belongs to (month named in the package, else the release month at or before the run).</summary>
        public static DateTime ReleaseDateOf(RunState run)
        {
            var name = run?.Package?.Name ?? "";
            for (int m = 0; m < 12; m++)
            {
                var full = MonthNames[m];
                var abbr = full.Substring(0, 3);
                var match = Regex.Match(name, $@"(?i)(?<![a-z])({full}|{abbr})[^a-z0-9]?(?<y>20\d\d)");
                if (match.Success) return new DateTime(int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture), m + 1, 1);
            }
            var created = (run?.CreatedUtc ?? DateTime.UtcNow).ToLocalTime();
            return ReleaseAtOrBefore(created);
        }

        public static DateTime ReleaseAtOrBefore(DateTime d)
        {
            int month = ReleaseMonths.Where(m => m <= d.Month).DefaultIfEmpty(0).Max();
            return month == 0 ? new DateTime(d.Year - 1, ReleaseMonths.Last(), 1) : new DateTime(d.Year, month, 1);
        }

        public static DateTime NextReleaseAfter(DateTime d)
        {
            foreach (var m in ReleaseMonths)
                if (m > d.Month) return new DateTime(d.Year, m, 1);
            return new DateTime(d.Year + 1, ReleaseMonths[0], 1);
        }

        /// <summary>
        /// Builds the calendar: one row per run (newest first) with its prepare, pilot and production
        /// periods, plus DISA release markers and the next expected release.
        /// </summary>
        public static QuarterTimeline Build(IEnumerable<RunState> runs, int soakDays, DateTime today, int monthsBack = 9, int monthsForward = 5)
        {
            today = today.Date;
            var tl = new QuarterTimeline
            {
                Today = today,
                From = new DateTime(today.Year, today.Month, 1).AddMonths(-monthsBack),
                To = new DateTime(today.Year, today.Month, 1).AddMonths(monthsForward + 1).AddDays(-1),
            };
            var list = runs.OrderBy(r => r.CreatedUtc).ToList();
            var promotedRuns = list.Where(r => r.PromotedUtc.HasValue).OrderBy(r => r.PromotedUtc).ToList();

            var latestRelease = list.Count == 0 ? ReleaseAtOrBefore(today) : list.Max(ReleaseDateOf);
            tl.NextExpectedRelease = NextReleaseAfter(latestRelease > ReleaseAtOrBefore(today) ? latestRelease : ReleaseAtOrBefore(today));
            for (var d = ReleaseAtOrBefore(tl.From); d <= tl.To; d = NextReleaseAfter(d))
            {
                if (d < tl.From) continue;
                bool expected = d == tl.NextExpectedRelease;
                tl.Markers.Add(new TimelineMarker
                {
                    Kind = expected ? MarkerKind.ExpectedRelease : MarkerKind.DisaRelease, Date = d,
                    Label = expected ? "Next DISA release expected" : "DISA " + MonthNames[d.Month - 1].Substring(0, 3) + " release",
                });
            }
            tl.Markers.Add(new TimelineMarker { Kind = MarkerKind.Today, Date = today, Label = "Today" });

            foreach (var r in list.AsEnumerable().Reverse())
            {
                var row = new TimelineRow
                {
                    RunId = r.RunId, Title = QuarterTitle(r), Status = r.StatusText(soakDays, today.ToUniversalTime()),
                    IsActive = r.Phase != RunPhase.Closed && r.Phase != RunPhase.RolledBack,
                };
                var created = r.CreatedUtc.ToLocalTime().Date;
                var pilot = r.PilotStartedUtc?.ToLocalTime().Date;
                var promoted = r.PromotedUtc?.ToLocalTime().Date;
                var rolledBack = r.History.Where(h => h.Phase == RunPhase.RolledBack.ToString()).Select(h => (DateTime?)h.Utc.ToLocalTime().Date).LastOrDefault();
                var closed = r.Phase == RunPhase.Closed ? r.History.Where(h => h.Phase == RunPhase.Closed.ToString()).Select(h => (DateTime?)h.Utc.ToLocalTime().Date).LastOrDefault() : null;
                DateTime? stop = rolledBack ?? closed;

                var prepEnd = pilot ?? promoted ?? stop ?? today;
                row.Segments.Add(new TimelineSegment { Kind = SegmentKind.Prepare, Start = created, End = prepEnd, Open = !pilot.HasValue && !promoted.HasValue && !stop.HasValue, Label = "Prepare" });
                if (pilot.HasValue)
                {
                    var end = promoted ?? stop ?? today;
                    row.Segments.Add(new TimelineSegment { Kind = SegmentKind.Pilot, Start = pilot.Value, End = end, Open = !promoted.HasValue && !stop.HasValue, Label = "Pilot" });
                    if (!promoted.HasValue && soakDays > 0)
                        row.Markers.Add(new TimelineMarker { Kind = MarkerKind.SoakEnds, Date = pilot.Value.AddDays(soakDays), Label = "Soak ends" });
                }
                if (promoted.HasValue)
                {
                    var next = promotedRuns.FirstOrDefault(p => p.PromotedUtc > r.PromotedUtc);
                    var end = next?.PromotedUtc?.ToLocalTime().Date ?? rolledBack ?? today;
                    row.Segments.Add(new TimelineSegment
                    {
                        Kind = SegmentKind.Production, Start = promoted.Value, End = end, Open = next == null && !rolledBack.HasValue,
                        Label = next == null ? "In production" : "Replaced by " + QuarterTitle(next).Replace(" release", ""),
                    });
                    row.Markers.Add(new TimelineMarker { Kind = MarkerKind.Promoted, Date = promoted.Value, Label = "Promoted" });
                }
                if (rolledBack.HasValue)
                    row.Segments.Add(new TimelineSegment { Kind = SegmentKind.RolledBack, Start = rolledBack.Value, End = rolledBack.Value, Label = "Rolled back" });
                tl.Rows.Add(row);
            }
            return tl;
        }
    }
}
