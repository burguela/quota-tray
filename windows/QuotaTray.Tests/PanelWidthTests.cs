using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using QuotaTray.Engine;
using QuotaTray.Services;
using QuotaTray.Views;

namespace QuotaTray.Tests;

/// <summary>A dependency-free Windows integration test using a real, invisible WPF popup.</summary>
internal static class PanelWidthTests
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var popup = new PopupWindow(new NoActions())
        {
            ShowActivated = false,
            Opacity = 0,
            Left = -10000,
            Top = -10000,
        };
        try
        {
            var desiredWidth = popup.Width;
            var dashboard = new Dashboard { GeneratedAt = DateTimeOffset.UtcNow };
            popup.Show();
            popup.Update(dashboard, null, false);
            DrainDispatcher();
            AssertWidth(popup, desiredWidth, "initial placement");

            // A 150% -> 100% transition can leave WPF's Width at 2/3 of its original value.
            // Resize the HWND to reproduce that state without changing the user's display settings.
            foreach (var factor in new[] { 2.0 / 3, 1.5, 2.0 / 3 })
            {
                ResizeNativeWindow(popup, desiredWidth * factor);
                DrainDispatcher();
                if (Math.Abs(popup.Width - desiredWidth) < 1)
                {
                    throw new InvalidOperationException("The test did not reproduce the native width change.");
                }
                popup.Update(dashboard, null, false);
                DrainDispatcher();
                AssertWidth(popup, desiredWidth, $"refresh after native resize by {factor:F3}");

                popup.Hide();
                popup.Show();
                popup.Update(dashboard, null, false);
                DrainDispatcher();
                AssertWidth(popup, desiredWidth, "reopen after native resize");
            }
            Console.WriteLine("PASS: panel retains its intended width after native resizes, refreshes, and reopenings.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL: {error.Message}");
            return 1;
        }
        finally
        {
            popup.Close();
            app.Shutdown();
        }
    }

    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void ResizeNativeWindow(Window window, double widthDip)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var width = (int)Math.Round(widthDip * GetDpiForWindow(handle) / 96.0);
        if (!GetWindowRect(handle, out var rect) ||
            !SetWindowPos(handle, IntPtr.Zero, 0, 0, width, rect.Bottom - rect.Top, 0x0002 | 0x0004 | 0x0010))
        {
            throw new InvalidOperationException($"Native resize failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private static void AssertWidth(Window window, double desiredWidth, string context)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var rect))
        {
            throw new InvalidOperationException($"GetWindowRect failed: {Marshal.GetLastWin32Error()}");
        }
        var expected = (int)Math.Round(desiredWidth * GetDpiForWindow(handle) / 96.0);
        var actual = rect.Right - rect.Left;
        if (Math.Abs(actual - expected) > 1)
        {
            throw new InvalidOperationException($"{context}: expected {expected}px, got {actual}px (WPF Width={window.Width:F2}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

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
