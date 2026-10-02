using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using QuotaTray.Engine;

namespace QuotaTray.Tray;

/// <summary>
/// Draws the notification-area icon: in Bars style (and whenever the taskbar strip can't show) up to
/// two mini meters for the pinned readings, otherwise the app icon.
/// </summary>
public static class TrayIconRenderer
{
    public static Icon LoadAppIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuotaTray.ico")
            ?? throw new InvalidOperationException("The embedded app icon is missing.");
        return new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    /// <summary>The meters the Bars icon draws: the first two pinned meter rows that have data.</summary>
    public static IReadOnlyList<RowInfo> IconMeters(Dashboard? dashboard) =>
        dashboard == null
            ? Array.Empty<RowInfo>()
            : dashboard.Providers
                .Where(p => p.Enabled)
                .SelectMany(p => p.Rows)
                .Where(r => r.Pinned && r.Kind == "meter" && r.HasData && r.Fraction.HasValue)
                .Take(2)
                .ToList();

    public static Icon DrawMeters(IReadOnlyList<RowInfo> meters, bool lightTaskbar, bool updateDot = false)
    {
        using var bitmap = DrawMetersBitmap(meters, System.Windows.Forms.SystemInformation.SmallIconSize, lightTaskbar);
        if (updateDot)
        {
            AddUpdateDot(bitmap);
        }
        return ToIcon(bitmap);
    }

    /// <summary>The app icon with the update dot, for when there are no meters to draw.</summary>
    public static Icon AppIconWithUpdateDot(Icon appIcon)
    {
        using var bitmap = appIcon.ToBitmap();
        AddUpdateDot(bitmap);
        return ToIcon(bitmap);
    }

    /// <summary>A blue dot with a thin white ring in the icon's top-right corner: a new version is ready.</summary>
    private static void AddUpdateDot(Bitmap bitmap)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var diameter = Math.Max(6f, bitmap.Width * 0.45f);
        var dot = new RectangleF(bitmap.Width - diameter - 0.5f, 0.5f, diameter, diameter);
        using var ring = new SolidBrush(Color.White);
        graphics.FillEllipse(ring, RectangleF.Inflate(dot, 1f, 1f));
        using var fill = new SolidBrush(Color.FromArgb(0x2F, 0x80, 0xED));
        graphics.FillEllipse(fill, dot);
    }

    /// <summary>The meters icon as a bitmap of <paramref name="size"/> (also used by the previews).</summary>
    internal static Bitmap DrawMetersBitmap(IReadOnlyList<RowInfo> meters, Size size, bool lightTaskbar)
    {
        var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var scale = size.Height / 16f;
            var barHeight = (meters.Count == 1 ? 6f : 5f) * scale;
            var gap = 2f * scale;
            var total = meters.Count * barHeight + (meters.Count - 1) * gap;
            var top = (size.Height - total) / 2f;
            var inset = 1f * scale;

            foreach (var meter in meters)
            {
                DrawBar(graphics, meter, new RectangleF(inset, top, size.Width - 2 * inset, barHeight), lightTaskbar);
                top += barHeight + gap;
            }
        }
        return bitmap;
    }

    private static void DrawBar(Graphics graphics, RowInfo meter, RectangleF rect, bool lightTaskbar)
    {
        var track = lightTaskbar ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(90, 255, 255, 255);
        using (var trackBrush = new SolidBrush(track))
        {
            FillRounded(graphics, trackBrush, rect);
        }
        var fraction = Math.Clamp(meter.Fraction ?? 0, 0, 1);
        if (fraction > 0)
        {
            using var fillBrush = new SolidBrush(FillColor(meter.Severity, lightTaskbar));
            FillRounded(graphics, fillBrush, rect with { Width = Math.Max(rect.Height, (float)(rect.Width * fraction)) });
        }
    }

    private static Color FillColor(string? severity, bool lightTaskbar) => severity switch
    {
        "warning" => Color.FromArgb(0xFF, 0xCC, 0x00),
        "critical" => Color.FromArgb(0xFF, 0x3B, 0x30),
        _ => lightTaskbar ? Color.FromArgb(0x1B, 0x1C, 0x1F) : Color.White,
    };

    private static void FillRounded(Graphics graphics, Brush brush, RectangleF rect)
    {
        var radius = rect.Height / 2f;
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, radius * 2, rect.Height, 90, 180);
        path.AddArc(rect.Right - radius * 2, rect.Top, radius * 2, rect.Height, 270, 180);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    public static bool TaskbarUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        // SystemUsesLightTheme covers the taskbar; missing (Windows 10 before 1903) means dark.
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle doesn't own the handle; clone it so the HICON can be freed right away.
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
