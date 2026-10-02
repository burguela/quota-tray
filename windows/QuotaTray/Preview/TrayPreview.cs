using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;
using QuotaTray.Engine;
using QuotaTray.Tray;

namespace QuotaTray.Preview;

/// <summary>
/// Renders the right end of the taskbar with Quota Tray's strip (the same
/// <see cref="TaskbarStripView"/> the app places there) beside the notification area, so the previews
/// show what sits in the taskbar. The taskbar around the strip is a simplified Windows 11 look.
/// </summary>
internal static class TrayPreview
{
    private const double Width = 420;
    private const double TaskbarHeight = 48;

    public static void Save(Dashboard dashboard, bool dark, string path, bool updateAvailable = false)
    {
        var groups = TaskbarStripView.Groups(dashboard);
        if (groups.Count == 0)
        {
            throw new InvalidOperationException("The preview dashboard has no pinned readings to draw.");
        }
        var text = TaskbarStripView.Foreground(lightTaskbar: !dark);

        var taskbar = new DockPanel
        {
            Width = Width,
            Height = TaskbarHeight,
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
            LastChildFill = false,
        };
        taskbar.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));

        var clock = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 14, 0) };
        clock.Children.Add(ClockLine("16:00", text));
        clock.Children.Add(ClockLine("23/09/2026", text));
        DockPanel.SetDock(clock, Dock.Right);
        taskbar.Children.Add(clock);

        var overflow = new Shapes.Path
        {
            Data = Geometry.Parse("M0,5 L5,0 L10,5"),
            Stroke = new SolidColorBrush(text),
            StrokeThickness = 1.2,
            Width = 10,
            Height = 6,
            Margin = new Thickness(8, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(overflow, Dock.Right);
        taskbar.Children.Add(overflow);

        // The strip with the same padding TaskbarStrip gives it.
        var strip = TaskbarStripView.Build(groups, text, updateAvailable);
        strip.Margin = new Thickness(8, 0, 8, 0);
        DockPanel.SetDock(strip, Dock.Right);
        taskbar.Children.Add(strip);

        PreviewRenderer.SaveElement(taskbar, Width, path);
    }

    private static TextBlock ClockLine(string value, Color color) => new()
    {
        Text = value,
        Foreground = new SolidColorBrush(color),
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Right,
    };
}
