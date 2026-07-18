using System;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
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
    public const int MinimumWidth = 680;
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

    public PageSwitcher()
    {
        // Do not publish the singleton until construction succeeds; a half-built
        // window must not be reachable via PageSwitcher.Instance after a startup throw.
        try
        {
            RatConfig.LoadConfig();

            InitializeComponent();
            _normalChrome = WindowChrome.GetWindowChrome(this) ?? new WindowChrome();
            ApplyWindowsTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            ResetWindowSize();
            Navigate(BlazorUI.Instance);

            _appStateService = BlazorUI.Instance.Services.GetRequiredService<AppStateService>();
            _appStateService.SidebarOpenChanged += OnSidebarOpenChanged;
            _appStateService.FocusNavigationToggleRequested += OnFocusNavigationToggleRequested;
            UpdateNavigationToggle(_appStateService.SidebarOpen);
            UpdateCaptionButtonAccessibility();

            AddJumpList();
            AddTrayIcon();

            if (RatConfig.LastWindowPositionX != int.MinValue && RatConfig.LastWindowPositionY != int.MinValue)
            {
                Left = RatConfig.LastWindowPositionX;
                Top = RatConfig.LastWindowPositionY;
            }
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

        UpdateMaximizeRestoreIcon();
        base.OnStateChanged(e);
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
        RatConfig.LastWindowMode = RatConfig.WindowMode.Normal;
        WindowChrome.SetWindowChrome(this, _normalChrome);
        ResetWindowSize();
        SetBackgroundOpacity(1);
        ShowTitleBar();
        Navigate(BlazorUI.Instance);
    }

    internal void ShowMinimalUI()
    {
        RatConfig.LastWindowMode = RatConfig.WindowMode.Minimal;
        WindowChrome.SetWindowChrome(this, _minimalChrome);
        WindowRoot.Margin = new Thickness(0);
        CollapseTitleBar();
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 0;
        MinHeight = 0;
        SizeToContent = SizeToContent.WidthAndHeight;
        SetBackgroundOpacity(RatConfig.MinimalUi.Opacity / 100f);
        Navigate(MinimalMenu.Instance);
    }

    internal void ExitApplication()
    {
        RatConfig.LastWindowPositionX = (int)Left;
        RatConfig.LastWindowPositionY = (int)Top;
        RatConfig.SaveConfig();
        Application.Current.Shutdown();
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

        string tooltipKey = open ? "CloseNavigation" : "OpenNavigation";
        string tooltip = Presentation.PresentationText.T(tooltipKey, tooltipKey);
        NavToggleButton.ToolTip = tooltip;
        AutomationProperties.SetName(NavToggleButton, tooltip);
        NavToggleIcon.Data = open ? GetPanelLeftCloseGeometry() : GetPanelLeftOpenGeometry();
    }

    private static Geometry GetPanelLeftCloseGeometry() =>
        Geometry.Parse("M 3,3 H 21 V 21 H 3 Z M 9,3 V 21 M 16,15 L 13,12 L 16,9");

    private static Geometry GetPanelLeftOpenGeometry() =>
        Geometry.Parse("M 3,3 H 21 V 21 H 3 Z M 9,3 V 21 M 8,9 L 11,12 L 8,15");

    private void OnTitleBarMinimize(object? sender, RoutedEventArgs e)
    {
        RatConfig.LastWindowMode = RatConfig.WindowMode.Minimized;
        WindowState = WindowState.Minimized;
    }

    private void OnTitleBarMaximizeRestore(object? sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        else
            WindowState = WindowState.Maximized;
    }

    private void OnTitleBarClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeRestoreIcon()
    {
        if (MaximizeRestoreIcon == null)
            return;

        const string maximizePath = "M 3,3 H 21 V 21 H 3 Z";
        const string restorePath = "M 4,10 H 18 V 20 H 4 Z M 8,4 H 20 V 14 H 18 V 6 H 8 Z";

        bool isMaximized = WindowState == WindowState.Maximized;
        MaximizeRestoreIcon.Data = Geometry.Parse(isMaximized ? restorePath : maximizePath);

        if (MaximizeRestoreButton != null)
        {
            string key = isMaximized ? "RestoreWindow" : "MaximizeWindow";
            string text = Presentation.PresentationText.T(key, isMaximized ? "Restore" : "Maximize");
            MaximizeRestoreButton.ToolTip = text;
            AutomationProperties.SetName(MaximizeRestoreButton, text);
        }
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

        UpdateMaximizeRestoreIcon();
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
