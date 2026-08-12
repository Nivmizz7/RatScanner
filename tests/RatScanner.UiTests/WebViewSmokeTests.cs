#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace RatScanner.UiTests;

public sealed class WebViewSmokeTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    [Trait("Category", "UI")]
    public async Task Main_shell_navigation_and_responsive_layout_work_in_WebView2()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        string repositoryRoot = FindRepositoryRoot();
        string configuration = GetConfiguration();
        string appDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "App",
            "bin",
            configuration,
            "net10.0-windows10.0.22621.0"
        );
        string appPath = Path.Combine(appDirectory, "RatScanner.exe");
        Assert.True(File.Exists(appPath), $"Build the {configuration} app before running UI tests: {appPath}");

        Process[] existingProcesses = Process.GetProcessesByName("RatScanner");
        Assert.Empty(existingProcesses);

        string artifactRoot =
            Environment.GetEnvironmentVariable("RATSCANNER_UI_ARTIFACTS")
            ?? Path.Combine(repositoryRoot, "artifacts", "ui-tests");
        string runDirectory = Path.Combine(
            artifactRoot,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(runDirectory);

        string profileDirectory = Path.Combine(Path.GetTempPath(), $"RatScanner-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDirectory);

        Process? app = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        bool traceStarted = false;
        bool failed = false;
        Exception? testFailure = null;
        ConcurrentQueue<string> runtimeFailures = new();

        try
        {
            ProcessStartInfo startInfo = new(appPath)
            {
                WorkingDirectory = appDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = "--remote-debugging-port=0";
            startInfo.Environment["WEBVIEW2_USER_DATA_FOLDER"] = profileDirectory;

            app = Process.Start(startInfo);
            Assert.NotNull(app);
            stdoutTask = app.StandardOutput.ReadToEndAsync(testCancellation);
            stderrTask = app.StandardError.ReadToEndAsync(testCancellation);

            int port = await WaitForDebugPortAsync(profileDirectory, app, StartupTimeout);
            File.WriteAllText(Path.Combine(runDirectory, "endpoint.txt"), $"http://127.0.0.1:{port}");

            playwright = await Playwright.CreateAsync();
            float slowMo = ParseSlowMo();
            browser = await playwright.Chromium.ConnectOverCDPAsync(
                $"http://127.0.0.1:{port}",
                new BrowserTypeConnectOverCDPOptions
                {
                    ArtifactsDir = runDirectory,
                    SlowMo = slowMo,
                    Timeout = (float)StartupTimeout.TotalMilliseconds,
                }
            );
            context = Assert.Single(browser.Contexts);
            await context.Tracing.StartAsync(
                new TracingStartOptions
                {
                    Screenshots = true,
                    Snapshots = true,
                    Sources = true,
                }
            );
            traceStarted = true;

            page = await WaitForAppPageAsync(context, app, StartupTimeout);
            AttachRuntimeDiagnostics(page, runtimeFailures);

            await ResizeAppWindowAsync(app, page, width: 1100, height: 850);

            await page.Locator(".scan-page")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 120_000 });
            await AssertVisibleAsync(page.Locator(".rs-search-field input"));
            await AssertVisibleAsync(page.GetByRole(AriaRole.Navigation).First);
            await AssertNoHorizontalOverflowAsync(page, "desktop scan page");
            await page.ScreenshotAsync(
                new PageScreenshotOptions { Path = Path.Combine(runDirectory, "desktop-scan.png"), FullPage = true }
            );

            ILocator settingsLink = page.Locator("a[href='/app/settings/general']");
            await settingsLink.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync("**/app/settings/general");
            await AssertVisibleAsync(page.Locator(".settings-page"));
            Assert.NotEqual("BODY", await page.EvaluateAsync<string>("document.activeElement?.tagName ?? ''"));

            ILocator aboutLink = page.Locator("a[href='/app/credits']");
            await aboutLink.ClickAsync();
            await page.WaitForURLAsync("**/app/credits");
            await page.GetByRole(AriaRole.Heading, new() { Level = 1 })
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
            Assert.False(
                string.IsNullOrWhiteSpace(
                    await page.GetByRole(AriaRole.Heading, new() { Level = 1 }).TextContentAsync()
                )
            );

            await page.Locator("a[href='/app/settings/general']").ClickAsync();
            await page.WaitForURLAsync("**/app/settings/general");
            await ResizeAppWindowAsync(app, page, width: 600, height: 850);
            await page.Locator(".sidebar.overlay")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
            await page.WaitForFunctionAsync(
                "() => document.querySelector('.sidebar.overlay')?.getBoundingClientRect().right <= 0"
            );
            await page.EvaluateAsync(
                """
                () => {
                    window.scrollTo(0, 0);
                    const main = document.querySelector('.main-content');
                    if (main) main.scrollTo({ left: 0, top: 0 });
                }
                """
            );
            await page.WaitForFunctionAsync(
                "() => window.scrollX === 0 && document.querySelector('.main-content')?.scrollLeft === 0"
            );
            await page.Locator(".settings-tabs")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            await AssertVisibleAsync(page.Locator(".settings-select"));
            await AssertNoHorizontalOverflowAsync(page, "narrow settings page");
            int viewportWidth = await page.EvaluateAsync<int>("window.innerWidth");
            await AssertInsideViewportAsync(
                page.Locator(".settings-shell"),
                width: viewportWidth,
                surface: "narrow settings page"
            );
            await page.ScreenshotAsync(
                new PageScreenshotOptions
                {
                    Path = Path.Combine(runDirectory, "narrow-settings.png"),
                    // Full-page capture includes the transformed-offscreen drawer in the image bounds.
                    // Capture the real narrow viewport so the visual artifact matches what a user sees.
                    FullPage = false,
                }
            );

            string ariaSnapshot = await page.Locator("body").AriaSnapshotAsync();
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "accessibility.yml"),
                ariaSnapshot,
                testCancellation
            );
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "current-url.txt"), page.Url, testCancellation);

            Assert.Empty(runtimeFailures);
        }
        catch (Exception exception)
        {
            failed = true;
            testFailure = exception;
            if (page is not null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(runDirectory, "failure-url.txt"), page.Url);
                    await CaptureFailureEvidenceAsync(page, runDirectory);
                }
                catch (Exception captureException)
                {
                    TryWriteDiagnostic(
                        Path.Combine(runDirectory, "failure-capture-error.txt"),
                        captureException.ToString()
                    );
                }
            }
        }

        List<Exception> cleanupFailures = [];
        await RecordCleanupFailureAsync(
            cleanupFailures,
            async () =>
            {
                if (!runtimeFailures.IsEmpty)
                    await File.WriteAllLinesAsync(Path.Combine(runDirectory, "browser-runtime.log"), runtimeFailures);
            }
        );

        if (traceStarted && context is not null)
        {
            await RecordCleanupFailureAsync(
                cleanupFailures,
                async () =>
                {
                    await context.Tracing.StopAsync(
                        failed ? new TracingStopOptions { Path = Path.Combine(runDirectory, "trace.zip") } : null
                    );
                }
            );
            RecordCleanupFailure(
                cleanupFailures,
                () =>
                {
                    foreach (string transientTrace in Directory.EnumerateFiles(runDirectory, "*.trace"))
                        File.Delete(transientTrace);
                    foreach (string transientNetwork in Directory.EnumerateFiles(runDirectory, "*.network"))
                        File.Delete(transientNetwork);
                }
            );
        }

        if (browser is not null)
            await RecordCleanupFailureAsync(cleanupFailures, async () => await browser.DisposeAsync());
        if (playwright is not null)
            RecordCleanupFailure(cleanupFailures, playwright.Dispose);

        if (app is not null)
        {
            await RecordCleanupFailureAsync(cleanupFailures, async () => await StopOwnedProcessAsync(app));
            bool appExited = false;
            RecordCleanupFailure(
                cleanupFailures,
                () =>
                {
                    app.Refresh();
                    appExited = app.HasExited;
                }
            );
            if (appExited)
            {
                if (stdoutTask is not null)
                    await RecordCleanupFailureAsync(
                        cleanupFailures,
                        async () =>
                            await File.WriteAllTextAsync(Path.Combine(runDirectory, "app-stdout.log"), await stdoutTask)
                    );
                if (stderrTask is not null)
                    await RecordCleanupFailureAsync(
                        cleanupFailures,
                        async () =>
                            await File.WriteAllTextAsync(Path.Combine(runDirectory, "app-stderr.log"), await stderrTask)
                    );
            }
        }

        string appLog = Path.Combine(appDirectory, "Log.txt");
        if (File.Exists(appLog))
            RecordCleanupFailure(
                cleanupFailures,
                () => File.Copy(appLog, Path.Combine(runDirectory, "RatScanner.log"), overwrite: true)
            );

        try
        {
            Directory.Delete(profileDirectory, recursive: true);
        }
        catch (IOException exception)
        {
            TryWriteDiagnostic(Path.Combine(runDirectory, "profile-cleanup-error.txt"), exception.ToString());
        }
        catch (UnauthorizedAccessException exception)
        {
            TryWriteDiagnostic(Path.Combine(runDirectory, "profile-cleanup-error.txt"), exception.ToString());
        }

        if (cleanupFailures.Count > 0)
            TryWriteDiagnostic(
                Path.Combine(runDirectory, "cleanup-errors.log"),
                string.Join(Environment.NewLine + Environment.NewLine, cleanupFailures)
            );

        if (testFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(testFailure).Throw();
        if (cleanupFailures.Count > 0)
            throw new AggregateException("UI smoke cleanup failed.", cleanupFailures);
    }

    private static async Task<int> WaitForDebugPortAsync(string profileDirectory, Process app, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            app.Refresh();
            if (app.HasExited)
                throw new InvalidOperationException($"RatScanner exited during startup with code {app.ExitCode}.");

            try
            {
                string? portFile = Directory
                    .EnumerateFiles(profileDirectory, "DevToolsActivePort", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (portFile is not null)
                {
                    string? firstLine = (await File.ReadAllLinesAsync(portFile)).FirstOrDefault();
                    if (int.TryParse(firstLine, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
                        return port;
                }
            }
            catch (IOException)
            {
                // Chromium can still be creating or replacing the readiness file; retry until the deadline.
            }
            catch (UnauthorizedAccessException)
            {
                // The profile tree can be transiently locked while WebView2 initializes; retry.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"WebView2 did not publish DevToolsActivePort within {timeout}.");
    }

    private static async Task<IPage> WaitForAppPageAsync(IBrowserContext context, Process app, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            app.Refresh();
            if (app.HasExited)
                throw new InvalidOperationException(
                    $"RatScanner exited before the /app WebView loaded: {app.ExitCode}."
                );

            IPage? page = context.Pages.FirstOrDefault(candidate =>
                Uri.TryCreate(candidate.Url, UriKind.Absolute, out Uri? uri) && uri.AbsolutePath == "/app"
            );
            if (page is not null)
                return page;

            await Task.Delay(250);
        }

        throw new TimeoutException("RatScanner's /app WebView target did not become ready.");
    }

    private static void AttachRuntimeDiagnostics(IPage page, ConcurrentQueue<string> failures)
    {
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
                failures.Enqueue($"console error: {message.Text}");
        };
        page.PageError += (_, error) => failures.Enqueue($"uncaught page error: {error}");
        page.RequestFailed += (_, request) =>
        {
            if (IsAppResource(request.Url))
                failures.Enqueue($"failed app request: {request.Method} {request.Url} ({request.Failure})");
        };
        page.Response += (_, response) =>
        {
            if (response.Status >= 500 && IsAppResource(response.Url))
                failures.Enqueue($"app response {response.Status}: {response.Url}");
        };
    }

    private static bool IsAppResource(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.Host is "0.0.0.1" or "local.data" || uri.Scheme == "file");

    private static async Task AssertNoHorizontalOverflowAsync(IPage page, string surface)
    {
        bool fits = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"
        );
        Assert.True(fits, $"Unexpected horizontal overflow on {surface}.");
    }

    private static async Task AssertVisibleAsync(ILocator locator)
    {
        await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.True(await locator.IsVisibleAsync());
    }

    private static async Task AssertInsideViewportAsync(ILocator locator, int width, string surface)
    {
        LocatorBoundingBoxResult? bounds = await locator.BoundingBoxAsync();
        Assert.NotNull(bounds);
        Assert.True(bounds.X >= 0, $"{surface} starts outside the viewport at x={bounds.X}.");
        Assert.True(bounds.X + bounds.Width <= width, $"{surface} extends beyond the {width}px viewport.");
    }

    private static async Task ResizeAppWindowAsync(Process app, IPage page, int width, int height)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        nint window = 0;
        while (DateTime.UtcNow < deadline)
        {
            app.Refresh();
            window = FindLargestOwnedWindow(app.Id);
            if (window != 0)
                break;
            if (app.HasExited)
                break;
            await Task.Delay(100);
        }

        if (window == 0)
            throw new InvalidOperationException("RatScanner did not expose a main window handle.");
        NativeMethods.ShowWindow(window, NativeMethods.RestoreWindow);
        uint dpi = NativeMethods.GetDpiForWindow(window);
        if (dpi == 0)
            throw new InvalidOperationException(
                $"Unable to determine RatScanner window DPI (Win32 error {Marshal.GetLastWin32Error()})."
            );
        int deviceWidth = checked((int)Math.Round(width * dpi / 96d, MidpointRounding.AwayFromZero));
        int deviceHeight = checked((int)Math.Round(height * dpi / 96d, MidpointRounding.AwayFromZero));
        int viewportWidth = await page.EvaluateAsync<int>("window.innerWidth");
        while (DateTime.UtcNow < deadline)
        {
            if (!NativeMethods.SetWindowPos(window, 0, 0, 0, deviceWidth, deviceHeight, NativeMethods.ResizeOnlyFlags))
                throw new InvalidOperationException(
                    $"Unable to resize RatScanner for UI validation (Win32 error {Marshal.GetLastWin32Error()})."
                );

            viewportWidth = await page.EvaluateAsync<int>("window.innerWidth");
            bool expectedBreakpoint = width <= 680 ? viewportWidth <= 680 : viewportWidth > 680;
            if (expectedBreakpoint)
                return;
            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"RatScanner did not reach the requested {width}px responsive viewport; actual width was {viewportWidth}px."
        );
    }

    private static async Task RecordCleanupFailureAsync(List<Exception> failures, Func<Task> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void RecordCleanupFailure(List<Exception> failures, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void TryWriteDiagnostic(string path, string contents)
    {
        try
        {
            File.WriteAllText(path, contents);
        }
        catch (Exception)
        {
            // Diagnostics are best effort and must not replace the original test or cleanup failure.
        }
    }

    private static nint FindLargestOwnedWindow(int expectedProcessId)
    {
        nint selected = 0;
        long largestArea = 0;
        for (
            nint candidate = NativeMethods.GetTopWindow(0);
            candidate != 0;
            candidate = NativeMethods.GetWindow(candidate, 2)
        )
        {
            uint threadId = NativeMethods.GetWindowThreadProcessId(candidate, out uint processId);
            if (threadId == 0 || processId != expectedProcessId || !NativeMethods.IsWindowVisible(candidate))
                continue;
            if (GetWindowTitle(candidate).Contains("Overlay", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!NativeMethods.GetWindowRect(candidate, out NativeMethods.WindowRect bounds))
                continue;

            long area = Math.Max(0, bounds.Right - bounds.Left) * (long)Math.Max(0, bounds.Bottom - bounds.Top);
            if (area > largestArea)
            {
                largestArea = area;
                selected = candidate;
            }
        }
        return selected;
    }

    private static string GetWindowTitle(nint window)
    {
        int length = NativeMethods.GetWindowTextLength(window);
        if (length == 0)
            return string.Empty;
        StringBuilder title = new(length + 1);
        return NativeMethods.GetWindowText(window, title, title.Capacity) == 0 ? string.Empty : title.ToString();
    }

    private static async Task CaptureFailureEvidenceAsync(IPage page, string runDirectory)
    {
        try
        {
            await page.ScreenshotAsync(
                new PageScreenshotOptions { Path = Path.Combine(runDirectory, "failure.png"), FullPage = true }
            );
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "failure-dom.html"), await page.ContentAsync());
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "failure-accessibility.yml"),
                await page.Locator("body").AriaSnapshotAsync()
            );
        }
        catch (PlaywrightException exception)
        {
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "failure-capture-error.txt"), exception.ToString());
        }
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        process.Refresh();
        if (process.HasExited)
            return;

        process.CloseMainWindow();
        using CancellationTokenSource gracefulTimeout = new(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(gracefulTimeout.Token);
            return;
        }
        catch (OperationCanceledException)
        {
            // Fall through to a scoped process-tree kill for the app launched by this test.
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    private static float ParseSlowMo() =>
        float.TryParse(
            Environment.GetEnvironmentVariable("RATSCANNER_UI_SLOWMO_MS"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float slowMo
        )
            ? Math.Max(0, slowMo)
            : 0;

    private static string GetConfiguration()
    {
        DirectoryInfo targetFramework = new(AppContext.BaseDirectory);
        return targetFramework.Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not determine the test build configuration.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RatScanner.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static class NativeMethods
    {
        internal const uint ResizeOnlyFlags = 0x0002 | 0x0004 | 0x0010; // no move, z-order change, or activation
        internal const int RestoreWindow = 9;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll")]
        internal static extern nint GetTopWindow(nint window);

        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint window, uint command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out WindowRect bounds);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowText(nint window, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowTextLength(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetDpiForWindow(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags
        );
    }
}
