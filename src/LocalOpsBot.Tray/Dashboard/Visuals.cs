using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LocalOpsBot.Tray.Dashboard;

/// <summary>
/// The device page's palette: the phone app's light "console" roles (mobile ui/Theme.kt) — a pale field,
/// white plates and an always-dark carbon instrument slab — so the PC and the phone read as one product.
/// </summary>
internal static class Hb
{
    public static readonly Brush Field = B(0xE3, 0xE7, 0xF3);
    public static readonly Brush Plate = B(0xFF, 0xFF, 0xFF);
    public static readonly Brush PlateHover = B(0xF2, 0xF4, 0xFA);
    public static readonly Brush Hairline = B(0xC9, 0xCF, 0xE3);
    public static readonly Brush Ink = B(0x21, 0x24, 0x2E);
    public static readonly Brush InkSoft = B(0x4A, 0x51, 0x80);
    public static readonly Brush Slab = B(0x21, 0x24, 0x2E);
    public static readonly Brush SlabRaised = B(0x2E, 0x32, 0x40);
    public static readonly Brush OnSlab = B(0xFF, 0xFF, 0xFF);
    public static readonly Brush OnSlabSoft = B(0xB4, 0xBE, 0xE2);
    public static readonly Brush MeterOk = B(0x8B, 0xA1, 0xD4);
    public static readonly Brush MeterOff = B(0x35, 0x3A, 0x4A);
    public static readonly Brush LedOn = B(0x43, 0xC4, 0x63);
    public static readonly Brush LedOff = B(0xFF, 0x4D, 0x4D);
    public static readonly Brush Idle = B(0x8A, 0x90, 0xA8);
    public static readonly Brush Accent = B(0xEC, 0xAB, 0x37);
    public static readonly Brush Teal = B(0x6F, 0xD3, 0xC1);
    public static readonly Brush Indigo = B(0x3D, 0x4F, 0x97);
    public static readonly Brush Danger = B(0xC4, 0x00, 0x10);
    public static readonly Brush WarnField = B(0xFF, 0xF4, 0xDD);
    public static readonly Brush Selected = B(0x21, 0x24, 0x2E);

    public static readonly FontFamily Ui = new("Segoe UI, Malgun Gothic");
    public static readonly FontFamily Display = new("Segoe UI Black, Arial Black, Malgun Gothic");

    /// <summary>Load colour: cool by default, amber from 75 %, red from 90 % (the phone's thresholds).</summary>
    public static Brush Load(double pct) => pct >= 90 ? LedOff : pct >= 75 ? Accent : MeterOk;

    /// <summary>Battery colour: red below 15 %, amber below 30 %, green otherwise.</summary>
    public static Brush Battery(int pct) => pct < 15 ? LedOff : pct < 30 ? Accent : LedOn;

    public static Brush WithAlpha(Brush b, byte alpha)
    {
        var c = ((SolidColorBrush)b).Color;
        var x = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        x.Freeze();
        return x;
    }

    private static Brush B(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }

    // ── Small element factories, so the detail builders read like layout ──

    public static TextBlock Text(string text, double size = 12, Brush? fg = null, FontWeight? weight = null, bool wrap = false) => new()
    {
        Text = text,
        FontFamily = Ui,
        FontSize = size,
        Foreground = fg ?? Ink,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    /// <summary>Small bold label (a slab cell's caption, a section heading).</summary>
    public static TextBlock Label(string text, Brush? fg = null, double size = 11) =>
        Text(text, size, fg ?? InkSoft, FontWeights.SemiBold);

    /// <summary>An instrument readout: bold, tabular digits so values don't jitter every second.</summary>
    public static TextBlock Readout(string text, double size = 14, Brush? fg = null)
    {
        var t = Text(text, size, fg ?? OnSlab, FontWeights.Bold);
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    public static Ellipse Led(Brush fill, double size = 8) => new()
    {
        Width = size,
        Height = size,
        Fill = fill,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Card(UIElement child, Thickness? padding = null, Brush? background = null) => new()
    {
        Background = background ?? Plate,
        BorderBrush = Hairline,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(14, 12, 14, 12),
        Child = child,
    };

    public static Border SlabPanel(UIElement child) => new()
    {
        Background = Slab,
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(18, 16, 18, 16),
        Child = child,
    };

    public static Border Cell(UIElement child) => new()
    {
        Background = SlabRaised,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8, 10, 8),
        Child = child,
    };

    /// <summary>A rounded pill (status chips, kind badges).</summary>
    public static Border Pill(string text, Brush background, Brush foreground, double size = 10.5) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 2, 8, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, size, foreground, FontWeights.SemiBold),
    };

    public static Grid Columns(params GridLength[] widths)
    {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }

