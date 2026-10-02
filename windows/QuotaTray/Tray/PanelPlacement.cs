using System;
using System.Runtime.InteropServices;
using static QuotaTray.Tray.NativeMethods;

namespace QuotaTray.Tray;

/// <summary>
/// Sizes the panel and places it against the taskbar, above the taskbar strip (or the notification
/// area when the strip isn't showing), like the Mac popover under its menu-bar item. Everything is read
/// live from Windows in physical pixels, so it stays right after a resolution, scale, or taskbar change;
/// WPF's <c>SystemParameters</c> keep the values from when the app started. Size and position go to
/// Windows in one call from the measured content, never from the window's current size, which can lag
/// behind a re-render or a scale change.
/// </summary>
internal static class PanelPlacement
{
    private enum Edge { Bottom, Top, Left, Right }

    /// <summary>Where the panel was asked to go and where Windows actually left it, for the log.</summary>
    internal sealed record Result(RECT Target, RECT Actual, RECT WorkArea, RECT? Taskbar, RECT? Anchor, uint Dpi)
    {
        /// <summary>False when Windows (or a scale change on the way) put the panel somewhere else.</summary>
        public bool Landed =>
            Math.Abs(Target.Left - Actual.Left) <= 1 && Math.Abs(Target.Top - Actual.Top) <= 1 &&
            Math.Abs(Target.Right - Actual.Right) <= 1 && Math.Abs(Target.Bottom - Actual.Bottom) <= 1;

        public override string ToString() =>
            $"target={Format(Target)} actual={Format(Actual)} work={Format(WorkArea)} " +
            $"taskbar={(Taskbar is { } bar ? Format(bar) : "none")} anchor={(Anchor is { } a ? Format(a) : "none")} dpi={Dpi}";

        private static string Format(RECT r) => $"{r.Left},{r.Top} {r.Width}x{r.Height}";
    }

    /// <summary>
    /// Sizes <paramref name="window"/> to <paramref name="desiredHeightDip"/> (at most the work area)
    /// and moves it to its spot. <paramref name="insetDip"/> is the transparent shadow margin around the
    /// visible panel, so the panel's own edge lines up with the anchor's.
    /// </summary>
    public static Result Place(IntPtr window, RECT? strip, double widthDip, double desiredHeightDip, double insetDip)
    {
        var hasBar = FindTaskbar(out var taskbar, out var bar);
        var work = WorkArea(hasBar ? bar : null, out var screen, out var dpi);
        var scale = dpi / 96.0;
        var width = Math.Min((int)Math.Round(widthDip * scale), work.Width);
        var height = Math.Min((int)Math.Round(desiredHeightDip * scale), work.Height);
        var inset = (int)Math.Round(insetDip * scale);

        int x, y;
        RECT? anchorUsed = null;
        if (hasBar)
        {
            var anchor = strip ?? NotificationArea(taskbar) ?? bar;
            anchorUsed = anchor;
            switch (EdgeOf(bar, screen))
            {
                case Edge.Top:
                    x = anchor.Right + inset - width;
                    y = Math.Max(work.Top, bar.Bottom);
                    break;
                case Edge.Left:
                    x = Math.Max(work.Left, bar.Right);
                    y = anchor.Bottom + inset - height;
                    break;
                case Edge.Right:
                    x = Math.Min(work.Right, bar.Left) - width;
                    y = anchor.Bottom + inset - height;
                    break;
                default:
                    // The panel's right edge lines up with the strip's, so it always opens in the same spot.
                    x = anchor.Right + inset - width;
                    y = Math.Min(work.Bottom, bar.Top) - height;
                    break;
            }
        }
        else
        {
            x = work.Right - width;
            y = work.Bottom - height;
        }
        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE))
        {
            throw new ExternalException("SetWindowPos failed for the panel", Marshal.GetLastWin32Error());
        }
        if (!GetWindowRect(window, out var actual))
        {
            throw new ExternalException("GetWindowRect failed for the panel", Marshal.GetLastWin32Error());
        }
        var target = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        return new Result(target, actual, work, hasBar ? bar : null, anchorUsed, dpi);
    }

    /// <summary>The tallest the panel can be, in device-independent pixels at the taskbar monitor's scale.</summary>
    public static double WorkAreaHeightDip()
    {
        var hasBar = FindTaskbar(out _, out var bar);
        var work = WorkArea(hasBar ? bar : null, out _, out var dpi);
        return work.Height * 96.0 / dpi;
    }

    private static bool FindTaskbar(out IntPtr taskbar, out RECT bar)
    {
        taskbar = FindWindow("Shell_TrayWnd", null);
        bar = default;
        return taskbar != IntPtr.Zero && GetWindowRect(taskbar, out bar);
    }

    /// <summary>
    /// The work area, full screen, and scale of the monitor the taskbar is on (the primary one when there
    /// is no taskbar). Found from the taskbar's rectangle, so it holds for an auto-hidden bar too.
    /// </summary>
    private static RECT WorkArea(RECT? bar, out RECT screen, out uint dpi)
    {
        IntPtr monitor;
        if (bar is { } rect)
        {
            monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            monitor = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
        }
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            throw new ExternalException("GetMonitorInfo failed", Marshal.GetLastWin32Error());
        }
        var result = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpi, out _);
        if (result != 0 || dpi == 0)
        {
            throw new ExternalException("GetDpiForMonitor failed", result);
        }
        screen = info.rcMonitor;
        return info.rcWork;
    }

    private static RECT? NotificationArea(IntPtr taskbar)
    {
        var notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        return notify != IntPtr.Zero && GetWindowRect(notify, out var rect) && rect.Width > 0 ? rect : null;
    }

    private static Edge EdgeOf(RECT bar, RECT screen)
    {
        if (bar.Width >= bar.Height)
        {
            return bar.Top + bar.Height / 2 < screen.Top + screen.Height / 2 ? Edge.Top : Edge.Bottom;
        }
        return bar.Left + bar.Width / 2 < screen.Left + screen.Width / 2 ? Edge.Left : Edge.Right;
    }
}
