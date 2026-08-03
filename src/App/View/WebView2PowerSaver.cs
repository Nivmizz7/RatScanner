using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RatScanner.View;

/// <summary>
/// Cuts GPU/CPU usage of WebView2 surfaces that are not on screen. Hiding the
/// host WPF window alone does not stop the Chromium compositor; the renderer
/// must be told it is invisible and then be suspended explicitly.
/// </summary>
internal static class WebView2PowerSaver
{
    /// <summary>
    /// Marks the WebView invisible (WPF keeps IsVisible=true for minimized
    /// windows, which would make suspension fail), drops its memory target,
    /// and suspends the renderer process.
    /// </summary>
    public static void Suspend(WebView2CompositionControl? webView)
    {
        CoreWebView2? core = webView?.CoreWebView2;
        if (webView is null || core is null)
            return;

        // Visibility.Hidden (not Collapsed) keeps the layout slot so resuming
        // never triggers a re-measure of the Blazor surface.
        webView.Visibility = Visibility.Hidden;

        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        }
        catch (Exception e)
        {
            Logger.LogWarning("Unable to lower the WebView2 memory target.", e);
        }

        if (!core.IsSuspended)
            _ = SuspendCoreAsync(core);
    }

    /// <summary>
    /// Restores visibility, memory target, and resumes the renderer so the
    /// surface is ready to present before its host window is shown.
    /// </summary>
    public static void Resume(WebView2CompositionControl? webView)
    {
        CoreWebView2? core = webView?.CoreWebView2;
        if (webView is null || core is null)
            return;

        webView.Visibility = Visibility.Visible;

        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            if (core.IsSuspended)
                core.Resume();
        }
        catch (Exception e)
        {
            Logger.LogWarning("Unable to resume the WebView2 renderer.", e);
        }
    }

    private static async Task SuspendCoreAsync(CoreWebView2 core)
    {
        try
        {
            await core.TrySuspendAsync();
        }
        catch (Exception)
        {
            // Suspension is best-effort: it fails while DevTools is open or if
            // the browser considers the page visible. The visibility change
            // above already stopped compositing, which is the main win.
        }
    }
}
