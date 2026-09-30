using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using QuotaTray.Engine;
using QuotaTray.Services;
using QuotaTray.Tray;

namespace QuotaTray.Views;

/// <summary>What the popup asks its owner (the tray controller) to do.</summary>
public interface IPopupActions
{
    void RefreshNow();
    void SetProviderEnabled(string providerId, bool enabled);
    void SetMeterStyle(bool showRemaining);
    void SetTrayStyle(TrayStyle style);
    void SetLaunchAtLogin(bool enabled);
    void OpenLogFolder();
    void Quit();
}

/// <summary>
/// The panel that opens above the taskbar strip (or the tray icon), laid out like the Mac popover:
/// the Total Spend card, then one grouped card per provider, and a pinned footer with the version,
/// the next-update countdown, and the Options menu. Settings opens in the same panel. Built in code from the
/// engine's display-ready dashboard; it closes when it loses focus.
/// </summary>
public partial class PopupWindow : Window
{
    // The Mac popover's metrics (DashboardView, DensitySetting.regular), in device-independent pixels.
    private const double OuterPadding = 14;
    private const double SectionSpacing = 14;
    private const double HeaderToCard = 4;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly IPopupActions _actions;
    private readonly HashSet<string> _expandedProviders = new();
    private readonly DispatcherTimer _clock;
    private Dashboard? _dashboard;
    private string? _engineError;
    private bool _refreshing;
    private bool _showingSettings;
    private bool _closing;
    private TextBlock? _footerStatus;
    private ScrollViewer? _scroller;
    /// <summary>Which screen <see cref="_scroller"/> shows, so a re-render keeps its scroll position.</summary>
    private bool _scrollerShowsSettings;
    /// <summary>Preview renders freeze the clock and let the panel grow to its full height.</summary>
    private DateTimeOffset? _previewNow;

    /// <summary>The taskbar strip's screen bounds while it shows; the panel opens right above it.</summary>
    internal Func<NativeMethods.RECT?>? StripBounds { get; set; }

    /// <summary>When the panel last hid itself on focus loss (see <see cref="ShouldIgnoreToggle"/>).</summary>
    public DateTime LastAutoHide { get; private set; } = DateTime.MinValue;

