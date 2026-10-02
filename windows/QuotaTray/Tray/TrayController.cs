using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using QuotaTray.Engine;
using QuotaTray.Services;
using QuotaTray.Views;
using Forms = System.Windows.Forms;

namespace QuotaTray.Tray;

/// <summary>
/// Owns the taskbar strip, the notification-area icon, their menu, the popup, and the refresh schedule. All engine work
/// runs off the UI thread; results are applied back on it.
/// </summary>
public sealed class TrayController : IPopupActions, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    // While the panel is closed, check for stale providers every five minutes (the engine's
    // refresh interval); while it is open, every minute so countdowns stay current.
    private const int HiddenTicksPerRefresh = 5;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);
    // If the installer hasn't replaced and restarted this app by then, it failed; offer a retry.
    private static readonly TimeSpan InstallGrace = TimeSpan.FromMinutes(2);

    private readonly App _app;
    private readonly EngineClient _engine;
    private readonly TrayIcon _icon;
    private readonly TaskbarStrip _strip;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly PopupWindow _popup;
    private readonly DispatcherTimer _timer;
    private readonly Forms.ToolStripMenuItem _launchAtLoginItem;
    private readonly Forms.ToolStripMenuItem _updateItem;
    private readonly DispatcherTimer _updateTimer;
    private UpdateRelease? _updateRelease;
    private UpdateOffer? _offer;
    private Dashboard? _dashboard;
    private string? _engineError;
    private bool _refreshing;
    private int _ticksSinceRefresh;
    private bool _disposed;

    public TrayController(App app)
    {
        _app = app;
        Forms.Application.EnableVisualStyles();
        _engine = EngineClient.Locate();
        AppLog.Info($"engine: {_engine.ExecutablePath}");

        _popup = new PopupWindow(this);

        _launchAtLoginItem = new Forms.ToolStripMenuItem("Launch at Login", null, (_, _) =>
            SetLaunchAtLogin(!LaunchAtLogin.IsEnabled));
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open Quota Tray", null, (_, _) => _popup.ShowNearTray());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(open);
        _updateItem = new Forms.ToolStripMenuItem("Install Update", null, (_, _) => InstallUpdate()) { Visible = false };
        menu.Items.Add(_updateItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Refresh Now", null, (_, _) => RefreshNow()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Settings", null, (_, _) => _popup.ShowNearTray(settings: true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_launchAtLoginItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Open Log Folder", null, (_, _) => OpenLogFolder()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit Quota Tray", null, (_, _) => Quit()));
        menu.Opening += (_, _) => _launchAtLoginItem.Checked = SafeLaunchAtLoginState();

        _menu = menu;
        _icon = new TrayIcon(menu, OnIconClick);
        _strip = new TaskbarStrip(TogglePopup, ShowMenuAtCursor);
        // When the strip can't show (a vertical taskbar, say), the tray icon draws the meters instead.
        _strip.ShowingChanged += UpdateIcon;
        _popup.StripBounds = () => _strip.ScreenBounds;

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => OnTick();
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
    }

    public void Start()
    {
        _icon.Show();
        UpdateTaskbar();
        _timer.Start();
        _updateTimer.Start();
        _ = StartupLoadAsync();
        _ = CheckForUpdateAsync();
    }

    /// <summary>Show what's cached immediately, then refresh whatever is stale.</summary>
    private async Task StartupLoadAsync()
    {
        await LoadAsync(RefreshMode.Cached, showsProgress: false);
        await LoadAsync(RefreshMode.IfStale, showsProgress: true);
    }

    private void OnTick()
    {
        _ticksSinceRefresh++;
        if (_refreshing)
        {
            return;
        }
        if (_popup.IsVisible || _ticksSinceRefresh >= HiddenTicksPerRefresh)
        {
            _ = LoadAsync(RefreshMode.IfStale, showsProgress: false);
        }
    }

    private void OnIconClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            TogglePopup();
        }
    }

    private void TogglePopup()
    {
        if (_popup.IsVisible)
        {
            _popup.Hide();
        }
        else if (!_popup.ShouldIgnoreToggle)
        {
            _popup.ShowNearTray();
        }
    }

    /// <summary>The tray icon's menu, for a right-click on the taskbar strip.</summary>
    private void ShowMenuAtCursor()
    {
        _menu.Show(Forms.Cursor.Position, Forms.ToolStripDropDownDirection.AboveLeft);
        // Like NotifyIcon does: bring the menu forward so a click anywhere else closes it.
        SetForegroundWindow(_menu.Handle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    // MARK: - Engine

    private async Task LoadAsync(RefreshMode mode, bool showsProgress)
    {
        if (mode != RefreshMode.Cached)
        {
            _refreshing = true;
            _ticksSinceRefresh = 0;
            if (showsProgress)
            {
                _popup.Update(null, _engineError, refreshing: true);
            }
        }
        try
        {
            Apply(await Task.Run(() => _engine.DashboardAsync(mode)), error: null);
        }
        catch (EngineException error)
        {
            AppLog.Error($"dashboard ({mode}) failed: {error}");
            Apply(null, error.Message);
        }
        catch (Exception error)
        {
            // Callers discard this task, so anything unexpected must be logged and shown here.
            AppLog.Error($"dashboard ({mode}) failed unexpectedly: {error}");
            Apply(null, $"Quota Tray couldn't refresh: {error.Message}");
        }
        finally
        {
            if (mode != RefreshMode.Cached)
            {
                _refreshing = false;
                _popup.Update(null, _engineError, refreshing: false);
            }
        }
    }

    private async Task RunCommandAsync(string description, Func<Task<Dashboard>> command)
    {
        try
        {
            Apply(await Task.Run(command), error: null);
        }
        catch (EngineException error)
        {
            AppLog.Error($"{description} failed: {error}");
            Apply(null, error.Message);
        }
        catch (Exception error)
        {
            AppLog.Error($"{description} failed unexpectedly: {error}");
            Apply(null, $"Quota Tray couldn't apply that change: {error.Message}");
        }
    }

    private void Apply(Dashboard? dashboard, string? error)
    {
        if (_disposed)
        {
            return;
        }
        if (dashboard != null)
        {
            _dashboard = dashboard;
        }
        _engineError = error;
        _popup.Update(dashboard, error, _refreshing);
        UpdateTaskbar();
    }

    /// <summary>Text style shows the readings in the taskbar strip, Bars style in the tray icon.</summary>
    private void UpdateTaskbar()
    {
        if (UiSettings.TrayStyle == TrayStyle.Text)
        {
            _strip.Show(TaskbarStripView.Groups(_dashboard), _offer != null);
        }
        else
        {
            _strip.Hide();
        }
        UpdateIcon();
    }

    private void UpdateIcon() => _icon.Update(_dashboard, _engineError, drawMeters: !_strip.IsShowing, _offer?.Version);

    // MARK: - Updates

    /// <summary>
    /// Asks GitHub for a newer release every few hours. A failed check (offline, rate-limited) is logged
    /// and tried again next time; it is not worth interrupting the user for.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        if (_offer?.Phase == UpdatePhase.Installing)
        {
            return;
        }
        try
        {
            var release = await Task.Run(() => UpdateChecker.CheckAsync());
            if (_disposed)
            {
                return;
            }
            _updateRelease = release;
            if (release == null)
            {
                if (_offer != null)
                {
                    SetOffer(null);
                }
            }
            else if (_offer?.Version != release.Version.ToString())
            {
                AppLog.Info($"update available: {release.Version}");
                SetOffer(new UpdateOffer(release.Version.ToString(), UpdatePhase.Available));
            }
        }
        catch (Exception error)
        {
            AppLog.Warn($"update check failed: {error.Message}");
        }
    }

    public void InstallUpdate()
    {
        if (_updateRelease is not { } release || _offer?.Phase == UpdatePhase.Installing)
        {
            return;
        }
        _ = InstallUpdateAsync(release);
    }

    private async Task InstallUpdateAsync(UpdateRelease release)
    {
        var version = release.Version.ToString();
        SetOffer(new UpdateOffer(version, UpdatePhase.Installing));
        try
        {
            var setup = await Task.Run(() => UpdateChecker.DownloadAsync(release));
            AppLog.Info($"installing update {version} from {setup}");
            UpdateChecker.StartInstaller(setup);
            // The installer ends this app and starts the new one; reaching the end of the wait means it didn't.
            await Task.Delay(InstallGrace);
            if (!_disposed && _offer?.Phase == UpdatePhase.Installing)
            {
                AppLog.Error($"update {version}: the installer didn't replace the app within {InstallGrace.TotalMinutes} minutes");
                SetOffer(new UpdateOffer(version, UpdatePhase.Failed, "The installer didn't finish. Try again."));
            }
        }
        catch (Exception error)
        {
            AppLog.Error($"update {version} failed: {error}");
            SetOffer(new UpdateOffer(version, UpdatePhase.Failed, "Quota Tray couldn't download the update. Try again."));
        }
    }

    private void SetOffer(UpdateOffer? offer)
    {
        _offer = offer;
        _updateItem.Visible = offer != null && offer.Phase != UpdatePhase.Installing;
        if (offer != null)
        {
            _updateItem.Text = $"Install Update ({offer.Version})";
        }
        _popup.SetUpdate(offer);
        UpdateTaskbar();
    }

    // MARK: - IPopupActions

    public void RefreshNow()
    {
        if (!_refreshing)
        {
            _ = LoadAsync(RefreshMode.Force, showsProgress: true);
        }
    }

    public void SetProviderEnabled(string providerId, bool enabled)
    {
        _ = RunCommandAsync($"{(enabled ? "enable" : "disable")} {providerId}", async () =>
        {
            var dashboard = await _engine.SetProviderEnabledAsync(providerId, enabled);
            // A newly enabled provider has nothing cached yet: fetch it right away.
            return enabled ? await _engine.DashboardAsync(RefreshMode.IfStale) : dashboard;
        });
    }

    public void SetMeterStyle(bool showRemaining)
    {
        _ = RunCommandAsync("meter style", () => _engine.SetMeterStyleAsync(showRemaining));
    }

    public void SetTrayStyle(TrayStyle style)
    {
        UiSettings.TrayStyle = style;
        UpdateTaskbar();
        _popup.Update(null, _engineError, _refreshing);
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        try
        {
            LaunchAtLogin.SetEnabled(enabled);
        }
        catch (Exception error)
        {
            AppLog.Error($"launch at login change failed: {error}");
            ShowError("Quota Tray couldn't change Launch at Login.");
        }
        _popup.Update(null, _engineError, _refreshing);
    }

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLog.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            AppLog.Error($"open log folder failed: {error}");
            ShowError($"Quota Tray couldn't open {AppLog.Directory}.");
        }
    }

    public void Quit()
    {
        AppLog.Info("quit requested");
        Dispose();
        _app.Shutdown();
    }

    // MARK: - Other

    public void ThemeChanged()
    {
        Theme.Reload();
        _popup.ApplyTheme();
        // The icons follow the taskbar's own light/dark setting.
        UpdateTaskbar();
    }

    public void ShowError(string message)
    {
        if (!_disposed)
        {
            _icon.ShowBalloon(message);
        }
    }

    private static bool SafeLaunchAtLoginState()
    {
        try
        {
            return LaunchAtLogin.IsEnabled;
        }
        catch (Exception error)
        {
            AppLog.Warn($"launch at login read failed: {error.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Stop();
        _updateTimer.Stop();
        _strip.Dispose();
        _icon.Dispose();
        _popup.Close();
    }
}
