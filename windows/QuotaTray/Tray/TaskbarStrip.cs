using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuotaTray.Services;
using static QuotaTray.Tray.NativeMethods;
using Forms = System.Windows.Forms;

namespace QuotaTray.Tray;

/// <summary>
/// The Windows counterpart of the Mac menu-bar strip: a small see-through window placed inside the
/// taskbar, just left of the notification area, drawing <see cref="TaskbarStripView"/>. Click it to
/// open the panel; right-click it for the menu. It follows the taskbar as the notification area
/// grows or shrinks, the scale changes, or Explorer restarts. A vertical taskbar has no room for it,
/// so there it stays hidden and the tray icon carries the readings.
/// </summary>
public sealed class TaskbarStrip : Forms.NativeWindow, IDisposable
{
    private const double HorizontalPadding = 8;
    private const double HoverInset = 4;
    // A layered window only takes clicks on pixels that aren't fully transparent, so the whole strip
    // gets this invisible fill; otherwise only the numbers and marks themselves would be clickable.
    private static readonly Color HitTestFill = Color.FromArgb(0x01, 0x00, 0x00, 0x00);

    private readonly Action _onClick;
    private readonly Action _onRightClick;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<StripGroup> _groups = Array.Empty<StripGroup>();
    private bool _wanted;
    private bool _updateAvailable;
    private bool _dirty = true;
    private bool _hover;
    private IntPtr _taskbar;
    private double _scale;
    private int _height;
    private int _width;
    private (int X, int Y)? _position;
    private string? _lastProblem;

    public TaskbarStrip(Action onClick, Action onRightClick)
    {
        _onClick = onClick;
        _onRightClick = onRightClick;
        // Re-anchoring is a few cheap window lookups; once a second keeps up with the tray changing width.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Place();
    }

    /// <summary>True while the strip is in the taskbar. Changes raise <see cref="ShowingChanged"/>.</summary>
    public bool IsShowing { get; private set; }

    public event Action? ShowingChanged;

    /// <summary>Where the strip sits on screen, in physical pixels, while it shows; the panel opens above it.</summary>
    internal RECT? ScreenBounds =>
        IsShowing && Handle != IntPtr.Zero && GetWindowRect(Handle, out var bounds) ? bounds : null;

    public void Show(IReadOnlyList<StripGroup> groups, bool updateAvailable)
    {
        _groups = groups;
        _updateAvailable = updateAvailable;
        _wanted = true;
        _dirty = true;
        _timer.Start();
        Place();
    }

    public void Hide()
    {
        _wanted = false;
        _timer.Stop();
        Place();
    }

    /// <summary>Redraws with the current taskbar colors (after a light/dark switch).</summary>
    public void ThemeChanged()
    {
        _dirty = true;
        Place();
    }

    private void Place()
    {
        try
        {
            SetShowing(_wanted && TryPlace());
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException)
        {
            Problem($"taskbar strip failed: {error}");
            SetShowing(false);
        }
    }

    private bool TryPlace()
    {
        if (!EnsureAttached())
        {
            return false;
        }
        if (!GetWindowRect(_taskbar, out var taskbar) || taskbar.Width <= taskbar.Height)
        {
            return Hidden("the taskbar is vertical or hidden, so the readings stay in the tray icon");
        }
        var notify = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero || !GetWindowRect(notify, out var tray) || tray.Width <= 0)
        {
            return Hidden("the notification area wasn't found, so the readings stay in the tray icon");
        }

        var scale = GetDpiForWindow(_taskbar) / 96.0;
        if (_dirty || scale != _scale || taskbar.Height != _height)
        {
            _scale = scale;
            _height = taskbar.Height;
            Render();
        }
        var position = (X: tray.Left - taskbar.Left - _width, Y: 0);
        if (position != _position || !IsShowing)
        {
            SetWindowPos(Handle, HWND_TOP, position.X, position.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _position = position;
        }
        _lastProblem = null;
        return true;
    }

