using System;
using System.Runtime.CompilerServices;
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
    /// Per-core suspend/resume state. All calls happen on the WPF UI thread,
    /// so no locking is needed — the async continuation from TrySuspendAsync
    /// also resumes on the dispatcher.
    /// </summary>
    private sealed class PowerState
    {
        public Task? PendingSuspend;
        public bool ResumeRequested;
    }

    private static readonly ConditionalWeakTable<CoreWebView2, PowerState> _powerStates = new();

    /// <summary>
    /// Marks the WebView invisible (WPF keeps IsVisible=true for minimized
    /// windows, which would make suspension fail) and suspends the renderer
    /// process. TrySuspendAsync itself drops the memory target to Low; do not
    /// set MemoryUsageTargetLevel separately (Microsoft recommends using only
    /// one mechanism).
    /// </summary>
    public static void Suspend(WebView2CompositionControl? webView)
    {
        CoreWebView2? core = webView?.CoreWebView2;
        if (webView is null || core is null)
            return;

        // Visibility.Hidden (not Collapsed) keeps the layout slot so resuming
        // never triggers a re-measure of the Blazor surface.
        webView.Visibility = Visibility.Hidden;

        PowerState state = _powerStates.GetOrCreateValue(core);
        if (state.PendingSuspend is not null || core.IsSuspended)
            return;

        state.ResumeRequested = false;
        state.PendingSuspend = SuspendCoreAsync(core, state);
    }

    /// <summary>
    /// Restores visibility and resumes the renderer so the surface is ready
    /// to present before its host window is shown. If a suspend is still in
    /// flight, the resume intent is recorded and applied once the suspend
    /// lands — preventing a frozen renderer on a quick hide-then-show.
    /// </summary>
    public static void Resume(WebView2CompositionControl? webView)
    {
        CoreWebView2? core = webView?.CoreWebView2;
        if (webView is null || core is null)
            return;

        webView.Visibility = Visibility.Visible;

        if (!_powerStates.TryGetValue(core, out PowerState? state))
        {
            TryResumeCore(core);
            return;
        }

        if (state.PendingSuspend is not null && !state.PendingSuspend.IsCompleted)
        {
            // Suspend is still in flight; mark intent so the continuation
            // resumes the renderer once the suspend lands.
            state.ResumeRequested = true;
            return;
        }

        state.PendingSuspend = null;
        TryResumeCore(core);
    }

    private static void TryResumeCore(CoreWebView2 core)
    {
        if (!core.IsSuspended)
            return;

        try
        {
            core.Resume();
        }
        catch (Exception e)
        {
            Logger.LogWarning("Unable to resume the WebView2 renderer.", e);
        }
    }

    private static async Task SuspendCoreAsync(CoreWebView2 core, PowerState state)
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
        finally
        {
            state.PendingSuspend = null;
            if (state.ResumeRequested && core.IsSuspended)
            {
                state.ResumeRequested = false;
                TryResumeCore(core);
            }
        }
    }
}
