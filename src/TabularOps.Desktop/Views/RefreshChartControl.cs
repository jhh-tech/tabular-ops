using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using TabularOps.Desktop.ViewModels;

namespace TabularOps.Desktop.Views;

/// <summary>
/// Scatter chart: time-of-day (X axis) vs. duration in seconds (Y axis).
/// Each dot is one refresh run. Drawn directly via DrawingContext — no charting library.
/// </summary>
public sealed class RefreshChartControl : FrameworkElement
{
    // Colours matching Colors.xaml
    private static readonly Color BgColor       = Color.FromRgb(0x11, 0x15, 0x1A);
    private static readonly Color GridColor      = Color.FromRgb(0x2A, 0x31, 0x3B);
    private static readonly Color LabelColor     = Color.FromRgb(0x4A, 0x53, 0x60);
    private static readonly Color CompletedColor = Color.FromRgb(0x4E, 0xC9, 0xB0);
    private static readonly Color FailedColor    = Color.FromRgb(0xE0, 0x6C, 0x75);
    private static readonly Color CancelledColor = Color.FromRgb(0xE4, 0xA8, 0x53);

    private static readonly Typeface LabelTypeface = new("Consolas");
    private const double FontSizePt = 9;
    private const double DotRadius  = 3.5;
    private const double PadL = 42, PadR = 12, PadT = 8, PadB = 20;

    // ── Dependency property ──────────────────────────────────────────────────

    public static readonly DependencyProperty RunsProperty =
        DependencyProperty.Register(
            nameof(Runs),
            typeof(IEnumerable<RefreshRunViewModel>),
            typeof(RefreshChartControl),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((RefreshChartControl)d).OnRunsChanged()));

    public IEnumerable<RefreshRunViewModel>? Runs
    {
        get => (IEnumerable<RefreshRunViewModel>?)GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    private INotifyCollectionChanged? _subscribedCollection;

    private void OnRunsChanged()
    {
        if (_subscribedCollection is not null)
            _subscribedCollection.CollectionChanged -= OnCollectionChanged;

        _subscribedCollection = Runs as INotifyCollectionChanged;
        if (_subscribedCollection is not null)
            _subscribedCollection.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    // ── Rendering ───────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double plotW = w - PadL - PadR;
        double plotH = h - PadT - PadB;
        if (plotW <= 0 || plotH <= 0) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(new SolidColorBrush(BgColor), null, new Rect(0, 0, w, h));

        var runs = Runs?
            .Where(r => r.DurationSeconds.HasValue)
            .ToList()
            ?? [];

        double maxDur  = runs.Count > 0 ? runs.Max(r => r.DurationSeconds!.Value) : 60;
        double niceMax = NiceMax(maxDur);

        var gridPen  = new Pen(new SolidColorBrush(GridColor) { Opacity = 0.6 }, 1);
        var axispen  = new Pen(new SolidColorBrush(GridColor), 1);
        var lblBrush = new SolidColorBrush(LabelColor);

        // Y-axis: 5 gridlines
        const int yTicks = 4;
        for (int i = 0; i <= yTicks; i++)
        {
            double frac   = (double)i / yTicks;
            double durVal = frac * niceMax;
            double y      = PadT + plotH - frac * plotH;

            dc.DrawLine(i == 0 ? axispen : gridPen,
                new Point(PadL, y), new Point(PadL + plotW, y));

            string label = durVal < 60 ? $"{durVal:F0}s" : $"{durVal / 60:F0}m";
            var ft = MakeText(label, dpi, lblBrush);
            dc.DrawText(ft, new Point(PadL - ft.Width - 4, y - ft.Height / 2));
        }

        // X-axis: every 6 hours
        for (int hour = 0; hour <= 24; hour += 6)
        {
            double x = PadL + (hour / 24.0) * plotW;
            dc.DrawLine(hour == 0 ? axispen : gridPen,
                new Point(x, PadT), new Point(x, PadT + plotH));

            var ft = MakeText($"{hour:00}", dpi, lblBrush);
            dc.DrawText(ft, new Point(x - ft.Width / 2, PadT + plotH + 3));
        }

        // Scatter dots
        foreach (var run in runs)
        {
            double x = PadL + (run.StartedAtHour / 24.0) * plotW;
            double y = PadT + plotH - (run.DurationSeconds!.Value / niceMax) * plotH;
            y = Math.Clamp(y, PadT + DotRadius, PadT + plotH - DotRadius);

            var color = run.IsCompleted ? CompletedColor
                      : run.IsFailed   ? FailedColor
                      : run.IsCancelled ? CancelledColor
                      : LabelColor;

            dc.DrawEllipse(new SolidColorBrush(color), null,
                new Point(x, y), DotRadius, DotRadius);
        }
    }

    private static double NiceMax(double value)
    {
        if (value <= 30)   return 30;
        if (value <= 60)   return 60;
        if (value <= 120)  return 120;
        if (value <= 300)  return 300;
        if (value <= 600)  return 600;
        if (value <= 1800) return 1800;
        return Math.Ceiling(value / 3600.0) * 3600;
    }

    private static FormattedText MakeText(string text, double dpi, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, FontSizePt, brush, dpi);
}