    public PopupWindow(IPopupActions actions)
    {
        _actions = actions;
        InitializeComponent();
        Closing += (_, _) =>
        {
            _closing = true;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Deactivated += (_, _) =>
        {
            // Closing (on Quit) also deactivates; hiding a closing window throws.
            if (_closing)
            {
                return;
            }
            LastAutoHide = DateTime.UtcNow;
            Hide();
        };
        SizeChanged += (_, _) => PositionNearTray();
        PreviewKeyDown += OnPreviewKeyDown;
        // The footer's "Next update in …" counts down every second while the panel is open.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateFooterStatus();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _clock.Start();
            }
            else
            {
                _clock.Stop();
            }
        };
        Render();
    }

    /// <summary>
    /// Clicking the tray icon while the panel is open first deactivates (hides) it, then delivers the
    /// click; treat that click as "close", not "open again".
    /// </summary>
    public bool ShouldIgnoreToggle => (DateTime.UtcNow - LastAutoHide).TotalMilliseconds < 350;

    public void ShowNearTray(bool settings = false)
    {
        var wasVisible = IsVisible;
        _showingSettings = settings;
        if (!wasVisible)
        {
            // A fresh open starts at the top; re-renders while it's open keep the scroll position.
            _scroller = null;
        }
        Render();
        Show();
        PositionNearTray();
        Activate();
        Focus();
        if (!wasVisible)
        {
            PlayOpenAnimation();
        }
    }

    /// <summary>
    /// Windows 11 flyouts fade in while rising a few pixels from the taskbar; do the same, unless the
    /// user turned animations off (Settings, Accessibility, Visual effects).
    /// </summary>
    private void PlayOpenAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }
        var duration = new Duration(TimeSpan.FromMilliseconds(170));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var rise = new TranslateTransform(0, 10);
        Frame.RenderTransform = rise;
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
        Frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    public void Update(Dashboard? dashboard, string? engineError, bool refreshing)
    {
        if (dashboard != null)
        {
            _dashboard = dashboard;
        }
        _engineError = engineError;
        _refreshing = refreshing;
        Render();
    }

    public void ApplyTheme() => Render();

    /// <summary>
    /// Renders the panel for <c>--render-preview</c> and hands back its frame, detached from this
    /// window so it can lay out at full height (a window can't grow past the screen).
    /// </summary>
    public FrameworkElement RenderForPreview(Dashboard dashboard, bool settings, bool expandFirstProvider, string? engineError = null)
    {
        _dashboard = dashboard;
        _engineError = engineError;
        _showingSettings = settings;
        _previewNow = dashboard.GeneratedAt;
        if (expandFirstProvider && dashboard.Providers.FirstOrDefault(p => p.Enabled) is { } first)
        {
            _expandedProviders.Add(first.Id);
        }
        Render();
        Content = null;
        return Frame;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_showingSettings)
            {
                ShowScreen(settings: false);
            }
            else
            {
                Hide();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F5 || (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control))
        {
            _actions.RefreshNow();
            e.Handled = true;
        }
    }

    private void ShowScreen(bool settings)
    {
        _showingSettings = settings;
        Render();
    }

    private void PositionNearTray()
    {
        if (!IsVisible || !ShowActivated)
        {
            return;
        }
        try
        {
            PanelPlacement.Place(new WindowInteropHelper(this).Handle, StripBounds?.Invoke(), Frame.Margin.Right);
        }
        catch (ExternalException error)
        {
            AppLog.Error($"panel placement failed: {error}");
        }
    }

    /// <summary>A resolution or scale change while the panel is open: fit and place it again.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && !_closing)
            {
                Render();
                PositionNearTray();
            }
        });

    // MARK: - Rendering

    private void Render()
    {
        var theme = Theme.Current;
        Frame.Background = theme.Tray;
        Frame.BorderBrush = theme.PanelBorder;
        Foreground = theme.TextPrimary;
        Resources["ScrollThumbBrush"] = theme.ScrollThumb;
        var focusKey = FocusedControlKey();
        Root.Children.Clear();

        if (_showingSettings)
        {
            var topBar = BuildTopBar(theme);
            DockPanel.SetDock(topBar, Dock.Top);
            Root.Children.Add(topBar);
        }

        var footer = BuildFooter(theme);
        DockPanel.SetDock(footer, Dock.Bottom);
        Root.Children.Add(footer);

        var body = new StackPanel
        {
            Margin = new Thickness(OuterPadding, _showingSettings ? 4 : OuterPadding, OuterPadding, 12),
        };
        if (_showingSettings)
        {
            BuildSettings(body, theme);
        }
        else
        {
            BuildDashboard(body, theme);
        }
        // Keep the reader's place when a refresh lands or a caret, period, or switch re-renders the
        // panel; switching between the dashboard and Settings starts the new screen at the top.
        var offset = _scroller != null && _scrollerShowsSettings == _showingSettings ? _scroller.VerticalOffset : 0;
        _scroller = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // Grow with the content up to the screen, like the Mac panel.
            MaxHeight = _previewNow != null
                ? double.PositiveInfinity
                : Math.Max(240, WorkAreaHeight() - 24 - 16 - 64 - (_showingSettings ? 44 : 0)),
            Focusable = false,
        };
        _scrollerShowsSettings = _showingSettings;
        // Applied on the next layout pass, once the new content has a height to scroll through.
        _scroller.ScrollToVerticalOffset(offset);
        Root.Children.Add(_scroller);
        if (focusKey != null)
        {
            RestoreFocus(focusKey);
        }
    }

    /// <summary>The automation id or name of the control that has keyboard focus, if it's in the panel.</summary>
    private string? FocusedControlKey()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused || !Root.IsAncestorOf(focused))
        {
            return null;
        }
        var id = AutomationProperties.GetAutomationId(focused);
        return string.IsNullOrEmpty(id) ? NullIfEmpty(AutomationProperties.GetName(focused)) : id;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// A re-render replaces every control, so keyboard focus would fall back to the window and the next
    /// Tab would start over. Move it to the rebuilt control with the same key instead.
    /// </summary>
    private void RestoreFocus(string key) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var match = Descendants(Root).FirstOrDefault(element => element.Focusable &&
                (AutomationProperties.GetAutomationId(element) == key || AutomationProperties.GetName(element) == key));
            match?.Focus();
        });

    private static IEnumerable<UIElement> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is UIElement element)
            {
                yield return element;
            }
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The taskbar monitor's current work-area height, read live so a resolution change counts.</summary>
    private static double WorkAreaHeight()
    {
        try
        {
            return PanelPlacement.WorkAreaHeightDip();
        }
        catch (ExternalException error)
        {
            AppLog.Error($"work area read failed, using the startup value: {error}");
            return SystemParameters.WorkArea.Height;
        }
    }

    /// <summary>Settings' navigation bar: a Back capsule and the centered screen title.</summary>
    private UIElement BuildTopBar(Theme theme)
    {
        var grid = new Grid { Height = 44, Margin = new Thickness(OuterPadding, 0, OuterPadding, 0) };
        grid.Children.Add(Text("Settings", theme.TextPrimary, 13, FontWeights.SemiBold,
            horizontalAlignment: HorizontalAlignment.Center, verticalAlignment: VerticalAlignment.Center));
        var back = new StackPanel { Orientation = Orientation.Horizontal };
        back.Children.Add(Glyph(Glyphs.ChevronLeft, theme.TextPrimary, 10, stroke: 1.6, margin: new Thickness(0, 0, 5, 0)));
        back.Children.Add(Text("Back", theme.TextPrimary, 13, FontWeights.Medium, verticalAlignment: VerticalAlignment.Center));
        var button = Capsule(back, theme, () => ShowScreen(settings: false), new Thickness(10, 0, 14, 0), "Back");
        button.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(button);
        return grid;
    }

    /// <summary>
    /// The pinned footer: "Quota Tray x.y.z" over the next-update countdown (click it to refresh now),
    /// and on the dashboard the Options menu capsule.
    /// </summary>
    /// <summary>Quota Tray's own version (the csproj's Version), without the build's commit suffix.</summary>
    private static readonly string AppVersion =
        (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion
            .Split('+')[0] ?? "";

    private UIElement BuildFooter(Theme theme)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(Text($"Quota Tray {AppVersion}", theme.TextSecondary, 11));
        _footerStatus = Text("", theme.TextSecondary, 11);
        _footerStatus.Cursor = Cursors.Hand;
        // Underlined under the pointer, so it reads as the refresh link it is.
        _footerStatus.MouseEnter += (sender, _) => ((TextBlock)sender).TextDecorations = TextDecorations.Underline;
        _footerStatus.MouseLeave += (sender, _) => ((TextBlock)sender).TextDecorations = null;
        _footerStatus.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_refreshing)
            {
                _actions.RefreshNow();
            }
        };
        identity.Children.Add(_footerStatus);
        UpdateFooterStatus();
        grid.Children.Add(identity);

        if (!_showingSettings)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(Text("Options", theme.TextPrimary, 13, FontWeights.SemiBold, verticalAlignment: VerticalAlignment.Center));
            label.Children.Add(Glyph(Glyphs.ChevronDown, theme.TextPrimary, 9, stroke: 1.8, margin: new Thickness(6, 1, 0, 0)));
            FrameworkElement? options = null;
            options = Capsule(label, theme, () => ShowMenu(options!, above: true, new MenuItemSpec[]
            {
                new("Settings", () => ShowScreen(settings: true)),
                new("Open Log Folder", _actions.OpenLogFolder),
                MenuItemSpec.Separator,
                new("Quit Quota Tray", _actions.Quit),
            }), new Thickness(14, 0, 12, 0), "Options");
            Grid.SetColumn(options, 1);
            grid.Children.Add(options);
        }

        return new Border
        {
            Background = theme.FooterFill,
            BorderBrush = theme.Separator,
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 12, 12),
            Padding = new Thickness(OuterPadding, 12, OuterPadding, 12),
            Child = grid,
        };
    }

    private void UpdateFooterStatus()
    {
        if (_footerStatus == null)
        {
            return;
        }
        _footerStatus.Text = FooterStatus(_previewNow ?? DateTimeOffset.UtcNow);
    }

    /// <summary>"Next update in 3m" (seconds under a minute), or "Updating…" while a refresh runs.</summary>
    private string FooterStatus(DateTimeOffset now)
    {
        if (_refreshing)
        {
            return "Updating…";
        }
        var newest = _dashboard?.Providers
            .Where(p => p.Enabled && p.RefreshedAt != null)
            .Select(p => p.RefreshedAt!.Value)
            .DefaultIfEmpty(now)
            .Max() ?? now;
        var remaining = newest + RefreshInterval - now;
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds <= 0)
        {
            // Due: the tray's next tick (at most a minute away while the panel is open) refreshes.
            return "Updating…";
        }
        return seconds >= 60 ? $"Next update in {(int)Math.Ceiling(seconds / 60.0)}m" : $"Next update in {seconds}s";
    }
}
