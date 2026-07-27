using System;
using System.Diagnostics;

namespace RatScanner;

/// <summary>
/// Single entry point for handing a link to the shell.
/// </summary>
/// <remarks>
/// Most link targets reach the UI as remote catalog data (tarkov.dev <c>wikiLink</c>/<c>link</c>
/// fields, replayed from the offline cache on disk). <c>UseShellExecute</c> resolves whatever the
/// string names — a local executable, a UNC path, or a registered protocol handler — so the target
/// is validated as an absolute http(s) URL before it is launched.
/// </remarks>
internal static class ExternalLinkLauncher
{
    /// <summary>
    /// True when <paramref name="url"/> is an absolute http or https URL.
    /// </summary>
    internal static bool IsSafeWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser. Untrusted or unopenable targets are
    /// logged and ignored; opening a link must never take the app down.
    /// </summary>
    internal static void Open(string? url)
    {
        if (!IsSafeWebUrl(url))
        {
            Logger.LogWarning($"Refused to open a link that is not an http(s) URL: {url ?? "<null>"}");
            return;
        }

        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Unable to open external URL: {url}", e);
        }
    }
}
