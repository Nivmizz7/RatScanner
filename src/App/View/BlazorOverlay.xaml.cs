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
        NativeMethods.SetWindowPos(
            handle,
            0,
            left,
            top,
            right - left,
            bottom - top,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate
        );
    }

    private void SetWindowStyle()
    {
        const int gwlExStyle = -20; // GWL_EXSTYLE
        const uint wsExToolWindow = 0x00000080; // WS_EX_TOOLWINDOW
        // The passive overlay spans every screen; without WS_EX_TRANSPARENT it
        // swallows all mouse input the moment a scan tooltip renders.
        const uint wsExTransparent = 0x00000020; // WS_EX_TRANSPARENT (click-through)
        const uint wsExLayered = 0x00080000; // WS_EX_LAYERED (required by WS_EX_TRANSPARENT)
        const uint wsExNoActivate = 0x08000000; // WS_EX_NOACTIVATE (never steal focus from the game)
        const nint clickThroughStyles = (nint)(wsExToolWindow | wsExTransparent | wsExLayered | wsExNoActivate);

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
            return;

        NativeMethods.SetWindowLongPtr(
            handle,
            gwlExStyle,
            NativeMethods.GetWindowLongPtr(handle, gwlExStyle) | clickThroughStyles
        );
        // Extended-style changes are cached by Windows and only take effect
        // after a frame-changed SetWindowPos. Without this the overlay can keep
        // swallowing every click on the desktop/taskbar despite the new style.
        NativeMethods.SetWindowPos(handle, 0, 0, 0, 0, 0, NativeMethods.SwpFrameChangedFlags);

        // WebView2 creates its own child input windows beneath the WPF host;
        // hit-test transparency on the top-level window alone does not cover
        // them. Apply click-through to every descendant as well.
        ApplyStyleToDescendants(handle, gwlExStyle, clickThroughStyles);
    }

    private static void ApplyStyleToDescendants(nint parent, int gwlExStyle, nint styles)
    {
        NativeMethods.EnumChildWindows(
            parent,
            (child, _) =>
            {
                NativeMethods.SetWindowLongPtr(
                    child,
                    gwlExStyle,
                    NativeMethods.GetWindowLongPtr(child, gwlExStyle) | styles
                );
                NativeMethods.SetWindowPos(child, 0, 0, 0, 0, 0, NativeMethods.SwpFrameChangedFlags);
                ApplyStyleToDescendants(child, gwlExStyle, styles);
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

    private static class NativeMethods
    {
        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpFrameChanged = 0x0020;
        public const uint SwpNoMove = 0x0002;
        public const uint SwpNoSize = 0x0001;
        public const uint SwpFrameChangedFlags = SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged;

        public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);
    }
}
