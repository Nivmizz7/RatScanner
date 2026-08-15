#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        await using UiSession session = await UiSession.StartAsync(testCancellation);
        try
        {
            IPage page = session.Page;

            await page.Locator(".scan-page")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 120_000 });
            await AssertVisibleAsync(page.Locator(".rs-search-field input"));
            await AssertVisibleAsync(page.GetByRole(AriaRole.Navigation).First);
            await session.ResizeAsync(width: 1100, height: 850);
            await AssertNoHorizontalOverflowAsync(page, "desktop scan page");
            await session.ScreenshotAsync("desktop-scan.png");

            ILocator settingsLink = page.Locator("a[href='/app/settings/general']");
            await settingsLink.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync("**/app/settings/general");
            await AssertVisibleAsync(page.Locator(".settings-page"));
            Assert.NotEqual("BODY", await page.EvaluateAsync<string>("document.activeElement?.tagName ?? ''"));

            await page.Locator("a[href='/app/settings/tracking']").ClickAsync();
            await page.WaitForURLAsync("**/app/settings/tracking");
            ILocator gameModeSelect = page.GetByRole(AriaRole.Combobox, new() { Name = "Game mode", Exact = true });
            await AssertVisibleAsync(gameModeSelect);
            await gameModeSelect.ClickAsync();
            ILocator pvpModeOption = page.GetByRole(AriaRole.Option, new() { Name = "PvP", Exact = true });
            await AssertVisibleAsync(pvpModeOption);
            await AssertVisibleAsync(page.GetByRole(AriaRole.Option, new() { Name = "PvE", Exact = true }));
            await AssertVisibleAsync(page.GetByRole(AriaRole.Option, new() { Name = "Seasonal PvP", Exact = true }));
            string selectedModeWeight = await gameModeSelect.EvaluateAsync<string>(
                "element => getComputedStyle(element).fontWeight"
            );
            string optionModeWeight = await pvpModeOption
                .Locator(".mud-list-item-text")
                .EvaluateAsync<string>("element => getComputedStyle(element).fontWeight");
            Assert.Equal(selectedModeWeight, optionModeWeight);
            await page.Keyboard.PressAsync("Escape");
            ILocator seasonalTrackingHeading = page.Locator(".tracking-settings .tracker-mode-title h3")
                .GetByText("Seasonal PvP", new() { Exact = true });
            await AssertVisibleAsync(seasonalTrackingHeading);
            Assert.Equal(3, await page.Locator(".tracking-settings .tracker-mode").CountAsync());
            ILocator manageApiKeysLink = page.GetByRole(
                AriaRole.Link,
                new() { Name = "Manage API keys", Exact = true }
            );
            await AssertVisibleAsync(manageApiKeysLink);
            Assert.Equal(1, await manageApiKeysLink.CountAsync());
            Assert.Equal("https://tarkovtracker.org/settings#api", await manageApiKeysLink.GetAttributeAsync("href"));
            Assert.Equal(0, await page.Locator(".tracker-manage-link").CountAsync());
            Assert.Equal(0, await page.Locator(".source-card").CountAsync());
            Assert.Equal(0, await page.GetByText("TarkovTracker.io", new() { Exact = false }).CountAsync());
            // A stale link can survive with a different label, so assert on the href too.
            Assert.Equal(0, await page.Locator("a[href*='tarkovtracker.io' i]").CountAsync());
            await AssertNoHorizontalOverflowAsync(page, "desktop tracking settings");
            await seasonalTrackingHeading.ScrollIntoViewIfNeededAsync();
            await session.ScreenshotAsync("desktop-seasonal-tracking.png", fullPage: false);

            await session.ResizeAsync(width: 600, height: 850);
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
            await page.Locator(".tracker-section-heading").ScrollIntoViewIfNeededAsync();
            await AssertNoHorizontalOverflowAsync(page, "narrow tracking settings");
            int trackingViewportWidth = await page.EvaluateAsync<int>("window.innerWidth");
            await AssertInsideViewportAsync(
                page.Locator(".tracking-settings"),
                width: trackingViewportWidth,
                surface: "narrow tracking settings"
            );
            await session.ScreenshotAsync("narrow-tracking-settings.png", fullPage: false);
            await session.ResizeAsync(width: 1100, height: 850);

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
            await session.ResizeAsync(width: 600, height: 850);
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
            await session.ScreenshotAsync(
                "narrow-settings.png",
                // Full-page capture includes the transformed-offscreen drawer in the image bounds.
                // Capture the real narrow viewport so the visual artifact matches what a user sees.
                fullPage: false
            );

            string ariaSnapshot = await page.Locator("body").AriaSnapshotAsync();
            await File.WriteAllTextAsync(
                Path.Combine(session.RunDirectory, "accessibility.yml"),
                ariaSnapshot,
                testCancellation
            );
            await File.WriteAllTextAsync(
                Path.Combine(session.RunDirectory, "current-url.txt"),
                page.Url,
                testCancellation
            );

            Assert.Empty(session.RuntimeFailures);
        }
        catch (Exception exception)
        {
            await session.MarkFailedAsync(exception);
            throw;
        }
    }

    [Fact]
    [Trait("Category", "UI")]
    public async Task Scan_result_card_contains_non_square_icons_and_details_state_follows_user_actions()
    {
        // Seed a tiny offline catalog (Pevko 1x2 portrait, Makarov PM 2x1 landscape,
        // M4A1 1x1 square) so the search-driven result card renders hermetically with
        // locally installed icons — no network and no dependency on a developer cache.
        using CatalogCacheSeed catalogSeed = new();
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        await using UiSession session = await UiSession.StartAsync(testCancellation);
        try
        {
            IPage page = session.Page;
            await page.Locator(".scan-page")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 120_000 });
            await session.ResizeAsync(width: 1100, height: 850);

            ILocator detailsToggle = page.Locator(".details-toggle");

            // Portrait non-square item (1x2, 64x127 local icon) must stay inside the frame.
            await SelectCatalogItemAsync(page, "Pevko");
            await AssertVisibleAsync(page.Locator(".result-card"));
            await WaitForArtImageLoadedAsync(page);
            await AssertArtContainedAsync(page, "desktop portrait (Pevko)");
            await session.ScreenshotAsync("result-card-pevko.png");
            await WaitForDetailsStateAsync(page, expanded: false);

            // Opening Details is a toggle, and a fresh scan-driven render must not close it.
            await detailsToggle.ClickAsync();
            await WaitForDetailsStateAsync(page, expanded: true);
            await AssertVisibleAsync(page.Locator(".details"));
            await session.ScreenshotAsync("result-card-details-open.png");

            // Deliberately picking a different item from search is an explicit switch:
            // it must close Details again, and the landscape icon must stay contained.
            await SelectCatalogItemAsync(page, "PM");
            await WaitForArtImageLoadedAsync(page);
            await WaitForArtAltAsync(page, "Makarov PM 9x18PM pistol");
            await WaitForDetailsStateAsync(page, expanded: false);
            await AssertArtContainedAsync(page, "desktop landscape (PM)");
            await session.ScreenshotAsync("result-card-pm.png");

            // Clicking a recent scan is an explicit switch too: it closes Details.
            await detailsToggle.ClickAsync();
            await WaitForDetailsStateAsync(page, expanded: true);
            ILocator recentThumb = page.Locator(".recent .thumb").First;
            Assert.True(await recentThumb.CountAsync() > 0, "Recent scans should list the scanned items.");
            await recentThumb.ClickAsync();
            await WaitForDetailsStateAsync(page, expanded: false);

            // Square icons must stay contained as well.
            await SelectCatalogItemAsync(page, "M4A1");
            await WaitForArtImageLoadedAsync(page);
            await WaitForArtAltAsync(page, "Colt M4A1 5.56x45 assault rifle");
            await AssertArtContainedAsync(page, "desktop square (M4A1)");

            // Narrow breakpoint: portrait icon still contained inside the 72x54 frame.
            await session.ResizeAsync(width: 600, height: 850);
            await SelectCatalogItemAsync(page, "Pevko");
            await WaitForArtImageLoadedAsync(page);
            await WaitForArtAltAsync(page, "Bottle of Pevko Light beer");
            await AssertArtContainedAsync(page, "narrow portrait (Pevko)");
            await session.ScreenshotAsync("result-card-narrow.png");

            Assert.Empty(session.RuntimeFailures);
        }
        catch (Exception exception)
        {
            await session.MarkFailedAsync(exception);
            throw;
        }
    }

    private static async Task SelectCatalogItemAsync(IPage page, string query)
    {
        ILocator searchInput = page.Locator(".rs-search-field input");
        await searchInput.FillAsync(string.Empty);
        await searchInput.FillAsync(query);
        // MudAutocomplete renders one .mud-list-item per result whose ShortName sits in a
        // <small>; match that exactly so a query like "PM" cannot land on another item
        // whose Name merely contains the text (HasText is a substring match).
        ILocator option = page.Locator(".mud-list-item")
            .Filter(
                new LocatorFilterOptions
                {
                    Has = page.Locator(".search-result small")
                        .GetByText(query, new LocatorGetByTextOptions { Exact = true }),
                }
            )
            .First;
        await option.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await option.ClickAsync();
    }

    private static async Task WaitForArtImageLoadedAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            """
            () => {
                const img = document.querySelector('.item-art img');
                return img !== null && img.complete && img.naturalWidth > 0;
            }
            """
        );
    }

    private static async Task WaitForArtAltAsync(IPage page, string expectedAlt)
    {
        await page.WaitForFunctionAsync(
            """
            (expected) => {
                const img = document.querySelector('.item-art img');
                return img !== null && img.getAttribute('alt') === expected;
            }
            """,
            expectedAlt
        );
    }

    private static async Task WaitForDetailsStateAsync(IPage page, bool expanded)
    {
        string expected = expanded ? "true" : "false";
        await page.WaitForFunctionAsync(
            """
            (expected) => {
                const toggle = document.querySelector('.details-toggle');
                return toggle !== null && toggle.getAttribute('aria-expanded') === expected;
            }
            """,
            expected
        );
    }

    private static async Task AssertArtContainedAsync(IPage page, string surface)
    {
        double[] box = await page.EvaluateAsync<double[]>(
            """
            () => {
                const frame = document.querySelector('.item-art').getBoundingClientRect();
                const img = document.querySelector('.item-art img').getBoundingClientRect();
                return [frame.left, frame.top, frame.right, frame.bottom, img.left, img.top, img.right, img.bottom];
            }
            """
        );
        Assert.True(
            box[4] >= box[0] - 0.5 && box[5] >= box[1] - 0.5 && box[6] <= box[2] + 0.5 && box[7] <= box[3] + 0.5,
            $"{surface}: item art image overflows the frame (frame [{box[0]:0.##},{box[1]:0.##},{box[2]:0.##},{box[3]:0.##}] "
                + $"vs img [{box[4]:0.##},{box[5]:0.##},{box[6]:0.##},{box[7]:0.##}])."
        );
    }

    private static async Task AssertNoHorizontalOverflowAsync(IPage page, string surface)
    {
        // The document element and .main-content are independent scroll containers, so
        // an inner overflow can hide behind a clean document-level measurement. Report
        // the offending container and its measurements to keep failures diagnosable.
        string? overflow = await page.EvaluateAsync<string?>(
            """
            () => {
                const containers = [['document', document.documentElement]];
                const main = document.querySelector('.main-content');
                if (main) containers.push(['.main-content', main]);
                for (const [name, element] of containers) {
                    if (element.scrollWidth > element.clientWidth)
                        return name + ' ' + element.scrollWidth + 'px > ' + element.clientWidth + 'px';
                }
                return null;
            }
            """
        );
        Assert.True(overflow is null, $"Unexpected horizontal overflow on {surface}: {overflow}.");
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

    /// <summary>
    /// Owns a launched RatScanner process (isolated WebView2 profile), the CDP
    /// connection used to drive it, and every artifact/cleanup responsibility of a
    /// UI run. The single-instance rule is enforced at startup: the test must never
    /// attach to or stop an unrelated RatScanner instance.
    /// </summary>
    private sealed class UiSession : IAsyncDisposable
    {
        private readonly Process _app;
        private readonly Task<string> _stdoutTask;
        private readonly Task<string> _stderrTask;
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly IBrowserContext _context;
        private readonly string _profileDirectory;
        private readonly string _appLogPath;
        private readonly ConcurrentQueue<string> _runtimeFailures;
        private readonly CancellationToken _cancellationToken;
        private bool _traceStarted;
        private bool _failed;
        private bool _disposed;

        internal IPage Page { get; }

        internal string RunDirectory { get; }

        internal ConcurrentQueue<string> RuntimeFailures => _runtimeFailures;

        private UiSession(
            Process app,
            Task<string> stdoutTask,
            Task<string> stderrTask,
            IPlaywright playwright,
            IBrowser browser,
            IBrowserContext context,
            IPage page,
            string runDirectory,
            string profileDirectory,
            string appLogPath,
            ConcurrentQueue<string> runtimeFailures,
            bool traceStarted,
            CancellationToken cancellationToken
        )
        {
            _app = app;
            _stdoutTask = stdoutTask;
            _stderrTask = stderrTask;
            _playwright = playwright;
            _browser = browser;
            _context = context;
            Page = page;
            RunDirectory = runDirectory;
            _profileDirectory = profileDirectory;
            _appLogPath = appLogPath;
            _runtimeFailures = runtimeFailures;
            _cancellationToken = cancellationToken;
            _traceStarted = traceStarted;
        }

        internal static async Task<UiSession> StartAsync(CancellationToken cancellationToken)
        {
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

            ProcessStartInfo startInfo = new(appPath)
            {
                WorkingDirectory = appDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.Environment["RATSCANNER_UI_TEST_CDP_PORT"] = "0";
            startInfo.Environment["RATSCANNER_UI_TEST_PROFILE"] = profileDirectory;

            Process? startedApp = Process.Start(startInfo);
            Assert.NotNull(startedApp);
            Process app = startedApp;
            Task<string> stdoutTask = app.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = app.StandardError.ReadToEndAsync(cancellationToken);

            IPlaywright? playwright = null;
            IBrowser? browser = null;
            try
            {
                int port = await WaitForDebugPortAsync(profileDirectory, app, StartupTimeout);
                await File.WriteAllTextAsync(
                    Path.Combine(runDirectory, "endpoint.txt"),
                    $"http://127.0.0.1:{port}",
                    cancellationToken
                );

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
                IBrowserContext context = Assert.Single(browser.Contexts);
                await context.Tracing.StartAsync(
                    new TracingStartOptions
                    {
                        Screenshots = true,
                        Snapshots = true,
                        Sources = true,
                    }
                );

                IPage page = await WaitForAppPageAsync(context, app, StartupTimeout);

                return new UiSession(
                    app,
                    stdoutTask,
                    stderrTask,
                    playwright,
                    browser,
                    context,
                    page,
                    runDirectory,
                    profileDirectory,
                    Path.Combine(appDirectory, "Log.txt"),
                    AttachRuntimeDiagnostics(page),
                    traceStarted: true,
                    cancellationToken
                );
            }
            catch
            {
                // One failure path for the whole post-start sequence: stop the owned process,
                // release any Playwright/browser resources created so far, and remove the
                // temporary profile directory. Cleanup is best effort so the original startup
                // failure stays the exception the caller sees.
                try
                {
                    await StopOwnedProcessAsync(app);
                }
                catch
                {
                    // Best effort; the startup failure is the reportable one.
                }

                if (browser is not null)
                {
                    try
                    {
                        await browser.DisposeAsync();
                    }
                    catch
                    {
                        // Best effort; the startup failure is the reportable one.
                    }
                }

                if (playwright is not null)
                {
                    try
                    {
                        playwright.Dispose();
                    }
                    catch
                    {
                        // Best effort; the startup failure is the reportable one.
                    }
                }

                try
                {
                    Directory.Delete(profileDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // Best effort; the startup failure is the reportable one.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best effort; the startup failure is the reportable one.
                }

                throw;
            }
        }

        internal async Task ResizeAsync(int width, int height)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            nint window = 0;
            while (DateTime.UtcNow < deadline)
            {
                _app.Refresh();
                window = FindLargestOwnedWindow(_app.Id);
                if (window != 0)
                    break;
                if (_app.HasExited)
                    break;
                await Task.Delay(100, _cancellationToken);
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
            int viewportWidth = await Page.EvaluateAsync<int>("window.innerWidth").WaitAsync(_cancellationToken);
            DateTime resizeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < resizeDeadline)
            {
                if (
                    !NativeMethods.SetWindowPos(
                        window,
                        0,
                        0,
                        0,
                        deviceWidth,
                        deviceHeight,
                        NativeMethods.ResizeOnlyFlags
                    )
                )
                    throw new InvalidOperationException(
                        $"Unable to resize RatScanner for UI validation (Win32 error {Marshal.GetLastWin32Error()})."
                    );

                viewportWidth = await Page.EvaluateAsync<int>("window.innerWidth").WaitAsync(_cancellationToken);
                bool expectedBreakpoint = width <= 680 ? viewportWidth <= 680 : viewportWidth > 680;
                if (expectedBreakpoint)
                    return;
                await Task.Delay(100, _cancellationToken);
            }

            throw new InvalidOperationException(
                $"RatScanner did not reach the requested {width}px responsive viewport; actual width was {viewportWidth}px."
            );
        }

        internal async Task ScreenshotAsync(string fileName, bool fullPage = true)
        {
            await Page.ScreenshotAsync(
                new PageScreenshotOptions { Path = Path.Combine(RunDirectory, fileName), FullPage = fullPage }
            );
        }

        /// <summary>Captures failure evidence; the session keeps the trace for diagnosis.</summary>
        internal async Task MarkFailedAsync(Exception exception)
        {
            _failed = true;
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(RunDirectory, "failure-url.txt"),
                    Page.Url,
                    _cancellationToken
                );
                await File.WriteAllTextAsync(
                    Path.Combine(RunDirectory, "failure-page-state.txt"),
                    await Page.EvaluateAsync<string>(
                        """
                        () => {
                            const toggle = document.querySelector('.details-toggle');
                            const card = document.querySelector('.result-card');
                            return JSON.stringify({
                                hidden: document.hidden,
                                visibilityState: document.visibilityState,
                                title: document.title,
                                innerWidth: window.innerWidth,
                                innerHeight: window.innerHeight,
                                toggleExists: toggle !== null,
                                ariaExpanded: toggle ? toggle.getAttribute('aria-expanded') : null,
                                detailsVisible: !!document.querySelector('.details'),
                                resultCard: card !== null,
                                artAlt: document.querySelector('.item-art img')?.getAttribute('alt') ?? null,
                            });
                        }
                        """
                    )
                );
                await CaptureFailureEvidenceAsync(Page, RunDirectory);
            }
            catch (Exception captureException)
            {
                TryWriteDiagnostic(
                    Path.Combine(RunDirectory, "failure-capture-error.txt"),
                    captureException.ToString()
                );
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            List<Exception> cleanupFailures = [];
            await RecordCleanupFailureAsync(
                cleanupFailures,
                async () =>
                {
                    if (!RuntimeFailures.IsEmpty)
                        await File.WriteAllLinesAsync(
                            Path.Combine(RunDirectory, "browser-runtime.log"),
                            RuntimeFailures
                        );
                }
            );

            if (_traceStarted)
            {
                await RecordCleanupFailureAsync(
                    cleanupFailures,
                    async () =>
                    {
                        await _context.Tracing.StopAsync(
                            _failed ? new TracingStopOptions { Path = Path.Combine(RunDirectory, "trace.zip") } : null
                        );
                    }
                );
                RecordCleanupFailure(
                    cleanupFailures,
                    () =>
                    {
                        foreach (string transientTrace in Directory.EnumerateFiles(RunDirectory, "*.trace"))
                            File.Delete(transientTrace);
                        foreach (string transientNetwork in Directory.EnumerateFiles(RunDirectory, "*.network"))
                            File.Delete(transientNetwork);
                    }
                );
            }

            await RecordCleanupFailureAsync(cleanupFailures, async () => await _browser.DisposeAsync());
            RecordCleanupFailure(cleanupFailures, _playwright.Dispose);

            await RecordCleanupFailureAsync(cleanupFailures, async () => await StopOwnedProcessAsync(_app));
            bool appExited = false;
            RecordCleanupFailure(
                cleanupFailures,
                () =>
                {
                    _app.Refresh();
                    appExited = _app.HasExited;
                }
            );
            if (appExited)
            {
                await RecordCleanupFailureAsync(
                    cleanupFailures,
                    async () =>
                        await File.WriteAllTextAsync(Path.Combine(RunDirectory, "app-stdout.log"), await _stdoutTask)
                );
                await RecordCleanupFailureAsync(
                    cleanupFailures,
                    async () =>
                        await File.WriteAllTextAsync(Path.Combine(RunDirectory, "app-stderr.log"), await _stderrTask)
                );
            }

            if (File.Exists(_appLogPath))
            {
                RecordCleanupFailure(
                    cleanupFailures,
                    () => File.Copy(_appLogPath, Path.Combine(RunDirectory, "RatScanner.log"), overwrite: true)
                );
            }

            try
            {
                Directory.Delete(_profileDirectory, recursive: true);
            }
            catch (IOException exception)
            {
                TryWriteDiagnostic(Path.Combine(RunDirectory, "profile-cleanup-error.txt"), exception.ToString());
            }
            catch (UnauthorizedAccessException exception)
            {
                TryWriteDiagnostic(Path.Combine(RunDirectory, "profile-cleanup-error.txt"), exception.ToString());
            }

            if (cleanupFailures.Count > 0)
            {
                TryWriteDiagnostic(
                    Path.Combine(RunDirectory, "cleanup-errors.log"),
                    string.Join(Environment.NewLine + Environment.NewLine, cleanupFailures)
                );
            }
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

        private static ConcurrentQueue<string> AttachRuntimeDiagnostics(IPage page)
        {
            ConcurrentQueue<string> failures = new();
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
            return failures;
        }

        private static bool IsAppResource(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && (uri.Host is "0.0.0.1" or "local.data" || uri.Scheme == "file");

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
                await File.WriteAllTextAsync(
                    Path.Combine(runDirectory, "failure-capture-error.txt"),
                    exception.ToString()
                );
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
    }

    /// <summary>
    /// Writes the tiny fixture catalog into the app's offline cache location
    /// (<c>%TEMP%\RatScanner\Cache</c>) for every locale/game-mode key the app can
    /// ask for, so the launched app serves the search from cache without touching
    /// the network or depending on a developer's cache. Pre-existing cache files
    /// are backed up and restored on dispose; only this fixture's files are removed.
    /// </summary>
    private sealed class CatalogCacheSeed : IDisposable
    {
        // Mirrors TarkovDevAPI.ItemsQueryKey(locale, gameMode) => $"items_{locale}_{gameMode}"
        // and RatConfig.GetCachePath (SHA-256 of the key, hex, .data). The app's
        // Newtonsoft deserialization matches property names case-insensitively.
        private static readonly string[] Locales =
        [
            "zh",
            "cs",
            "en",
            "es",
            "fr",
            "de",
            "hu",
            "it",
            "ja",
            "ko",
            "pl",
            "pt",
            "ru",
            "sk",
            "tr",
        ];

        private static readonly string[] GameModes = ["Regular", "Pve", "Seasonal"];

        private readonly string _cacheDirectory;
        private readonly Dictionary<string, string> _backups = new(StringComparer.OrdinalIgnoreCase);

        internal CatalogCacheSeed()
        {
            _cacheDirectory = Path.Combine(Path.GetTempPath(), "RatScanner", "Cache");
            Assert.Empty(Process.GetProcessesByName("RatScanner"));

            foreach (string key in CacheKeys())
            {
                string path = Path.Combine(_cacheDirectory, CacheFileName(key));
                if (File.Exists(path))
                {
                    string backup = path + "." + Guid.NewGuid().ToString("N") + ".bak";
                    File.Move(path, backup);
                    _backups[path] = backup;
                }
            }

            Directory.CreateDirectory(_cacheDirectory);
            foreach (string key in CacheKeys())
                File.WriteAllText(Path.Combine(_cacheDirectory, CacheFileName(key)), FixtureItemsJson);
        }

        public void Dispose()
        {
            // The app process is stopped before this runs, but WebView2 child processes can
            // linger and hold cache files briefly; retry so transient locks do not fail the
            // run. Any persistent failure is reported so the test run artifacts show it.
            List<Exception> cleanupFailures = [];
            foreach (string key in CacheKeys())
            {
                string path = Path.Combine(_cacheDirectory, CacheFileName(key));
                try
                {
                    DeleteWithRetries(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleanupFailures.Add(exception);
                }
            }

            foreach ((string path, string backup) in _backups)
            {
                try
                {
                    File.Move(backup, path, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleanupFailures.Add(exception);
                }
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    $"{cleanupFailures.Count} catalog cache cleanup operation(s) failed; "
                        + "a later run may back up and restore stale fixture files.",
                    cleanupFailures
                );
            }
        }

        private static IEnumerable<string> CacheKeys()
        {
            foreach (string locale in Locales)
            {
                foreach (string gameMode in GameModes)
                    yield return $"items_{locale}_{gameMode}";
            }
        }

        private static string CacheFileName(string key)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash) + ".data";
        }

        private static void DeleteWithRetries(string path)
        {
            const int attempts = 3;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Delete(path);
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (attempt + 1 >= attempts)
                        throw;
                    Thread.Sleep(150);
                }
            }
        }

        // Item ids, names and icons match the repository's installed Data/icons so the
        // result card resolves the local icons (64x127 portrait, 127x64 landscape, 64x64
        // square) instead of a remote asset.
        private const string FixtureItemsJson = """
            [
              {
                "Id": "62a09f32621468534a797acb",
                "Name": "Bottle of Pevko Light beer",
                "ShortName": "Pevko",
                "Updated": "2026-08-08T04:56:48.000Z",
                "Width": 1,
                "Height": 2,
                "WikiLink": "https://escapefromtarkov.fandom.com/wiki/Bottle_of_Pevko_Light_beer",
                "Link": "https://tarkov.dev/item/bottle-of-pevko-light-beer",
                "IconLink": "https://assets.tarkov.dev/62a09f32621468534a797acb-icon.webp",
                "BaseImageLink": "https://assets.tarkov.dev/62a09f32621468534a797acb-base-image.webp",
                "Avg24HPrice": 5200,
                "BackgroundColor": "green",
                "Types": ["other"]
              },
              {
                "Id": "5448bd6b4bdc2dfc2f8b4569",
                "Name": "Makarov PM 9x18PM pistol",
                "ShortName": "PM",
                "Updated": "2026-08-08T04:51:41.000Z",
                "Width": 2,
                "Height": 1,
                "WikiLink": "https://escapefromtarkov.fandom.com/wiki/Makarov_PM_9x18PM_pistol",
                "Link": "https://tarkov.dev/item/makarov-pm-9x18pm-pistol",
                "IconLink": "https://assets.tarkov.dev/5448bd6b4bdc2dfc2f8b4569-icon.webp",
                "BaseImageLink": "https://assets.tarkov.dev/5448bd6b4bdc2dfc2f8b4569-base-image.webp",
                "Avg24HPrice": 4800,
                "BackgroundColor": "yellow",
                "Types": ["gun", "wearable"]
              },
              {
                "Id": "5447a9cd4bdc2dbd208b4567",
                "Name": "Colt M4A1 5.56x45 assault rifle",
                "ShortName": "M4A1",
                "Updated": "2026-08-08T04:51:41.000Z",
                "Width": 1,
                "Height": 1,
                "WikiLink": "https://escapefromtarkov.fandom.com/wiki/Colt_M4A1_5.56x45_assault_rifle",
                "Link": "https://tarkov.dev/item/colt-m4a1-556x45-assault-rifle",
                "IconLink": "https://assets.tarkov.dev/5447a9cd4bdc2dbd208b4567-icon.webp",
                "BaseImageLink": "https://assets.tarkov.dev/5447a9cd4bdc2dbd208b4567-base-image.webp",
                "Avg24HPrice": 38500,
                "BackgroundColor": "yellow",
                "Types": ["gun"]
              }
            ]
            """;
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
