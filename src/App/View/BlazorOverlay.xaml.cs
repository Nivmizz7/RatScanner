using System.Diagnostics;
using System.Linq;
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
/// Interaction logic for BlazorOverlay.xaml
/// </summary>
public partial class BlazorOverlay : Window
{
    private WebView2CompositionControl? _initializedWebView;
    private DispatcherOperation? _pendingDpiRefresh;

    public BlazorOverlay(ServiceProvider serviceProvider)
    {
        Resources.Add("services", serviceProvider);

        InitializeComponent();
        DpiChanged += HostWindow_DpiChanged;
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Apply click-through styles as soon as the native window exists so there is
        // no window of time where the all-screens overlay can swallow mouse input.
        SetWindowStyle();
    }

    private void BlazorWebView_Initialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        WebView2DpiWorkaround.CancelPendingRefresh(ref _pendingDpiRefresh);
        if (_initializedWebView is not null)
            _initializedWebView.NavigationCompleted -= WebView_Loaded;

        _initializedWebView = e.WebView;
        _initializedWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        SetSize();
        SetWindowStyle();
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

    private void SetSize()
    {
        System.Collections.Generic.IEnumerable<System.Drawing.Rectangle> bounds = Screen.AllScreens.Select(screen =>
            screen.Bounds
        );
        int left = 0;
        int top = 0;
        int right = 0;
        int bottom = 0;
        foreach (System.Drawing.Rectangle bound in bounds)
        {
            if (bound.Left < left)
                left = bound.Left;
            if (bound.Top < top)
                top = bound.Top;
            if (bound.Right > right)
                right = bound.Right;
            if (bound.Bottom > bottom)
                bottom = bound.Bottom;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        // SWP_NOZORDER keeps the WPF-declared Topmost state untouched and
        // SWP_NOACTIVATE prevents the resize from ever activating the overlay.
        OverlayNativeMethods.SetWindowPos(
            handle,
            0,
            left,
            top,
            right - left,
            bottom - top,
            OverlayNativeMethods.SwpNoZOrder | OverlayNativeMethods.SwpNoActivate
        );
    }

    private void SetWindowStyle()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
            return;

        // The passive overlay spans every screen; without these styles it can
        // swallow all mouse input the moment its WebView is created.
        OverlayNativeMethods.SetWindowLongPtr(
            handle,
            OverlayNativeMethods.GwlExStyle,
            OverlayNativeMethods.GetWindowLongPtr(handle, OverlayNativeMethods.GwlExStyle)
                | OverlayNativeMethods.PassiveClickThroughStyles
        );
        // Extended-style changes are cached by Windows and only take effect
        // after a frame-changed SetWindowPos. Without this the overlay can keep
        // swallowing every click on the desktop/taskbar despite the new style.
        OverlayNativeMethods.SetWindowPos(handle, 0, 0, 0, 0, 0, OverlayNativeMethods.SwpFrameChangedFlags);

        // WebView2 creates its own child input windows beneath the WPF host;
        // hit-test transparency on the top-level window alone does not cover
        // them. Apply click-through to every descendant as well.
        ApplyStyleToDescendants(handle);
    }

    private static void ApplyStyleToDescendants(nint parent)
    {
        // EnumChildWindows already walks the complete descendant tree. Do not
        // recurse here or deeper WebView2 windows are frame-refreshed repeatedly.
        OverlayNativeMethods.EnumChildWindows(
            parent,
            (child, _) =>
            {
                OverlayNativeMethods.SetWindowLongPtr(
                    child,
                    OverlayNativeMethods.GwlExStyle,
                    OverlayNativeMethods.GetWindowLongPtr(child, OverlayNativeMethods.GwlExStyle)
                        | OverlayNativeMethods.PassiveClickThroughStyles
                );
                OverlayNativeMethods.SetWindowPos(child, 0, 0, 0, 0, 0, OverlayNativeMethods.SwpFrameChangedFlags);
                return true;
            },
            0
        );
    }

    private void WebView_Loaded(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // WebView2 may (re)create its child input windows during navigation;
        // re-apply click-through so a late-created input window cannot start
        // swallowing desktop/taskbar clicks.
        SetWindowStyle();

        // If we are running in a development/debugger mode, open dev tools to help out
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
        blazorOverlayWebView?.WebView?.Dispose();
        Resources.Remove("services");
        base.OnClosed(e);
    }
}
