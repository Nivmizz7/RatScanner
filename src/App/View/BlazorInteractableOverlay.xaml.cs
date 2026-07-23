using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RatScanner.View;

/// <summary>
/// Interaction logic for BlazorInteractableOverlay.xaml
/// </summary>
public partial class BlazorInteractableOverlay : Window
{
    private WebView2CompositionControl? _initializedWebView;
    private DispatcherOperation? _pendingDpiRefresh;

    public BlazorInteractableOverlay(ServiceProvider serviceProvider)
    {
        Resources.Add("services", serviceProvider);

        InitializeComponent();
        DpiChanged += HostWindow_DpiChanged;
    }

    private void BlazorWebView_Initialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        if (_initializedWebView is not null)
            _initializedWebView.NavigationCompleted -= WebView_Loaded;

        _initializedWebView = e.WebView;
        _initializedWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        SetWindowStyle();
        SetPosition();
        ApplyBlurBehind();
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

    private void SetPosition()
    {
        System.Collections.Generic.IEnumerable<Screen> hoveredScreen = Screen.AllScreens.Where(screen =>
            screen.Bounds.Contains(UserActivityHelper.GetMousePosition())
        );
        Screen? screen = hoveredScreen.FirstOrDefault() ?? Screen.PrimaryScreen;
        if (screen is null)
            return;
        System.Drawing.Rectangle b = screen.Bounds;
        nint handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(handle, 0, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, 0);
    }

    private void ApplyBlurBehind()
    {
        WindowBlurEffect.AccentState accent = WindowBlurEffect.AccentState.ACCENT_DISABLED;
        if (RatConfig.Overlay.Search.BlurBehind)
            accent = WindowBlurEffect.AccentState.ACCENT_ENABLE_BLURBEHIND;
        WindowBlurEffect.SetBlur(this, accent);
    }

    internal async void ShowOverlay()
    {
        // Guard the async WebView calls: an overlay-show failure must be logged, not surface as
        // an unobserved async-void exception that tears down the app.
        try
        {
            ApplyBlurBehind();
            SetPosition();
            Show();
            await blazorInteractableOverlayWebView.WebView.EnsureCoreWebView2Async();
            await blazorInteractableOverlayWebView.WebView.ExecuteScriptAsync("ShowOverlay()");
        }
        catch (Exception ex)
        {
            RatScanner.Logger.LogWarning("Failed to show the interactable overlay.", ex);
        }
    }

    internal void HideOverlay()
    {
        Hide();
    }

    private void SetWindowStyle()
    {
        const int gwlExStyle = -20; // GWL_EXSTYLE
        const uint wsExToolWindow = 0x00000080; // WS_EX_TOOLWINDOW

        nint handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowLongPtr(
            handle,
            gwlExStyle,
            NativeMethods.GetWindowLongPtr(handle, gwlExStyle) | (nint)wsExToolWindow
        );
    }

    private void WebView_Loaded(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // if we are running in a debug mode, open dev tools to help out
        if (Debugger.IsAttached)
            _initializedWebView?.CoreWebView2.OpenDevToolsWindow();
    }

    private void HostWindow_DpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        WebView2DpiWorkaround.CancelPendingRefresh(ref _pendingDpiRefresh);
        _pendingDpiRefresh = WebView2DpiWorkaround.RefreshAfterDpiChange(_initializedWebView);
    }

    protected override void OnClosed(System.EventArgs e)
    {
        DpiChanged -= HostWindow_DpiChanged;
        WebView2DpiWorkaround.CancelPendingRefresh(ref _pendingDpiRefresh);
        if (_initializedWebView is not null)
            _initializedWebView.NavigationCompleted -= WebView_Loaded;
        // WebView may not be created if the window closes before initialization completes.
        blazorInteractableOverlayWebView?.WebView?.Dispose();
        Resources.Remove("services");
        base.OnClosed(e);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            nint hWnd,
            nint hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags
        );
    }
}
