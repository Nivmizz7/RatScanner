using System;
using System.Collections.Concurrent;
using System.IO;

namespace RatScanner.Presentation;

/// <summary>
/// Resolves the URL used to display an item icon, preferring the locally installed
/// icon over the remote catalog link.
/// </summary>
/// <remarks>
/// <para>
/// The remote link (<c>assets.tarkov.dev</c>) costs a network round trip on a cache
/// miss. Because Blazor patches the <c>src</c> of an existing <c>&lt;img&gt;</c>
/// rather than replacing the element, the browser keeps presenting the *previous*
/// item's decoded pixels until that request completes — so a scan can show the
/// correct name and price next to the previous item's icon. Local icons are already
/// on disk (the scan engine matches against them), which removes the wait and the
/// stale frame.
/// </para>
/// <para>
/// Both WebViews map <c>https://local.data/</c> to <see cref="RatConfig.Paths.Data"/>,
/// so an icon under <c>Data/icons</c> is addressable from either surface.
/// </para>
/// </remarks>
internal static class ItemIconResolver
{
    internal const string LocalHost = "https://local.data/";

    /// <summary>
    /// Caches confirmed icon files. Missing files are deliberately not cached so a
    /// Data refresh performed during a long-running development session can become
    /// visible without restarting the app.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> IconExists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Preferred display URL for an item icon.
    /// </summary>
    /// <param name="iconPath">
    /// Icon path reported by the scan engine (may be absolute, may be empty).
    /// </param>
    /// <param name="itemId">Catalog item id, used to find a static icon by name.</param>
    /// <param name="remoteUrl">Catalog image link used when no local icon is available.</param>
    internal static string Resolve(string? iconPath, string? itemId, string? remoteUrl)
    {
        if (TryMapEngineIconPath(iconPath, out string mapped))
            return mapped;
        if (TryMapItemId(itemId, out string byId))
            return byId;
        return remoteUrl ?? string.Empty;
    }

    /// <summary>
    /// Converts an engine-reported icon path into a <c>local.data</c> URL. Returns
    /// false for paths outside the installed icon directory, such as the dynamic EFT
    /// icon cache, which the WebView cannot address.
    /// </summary>
    internal static bool TryMapEngineIconPath(string? iconPath, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrEmpty(iconPath))
            return false;

        string normalized = iconPath.Replace("\\", "/", StringComparison.Ordinal);
        string[] suppliedSegments = normalized.Split('/');
        foreach (string segment in suppliedSegments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
        }

        try
        {
            string iconRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RatConfig.Paths.StaticIcon));
            string fullPath = Path.GetFullPath(normalized, RatConfig.Paths.Base);
            string rootPrefix = iconRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
                return false;

            string relative = fullPath[rootPrefix.Length..];
            string[] relativeSegments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.None
            );
            foreach (string segment in relativeSegments)
            {
                if (segment.Length == 0 || segment is "." or "..")
                    return false;
            }

            url = LocalHost + "icons/" + string.Join("/", Array.ConvertAll(relativeSegments, Uri.EscapeDataString));
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Falls back to the installed static icon named after the item id, which covers
    /// manual selections and name scans that did not report an icon path.
    /// </summary>
    private static bool TryMapItemId(string? itemId, out string url) =>
        TryMapItemId(
            itemId,
            path =>
            {
                try
                {
                    if (IconExists.ContainsKey(path))
                        return true;
                    if (!File.Exists(path))
                        return false;
                    IconExists.TryAdd(path, true);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            },
            out url
        );

    /// <summary>
    /// Maps an item id to its installed icon when the supplied existence probe finds
    /// the expected file. The probe is injectable so path handling stays hermetic in
    /// tests instead of depending on a developer's downloaded Data directory.
    /// </summary>
    internal static bool TryMapItemId(string? itemId, Func<string, bool> fileExists, out string url)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(itemId))
            return false;

        // Whitelist rather than blacklist: catalog ids are alphanumeric, so anything
        // else cannot be used to escape the icon directory.
        foreach (char character in itemId)
        {
            if (!char.IsAsciiLetterOrDigit(character))
                return false;
        }

        string path = Path.Combine(RatConfig.Paths.StaticIcon, itemId + ".png");
        if (!fileExists(path))
            return false;

        url = LocalHost + "icons/" + itemId + ".png";
        return true;
    }

    /// <summary>Clears the existence cache. Test-only seam.</summary>
    internal static void ResetForTests() => IconExists.Clear();
}
