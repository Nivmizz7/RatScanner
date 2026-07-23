using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace RatScanner.View;

/// <summary>
/// Keeps WebView2CompositionControl's physical-pixel transforms synchronized with WPF DPI changes.
/// </summary>
internal static class WebView2DpiWorkaround
{
    public static DispatcherOperation? RefreshAfterDpiChange(WebView2CompositionControl? webView)
    {
        if (webView is null || webView.Dispatcher.HasShutdownStarted)
            return null;

        // The composition control refreshes its cached DPI scale from SizeChanged, but WPF can
        // raise DpiChanged without changing the control's logical size when a window crosses
        // monitors. Force one harmless layout-size transition after WPF finishes the DPI change.
        return webView.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RefreshLayoutForCurrentDpi(webView));
    }

    public static void CancelPendingRefresh(ref DispatcherOperation? operation)
    {
        _ = operation?.Abort();
        operation = null;
    }

    private static void RefreshLayoutForCurrentDpi(WebView2CompositionControl webView)
    {
        if (!webView.IsLoaded)
            return;

        // A control this narrow has no usable input area to correct. Its next growth raises the
        // SizeChanged event that refreshes WebView2's DPI scale through the built-in path.
        if (webView.ActualWidth <= 1)
            return;

        double originalMaxWidth = webView.MaxWidth;
        try
        {
            webView.SetCurrentValue(WebView2CompositionControl.MaxWidthProperty, webView.ActualWidth - 1);
            webView.UpdateLayout();
        }
        finally
        {
            webView.SetCurrentValue(WebView2CompositionControl.MaxWidthProperty, originalMaxWidth);
            webView.UpdateLayout();
        }
    }
}
