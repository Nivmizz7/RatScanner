using System;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace RatScanner.View;

/// <summary>
/// Keeps WebView2CompositionControl's physical-pixel transforms synchronized with WPF DPI changes.
/// </summary>
internal static class WebView2DpiWorkaround
{
    public static void RefreshAfterDpiChange(WebView2CompositionControl? webView)
    {
        if (webView is null)
            return;

        // The composition control refreshes its cached DPI scale from SizeChanged, but WPF can
        // raise DpiChanged without changing the control's logical size when a window crosses
        // monitors. Force one harmless layout-size transition after WPF finishes the DPI change.
        _ = webView.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RefreshLayoutForCurrentDpi(webView));
    }

    private static void RefreshLayoutForCurrentDpi(WebView2CompositionControl webView)
    {
        if (!webView.IsLoaded || webView.ActualWidth <= 1)
            return;

        double originalMaxWidth = webView.MaxWidth;
        try
        {
            webView.MaxWidth = Math.Max(0, webView.ActualWidth - 1);
            webView.UpdateLayout();
        }
        finally
        {
            webView.MaxWidth = originalMaxWidth;
            webView.UpdateLayout();
        }
    }
}
