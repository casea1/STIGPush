using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StigGpoManager.Core;

namespace StigGpoManager
{
    /// <summary>
    /// Calendar of quarterly GPO updates: months across the top, DISA release markers, a "today" line,
    /// and one row per update with its prepare / pilot / production periods. Click a row to open it.
    /// </summary>
    public sealed class TimelineView : FrameworkElement
    {
        private const double LabelWidth = 190, HeaderHeight = 40, RowHeight = 38, BarHeight = 16, FooterHeight = 26;
        private QuarterTimeline _tl;

        public event Action<string> RowClicked;

        public QuarterTimeline Timeline
        {
            get => _tl;
            set { _tl = value; InvalidateMeasure(); InvalidateVisual(); }
        }

        private static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));
        private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
        private static readonly Brush GridLine = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF4)));
        private static readonly Brush QuarterBand = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)));
        private static readonly Brush PrepareBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
        private static readonly Brush PilotBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
        private static readonly Brush ProdBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)));
        private static readonly Brush ProdOldBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC)));
        private static readonly Brush RollbackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));
        private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
        private static readonly Brush ActiveRow = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF)));
        private static readonly Pen GridPen = Freeze(new Pen(GridLine, 1));
        private static readonly Pen ReleasePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD))), 1.5) { DashStyle = DashStyles.Dash });
        private static readonly Pen TodayPen = Freeze(new Pen(Accent, 2));

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        public TimelineView()
        {
            Cursor = Cursors.Hand;
            System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 200);
        }

        protected override Size MeasureOverride(Size available)
        {
            int rows = Math.Max(1, _tl?.Rows.Count ?? 0);
            double w = double.IsInfinity(available.Width) ? 900 : available.Width;
            return new Size(w, HeaderHeight + rows * RowHeight + FooterHeight);
        }

        private double X(DateTime d, double width)
        {
            var span = (_tl.To - _tl.From).TotalDays;
            return LabelWidth + (d - _tl.From).TotalDays / span * (width - LabelWidth - 8);
        }

        private static FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
            new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                              new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                              size, brush, 1.0);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            if (_tl == null) return;
            // Grid, bands and lines stop at the bottom of the rows; the footer (legend, "Today") stays clean.
            double gridBottom = HeaderHeight + Math.Max(1, _tl.Rows.Count) * RowHeight;

            // month columns, quarter bands
            for (var m = new DateTime(_tl.From.Year, _tl.From.Month, 1); m <= _tl.To; m = m.AddMonths(1))
            {
                double x0 = X(m, w), x1 = X(m.AddMonths(1), w);
                if ((m.Month - 1) / 3 % 2 == 0) dc.DrawRectangle(QuarterBand, null, new Rect(x0, HeaderHeight - 6, Math.Max(0, x1 - x0), gridBottom - HeaderHeight + 6));
                dc.DrawLine(GridPen, new Point(x0, HeaderHeight - 6), new Point(x0, gridBottom));
                var label = m.ToString(m.Month == 1 || m == new DateTime(_tl.From.Year, _tl.From.Month, 1) ? "MMM yyyy" : "MMM", CultureInfo.CurrentUICulture);
                var ft = Text(label, 11, Muted);
                dc.DrawText(ft, new Point(x0 + 4, 4));
            }

            // release markers
            foreach (var mk in _tl.Markers.Where(m => m.Kind == MarkerKind.DisaRelease || m.Kind == MarkerKind.ExpectedRelease))
            {
                double x = X(mk.Date, w);
                dc.DrawLine(ReleasePen, new Point(x, HeaderHeight - 8), new Point(x, gridBottom));
                var ft = Text(mk.Kind == MarkerKind.ExpectedRelease ? "◆ next release" : "◆ DISA", 10, mk.Kind == MarkerKind.ExpectedRelease ? Accent : Muted, mk.Kind == MarkerKind.ExpectedRelease);
                dc.DrawText(ft, new Point(x + 3, 21));
            }

            if (_tl.Rows.Count == 0)
            {
                var ft = Text("No quarterly updates yet. Click \"New quarterly update\" to start the first one.", 13, Muted);
                dc.DrawText(ft, new Point(LabelWidth, HeaderHeight + 10));
            }

            for (int i = 0; i < _tl.Rows.Count; i++)
            {
                var row = _tl.Rows[i];
                double y = HeaderHeight + i * RowHeight;
                if (row.IsActive) dc.DrawRectangle(ActiveRow, null, new Rect(0, y, w, RowHeight));
                var title = Text(row.Title.Replace(" release", ""), 13, Ink, row.IsActive);
                dc.DrawText(title, new Point(10, y + 3));
                var status = Text(Trim(row.Status, 30), 10.5, row.IsActive ? Accent : Muted);
                dc.DrawText(status, new Point(10, y + 20));

                double barY = y + (RowHeight - BarHeight) / 2;
                foreach (var s in row.Segments)
                {
                    double x0 = Math.Max(LabelWidth, X(s.Start, w)), x1 = Math.Min(w - 8, X(s.End.AddDays(1), w));
                    if (s.Kind == SegmentKind.RolledBack)
                    {
                        double x = X(s.Start, w);
                        dc.DrawEllipse(RollbackBrush, null, new Point(x, barY + BarHeight / 2), 6, 6);
                        dc.DrawText(Text("rolled back", 10, RollbackBrush), new Point(x + 8, barY));
                        continue;
                    }
                    if (x1 - x0 < 8) x1 = x0 + 8; // a period of a day or two is still visible
                    var brush = s.Kind == SegmentKind.Prepare ? PrepareBrush : s.Kind == SegmentKind.Pilot ? PilotBrush : s.Open ? ProdBrush : ProdOldBrush;
                    dc.DrawRoundedRectangle(brush, null, new Rect(x0, barY, x1 - x0, BarHeight), 4, 4);
                    var lab = Text(s.Label, 10.5, Brushes.White, true);
                    if (lab.Width + 10 < x1 - x0) dc.DrawText(lab, new Point(x0 + 6, barY + 1));
                    else if (s.Open && s.Kind == SegmentKind.Production) // a pilot row already has its "soak ends" label there
                    {
                        // Too short for its label (e.g. promoted today): say it beside the bar, clear of the today line.
                        var outside = Text(s.Label + " since " + s.Start.ToString("d MMM", CultureInfo.CurrentUICulture), 10.5, brush == ProdBrush ? ProdBrush : Muted, true);
                        dc.DrawText(outside, new Point(Math.Min(x1 + 8, w - outside.Width - 4), barY + 1));
                    }
                }
                foreach (var mk in row.Markers.Where(m => m.Kind == MarkerKind.SoakEnds))
                {
                    double x = X(mk.Date, w);
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(x, barY - 3), true, true);
                        c.LineTo(new Point(x + 6, barY + BarHeight / 2), true, false);
                        c.LineTo(new Point(x, barY + BarHeight + 3), true, false);
                        c.LineTo(new Point(x - 6, barY + BarHeight / 2), true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(Brushes.White, new Pen(PilotBrush, 2), g);
                    dc.DrawText(Text("soak ends " + mk.Date.ToString("d MMM", CultureInfo.CurrentUICulture), 10, Muted), new Point(x + 8, barY + 1));
                }
            }

            // today line
            double tx = X(_tl.Today, w);
            dc.DrawLine(TodayPen, new Point(tx, HeaderHeight - 8), new Point(tx, gridBottom + 2));
            var today = Text("Today", 10, Accent, true);
            dc.DrawText(today, new Point(tx - today.Width / 2, gridBottom + 5));

            // legend
            double lx = 10;
            foreach (var (b, t) in new[] { (PrepareBrush, "Prepare"), (PilotBrush, "Pilot"), (ProdBrush, "In production"), (ProdOldBrush, "Replaced") })
            {
                dc.DrawRoundedRectangle(b, null, new Rect(lx, gridBottom + 9, 12, 8), 2, 2);
                var ft = Text(t, 10, Muted);
                dc.DrawText(ft, new Point(lx + 16, gridBottom + 5));
                lx += ft.Width + 34;
            }
        }

        private static string Trim(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s ?? "" : s.Substring(0, n - 1) + "…";

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_tl == null) return;
            int i = (int)((e.GetPosition(this).Y - HeaderHeight) / RowHeight);
            if (i >= 0 && i < _tl.Rows.Count) RowClicked?.Invoke(_tl.Rows[i].RunId);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_tl == null) return;
            int i = (int)((e.GetPosition(this).Y - HeaderHeight) / RowHeight);
            ToolTip = i >= 0 && i < _tl.Rows.Count
                ? $"{_tl.Rows[i].Title}: {_tl.Rows[i].Status}\nClick to open this update."
                : $"Next DISA release expected {_tl.NextExpectedRelease:MMMM yyyy}.";
        }
    }
}
