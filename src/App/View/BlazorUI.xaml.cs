using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MudBlazor.Services;
using RatScanner.Presentation;
using RatScanner.ViewModel;

namespace RatScanner.View;

/// <summary>
/// Interaction logic for BlazorUI.xaml
/// </summary>
public sealed partial class BlazorUI : UserControl, ISwitchable, IDisposable
{
    private static readonly object InstanceLock = new();
    private static BlazorUI _instance = null!;
    private static bool _shutdownStarted;

    public static BlazorUI Instance
    {
        get
        {
            lock (InstanceLock)
            {
                ObjectDisposedException.ThrowIf(_shutdownStarted, typeof(BlazorUI));
                return _instance ??= new BlazorUI();
            }
        }
    }

    internal static void DisposeInstance()
    {
        BlazorUI? instance;
        lock (InstanceLock)
        {
            _shutdownStarted = true;
            instance = _instance;
            _instance = null!;
        }
        instance?.Dispose();
    }

    public IServiceProvider Services => _serviceProvider;

    private readonly ServiceProvider _serviceProvider;
    private readonly object _overlayInitializationLock = new();
    private BlazorOverlay? _blazorOverlay;
    private DispatcherOperation? _pendingOverlayInitialization;
    private WebView2CompositionControl? _initializedWebView;
    private Window? _dpiHostWindow;
    private DispatcherOperation? _pendingDpiRefresh;
    private bool _disposed;

    private BlazorUI()
    {
        Diagnostics.PerfTrace startup = Diagnostics.PerfTraceStore.Startup;
        using Diagnostics.PerfTrace.PerfScope constructorScope = startup.Measure("startup.blazor_ui_ctor");

        ServiceCollection serviceCollection = new();
        serviceCollection.AddWpfBlazorWebView();
        serviceCollection.AddMudServices();

        serviceCollection.AddSingleton<MenuVM>(s => new MenuVM(RatScannerMain.Instance));
        serviceCollection.AddSingleton<RecentScansService>(services =>
        {
            MenuVM menu = services.GetRequiredService<MenuVM>();
            return new RecentScansService(menu.ItemScans, scan => ScanResultAdapter.Map(scan, menu, false));
        });

        LocalizationService localizationService = new();
        serviceCollection.AddSingleton(localizationService);
        // Presentation helpers build user-visible strings outside Razor; share the same catalog.
        Presentation.PresentationText.Localizer = localizationService;

        serviceCollection.AddSingleton<SettingsPersistenceService>();
        serviceCollection.AddSingleton<SettingsVM>(services => new SettingsVM(
            services.GetRequiredService<LocalizationService>(),
            services.GetRequiredService<SettingsPersistenceService>()
        ));

        System.Collections.Generic.IEnumerable<System.Drawing.Rectangle> bounds =
            System.Windows.Forms.Screen.AllScreens.Select(screen => screen.Bounds);
        int left = 0;
        int top = 0;
        foreach (System.Drawing.Rectangle bound in bounds)
        {
            if (bound.Left < left)
                left = bound.Left;
            if (bound.Top < top)
                top = bound.Top;
        }
        serviceCollection.AddSingleton<VirtualScreenOffset>(s => new VirtualScreenOffset(left, top));

        serviceCollection.AddSingleton<TarkovTrackerDB>(s => RatScannerMain.Instance.TarkovTrackerDB);
        serviceCollection.AddSingleton<AppStateService>();

        _serviceProvider = serviceCollection.BuildServiceProvider();

        Resources.Add("services", _serviceProvider);

        using (startup.Measure("startup.blazor_ui_initialize_component"))
            InitializeComponent();
        Loaded += BlazorUI_Loaded;
        Unloaded += BlazorUI_Unloaded;

        // The passive tooltip overlay owns a second WebView2 process tree. Creating
        // it before the main shell's first paint made Chromium startup, the offline
        // catalog parse, and RatEye construction land in one dispatcher burst. Queue
        // it from Loaded so WPF has attached this control to the visible window first.
    }

    private void BlazorWebView_Initialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        WebView2DpiWorkaround.CancelPendingRefresh(ref _pendingDpiRefresh);
        if (_initializedWebView is not null)
            _initializedWebView.NavigationCompleted -= WebView_Loaded;

        _initializedWebView = e.WebView;
        _initializedWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        _initializedWebView.NavigationCompleted += WebView_Loaded;

        CoreWebView2 coreWebView = _initializedWebView.CoreWebView2;
        coreWebView.SetVirtualHostNameToFolderMapping(
            "local.data",
            RatConfig.Paths.Data,
            CoreWebView2HostResourceAccessKind.Allow
        );
        coreWebView.Settings.AreDefaultContextMenusEnabled = false;
        coreWebView.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // If the host window was minimized before the WebView finished
        // initializing, the earlier SuspendActiveWebView call was a no-op.
        // Re-apply the current power state so the renderer does not start
        // compositing in the background.
        Window? hostWindow = Window.GetWindow(this);
        if (hostWindow is not null && hostWindow.WindowState == WindowState.Minimized)
            WebView2PowerSaver.Suspend(_initializedWebView);

