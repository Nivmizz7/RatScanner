using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
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

    public static BlazorOverlay BlazorOverlay { get; set; } = null!;
    public static BlazorInteractableOverlay BlazorInteractableOverlay { get; set; } = null!;

    private readonly ServiceProvider _serviceProvider;
    private WebView2CompositionControl? _initializedWebView;
    private bool _disposed;

    private BlazorUI()
    {
        ServiceCollection serviceCollection = new();
        serviceCollection.AddWpfBlazorWebView();
        serviceCollection.AddMudServices();

        serviceCollection.AddSingleton<MenuVM>(s => new MenuVM(RatScannerMain.Instance));
        serviceCollection.AddSingleton<SessionHistoryService>(services =>
        {
            MenuVM menu = services.GetRequiredService<MenuVM>();
            return new SessionHistoryService(menu.ItemScans, scan => ScanResultAdapter.Map(scan, menu, false));
        });

        LocalizationService localizationService = new();
        serviceCollection.AddSingleton(localizationService);
        // Presentation helpers build user-visible strings outside Razor; share the same catalog.
        Presentation.PresentationText.Localizer = localizationService;

        serviceCollection.AddSingleton<SettingsVM>(services => new SettingsVM(
            services.GetRequiredService<LocalizationService>()
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

        _serviceProvider = serviceCollection.BuildServiceProvider();

        Resources.Add("services", _serviceProvider);

        BlazorOverlay ??= new BlazorOverlay(_serviceProvider);
        BlazorOverlay.Show();

        BlazorInteractableOverlay ??= new BlazorInteractableOverlay(_serviceProvider);

        InitializeComponent();
    }

    private void BlazorWebView_Initialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
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
    }

    private void WebView_Loaded(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // If we are running in a development/debugger mode, open dev tools to help out
        if (Debugger.IsAttached)
            _initializedWebView?.CoreWebView2.OpenDevToolsWindow();
    }

    private void UpdateElements() { }

    private void HyperlinkRequestNavigate(object? sender, RequestNavigateEventArgs e)
    {
        ProcessStartInfo psi = new() { FileName = e.Uri.ToString(), UseShellExecute = true };
        Process.Start(psi);
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
    }

    public void OnClose() { }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_initializedWebView is not null)
            _initializedWebView.NavigationCompleted -= WebView_Loaded;

        BlazorInteractableOverlay?.Close();
        BlazorOverlay?.Close();
        // WebView may not be created if startup failed or exit happened before init.
        blazorWebView?.WebView?.Dispose();
        Resources.Remove("services");
        _serviceProvider.Dispose();
        BlazorInteractableOverlay = null!;
        BlazorOverlay = null!;
    }
}
