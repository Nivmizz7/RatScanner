using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NuGet.Versioning;

namespace RatScanner;

/// <summary>
/// Checks GitHub Releases on the maintained fork and applies updates by downloading
/// RatScanner.zip, extracting it, then swapping files after this process exits.
/// Does not use api.ratscanner.com or the stock RatUpdater download URL (upstream).
/// </summary>
internal static class GitHubUpdateService
{
    internal const string Owner = "tarkovtracker-org";
    internal const string Repo = "RatScanner";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    private const string ReleasesApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=100";
    private const string AssetZipName = "RatScanner.zip";

    private static readonly HttpClient Http = CreateHttpClient();

    // Redirects are validated per hop in DownloadToFile, so this client must not
    // follow them silently (a trusted GitHub asset URL redirects on the normal path
    // and must never be able to land the download on an untrusted host).
    private static readonly HttpClient NoRedirectHttp = CreateNoRedirectHttpClient();

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new(
            new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }
        );
        // GitHub API requires a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"RatScanner/{RatConfig.Version} (+https://github.com/{Owner}/{Repo})"
        );
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    private static HttpClient CreateNoRedirectHttpClient()
    {
        HttpClient client = new(
            new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = false,
            }
        );
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"RatScanner/{RatConfig.Version} (+https://github.com/{Owner}/{Repo})"
        );
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    internal sealed class LatestRelease
    {
        internal required string TagName { get; init; }
        internal required string Version { get; init; }
        internal required string ZipDownloadUrl { get; init; }
        internal required string HtmlUrl { get; init; }
    }

    /// <summary>
    /// Returns the newest release eligible for the installed update channel, or null on failure / no asset.
    /// Stable installs read GitHub's Latest release. Pre-release installs include published GitHub pre-releases.
    /// </summary>
    internal static async Task<LatestRelease?> TryGetLatestReleaseAsync()
    {
        try
        {
            bool includePrereleases =
                TryParseSemanticVersion(RatConfig.Version, out NuGetVersion current) && current.IsPrerelease;
            string endpoint = includePrereleases ? ReleasesApi : LatestReleaseApi;

            using HttpResponseMessage response = await Http.GetAsync(endpoint).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning($"GitHub release check failed: HTTP {(int)response.StatusCode}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return SelectUpdateRelease(json, RatConfig.Version, includePrereleases);
        }
        catch (Exception e)
        {
            Logger.LogWarning("GitHub release check failed.", e);
            return null;
        }
    }

    internal static LatestRelease? SelectUpdateRelease(string json, string currentVersion, bool includePrereleases)
    {
        JToken root = JToken.Parse(json);
        IEnumerable<JObject> candidates = root switch
        {
            JObject release => [release],
            JArray releases => releases.OfType<JObject>(),
            _ => [],
        };

        return candidates
            .Where(release => release["draft"]?.Value<bool>() != true)
            .Where(release => includePrereleases || release["prerelease"]?.Value<bool>() != true)
            .Select(CreateRelease)
            .Where(release => release != null && IsNewerVersion(currentVersion, release.Version))
            .OrderByDescending(
                release => ParseSemanticVersion(release!.Version),
                Comparer<NuGetVersion>.Create(CompareSemanticVersions)
            )
            .FirstOrDefault();
    }

    private static LatestRelease? CreateRelease(JObject root)
    {
        string? tag = root["tag_name"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        string? htmlUrl = root["html_url"]?.Value<string>() ?? $"https://github.com/{Owner}/{Repo}/releases";
        string version = tag.TrimStart('v', 'V');
        string? zipUrl = root["assets"]
            ?.OfType<JObject>()
            .Select(asset => new
            {
                Name = asset["name"]?.Value<string>(),
                Url = asset["browser_download_url"]?.Value<string>(),
            })
            .FirstOrDefault(asset =>
                string.Equals(asset.Name, AssetZipName, StringComparison.OrdinalIgnoreCase)
                && IsAllowedReleaseAssetUrl(asset.Url)
            )
            ?.Url;

        if (string.IsNullOrEmpty(zipUrl))
            return null;

        return new LatestRelease
        {
            TagName = tag,
            Version = version,
            ZipDownloadUrl = zipUrl,
            HtmlUrl = htmlUrl,
        };
    }

    private static NuGetVersion ParseSemanticVersion(string versionText) =>
        TryParseSemanticVersion(versionText, out NuGetVersion version) ? version : new NuGetVersion(0, 0, 0);

    internal static bool IsNewerVersion(string currentVersion, string availableVersion)
    {
        if (
            !TryParseSemanticVersion(currentVersion, out NuGetVersion current)
            || !TryParseSemanticVersion(availableVersion, out NuGetVersion available)
        )
            return false;

        // Never auto-offer a pre-release onto a stable install.
        if (available.IsPrerelease && !current.IsPrerelease)
            return false;

        return CompareSemanticVersions(available, current) > 0;
    }

    private static int CompareSemanticVersions(NuGetVersion left, NuGetVersion right)
    {
        int comparison = left.Major.CompareTo(right.Major);
        if (comparison != 0)
            return comparison;

        comparison = left.Minor.CompareTo(right.Minor);
        if (comparison != 0)
            return comparison;

        comparison = left.Patch.CompareTo(right.Patch);
        if (comparison != 0)
            return comparison;

        if (left.IsPrerelease != right.IsPrerelease)
            return left.IsPrerelease ? -1 : 1;
        if (!left.IsPrerelease)
            return 0;

        string[] leftLabels = left.ReleaseLabels.ToArray();
        string[] rightLabels = right.ReleaseLabels.ToArray();
        int count = Math.Min(leftLabels.Length, rightLabels.Length);
        for (int index = 0; index < count; index++)
        {
            comparison = CompareSemanticIdentifiers(leftLabels[index], rightLabels[index]);
            if (comparison != 0)
                return comparison;
        }

        return leftLabels.Length.CompareTo(rightLabels.Length);
    }

    private static int CompareSemanticIdentifiers(string left, string right)
    {
        bool leftNumeric = IsNumericIdentifier(left);
        bool rightNumeric = IsNumericIdentifier(right);

        if (leftNumeric != rightNumeric)
            return leftNumeric ? -1 : 1;
        if (leftNumeric)
        {
            int lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumericIdentifier(string value)
    {
        foreach (char character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return value.Length > 0;
    }

    internal static bool TryParseVersion(string versionText, out Version version)
    {
        bool parsed = TryParseSemanticVersion(versionText, out NuGetVersion semanticVersion);
        version = parsed
            ? new Version(semanticVersion.Major, semanticVersion.Minor, semanticVersion.Patch)
            : new Version(0, 0);
        return parsed;
    }

    private static bool TryParseSemanticVersion(string versionText, out NuGetVersion version)
    {
        version = new NuGetVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(versionText))
            return false;

        string cleaned = versionText.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring(1);

        if (!NuGetVersion.TryParseStrict(cleaned, out NuGetVersion? parsed) || parsed is null)
            return false;

        version = parsed;
        return true;
    }

    /// <summary>
    /// True when <paramref name="url"/> is an HTTPS GitHub release asset download host we trust.
    /// </summary>
    internal static bool IsAllowedReleaseAssetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Seamless update: download zip, stage files, spawn apply script, exit current process.
    /// Returns false if the update could not be started (caller may continue startup).
    /// </summary>
    internal static bool TryApplyUpdate(LatestRelease release)
    {
        string? stagingRoot = null;
        try
        {
            if (!IsAllowedReleaseAssetUrl(release.ZipDownloadUrl))
            {
                Logger.LogWarning($"Rejected update download from untrusted URL: {release.ZipDownloadUrl}");
                return false;
            }

            string installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            stagingRoot = Path.Combine(Path.GetTempPath(), "RatScanner-update-" + Guid.NewGuid().ToString("N"));
            string zipPath = Path.Combine(stagingRoot, AssetZipName);
            string extractDir = Path.Combine(stagingRoot, "extract");
            string applyScript = Path.Combine(stagingRoot, "apply-update.ps1");

            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(extractDir);

            Logger.LogInfo($"Downloading update {release.TagName} from {release.ZipDownloadUrl}...");
            DownloadToFile(release.ZipDownloadUrl, zipPath);

            Logger.LogInfo("Extracting update package...");
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // Some zips nest everything under a single top folder; normalize to the payload root.
            string payloadDir = ResolvePayloadRoot(extractDir);
            if (!File.Exists(Path.Combine(payloadDir, "RatScanner.exe")))
            {
                Logger.LogWarning("Update package does not contain RatScanner.exe.");
                TryDeleteDirectory(stagingRoot);
                return false;
            }

            WriteApplyScript(applyScript, installDir, payloadDir, Environment.ProcessId);
            Logger.LogInfo("Launching update applicator...");

            ProcessStartInfo psi = new()
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = stagingRoot,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(applyScript);
            Process.Start(psi);
            return true;
        }
        catch (Exception e)
        {
            // Do not use LogError here — it treats the app as crashed and exits.
            Logger.LogWarning("Failed to apply update automatically.", e);
            // The success path hands the staging directory off to the apply script; on any
            // failure it is orphaned, so remove it here.
            if (stagingRoot != null)
                TryDeleteDirectory(stagingRoot);
            return false;
        }
    }

    private static string ResolvePayloadRoot(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, "RatScanner.exe")))
            return extractDir;

        string[] children = Directory.GetDirectories(extractDir);
        if (children.Length == 1 && File.Exists(Path.Combine(children[0], "RatScanner.exe")))
            return children[0];

        return extractDir;
    }

    private const int MaxRedirects = 5;

    private static void DownloadToFile(string url, string destination)
    {
        Uri current = new(url, UriKind.Absolute);
        HttpResponseMessage? response = null;
        try
        {
            for (int hop = 0; ; hop++)
            {
                if (!IsAllowedReleaseAssetUrl(current.AbsoluteUri))
                    throw new InvalidOperationException(
                        $"Refusing to download update from untrusted URL: {current.AbsoluteUri}"
                    );

                response?.Dispose();
                response = NoRedirectHttp
                    .GetAsync(current, HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter()
                    .GetResult();

                if (
                    response.StatusCode
                    is not (
                        HttpStatusCode.Moved
                        or HttpStatusCode.Redirect
                        or HttpStatusCode.RedirectMethod
                        or HttpStatusCode.RedirectKeepVerb
                        or HttpStatusCode.PermanentRedirect
                    )
                )
                    break;

                if (hop >= MaxRedirects)
                    throw new InvalidOperationException(
                        $"Update download exceeded {MaxRedirects} redirects; refusing to continue."
                    );

                Uri? location = response.Headers.Location;
                if (location is null)
                    throw new InvalidOperationException("Update download redirect is missing a Location header.");

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
            }

            response.EnsureSuccessStatusCode();
            using (response)
            {
                using Stream network = response.Content.ReadAsStream();
                using FileStream file = File.Create(destination);
                network.CopyTo(file);
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// PowerShell script: wait for the app PID to exit, copy staged files over the install dir
    /// (preserving config), then relaunch. Deletes staging when done.
    /// </summary>
    internal static void WriteApplyScript(string scriptPath, string installDir, string payloadDir, int appPid)
    {
        // Keep user config and local caches; only replace app binaries/assets from the zip.
        string[] preserveNames = { "config.cfg", "Log.txt", "RatScannerLog.txt", "RatEyeLog.txt" };

        StringBuilder sb = new();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$installDir = {PsQuote(installDir)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$payloadDir = {PsQuote(payloadDir)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$stagingRoot = {PsQuote(Path.GetDirectoryName(scriptPath)!)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$appPid = {appPid}");
        sb.AppendLine("$preserve = @(" + string.Join(", ", preserveNames.Select(PsQuote)) + ")");
        sb.AppendLine(
            """
            function Wait-ProcessExit([int]$ProcessId, [int]$TimeoutSec = 120) {
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
                    try {
                        $p = Get-Process -Id $ProcessId -ErrorAction Stop
                        if ($null -eq $p) { return $true }
                    } catch {
                        return $true
                    }
                    Start-Sleep -Milliseconds 250
                }
                return $false
            }

            function Copy-PayloadEntry {
                param(
                    [Parameter(Mandatory = $true)]
                    [System.IO.FileSystemInfo]$Entry,
                    [Parameter(Mandatory = $true)]
                    [string]$Destination
                )

                if (-not $Entry.PSIsContainer) {
                    Copy-Item -LiteralPath $Entry.FullName -Destination $Destination -Force
                    return
                }

                # Copy the directory contents instead of the directory itself. Copy-Item
                # nests a source directory when the same-named destination already exists.
                [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
                Get-ChildItem -LiteralPath $Entry.FullName -Force | ForEach-Object {
                    Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
                }
            }

            function Remove-StagingRoot {
                param([string]$Path)
                if ([string]::IsNullOrWhiteSpace($Path)) { return }
                Start-Sleep -Seconds 1
                try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue } catch {}
            }

            try {
                # Give the main process a moment to call Environment.Exit.
                Start-Sleep -Milliseconds 500
                if (-not (Wait-ProcessExit -ProcessId $appPid)) {
                    throw "RatScanner did not exit before the update timeout."
                }

                # Extra wait: single-file publish can hold locks briefly.
                Start-Sleep -Seconds 1

                # Copy payload over install, excluding user config files if present in zip.
                Get-ChildItem -LiteralPath $payloadDir -Force | ForEach-Object {
                    $name = $_.Name
                    $dest = Join-Path $installDir $name
                    if ($preserve -contains $name) {
                        # Only copy config from package if the user does not already have one.
                        if (-not (Test-Path -LiteralPath $dest)) {
                            Copy-PayloadEntry -Entry $_ -Destination $dest
                        }
                        return
                    }
                    Copy-PayloadEntry -Entry $_ -Destination $dest
                }

                $exe = Join-Path $installDir 'RatScanner.exe'
                if (Test-Path -LiteralPath $exe) {
                    Start-Process -FilePath $exe -WorkingDirectory $installDir
                }
            } catch {
                # Best-effort restart even after a partial copy so the user is not left with a stopped app.
                $exe = Join-Path $installDir 'RatScanner.exe'
                if (Test-Path -LiteralPath $exe) {
                    try { Start-Process -FilePath $exe -WorkingDirectory $installDir } catch {}
                }
                throw
            } finally {
                # Staging always lives outside the install tree; remove it whether apply succeeded or failed.
                Remove-StagingRoot -Path $stagingRoot
            }
            """
        );

        File.WriteAllText(scriptPath, sb.ToString(), Encoding.UTF8);
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
