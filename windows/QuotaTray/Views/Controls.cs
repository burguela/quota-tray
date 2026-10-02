using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using QuotaTray.Services;

namespace QuotaTray.Views;

/// <summary>
/// The Mac meter: a full-width capsule track with a leading capsule fill in the severity color, and a
/// thin even-pace tick that pokes out above and below the bar.
/// </summary>
public sealed class MeterBar : FrameworkElement
{
    private const double BarHeight = 5;
    private const double TickWidth = 2;
    private const double TickOverhang = 4;
    private readonly double _fraction;
    private readonly bool _hasData;
    private readonly double? _tick;
    private readonly Brush _fill;

    public MeterBar(double fraction, bool hasData, double? tick, Brush fill)
    {
        _fraction = Math.Clamp(fraction, 0, 1);
        _hasData = hasData;
        _tick = tick;
        _fill = fill;
        Height = BarHeight;
    }

    protected override void OnRender(DrawingContext context)
    {
        var theme = Theme.Current;
        var width = ActualWidth;
        var radius = BarHeight / 2;
        context.DrawRoundedRectangle(theme.Track, null, new Rect(0, 0, width, BarHeight), radius, radius);
        if (_hasData && _fraction > 0)
        {
            // Any non-zero value shows at least a full dot, so 1–2% never vanishes.
            var fillWidth = Math.Max(BarHeight, width * _fraction);
            context.DrawRoundedRectangle(_fill, null, new Rect(0, 0, fillWidth, BarHeight), radius, radius);
        }
        if (_tick is double tick)
        {
            var x = Math.Clamp(width * tick - TickWidth / 2, 0, Math.Max(width - TickWidth, 0));
            context.DrawRoundedRectangle(theme.Tick, null,
                new Rect(x, -TickOverhang / 2, TickWidth, BarHeight + TickOverhang), 1, 1);
        }
    }
}

/// <summary>The Usage Trend sparkline: one blue bar per day, 1px apart, bottom-aligned.</summary>
public sealed class TrendBars : FrameworkElement
{
    private const double ChartHeight = 18;
    private readonly double[] _values;

    public TrendBars(double[] values)
    {
        _values = values;
        Height = ChartHeight;
        MinWidth = 90;
        MaxWidth = 150;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? MaxWidth : Math.Clamp(availableSize.Width, MinWidth, MaxWidth), ChartHeight);

    protected override void OnRender(DrawingContext context)
    {
        if (_values.Length == 0)
        {
            return;
        }
        var max = 1.0;
        foreach (var value in _values)
        {
            max = Math.Max(max, value);
        }
        const double spacing = 1;
        var barWidth = Math.Max(1, (ActualWidth - spacing * (_values.Length - 1)) / _values.Length);
        var blue = Theme.Current.Blue;
        for (var index = 0; index < _values.Length; index++)
        {
            var value = _values[index];
            var height = value > 0 ? Math.Max(ChartHeight * 0.18, ChartHeight * Math.Min(1, value / max)) : 2;
            var x = index * (barWidth + spacing);
            context.DrawRoundedRectangle(blue, null, new Rect(x, ChartHeight - height, barWidth, height), 1, 1);
        }
    }
}

/// <summary>
/// The Total Spend donut: one sector per provider, clockwise from noon, with thin gaps between
/// sectors and a minimum visible share so tiny spenders still show.
/// </summary>
public sealed class SpendRing : FrameworkElement
{
    private const double InnerRatio = 0.618;
    private const double Gap = 1.6;
    private const double MinimumShare = 0.025;
    private readonly IReadOnlyList<(double Amount, Brush Fill)> _slices;

    public SpendRing(IReadOnlyList<(double Amount, Brush Fill)> slices, double diameter)
    {
        _slices = slices;
        Width = diameter;
        Height = diameter;
    }

    protected override void OnRender(DrawingContext context)
    {
        var total = 0.0;
        foreach (var slice in _slices)
        {
            total += slice.Amount;
        }
        if (total <= 0)
        {
            return;
        }
        var shares = new double[_slices.Count];
        var sum = 0.0;
        for (var index = 0; index < _slices.Count; index++)
        {
            shares[index] = Math.Max(_slices[index].Amount / total, MinimumShare);
            sum += shares[index];
        }
        var outer = Math.Min(ActualWidth, ActualHeight) / 2;
        var inner = outer * InnerRatio;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var halfGap = _slices.Count > 1 ? Gap / outer / 2 : 0;
        var cursor = 0.0;
        for (var index = 0; index < _slices.Count; index++)
        {
            var width = shares[index] / sum;
            var a0 = -Math.PI / 2 + cursor * 2 * Math.PI + halfGap;
            var a1 = -Math.PI / 2 + (cursor + width) * 2 * Math.PI - halfGap;
            cursor += width;
            if (a1 - a0 < 0.001)
            {
                continue;
            }
            context.DrawGeometry(_slices[index].Fill, null, Sector(center, inner, outer, a0, a1));
        }
    }

