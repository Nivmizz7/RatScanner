using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using RatScanner.Display;
using RatScanner.View;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using NotifyIcon = System.Windows.Forms.NotifyIcon;

namespace RatScanner;

/// <summary>
/// Interaction logic for PageSwitcher.xaml
/// </summary>
public partial class PageSwitcher : Window
{
    public const int DefaultWidth = 1080;
    public const int DefaultHeight = 720;
    public const int MinimumWidth = 450;
    public const int MinimumHeight = 520;

    private NotifyIcon _notifyIcon = null!;
    private ContextMenuStrip _contextMenuStrip = new();
    private AppStateService? _appStateService;
    private WindowChrome _normalChrome = null!;
    private readonly WindowChrome _minimalChrome = new()
    {
        CaptionHeight = 0,
        CornerRadius = new CornerRadius(0),
        GlassFrameThickness = new Thickness(0),
        ResizeBorderThickness = new Thickness(0),
        UseAeroCaptionButtons = false,
    };

    private static PageSwitcher _instance = null!;
    public static PageSwitcher Instance => _instance ??= new PageSwitcher();

    private UserControl? activeControl;
    private bool _isMinimalUi;
    private bool _isExiting;
    private bool _hasPersistedWindowBounds;
    private WindowState _restoreWindowState = WindowState.Normal;
    private Rect _restoreBounds;

    // Screen-space offset from the window's outer top-right corner to the
    // minimal-UI button center. Captured when the user clicks the button to
    // enter minimal UI, used when exiting to position the main window so the
    // button lands under the mouse — no matter where they double-clicked on
    // the compact overlay.
    private Vector? _minimalButtonOffset;

    public PageSwitcher()
    {
        // Do not publish the singleton until construction succeeds; a half-built
        // window must not be reachable via PageSwitcher.Instance after a startup throw.
        try
        {
            RatConfig.LoadConfig();

            InitializeComponent();
            _normalChrome = WindowChrome.GetWindowChrome(this) ?? new WindowChrome();
            Title = Constants.Branding.Name;
            BrandTextBlock.Text = Constants.Branding.Name;
            VersionTextBlock.Text = GetProductVersionDisplay();
            ApplyWindowsTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            ResetWindowSize();
            Navigate(BlazorUI.Instance);

            _appStateService = BlazorUI.Instance.Services.GetRequiredService<AppStateService>();
            _appStateService.SidebarOpenChanged += OnSidebarOpenChanged;
            _appStateService.FocusNavigationToggleRequested += OnFocusNavigationToggleRequested;
            UpdateNavigationToggle(_appStateService.IsSidebarOpen);
            UpdateCaptionButtonAccessibility();
            UpdateMinimalUIButton();

            AddJumpList();
            AddTrayIcon();

            RestoreWindowBounds();
            Topmost = RatConfig.AlwaysOnTop;
            if (RatConfig.LastWindowMode == RatConfig.WindowMode.Minimal)
                ShowMinimalUI();

            _instance = this;
        }
        catch (Exception e)
        {
            // LogError terminates the process; do not leave a half-built singleton published.
            Logger.LogError(e.Message, e);
        }
    }

    internal void ResetWindowSize()
    {
        WindowRoot.Margin = new Thickness(7);
        SizeToContent = SizeToContent.Manual;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = MinimumWidth;
        MinHeight = MinimumHeight;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        Width = DefaultWidth;
        Height = DefaultHeight;
    }

    /// <summary>
    /// Restores the persisted normal-mode window size and position from the
    /// previous session. Saved bounds are validated against the currently
    /// attached displays: a monitor layout change since the last run must not
    /// resurrect the window on a screen that no longer exists.
    /// </summary>
    private void RestoreWindowBounds()
    {
        if (
            !TryGetRestorableBounds(
                RatConfig.LastWindowPositionX,
                RatConfig.LastWindowPositionY,
                RatConfig.LastWindowWidth,
                RatConfig.LastWindowHeight,
                Width,
                Height,
                GetLogicalWorkingAreas(),
                out Rect bounds
            )
        )
            return;

        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
    }

    /// <summary>
    /// True when enough of the window (roughly the caption strip) intersects
    /// the working area of at least one attached display for the user to grab it.
    /// </summary>
    internal static bool IsVisibleOnAnyScreen(double left, double top, double width, double height)
    {
        return IsVisibleOnAnyScreen(left, top, width, height, GetLogicalWorkingAreas());
    }

