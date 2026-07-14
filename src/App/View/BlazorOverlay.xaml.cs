using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
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

    public BlazorOverlay(ServiceProvider serviceProvider)
    {
        Resources.Add("services", serviceProvider);

        InitializeComponent();
    }

    private void BlazorWebView_Initialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
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
        NativeMethods.SetWindowPos(handle, 0, left, top, right - left, bottom - top, 0);
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
        // If we are running in a development/debugger mode, open dev tools to help out
        if (Debugger.IsAttached)
            _initializedWebView?.CoreWebView2.OpenDevToolsWindow();
    }

    protected override void OnClosed(System.EventArgs e)
    {
        if (_initializedWebView is not null)
            _initializedWebView.NavigationCompleted -= WebView_Loaded;
        // WebView may not be created if the window closes before initialization completes.
        blazorOverlayWebView?.WebView?.Dispose();
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