    private static Geometry Sector(Point center, double inner, double outer, double a0, double a1)
    {
        // A full circle can't be one arc segment; split it so a single provider draws a whole ring.
        if (a1 - a0 >= 2 * Math.PI - 0.0001)
        {
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(center, outer, outer));
            ring.Children.Add(new EllipseGeometry(center, inner, inner));
            return ring;
        }
        Point At(double radius, double angle) => new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        var large = a1 - a0 > Math.PI;
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(At(outer, a0), true, true);
            sink.ArcTo(At(outer, a1), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, true);
            sink.LineTo(At(inner, a1), true, true);
            sink.ArcTo(At(inner, a0), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>The small Mac switch used on settings rows: a capsule with a white knob, blue when on.</summary>
public sealed class ToggleSwitch : FrameworkElement
{
    private const double SwitchWidth = 28;
    private const double SwitchHeight = 16;
    private readonly Action<bool> _onChange;
    private bool _isOn;

    public ToggleSwitch(bool isOn, Action<bool> onChange)
    {
        _isOn = isOn;
        _onChange = onChange;
        Width = SwitchWidth;
        Height = SwitchHeight;
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = PopupWindow.FocusRing(SwitchHeight / 2);
        VerticalAlignment = VerticalAlignment.Center;
    }

    public bool IsOn => _isOn;

    protected override AutomationPeer OnCreateAutomationPeer() => new ToggleSwitchPeer(this);

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Flip();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            e.Handled = true;
            Flip();
        }
    }

    /// <summary>Flips the switch, as a click, Space/Enter, a click on its row, or a screen reader does.</summary>
    internal void Flip()
    {
        _isOn = !_isOn;
        InvalidateVisual();
        (UIElementAutomationPeer.FromElement(this) as ToggleSwitchPeer)?.RaiseToggled(!_isOn, _isOn);
        _onChange(_isOn);
    }

    protected override void OnRender(DrawingContext context)
    {
        var theme = Theme.Current;
        var radius = SwitchHeight / 2;
        context.DrawRoundedRectangle(_isOn ? theme.Blue : theme.SwitchOff, null,
            new Rect(0, 0, SwitchWidth, SwitchHeight), radius, radius);
        var knob = SwitchHeight - 3;
        var x = _isOn ? SwitchWidth - knob - 1.5 : 1.5;
        var shadow = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
        context.DrawEllipse(shadow, null, new Point(x + knob / 2, 1.5 + knob / 2 + 0.5), knob / 2 + 0.3, knob / 2 + 0.3);
        context.DrawEllipse(Brushes.White, null, new Point(x + knob / 2, 1.5 + knob / 2), knob / 2, knob / 2);
    }
}

/// <summary>Small vector glyphs standing in for the SF Symbols the Mac panel uses.</summary>
public static class Glyphs
{
    public static readonly Geometry ChevronDown = Parse("M1,3.5 L6,8.5 L11,3.5");
    public static readonly Geometry ChevronUp = Parse("M1,8.5 L6,3.5 L11,8.5");
    public static readonly Geometry ArrowUp = Parse("M6,10.5 L6,1.5 M2,5.5 L6,1.5 L10,5.5");
    public static readonly Geometry ChevronLeft = Parse("M8,1 L3,6 L8,11");
    public static readonly Geometry ChevronUpDown = Parse("M2.5,4.5 L6,1.5 L9.5,4.5 M2.5,7.5 L6,10.5 L9.5,7.5");
    public static readonly Geometry ArrowUpRight = Parse("M3,9 L9,3 M4,3 L9,3 L9,8");
    public static readonly Geometry Info = Parse("M6,0.75 A5.25,5.25 0 1 1 5.99,0.75 Z M6,5.3 L6,8.9 M6,3.4 L6,3.5");
    /// <summary>A filled flame (SF Symbols' flame.fill).</summary>
    public static readonly Geometry Flame = Parse(
        "F1 M5.2,0 C5.6,2.2 9,3.9 9,7.6 C9,10.1 7.2,12 4.9,12 C2.5,12 0.8,10.2 0.8,7.9 C0.8,6.1 1.8,4.9 2.6,4.1 " +
        "C2.7,5.4 3.2,6.2 3.9,6.5 C3.6,4.4 4.3,2.2 5.2,0 Z");
    /// <summary>A filled warning triangle with its exclamation mark cut out.</summary>
    public static readonly Geometry Warning = Parse(
        "F0 M6,0.6 C6.4,0.6 6.7,0.8 6.9,1.2 L11.8,10.3 C12.2,11 11.7,11.8 10.9,11.8 L1.1,11.8 " +
        "C0.3,11.8 -0.2,11 0.2,10.3 L5.1,1.2 C5.3,0.8 5.6,0.6 6,0.6 Z M5.3,4.3 L5.5,8 L6.5,8 L6.7,4.3 Z " +
        "M6,8.7 A0.75,0.75 0 1 0 6.01,8.7 Z");

    private static Geometry Parse(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