    public static T At<T>(T element, int column, int row = 0) where T : UIElement
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        return element;
    }

    public static GridLength Star(double n = 1) => new(n, GridUnitType.Star);
    public static GridLength Px(double n) => new(n);
    public static GridLength Auto => GridLength.Auto;

    /// <summary>Monitor and phone outlines for the device list and page header.</summary>
    public static Path Glyph(bool pc, Brush stroke, double size = 18) => new()
    {
        Data = Geometry.Parse(pc
            ? "M2,3 H22 V16 H2 Z M9,20 H15 M12,16 V20"
            : "M7,2 H17 A2,2 0 0 1 19,4 V20 A2,2 0 0 1 17,22 H7 A2,2 0 0 1 5,20 V4 A2,2 0 0 1 7,2 Z M11,18.5 H13"),
        Stroke = stroke,
        StrokeThickness = 1.8,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Stretch = Stretch.Uniform,
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>A rolling trace of the last <see cref="Capacity"/> values (the phone's 60-second strips).</summary>
internal sealed class TraceView : FrameworkElement
{
    private IReadOnlyList<double> _values = Array.Empty<double>();

    public Brush Stroke { get; set; } = Hb.MeterOk;
    public int Capacity { get; set; } = 60;

    /// <summary>Fixed top of the scale (100 for percentages); NaN scales to the largest value shown.</summary>
    public double Max { get; set; } = double.NaN;

    public void SetValues(IReadOnlyList<double> values)
    {
        _values = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_values.Count < 2) return;
        var max = double.IsNaN(Max) ? Math.Max(_values.Max(), 1) : Max;
        var step = w / (Capacity - 1);
        var start = w - step * (_values.Count - 1);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            Point P(int i) => new(start + i * step, h - 1 - Math.Clamp(_values[i] / max, 0, 1) * (h - 2));
            l.BeginFigure(P(0), false, false);
            a.BeginFigure(new Point(start, h), true, true);
            a.LineTo(P(0), false, false);
            for (var i = 1; i < _values.Count; i++)
            {
                l.LineTo(P(i), true, true);
                a.LineTo(P(i), false, false);
            }
            a.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(Hb.WithAlpha(Stroke, 0x38), null, area);
        var pen = new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>A 270° arc gauge (CPU, RAM, GPU, battery) with its value in the middle.</summary>
internal sealed class RingGauge : FrameworkElement
{
    private double _value;
    private Brush _brush = Hb.MeterOk;

    public double Thickness { get; set; } = 9;

    public void Set(double pct, Brush brush)
    {
        _value = Math.Clamp(pct, 0, 100);
        _brush = brush;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 - Thickness / 2;
        DrawArc(dc, c, r, 135, 270, Hb.MeterOff);
        if (_value > 0.5) DrawArc(dc, c, r, 135, 270 * _value / 100, _brush);
    }

    private void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Brush brush)
    {
        static Point On(Point c, double r, double deg) =>
            new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(On(c, r, startDeg), false, false);
            ctx.ArcTo(On(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>A flat horizontal meter (drives, memory, storage).</summary>
internal sealed class MeterBar : FrameworkElement
{
    private double _value;
    private Brush _brush = Hb.MeterOk;

    public Brush Track { get; set; } = Hb.MeterOff;

    public MeterBar()
    {
        Height = 8;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public void Set(double pct, Brush brush)
    {
        _value = Math.Clamp(pct, 0, 100);
        _brush = brush;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var radius = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);
        var fill = w * _value / 100;
        if (fill >= 1) dc.DrawRoundedRectangle(_brush, null, new Rect(0, 0, Math.Max(fill, h), h), radius, radius);
    }
}

/// <summary>Number formatting shared by the device page (Korean-first, like the phone app's Fmt).</summary>
internal static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Pct(double v) => v.ToString(v < 10 ? "0.0" : "0", Inv) + "%";

    public static string Gb(long bytes) => (bytes / 1073741824.0).ToString(bytes >= 100L * 1073741824 ? "0" : "0.0", Inv) + " GB";

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1073741824 => (bytes / 1073741824.0).ToString("0.0", Inv) + " GB",
        >= 1048576 => (bytes / 1048576.0).ToString(bytes >= 100 * 1048576L ? "0" : "0.0", Inv) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0", Inv) + " KB",
        _ => bytes.ToString(Inv) + " B",
    };

    public static string Rate(long bps) => bps switch
    {
        >= 1048576 => (bps / 1048576.0).ToString(bps >= 100 * 1048576L ? "0" : "0.0", Inv) + " MB/s",
        >= 1024 => (bps / 1024.0).ToString("0", Inv) + " KB/s",
        _ => bps.ToString(Inv) + " B/s",
    };

    public static string Ms(double ms) => ms.ToString(ms < 10 ? "0.0" : "0", Inv) + " ms";

    public static string Temp(double c) => c.ToString("0", Inv) + "°C";

    public static string Uptime(long sec)
    {
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalDays >= 1 ? L.DetUptimeDays((int)t.TotalDays, t.Hours) : L.DetUptimeHours((int)t.TotalHours, t.Minutes);
    }

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var t = now - at;
        return t.TotalSeconds < 45 ? L.DevJustNow
            : t.TotalHours < 1 ? L.DevMinutes(Math.Max(1, (int)t.TotalMinutes))
            : t.TotalDays < 1 ? L.DevHours((int)t.TotalHours)
            : L.DevDays((int)t.TotalDays);
    }

    public static string Duration(long sec)
    {
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1 ? L.DetHoursMinutes((int)t.TotalHours, t.Minutes) : L.DetMinutesOnly(Math.Max(1, t.Minutes));
    }
}