    internal static bool IsVisibleOnAnyScreen(
        double left,
        double top,
        double width,
        double height,
        IReadOnlyList<LogicalWorkingArea> workingAreas
    )
    {
        // The title bar is the reliable grab strip; require most of it plus a
        // minimal slice of the window body to land on some screen.
        double grabLeft = left + Math.Min(40, width / 4);
        double grabRight = left + width - Math.Min(40, width / 4);
        double grabTop = top;
        double grabBottom = top + Math.Min(48, height);

        foreach (LogicalWorkingArea area in workingAreas)
        {
            bool intersects =
                grabRight > area.Left && grabLeft < area.Right && grabBottom > area.Top && grabTop < area.Bottom;
            if (intersects)
                return true;
        }
        return false;
    }

    internal static bool TryGetRestorableBounds(
        int savedLeft,
        int savedTop,
        int savedWidth,
        int savedHeight,
        double defaultWidth,
        double defaultHeight,
        IReadOnlyList<LogicalWorkingArea> workingAreas,
        out Rect bounds
    )
    {
        bounds = Rect.Empty;
        if (savedLeft == int.MinValue || savedTop == int.MinValue)
            return false;

        bool hasValidSavedSize = savedWidth >= MinimumWidth && savedHeight >= MinimumHeight;
        double width = hasValidSavedSize ? savedWidth : defaultWidth;
        double height = hasValidSavedSize ? savedHeight : defaultHeight;
        if (
            !double.IsFinite(width)
            || !double.IsFinite(height)
            || width < MinimumWidth
            || height < MinimumHeight
            || !IsVisibleOnAnyScreen(savedLeft, savedTop, width, height, workingAreas)
        )
            return false;

        bounds = new Rect(savedLeft, savedTop, width, height);
        return true;
    }

    internal static bool TryPhysicalToLogicalWorkingArea(
        System.Drawing.Rectangle physicalArea,
        double dpiScale,
        bool isDpiReliable,
        out LogicalWorkingArea workingArea
    )
    {
        workingArea = default;
        if (!isDpiReliable || !double.IsFinite(dpiScale) || dpiScale <= 0)
            return false;

        workingArea = new LogicalWorkingArea(
            physicalArea.Left / dpiScale,
            physicalArea.Top / dpiScale,
            physicalArea.Right / dpiScale,
            physicalArea.Bottom / dpiScale
        );
        return true;
    }

    private static IReadOnlyList<LogicalWorkingArea> GetLogicalWorkingAreas()
    {
        System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
        List<LogicalWorkingArea> workingAreas = new(screens.Length);
        foreach (System.Windows.Forms.Screen screen in screens)
        {
            (double dpiScale, bool isDpiReliable) = WindowsGameDisplayService.GetDpiScale(screen.Bounds);
            // WinForms exposes physical pixels while WPF persists window
            // coordinates in device-independent units.
            if (
                TryPhysicalToLogicalWorkingArea(
                    screen.WorkingArea,
                    dpiScale,
                    isDpiReliable,
                    out LogicalWorkingArea workingArea
                )
            )
                workingAreas.Add(workingArea);
        }
        return workingAreas;
    }

    internal readonly record struct LogicalWorkingArea(double Left, double Top, double Right, double Bottom);

    internal void Navigate(UserControl nextControl, object? state = null)
    {
        if (!(nextControl is ISwitchable))
            throw new ArgumentException("NextPage is not ISwitchable! " + nextControl.Name);

        if (activeControl != null)
        {
            ISwitchable activeControlSwitchable = (ISwitchable)activeControl;
            activeControlSwitchable.OnClose();
        }

        ContentControl.Content = nextControl;
        activeControl = nextControl;

        ISwitchable nextControlSwitchable = (ISwitchable)nextControl;
        if (state != null)
            nextControlSwitchable.UtilizeState(state);

        nextControlSwitchable.OnOpen();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (RatConfig.MinimizeToTray && WindowState == WindowState.Minimized)
            Hide();

        base.OnStateChanged(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
            PersistWindowBoundsOnce();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_appStateService != null)
        {
            _appStateService.SidebarOpenChanged -= OnSidebarOpenChanged;
            _appStateService.FocusNavigationToggleRequested -= OnFocusNavigationToggleRequested;
        }
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _contextMenuStrip.Dispose();

        base.OnClosed(e);
        ExitApplication();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.Invoke(ApplyWindowsTheme);
    }

