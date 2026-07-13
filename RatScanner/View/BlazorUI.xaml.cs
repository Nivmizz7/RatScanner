using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
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
    private bool _webViewEventsAttached;
    private bool _disposed;

    private BlazorUI()
    {
        ServiceCollection serviceCollection = new();
        serviceCollection.AddWpfBlazorWebView();
        serviceCollection.AddMudServices();

        serviceCollection.AddSingleton<MenuVM>(s => new MenuVM(RatScannerMain.Instance));
        serviceCollection.AddSingleton<SessionHistoryService>();

        LocalizationService localizationService = new();
        serviceCollection.AddSingleton(localizationService);

        SettingsVM settingsVM = new(localizationService);
        serviceCollection.AddSingleton<SettingsVM>(s => settingsVM);

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

    private void BlazorUI_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_webViewEventsAttached)
            return;
        _webViewEventsAttached = true;

        blazorWebView.WebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        blazorWebView.WebView.NavigationCompleted += WebView_Loaded;
        blazorWebView.WebView.CoreWebView2InitializationCompleted += CoreWebView_Loaded;
    }

    private void WebView_Loaded(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // If we are running in a development/debugger mode, open dev tools to help out
        if (Debugger.IsAttached)
            blazorWebView.WebView.CoreWebView2.OpenDevToolsWindow();
    }

    private void CoreWebView_Loaded(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        blazorWebView.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "local.data",
            "Data",
            CoreWebView2HostResourceAccessKind.Allow
        );
        blazorWebView.WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        blazorWebView.WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
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

        if (_webViewEventsAttached)
        {
            blazorWebView.WebView.NavigationCompleted -= WebView_Loaded;
            blazorWebView.WebView.CoreWebView2InitializationCompleted -= CoreWebView_Loaded;
        }

        BlazorInteractableOverlay?.Close();
        BlazorOverlay?.Close();
        blazorWebView.WebView.Dispose();
        Resources.Remove("services");
        _serviceProvider.Dispose();
        BlazorInteractableOverlay = null!;
        BlazorOverlay = null!;
    }
}