        if (IsLoaded)
            QueueDpiRefresh();
    }

    private void BlazorWebView_Initializing(object? sender, BlazorWebViewInitializingEventArgs e) =>
        WebView2TestEnvironment.Apply(e);

    private void WebView_Loaded(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // If we are running in a development/debugger mode, open dev tools to help out
        if (Debugger.IsAttached)
            _initializedWebView?.CoreWebView2.OpenDevToolsWindow();
    }

    private void BlazorUI_Loaded(object sender, RoutedEventArgs e)
    {
        AttachDpiHostWindow();
        QueueDpiRefresh();
        if (_blazorOverlay is null && _pendingOverlayInitialization is null)
            QueueOverlayInitialization();
    }

    private void BlazorUI_Unloaded(object sender, RoutedEventArgs e)
    {
        WebView2DpiWorkaround.CancelPendingRefresh(ref _pendingDpiRefresh);
        DetachDpiHostWindow();
    }

    private void QueueOverlayInitialization()
    {
        _pendingOverlayInitialization = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            EnsureOverlayInitialized
        );
    }

    private void EnsureOverlayInitialized()
    {
        lock (_overlayInitializationLock)
        {
            _pendingOverlayInitialization = null;
            if (_disposed || _blazorOverlay is not null)
                return;

            try
            {
                double startedAtMs = Diagnostics.PerfTrace.MonotonicMs();
                _blazorOverlay = new BlazorOverlay(_serviceProvider);
                _blazorOverlay.Show();
                Diagnostics.PerfTraceStore.SetGauge(
                    "overlay.bootstrap_ms",
                    (long)Math.Round(Diagnostics.PerfTrace.MonotonicMs() - startedAtMs)
                );
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Unable to initialize the scan tooltip overlay.", exception);
            }
        }
    }

    private void HostWindow_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        QueueDpiRefresh();
    }

    private void AttachDpiHostWindow()
    {
        Window? hostWindow = Window.GetWindow(this);
        if (ReferenceEquals(_dpiHostWindow, hostWindow))
            return;

        DetachDpiHostWindow();
        _dpiHostWindow = hostWindow;
        if (_dpiHostWindow is not null)
            _dpiHostWindow.DpiChanged += HostWindow_DpiChanged;
    }

    private void DetachDpiHostWindow()
    {
        if (_dpiHostWindow is null)
            return;

        _dpiHostWindow.DpiChanged -= HostWindow_DpiChanged;
        _dpiHostWindow = null;
    }

    private void QueueDpiRefresh()
    {
        WebView2DpiWorkaround.CancelPendingRefresh(ref _pendingDpiRefresh);
        _pendingDpiRefresh = WebView2DpiWorkaround.RefreshAfterDpiChange(_initializedWebView);
    }

    private void UpdateElements() { }

    private void HyperlinkRequestNavigate(object? sender, RequestNavigateEventArgs e)
    {
        ExternalLinkLauncher.Open(e.Uri.ToString());
        e.Handled = true;
    }

    public void UtilizeState(object state)
    {
        throw new NotImplementedException();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e) { }

    private void OnPreviewMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            PageSwitcher.Instance.DragMove();
            e.Handled = true;
        }
    }

    public void OnOpen()
    {
        UpdateElements();
        WebView2PowerSaver.Resume(_initializedWebView);
    }

    // Navigating away (minimal UI) detaches the control but keeps the WebView2
    // renderer alive; suspend it so the compact overlay costs no GPU frames.
    public void OnClose() => WebView2PowerSaver.Suspend(_initializedWebView);

    /// <summary>
    /// Suspends the main UI WebView renderer without creating the singleton.
    /// Used when the host window is minimized or hidden to the tray.
    /// </summary>
    internal static void SuspendActiveWebView() => PeekInstance()?.OnClose();

    /// <summary>
    /// Resumes the main UI WebView renderer without creating the singleton.
    /// </summary>
    internal static void ResumeActiveWebView()
    {
        BlazorUI? instance = PeekInstance();
        if (instance is not null)
            WebView2PowerSaver.Resume(instance._initializedWebView);
    }

    private static BlazorUI? PeekInstance()
    {
        lock (InstanceLock)
        {
            return _shutdownStarted ? null : _instance;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Loaded -= BlazorUI_Loaded;
        Unloaded -= BlazorUI_Unloaded;
        WebView2DpiWorkaround.CancelPendingRefresh(ref _pendingDpiRefresh);
        DetachDpiHostWindow();
        if (_initializedWebView is not null)
            _initializedWebView.NavigationCompleted -= WebView_Loaded;

        _pendingOverlayInitialization?.Abort();
        _pendingOverlayInitialization = null;
        lock (_overlayInitializationLock)
        {
            _blazorOverlay?.Close();
            _blazorOverlay = null;
        }
        // WebView may not be created if startup failed or exit happened before init.
        blazorWebView?.WebView?.Dispose();
        Resources.Remove("services");
        _serviceProvider.Dispose();
    }
}
