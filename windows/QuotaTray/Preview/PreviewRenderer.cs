using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuotaTray.Engine;
using QuotaTray.Services;
using QuotaTray.Views;

namespace QuotaTray.Preview;

/// <summary>
/// <c>QuotaTray.exe --render-preview dashboard.json out-folder</c>: renders the panel (dashboard and
/// Settings) and the taskbar icon with its tooltip, light and dark, to PNGs from a saved dashboard
/// document, without the tray or the engine.
/// CI uses it to publish screenshots of the Windows UI.
/// </summary>
public static class PreviewRenderer
{
    public static int Run(string dashboardPath, string outputFolder)
    {
        var dashboard = Load(dashboardPath);
        Directory.CreateDirectory(outputFolder);
        foreach (var dark in new[] { false, true })
        {
            foreach (var settings in new[] { false, true })
            {
                Theme.Force(dark);
                var window = new PopupWindow(new NoActions());
                var panel = window.RenderForPreview(dashboard, settings, expandFirstProvider: true);
                var name = $"{(settings ? "settings" : "dashboard")}-{(dark ? "dark" : "light")}.png";
                Save(panel, window, Path.Combine(outputFolder, name));
                window.Close();
            }
            TrayPreview.Save(dashboard, dark, Path.Combine(outputFolder, $"tray-{(dark ? "dark" : "light")}.png"));
            TrayPreview.Save(dashboard, dark, Path.Combine(outputFolder, $"tray-update-{(dark ? "dark" : "light")}.png"), updateAvailable: true);
        }

        // The panel's two edge states, light only: the engine failed (the notice with Try Again), and
        // every provider is off (the Open Settings prompt).
        Theme.Force(false);
        SaveState(dashboard, "The Quota Tray engine didn't answer in time. Check the log folder for details.",
            Path.Combine(outputFolder, "notice-light.png"));
        var updating = new PopupWindow(new NoActions());
        updating.SetUpdate(new UpdateOffer("0.1.9", UpdatePhase.Available));
        Save(updating.RenderForPreview(dashboard, settings: false, expandFirstProvider: false), updating,
            Path.Combine(outputFolder, "update-light.png"));
        updating.Close();
        var empty = Load(dashboardPath);
        empty.TotalSpend = null;
        empty.Providers.ForEach(p => p.Enabled = false);
        SaveState(empty, null, Path.Combine(outputFolder, "no-providers-light.png"));
        return 0;
    }

    private static Dashboard Load(string path) =>
        JsonSerializer.Deserialize<Dashboard>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"{path} is not a dashboard document.");

    private static void SaveState(Dashboard dashboard, string? engineError, string path)
    {
        var window = new PopupWindow(new NoActions());
        var panel = window.RenderForPreview(dashboard, settings: false, expandFirstProvider: false, engineError);
        Save(panel, window, path);
        window.Close();
    }

    private static void Save(FrameworkElement panel, Window window, string path)
    {
        // Lay the panel out on a neutral backdrop (so its shadow and corners read), at the window's
        // width and whatever height its content needs.
        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)),
            Child = panel,
            Width = window.Width,
        };
        host.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
        host.SetValue(TextElement.FontSizeProperty, window.FontSize);
        host.Resources.MergedDictionaries.Add(window.Resources);
        SaveElement(host, window.Width, path);
    }

    /// <summary>Lays <paramref name="host"/> out at <paramref name="width"/> and saves it as a 2x PNG.</summary>
    internal static void SaveElement(FrameworkElement host, double width, string path)
    {
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Ideal);
        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();

        const double scale = 2;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(host.ActualWidth * scale), (int)Math.Ceiling(host.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class NoActions : IPopupActions
    {
        public void RefreshNow() { }
        public void SetProviderEnabled(string providerId, bool enabled) { }
        public void SetMeterStyle(bool showRemaining) { }
        public void SetTrayStyle(TrayStyle style) { }
        public void SetLaunchAtLogin(bool enabled) { }
        public void OpenLogFolder() { }
        public void InstallUpdate() { }
        public void Quit() { }
    }
}
