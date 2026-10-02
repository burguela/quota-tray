using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuotaTray.Engine;
using QuotaTray.Services;

namespace QuotaTray.Tray;

/// <summary>One provider's segment of the strip: its mark and its one or two pinned values.</summary>
public sealed record StripGroup(string ProviderId, string DisplayName, IReadOnlyList<RowInfo> Metrics);

/// <summary>
/// The taskbar strip's artwork, a port of the Mac menu-bar Text strip (MenuBarStripRenderer.swift):
/// per pinned provider, its mark followed by its values with no labels, one value as a single bold
/// number and two stacked on tight lines, all in the taskbar's text color. Sized up from the Mac's
/// 22-point menu bar to Windows' taller taskbar, where the clock's two 12-pixel lines set the scale.
/// </summary>
public static class TaskbarStripView
{
    /// <summary>At most two pins per provider render, as on the Mac.</summary>
    private const int MetricsPerProvider = 2;
    private const double GlyphSide = 20;
    private const double SingleValueSize = 15;
    private const double StackedValueSize = 12;
    private const double GroupSpacing = 14;
    private const double GlyphSpacing = 5;

    /// <summary>
    /// Enabled providers with pinned values that have data, in dashboard order. A provider whose pins
    /// all lack data drops out, so the strip never shows placeholders.
    /// </summary>
    public static IReadOnlyList<StripGroup> Groups(Dashboard? dashboard) =>
        dashboard == null
            ? Array.Empty<StripGroup>()
            : dashboard.Providers
                .Where(p => p.Enabled)
                .Select(p => new StripGroup(p.Id, p.DisplayName, p.Rows
                    .Where(r => r.Pinned && r.HasData && r.CompactValue.Length > 0 && r.CompactValue != "—")
                    .Take(MetricsPerProvider)
                    .ToList()))
                .Where(g => g.Metrics.Count > 0)
                .ToList();

    /// <summary>
    /// The strip content, or the app icon alone when nothing has data (as on the Mac). With
    /// <paramref name="updateAvailable"/>, a small blue up-arrow badge sits at the end.
    /// </summary>
    public static FrameworkElement Build(IReadOnlyList<StripGroup> groups, Color foreground, bool updateAvailable = false)
    {
        var brush = new SolidColorBrush(foreground);
        brush.Freeze();
        var strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        strip.SetValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));
        strip.SetValue(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        TextOptions.SetTextFormattingMode(strip, TextFormattingMode.Display);

        if (groups.Count == 0)
        {
            strip.Children.Add(new Image { Source = AppIconImage.Value, Width = GlyphSide, Height = GlyphSide });
            AddUpdateBadge(strip, updateAvailable);
            return strip;
        }
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var segment = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(i == 0 ? 0 : GroupSpacing, 0, 0, 0),
            };
            segment.Children.Add(Glyph(group.ProviderId, brush));
            segment.Children.Add(Values(group.Metrics, brush));
            strip.Children.Add(segment);
        }
        AddUpdateBadge(strip, updateAvailable);
        return strip;
    }

    /// <summary>A blue disc with a white up-arrow: "a new version is ready", readable on light and dark taskbars.</summary>
    private static void AddUpdateBadge(StackPanel strip, bool updateAvailable)
    {
        if (!updateAvailable)
        {
            return;
        }
        var badge = new Grid
        {
            Width = GlyphSide - 2,
            Height = GlyphSide - 2,
            Margin = new Thickness(strip.Children.Count == 0 ? 0 : GroupSpacing, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x2F, 0x80, 0xED)) });
        badge.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M4,8 L4,0.8 M0.8,3.8 L4,0.8 L7.2,3.8"),
            Stroke = Brushes.White,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 8,
            Height = 9,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        strip.Children.Add(badge);
    }

    private static FrameworkElement Glyph(string providerId, Brush brush)
    {
        FrameworkElement glyph = ProviderMarks.For(providerId) is Geometry mark
            ? new System.Windows.Shapes.Path { Data = mark, Fill = brush, Stretch = Stretch.Uniform, Width = GlyphSide - 2, Height = GlyphSide - 2 }
            : new System.Windows.Shapes.Ellipse { Fill = brush, Width = GlyphSide - 3, Height = GlyphSide - 3 };
        glyph.Margin = new Thickness(0, 0, GlyphSpacing, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }

    /// <summary>One value as a single large number; two stacked on tight lines, read positionally.</summary>
    private static FrameworkElement Values(IReadOnlyList<RowInfo> metrics, Brush brush)
    {
        if (metrics.Count == 1)
        {
            return Value(metrics[0].CompactValue, brush, SingleValueSize, FontWeights.Bold);
        }
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var metric in metrics)
        {
            var line = Value(metric.CompactValue, brush, StackedValueSize, FontWeights.SemiBold);
            line.HorizontalAlignment = HorizontalAlignment.Right;
            line.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            line.LineHeight = StackedValueSize + 2;
            stack.Children.Add(line);
        }
        return stack;
    }

    private static TextBlock Value(string text, Brush brush, double size, FontWeight weight) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = size,
        FontWeight = weight,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static readonly Lazy<BitmapSource> AppIconImage = new(() =>
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("QuotaTray.ico")
            ?? throw new InvalidOperationException("The embedded app icon is missing.");
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        // The largest frame, so the icon stays sharp at any taskbar scale.
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        frame.Freeze();
        return frame;
    });

    /// <summary>The taskbar's text color: white on a dark taskbar, near-black on a light one.</summary>
    public static Color Foreground(bool lightTaskbar) =>
        lightTaskbar ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White;
}
