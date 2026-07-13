using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RatScanner.Tests;

public sealed class GitHubUpdateApplyScriptTests
{
    [Fact]
    public async Task Apply_script_merges_existing_directories_and_preserves_user_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "RatScanner-update-test-" + Guid.NewGuid().ToString("N"));
        string installDir = Path.Combine(root, "install");
        string payloadDir = Path.Combine(root, "payload");
        string stagingDir = Path.Combine(root, "staging");
        string scriptPath = Path.Combine(stagingDir, "apply-update.ps1");

        try
        {
            Directory.CreateDirectory(Path.Combine(installDir, "wwwroot"));
            Directory.CreateDirectory(Path.Combine(payloadDir, "wwwroot", "css"));
            Directory.CreateDirectory(stagingDir);

            File.WriteAllText(Path.Combine(installDir, "wwwroot", "index.html"), "old index");
            File.WriteAllText(Path.Combine(installDir, "wwwroot", "keep.txt"), "keep me");
            File.WriteAllText(Path.Combine(payloadDir, "wwwroot", "index.html"), "new index");
            File.WriteAllText(Path.Combine(payloadDir, "wwwroot", "css", "theme.css"), "new theme");
            File.WriteAllText(Path.Combine(payloadDir, "new-native.dll"), "new binary");

            File.WriteAllText(Path.Combine(installDir, "config.cfg"), "user config");
            File.WriteAllText(Path.Combine(installDir, "Log.txt"), "user log");
            File.WriteAllText(Path.Combine(payloadDir, "config.cfg"), "package config");
            File.WriteAllText(Path.Combine(payloadDir, "Log.txt"), "package log");
            File.WriteAllText(Path.Combine(payloadDir, "RatScannerLog.txt"), "package scanner log");

            GitHubUpdateService.WriteApplyScript(scriptPath, installDir, payloadDir, int.MaxValue);

            ProcessStartInfo startInfo = new("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);

            using Process process =
                Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Windows PowerShell.");
            CancellationToken testCancellation = TestContext.Current.CancellationToken;
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(testCancellation);
            Task<string> standardError = process.StandardError.ReadToEndAsync(testCancellation);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            string output = await standardOutput;
            string error = await standardError;
            Assert.True(process.ExitCode == 0, $"Apply script failed. Output: {output} Error: {error}");

            Assert.Equal("new index", File.ReadAllText(Path.Combine(installDir, "wwwroot", "index.html")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(installDir, "wwwroot", "keep.txt")));
            Assert.Equal("new theme", File.ReadAllText(Path.Combine(installDir, "wwwroot", "css", "theme.css")));
            Assert.Equal("new binary", File.ReadAllText(Path.Combine(installDir, "new-native.dll")));
            Assert.False(Directory.Exists(Path.Combine(installDir, "wwwroot", "wwwroot")));

            Assert.Equal("user config", File.ReadAllText(Path.Combine(installDir, "config.cfg")));
            Assert.Equal("user log", File.ReadAllText(Path.Combine(installDir, "Log.txt")));
            Assert.Equal("package scanner log", File.ReadAllText(Path.Combine(installDir, "RatScannerLog.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
