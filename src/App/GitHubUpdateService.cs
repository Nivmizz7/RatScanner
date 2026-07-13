using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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
    private const string AssetZipName = "RatScanner.zip";

    private static readonly HttpClient Http = CreateHttpClient();

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

    internal sealed class LatestRelease
    {
        internal required string TagName { get; init; }
        internal required string Version { get; init; }
        internal required string ZipDownloadUrl { get; init; }
        internal required string HtmlUrl { get; init; }
    }

    /// <summary>
    /// Returns latest published release with a RatScanner.zip asset, or null on failure / no asset.
    /// </summary>
    internal static async Task<LatestRelease?> TryGetLatestReleaseAsync()
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(LatestReleaseApi).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning($"GitHub release check failed: HTTP {(int)response.StatusCode}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JObject root = JObject.Parse(json);

            string? tag = root["tag_name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            string? htmlUrl = root["html_url"]?.Value<string>() ?? $"https://github.com/{Owner}/{Repo}/releases/latest";
            string version = tag.TrimStart('v', 'V');

            JToken? assets = root["assets"];
            string? zipUrl = assets
                ?.OfType<JObject>()
                .Select(a => new
                {
                    Name = a["name"]?.Value<string>(),
                    Url = a["browser_download_url"]?.Value<string>(),
                })
                .FirstOrDefault(a =>
                    string.Equals(a.Name, AssetZipName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(a.Url)
                )
                ?.Url;

            if (string.IsNullOrEmpty(zipUrl))
            {
                Logger.LogWarning($"Latest GitHub release does not contain {AssetZipName}.");
                return null;
            }

            return new LatestRelease
            {
                TagName = tag,
                Version = version,
                ZipDownloadUrl = zipUrl,
                HtmlUrl = htmlUrl,
            };
        }
        catch (Exception e)
        {
            Logger.LogWarning("GitHub release check failed.", e);
            return null;
        }
    }

    internal static bool IsNewerVersion(string currentVersion, string availableVersion)
    {
        if (
            !TryParseVersionInfo(currentVersion, out VersionInfo current)
            || !TryParseVersionInfo(availableVersion, out VersionInfo available)
        )
            return false;

        // Never auto-offer a pre-release onto a stable install.
        if (available.IsPrerelease && !current.IsPrerelease)
            return false;

        int comparison = available.Version.CompareTo(current.Version);
        if (comparison > 0)
            return true;
        if (comparison < 0)
            return false;

        // Same numeric version: allow upgrading from pre-release to the matching stable.
        return current.IsPrerelease && !available.IsPrerelease;
    }

    internal static bool TryParseVersion(string versionText, out Version version)
    {
        bool parsed = TryParseVersionInfo(versionText, out VersionInfo info);
        version = info.Version;
        return parsed;
    }

    private readonly record struct VersionInfo(Version Version, bool IsPrerelease);

    private static bool TryParseVersionInfo(string versionText, out VersionInfo info)
    {
        info = new VersionInfo(new Version(0, 0), false);
        if (string.IsNullOrWhiteSpace(versionText))
            return false;

        string cleaned = versionText.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring(1);

        int cut = cleaned.IndexOfAny(['-', '+']);
        bool isPrerelease = cut >= 0 && cleaned[cut] == '-';
        if (cut >= 0)
            cleaned = cleaned.Substring(0, cut);

        if (!Version.TryParse(cleaned, out Version? result) || result is null)
            return false;

        info = new VersionInfo(result, isPrerelease);
        return true;
    }

    /// <summary>
    /// Seamless update: download zip, stage files, spawn apply script, exit current process.
    /// Returns false if the update could not be started (caller may continue startup).
    /// </summary>
    internal static bool TryApplyUpdate(LatestRelease release)
    {
        try
        {
            string installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            string stagingRoot = Path.Combine(Path.GetTempPath(), "RatScanner-update-" + Guid.NewGuid().ToString("N"));
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

            WriteApplyScript(applyScript, installDir, payloadDir, Process.GetCurrentProcess().Id);
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

    private static void DownloadToFile(string url, string destination)
    {
        using HttpResponseMessage response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();
        using Stream network = response.Content.ReadAsStream();
        using FileStream file = File.Create(destination);
        network.CopyTo(file);
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
        sb.AppendLine($"$installDir = {PsQuote(installDir)}");
        sb.AppendLine($"$payloadDir = {PsQuote(payloadDir)}");
        sb.AppendLine($"$stagingRoot = {PsQuote(Path.GetDirectoryName(scriptPath)!)}");
        sb.AppendLine($"$appPid = {appPid}");
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

            # Give the main process a moment to call Environment.Exit.
            Start-Sleep -Milliseconds 500
            if (-not (Wait-ProcessExit -ProcessId $appPid)) {
                throw "RatScanner did not exit before the update timeout."
            }

            # Extra wait: single-file publish can hold locks briefly.
            Start-Sleep -Seconds 1

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

            # Best-effort cleanup of staging (this script lives there).
            Start-Sleep -Seconds 1
            try { Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {}
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