    /// <summary>Creates the window inside the current taskbar, again after Explorer restarts.</summary>
    private bool EnsureAttached()
    {
        if (Handle != IntPtr.Zero && _taskbar != IntPtr.Zero && IsWindow(_taskbar))
        {
            return true;
        }
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
        _position = null;
        _dirty = true;
        _taskbar = FindWindow("Shell_TrayWnd", null);
        if (_taskbar == IntPtr.Zero)
        {
            return Hidden("the taskbar wasn't found");
        }
        CreateHandle(new Forms.CreateParams
        {
            Caption = "Quota Tray",
            Style = WS_CHILD | WS_CLIPSIBLINGS,
            ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            Parent = _taskbar,
        });
        if (Handle == IntPtr.Zero)
        {
            return Hidden($"the strip window couldn't be created in the taskbar (error {Marshal.GetLastWin32Error()})");
        }
        AppLog.Info("taskbar strip attached");
        return true;
    }

    /// <summary>Draws the strip with WPF and hands the pixels to the layered window.</summary>
    private void Render()
    {
        var heightDip = _height / _scale;
        var hoverFill = TrayIconRenderer.TaskbarUsesLightTheme()
            ? Color.FromArgb(0x14, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
        var root = new Border
        {
            Height = heightDip,
            Background = new SolidColorBrush(HitTestFill),
            Padding = new Thickness(0, HoverInset, 0, HoverInset),
            Child = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(_hover ? hoverFill : HitTestFill),
                Padding = new Thickness(HorizontalPadding, 0, HorizontalPadding, 0),
                Child = TaskbarStripView.Build(_groups, TaskbarStripView.Foreground(TrayIconRenderer.TaskbarUsesLightTheme()), _updateAvailable),
            },
        };
        root.Measure(new Size(double.PositiveInfinity, heightDip));
        root.Arrange(new Rect(new Size(root.DesiredSize.Width, heightDip)));
        root.UpdateLayout();

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * _scale));
        var pixelHeight = Math.Max(1, _height);
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        Present(bitmap);
        _width = pixelWidth;
        _dirty = false;
    }

    private void Present(BitmapSource bitmap)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = bitmap.PixelWidth,
            biHeight = -bitmap.PixelHeight, // top-down, like WPF's pixels
            biPlanes = 1,
            biBitCount = 32,
        };
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var dib = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero)
            {
                throw new ExternalException("CreateDIBSection failed", Marshal.GetLastWin32Error());
            }
            var stride = bitmap.PixelWidth * 4;
            // Pbgra32 is the premultiplied BGRA layout UpdateLayeredWindow expects.
            bitmap.CopyPixels(Int32Rect.Empty, bits, stride * bitmap.PixelHeight, stride);
            var previous = SelectObject(memory, dib);
            var size = new SIZE { Width = bitmap.PixelWidth, Height = bitmap.PixelHeight };
            var source = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            var updated = UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, ULW_ALPHA);
            var error = Marshal.GetLastWin32Error();
            SelectObject(memory, previous);
            if (!updated)
            {
                throw new ExternalException("UpdateLayeredWindow failed", error);
            }
        }
        finally
        {
            if (dib != IntPtr.Zero)
            {
                DeleteObject(dib);
            }
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void WndProc(ref Forms.Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            case WM_MOUSEMOVE when !_hover:
                var track = new TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                    dwFlags = TME_LEAVE,
                    hwndTrack = Handle,
                };
                TrackMouseEvent(ref track);
                SetHover(true);
                break;
            case WM_MOUSELEAVE:
                SetHover(false);
                break;
            case WM_LBUTTONUP:
                _onClick();
                break;
            case WM_RBUTTONUP:
                _onRightClick();
                break;
        }
        base.WndProc(ref m);
    }

    private void SetHover(bool hover)
    {
        _hover = hover;
        _dirty = true;
        Place();
    }

    private void SetShowing(bool showing)
    {
        if (!showing && Handle != IntPtr.Zero && IsShowing)
        {
            SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            _position = null;
        }
        if (showing != IsShowing)
        {
            IsShowing = showing;
            ShowingChanged?.Invoke();
        }
    }

    private bool Hidden(string reason)
    {
        Problem($"taskbar strip hidden: {reason}");
        return false;
    }

    /// <summary>Logs each distinct problem once, not on every tick.</summary>
    private void Problem(string message)
    {
        if (message != _lastProblem)
        {
            AppLog.Warn(message);
            _lastProblem = message;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }
}
