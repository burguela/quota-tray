using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using QuotaTray.Services;

namespace QuotaTray.Views;

/// <summary>One entry of the panel's pop-up menus; a null label is a separator.</summary>
public sealed record MenuItemSpec(string? Label, Action? Action, bool IsChecked = false)
{
    public static readonly MenuItemSpec Separator = new(null, null);
}

// Small element builders shared by the dashboard and Settings screens.
public partial class PopupWindow
{
    private static TextBlock Text(
        string text,
        Brush brush,
        double size,
        FontWeight? weight = null,
        Thickness? margin = null,
        VerticalAlignment verticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left,
        bool wrap = false,
        bool trim = false) => new()
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Margin = margin ?? new Thickness(0),
            VerticalAlignment = verticalAlignment,
            HorizontalAlignment = horizontalAlignment,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            // Segoe UI's default line is ~1.33 em; SF Pro's is ~1.2. Match the Mac's tighter rhythm.
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = Math.Round(size * 1.22),
        };

    /// <summary>A vector glyph: stroked (chevrons, arrows) when <paramref name="stroke"/> is set, else filled.</summary>
    private static FrameworkElement Glyph(Geometry geometry, Brush brush, double size, double? stroke = null, Thickness? margin = null)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            Margin = margin ?? new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = false,
        };
        if (stroke is double thickness)
        {
            path.Stroke = brush;
            path.StrokeThickness = thickness;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
            path.StrokeLineJoin = PenLineJoin.Round;
        }
        else
        {
            path.Fill = brush;
        }
        return path;
    }

    /// <summary>A provider mark in the hierarchical secondary tint the Mac section headers use.</summary>
    private static FrameworkElement? MarkIcon(string providerId, Brush brush, double size, Thickness margin) =>
        ProviderMarks.For(providerId) is Geometry mark ? Glyph(mark, brush, size, margin: margin) : null;

    /// <summary>A grouped card: the lifted fill in the shared 12px rounded shape.</summary>
    private static Border Card(UIElement child, Theme theme, Thickness? padding = null) => new()
    {
        Background = theme.Card,
        CornerRadius = new CornerRadius(12),
        Padding = padding ?? new Thickness(0),
        Child = child,
    };

    /// <summary>
    /// Makes an element clickable (mouse and keyboard) with an optional hover fill, a focus ring for
    /// keyboard users that follows its corners, and a name for screen readers.
    /// </summary>
    private static Pressable Clickable(Pressable element, Action onClick, Brush? hover = null, Brush? rest = null, string? name = null)
    {
        element.OnPress = onClick;
        element.Cursor = Cursors.Hand;
        element.Focusable = true;
        element.FocusVisualStyle = FocusRing(element.CornerRadius.TopLeft);
        if (name != null)
        {
            AutomationProperties.SetName(element, name);
        }
        element.Background ??= Brushes.Transparent;
        var resting = rest ?? element.Background;
        if (hover != null)
        {
            element.MouseEnter += (_, _) => element.Background = hover;
            element.MouseLeave += (_, _) => element.Background = resting;
        }
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        element.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                onClick();
            }
        };
        return element;
    }

    /// <summary>
    /// The keyboard focus ring (shown only when focus arrives by keyboard): a 2px accent outline just
    /// outside the element, rounded to match its <paramref name="cornerRadius"/>.
    /// </summary>
    internal static Style FocusRing(double cornerRadius)
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.BorderBrushProperty, Theme.Current.Blue);
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius + 2));
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
        ring.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = ring }));
        style.Seal();
        return style;
    }

    /// <summary>The 28px chrome capsule the Mac footer and navigation bar use (Options, Back).</summary>
    private static Pressable Capsule(UIElement content, Theme theme, Action onClick, Thickness padding, string? name = null) =>
        Clickable(new Pressable
        {
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = theme.ChromeFill,
            BorderBrush = theme.ChromeBorder,
            BorderThickness = new Thickness(1),
            Padding = padding,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        }, onClick, theme.Hover, theme.ChromeFill, name);

    /// <summary>A small bordered push button (the Mac's .bordered, .small control).</summary>
    private static Pressable SmallButton(UIElement content, Theme theme, Action onClick, string? name = null) =>
        Clickable(new Pressable
        {
            CornerRadius = new CornerRadius(5),
            Background = theme.ButtonFill,
            Padding = new Thickness(8, 3, 8, 4),
            Child = content,
        }, onClick, theme.ButtonHover, theme.ButtonFill, name);

    /// <summary>
    /// A pop-up menu styled like a Mac menu (rounded, shadowed, blue highlight), opening above or below
    /// <paramref name="anchor"/> with right edges aligned.
    /// </summary>
    private void ShowMenu(FrameworkElement anchor, bool above, IReadOnlyList<MenuItemSpec> items)
    {
        var theme = Theme.Current;
        var list = new StackPanel { MinWidth = 150 };
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Custom,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
        };
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width + 8, above ? -popupSize.Height + 4 : targetSize.Height - 4),
                PopupPrimaryAxis.Horizontal),
        };
        foreach (var item in items)
        {
            if (item.Label == null)
            {
                list.Children.Add(new Border { Height = 1, Background = theme.Separator, Margin = new Thickness(8, 4, 8, 4) });
                continue;
            }
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var check = Text(item.IsChecked ? "✓" : "", theme.TextPrimary, 12, verticalAlignment: VerticalAlignment.Center);
            var label = Text(item.Label, theme.TextPrimary, 13, verticalAlignment: VerticalAlignment.Center);
            Grid.SetColumn(label, 1);
            row.Children.Add(check);
            row.Children.Add(label);
            var entry = new Pressable { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 3, 12, 4), Child = row };
            var action = item.Action;
            Clickable(entry, () =>
            {
                popup.IsOpen = false;
                action?.Invoke();
            }, name: item.Label);
            entry.MouseEnter += (_, _) =>
            {
                entry.Background = theme.Blue;
                check.Foreground = Brushes.White;
                label.Foreground = Brushes.White;
            };
            entry.MouseLeave += (_, _) =>
            {
                entry.Background = Brushes.Transparent;
                check.Foreground = theme.TextPrimary;
                label.Foreground = theme.TextPrimary;
            };
            list.Children.Add(entry);
        }
        popup.Child = new Border
        {
            Margin = new Thickness(8),
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(8),
            Background = theme.ChromeFill,
            BorderBrush = theme.ChromeBorder,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25 },
            Child = list,
        };
        popup.IsOpen = true;
    }

    /// <summary>"A", "A and B", "A, B, and C".</summary>
    private static string JoinWithAnd(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names, 0, names.Count - 1) + ", and " + names[^1],
    };

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error($"could not open link: {error.Message}");
            MessageBox.Show($"Quota Tray couldn't open {url}.", "Quota Tray", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
