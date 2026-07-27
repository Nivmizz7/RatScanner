using System;
using System.Diagnostics;
using System.Linq;

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
    internal static bool IsSafeWebUrl(string? url) => TryGetSafeWebUri(url, out _);

    private static bool TryGetSafeWebUri(string? url, out Uri? uri)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            uri = null;
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            return false;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser. Untrusted or unopenable targets are
    /// logged and ignored; opening a link must never take the app down.
    /// </summary>
    internal static void Open(string? url) => Open(url, Process.Start, message => Logger.LogWarning(message));

    internal static void Open(string? url, Func<ProcessStartInfo, Process?> startProcess, Action<string> logWarning)
    {
        ArgumentNullException.ThrowIfNull(startProcess);
        ArgumentNullException.ThrowIfNull(logWarning);

        string logTarget = GetSafeLogTarget(url);
        if (!TryGetSafeWebUri(url, out Uri? uri))
        {
            logWarning($"Refused to open a link that is not an http(s) URL: {logTarget}");
            return;
        }

        try
        {
            using Process? _ = startProcess(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            logWarning($"Unable to open external URL: {logTarget}");
        }
    }

    private static string GetSafeLogTarget(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return string.IsNullOrWhiteSpace(url) ? "<null-or-empty>" : "<invalid>";

        string host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost}]" : uri.IdnHost;
        string port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        string path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        string sanitized = $"{uri.Scheme}://{host}{port}/{path}";
        return string.Concat(sanitized.Where(character => !char.IsControl(character)));
    }
}