    private void ApplyWindowsTheme()
    {
        bool useLightTheme = true;
        using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
        );
        if (personalize?.GetValue("AppsUseLightTheme") is int value)
            useLightTheme = value != 0;

        Application.Current.Resources["NativeTitleBarBackgroundBrush"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(useLightTheme ? "#FFFFFF" : "#101620")
        );
        Application.Current.Resources["NativeTitleBarForegroundBrush"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(useLightTheme ? "#111318" : "#F4F7FA")
        );
        Application.Current.Resources["NativeTitleBarInactiveForegroundBrush"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(useLightTheme ? "#777777" : "#778291")
        );
        Application.Current.Resources["NativeTitleBarHoverBrush"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(useLightTheme ? "#E5E5E5" : "#202A36")
        );
        Application.Current.Resources["NativeTitleBarPressedBrush"] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(useLightTheme ? "#CACACA" : "#293341")
        );
    }

    private void AddJumpList()
    {
        JumpTask showUITask = new()
        {
            Title = Presentation.PresentationText.T("JumpShowUI", "Show UI"),
            Arguments = "/showUI",
            Description = Presentation.PresentationText.T(
                "JumpShowUIDescription",
                "Opens the main interface of RatScanner"
            ),
            IconResourcePath = Environment.ProcessPath,
            ApplicationPath = Environment.ProcessPath,
        };

        JumpTask showMinimalUITask = new()
        {
            Title = Presentation.PresentationText.T("JumpShowMinimalUI", "Show Minimal UI"),
            Arguments = "/showMinimalUI",
            Description = Presentation.PresentationText.T(
                "JumpShowMinimalUIDescription",
                "Opens the minimal interface of RatScanner"
            ),
            IconResourcePath = Environment.ProcessPath,
            ApplicationPath = Environment.ProcessPath,
        };

        JumpTask showOverlayTask = new()
        {
            Title = Presentation.PresentationText.T("JumpShowOverlay", "Show Overlay"),
            Arguments = "/showOverlay",
            Description = Presentation.PresentationText.T(
                "JumpShowOverlayDescription",
                "Opens the interactive overlay of RatScanner"
            ),
            IconResourcePath = Environment.ProcessPath,
            ApplicationPath = Environment.ProcessPath,
        };

        JumpList jumpList = new();
        jumpList.JumpItems.Add(showUITask);
        jumpList.JumpItems.Add(showMinimalUITask);
        jumpList.JumpItems.Add(showOverlayTask);
        jumpList.ShowFrequentCategory = false;
        jumpList.ShowRecentCategory = false;

        JumpList.SetJumpList(Application.Current, jumpList);
    }

    [MemberNotNull(nameof(_notifyIcon))]
    private void AddTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = Presentation.PresentationText.T("TrayShow", "Show"),
            Visible = true,
            Icon = Properties.Resources.RatLogoSmall,
        };

        _contextMenuStrip.Items.Add(
            Presentation.PresentationText.T("JumpShowUI", "Show UI"),
            null,
            OnContextMenuShowUI
        );
        _contextMenuStrip.Items.Add(
            Presentation.PresentationText.T("JumpShowMinimalUI", "Show Minimal UI"),
            null,
            OnContextMenuShowMinimalUI
        );
        _contextMenuStrip.Items.Add(
            Presentation.PresentationText.T("JumpShowOverlay", "Show Overlay"),
            null,
            OnContextMenuShowOverlay
        );
        _contextMenuStrip.Items.Add(
            Presentation.PresentationText.T("TrayExit", "Exit"),
            null,
            OnContextMenuExitApplication
        );

        _notifyIcon.ContextMenuStrip = _contextMenuStrip;

        _notifyIcon.MouseClick += (sender, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                RestoreWindow();
        };
    }

    private void OnContextMenuShowOverlay(object? sender, EventArgs e) => ShowOverlay();

    private void OnContextMenuShowUI(object? sender, EventArgs e)
    {
        RestoreWindow();
        ShowUI();
    }

    private void OnContextMenuShowMinimalUI(object? sender, EventArgs e)
    {
        RestoreWindow();
        ShowMinimalUI();
    }

    // Ensure a hidden/minimized window is actually visible and focused before we swap its
    // content, so the tray "Show UI" / "Show Minimal UI" commands work from any state.
    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnContextMenuExitApplication(object? sender, EventArgs e) => ExitApplication();

    internal void ShowOverlay()
    {
        BlazorUI.BlazorInteractableOverlay.ShowOverlay();
    }

    internal void ShowUI()
    {
        if (_isMinimalUi)
        {
            // Capture the minimal UI's current geometry BEFORE any changes.
            // The minimal window has WindowRoot.Margin=0, so Left+ActualWidth is
            // the actual visible right edge. ShowMinimalUI shifted the window left
            // by half its width for ergonomics, so we compensate here to land the
            // main window's title-bar corner at the correct anchor point.
            double minimalWidth = ActualWidth;
            double minimalRight = Left + minimalWidth + minimalWidth / 2;
            double minimalTop = Top;

            // If we have a captured button offset (entered via the title-bar
            // button) and the mouse is currently over the minimal UI, position
            // the main window so the minimal-UI button lands exactly under the
            // mouse. This prevents the user from accidentally landing on the
            // close button when double-clicking an arbitrary spot on the
            // compact overlay.
            System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
            bool mouseOverMinimal =
                cursor.X >= Left && cursor.X <= Left + ActualWidth && cursor.Y >= Top && cursor.Y <= Top + ActualHeight;
            bool useMouseAnchor = _minimalButtonOffset.HasValue && mouseOverMinimal;

            // Fade out during the transition so the user never sees the
            // intermediate default-size flash from ResetWindowSize or the chrome
            // swap. Using Opacity instead of Visibility=Hidden keeps the window
            // alive in the taskbar — Visibility toggling makes the taskbar entry
            // disappear and reappear, which looks like the app is restarting.
            double savedOpacity = Opacity;
            Opacity = 0;

            // Stop SizeToContent before navigating so the content swap doesn't
            // trigger a window resize via SizeToContent.
            SizeToContent = SizeToContent.Manual;

            // Navigate away from MinimalMenu FIRST, before any window size changes.
            // MinimalMenu.OnSizeChanged shifts Left when the window is on the right
            // half of the screen; if it fires during the size changes below it would
            // override the restored bounds and shift the window off-position.
            Navigate(BlazorUI.Instance);
            _isMinimalUi = false;

            RatConfig.LastWindowMode = RatConfig.WindowMode.Normal;
            WindowChrome.SetWindowChrome(this, _normalChrome);
            ResetWindowSize();
            SetBackgroundOpacity(1);
            ShowTitleBar();

            const double chromeMargin = 7;

            if (_restoreWindowState == WindowState.Maximized)
            {
                if (!_restoreBounds.IsEmpty)
                {
                    WindowState = WindowState.Normal;
                    Width = _restoreBounds.Width;
                    Height = _restoreBounds.Height;

                    if (useMouseAnchor)
                    {
                        // Position so the minimal-UI button (at offset from the
                        // window's outer top-right) lands at the current mouse
                        // position: Left + Width + offset.X = mouseX.
                        Left = cursor.X - _minimalButtonOffset!.Value.X - Width;
                        Top = cursor.Y - _minimalButtonOffset.Value.Y;
                    }
                    else
                    {
                        Left = minimalRight - Width + chromeMargin;
                        Top = minimalTop - chromeMargin;
                    }
                }
                WindowState = WindowState.Maximized;
            }
            else if (!_restoreBounds.IsEmpty)
            {
                WindowState = WindowState.Normal;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;

                if (useMouseAnchor)
                {
                    Left = cursor.X - _minimalButtonOffset!.Value.X - Width;
                    Top = cursor.Y - _minimalButtonOffset.Value.Y;
                }
                else
                {
                    Left = minimalRight - Width + chromeMargin;
                    Top = minimalTop - chromeMargin;
                }
            }
            else
            {
                // Entered minimal UI straight from startup (LastWindowMode =
                // Minimal), so no in-session restore bounds exist. Fall back
                // to the persisted normal-mode bounds.
                WindowState = WindowState.Normal;
                RestoreWindowBounds();
            }

            // The offset is only valid for one exit; clear it so a subsequent
            // tray-menu exit doesn't use a stale value.
            _minimalButtonOffset = null;

            Opacity = savedOpacity;
            Activate();
        }
        else
        {
            // The window is already in normal UI mode; just ensure it is visible.
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Show();
            Activate();
        }

        RatConfig.LastWindowMode = RatConfig.WindowMode.Normal;
        UpdateMinimalUIButton();
    }

    internal void ShowMinimalUI()
    {
        if (_isMinimalUi)
        {
            Show();
            Activate();
            return;
        }

        // Capture the top-right corner of the title bar (where the minimal-UI
        // button sits). The main window has a 7px chrome margin (WindowRoot.Margin
        // = 7, ResizeBorderThickness = 7), so the title bar's actual top-right is
        // inset from the outer window bounds. Anchoring at the title bar corner
        // instead of the outer window corner makes the minimal UI appear exactly
        // where the button is, not 7px above and to the right.
        const double chromeMargin = 7;
        double rightEdge = Left + Width - chromeMargin;
        double topEdge = Top + chromeMargin;

        _restoreBounds = RestoreBounds;
        _restoreWindowState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;

        // Fade out during the transition so the user doesn't see the window
        // shrink toward the top-left before we reposition. Using Opacity instead
        // of Visibility=Hidden keeps the window alive in the taskbar.
        double savedOpacity = Opacity;
        Opacity = 0;

        RatConfig.LastWindowMode = RatConfig.WindowMode.Minimal;
        WindowChrome.SetWindowChrome(this, _minimalChrome);
        WindowRoot.Margin = new Thickness(0);
        CollapseTitleBar();
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 0;
        MinHeight = 0;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        WindowState = WindowState.Normal;
        SizeToContent = SizeToContent.WidthAndHeight;
        SetBackgroundOpacity(RatConfig.MinimalUi.Opacity / 100f);
        Navigate(MinimalMenu.Instance);

        // Force a layout pass so the content-derived size from SizeToContent is
        // available, then anchor the minimal window's top-right corner near the
        // main window's top-right corner (where the minimal-UI button sits).
        UpdateLayout();
        AnchorNearTopRight(rightEdge, topEdge);

        // Shift the minimal UI left by half its width so the mouse — which was
        // near the right edge of the title bar (where the minimal-UI button sits)
        // — lands closer to the center of the compact overlay instead of at its
        // right edge.
        if (ActualWidth > 0)
            Left -= ActualWidth / 2;

        Opacity = savedOpacity;
        Activate();

        _isMinimalUi = true;
        UpdateMinimalUIButton();
    }

    /// <summary>
    /// Positions the window so its top-right corner is at the given screen
    /// coordinates. Falls back to measuring the content directly if
    /// ActualWidth/Height are not yet updated (SizeToContent may defer the
    /// Win32 resize).
    /// </summary>
    private void AnchorNearTopRight(double rightEdge, double topEdge)
    {
        double w = ActualWidth;
        double h = ActualHeight;

        if (w <= 0 || h <= 0 || double.IsNaN(w) || double.IsNaN(h))
        {
            if (ContentControl.Content is FrameworkElement content)
            {
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                w = content.DesiredSize.Width;
                h = content.DesiredSize.Height;
            }
        }

        if (w > 0 && h > 0)
        {
            Left = rightEdge - w;
            Top = topEdge;
        }
    }

    internal void ExitApplication()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        PersistWindowBoundsOnce();
        Application.Current.Shutdown();
    }

    private void PersistWindowBoundsOnce()
    {
        if (_hasPersistedWindowBounds)
            return;

        PersistWindowBounds();
        RatConfig.SaveConfig();
        _hasPersistedWindowBounds = true;
    }

    /// <summary>
    /// Persists the normal-mode window bounds so the next launch restores the
    /// user's size and position. When closing from minimal UI or a maximized
    /// window, the pre-minimal / pre-maximize restore bounds are what the user
    /// actually arranged, so those are saved instead of the live geometry.
    /// </summary>
    private void PersistWindowBounds()
    {
        if (
            !TryGetPersistableBounds(
                _isMinimalUi,
                WindowState,
                _restoreBounds,
                RestoreBounds,
                new Rect(Left, Top, Width, Height),
                out Rect bounds
            )
        )
            return;

        RatConfig.LastWindowPositionX = (int)bounds.X;
        RatConfig.LastWindowPositionY = (int)bounds.Y;
        RatConfig.LastWindowWidth = (int)bounds.Width;
        RatConfig.LastWindowHeight = (int)bounds.Height;
    }

    internal static bool TryGetPersistableBounds(
        bool isMinimalUi,
        WindowState windowState,
        Rect minimalRestoreBounds,
        Rect stateRestoreBounds,
        Rect liveBounds,
        out Rect bounds
    )
    {
        bounds =
            isMinimalUi ? minimalRestoreBounds
            : windowState != WindowState.Normal ? stateRestoreBounds
            : liveBounds;
        return !bounds.IsEmpty
            && double.IsFinite(bounds.X)
            && double.IsFinite(bounds.Y)
            && double.IsFinite(bounds.Width)
            && double.IsFinite(bounds.Height)
            && bounds.Width >= MinimumWidth
            && bounds.Height >= MinimumHeight;
    }

    private void OnToggleSidebar(object? sender, RoutedEventArgs e)
    {
        _appStateService?.ToggleSidebar();
    }

    private void OnSidebarOpenChanged(object? sender, bool open)
    {
        Dispatcher.Invoke(() => UpdateNavigationToggle(open));
    }

    private void OnFocusNavigationToggleRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => NavToggleButton?.Focus());
    }

    private void UpdateNavigationToggle(bool open)
    {
        if (NavToggleIcon == null || NavToggleButton == null)
            return;

        string tooltipKey = open ? "CollapseNavigation" : "ExpandNavigation";
        string tooltip = Presentation.PresentationText.T(tooltipKey, tooltipKey);
        NavToggleButton.ToolTip = tooltip;
        AutomationProperties.SetName(NavToggleButton, tooltip);
        NavToggleIcon.Data = open ? GetPanelLeftCloseGeometry() : GetPanelLeftOpenGeometry();
    }

    private static Geometry GetPanelLeftCloseGeometry() => Geometry.Parse("M 4,4 V 20 M 14,8 L 8,12 L 14,16");

    private static Geometry GetPanelLeftOpenGeometry() => Geometry.Parse("M 4,4 V 20 M 8,8 L 14,12 L 8,16");

    private static Geometry GetMinimalUIIconGeometry() =>
        Geometry.Parse("M 3,3 H 21 V 21 H 3 Z M 13,13 H 21 V 21 H 13 Z");

    private void OnTitleBarMinimal(object? sender, RoutedEventArgs e)
    {
        if (_isMinimalUi)
        {
            ShowUI();
        }
        else
        {
            // Capture the mouse's screen-space offset from the window's outer
            // top-right corner. The mouse is on the minimal-UI button, so this
            // gives us the button's position relative to the window. We'll use
            // it when exiting minimal UI to keep the button under the mouse.
            System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
            _minimalButtonOffset = new Vector(cursor.X - (Left + Width), cursor.Y - Top);
            ShowMinimalUI();
        }
    }

    private void OnTitleBarMinimize(object? sender, RoutedEventArgs e)
    {
        RatConfig.LastWindowMode = RatConfig.WindowMode.Minimized;
        WindowState = WindowState.Minimized;
    }

    private void OnTitleBarClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateCaptionButtonAccessibility()
    {
        if (MinimizeButton != null)
        {
            string text = Presentation.PresentationText.T("MinimizeWindow", "Minimize");
            MinimizeButton.ToolTip = text;
            AutomationProperties.SetName(MinimizeButton, text);
        }

        if (CloseButton != null)
        {
            string text = Presentation.PresentationText.T("CloseWindow", "Close");
            CloseButton.ToolTip = text;
            AutomationProperties.SetName(CloseButton, text);
        }
    }

    private void UpdateMinimalUIButton()
    {
        if (MinimalUIIcon == null || MinimalUIButton == null)
            return;

        string key = _isMinimalUi ? "ExitMinimalUi" : "EnterMinimalUi";
        string fallback = _isMinimalUi ? "Exit minimal UI" : "Enter minimal UI";
        string text = Presentation.PresentationText.T(key, fallback);
        MinimalUIButton.ToolTip = text;
        AutomationProperties.SetName(MinimalUIButton, text);
        MinimalUIIcon.Data = GetMinimalUIIconGeometry();
    }

    private string GetProductVersionDisplay()
    {
        string version = RatConfig.VersionDisplay;
        int plus = version.IndexOf('+');
        if (plus >= 0)
            version = version[..plus];

        return version;
    }

    internal void CollapseTitleBar()
    {
        TitleBar.Visibility = Visibility.Collapsed;
    }

    internal void ShowTitleBar()
    {
        TitleBar.Visibility = Visibility.Visible;
    }

    internal void SetBackgroundOpacity(float opacity)
    {
        Background.Opacity = Math.Clamp(opacity, 1f / 510f, 1f);
    }
}
