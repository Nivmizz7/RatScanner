using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using RatEye;
using RatScanner.Display;
using RatScanner.Scan;
using RatStash;
using GameMode = RatScanner.TarkovDev.GameMode;
using MessageBox = System.Windows.MessageBox;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using PvpSource = RatScanner.TarkovDev.PvpSource;
using Size = System.Drawing.Size;
using TarkovItem = RatScanner.TarkovDev.Item;
using Timer = System.Threading.Timer;

namespace RatScanner;

internal readonly record struct TrackerConfigurationHandle(long Generation);

public sealed class RatScannerMain : INotifyPropertyChanged, IDisposable
{
    private static readonly object InstanceLock = new();
    private static RatScannerMain _instance = null!;
    private static bool _shutdownStarted;

    internal static RatScannerMain Instance
    {
        get
        {
            lock (InstanceLock)
            {
                ObjectDisposedException.ThrowIf(_shutdownStarted, typeof(RatScannerMain));
                return _instance ??= new RatScannerMain();
            }
        }
    }

    internal static void DisposeInstance()
    {
        RatScannerMain? instance;
        lock (InstanceLock)
        {
            _shutdownStarted = true;
            instance = _instance;
            _instance = null!;
        }
        instance?.Dispose();
    }

    internal readonly HotkeyManager HotkeyManager;

    private Timer? _tarkovTrackerDBRefreshTimer;
    private Timer? _scanRefreshTimer;
    private readonly object _scanRefreshTimerLock = new();
    private readonly object _tarkovTrackerTimerLock = new();
    private readonly object _ratEyeSetupLock = new();
    private readonly object _trackerConfigurationLock = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ScanDiagnosticStore _scanDiagnostics = new();
    private int _trackerRefreshInProgress;
    private bool _ratEyeReady;
    private bool _disposed;

    /// <summary>
    /// Lock for name scanning
    /// </summary>
    /// <remarks>
    /// Lock order: 0
    /// </remarks>
    internal static object NameScanLock = new();

    /// <summary>
    /// Lock for icon scanning
    /// </summary>
    /// <remarks>
    /// Lock order: 1
    /// </remarks>
    internal static object IconScanLock = new();

    /// <summary>
    /// Caps the scan rate across all per-position scan entry points (name scan
    /// and icon scan). Hotkey spam must not be able to drive the OCR pipeline
    /// and the overlay compositor at unbounded rate. NameScanScreen is exempt:
    /// it is opt-in and already debounced by the 500 ms auto-scan click window.
    /// </summary>
    private readonly ScanThrottle _scanThrottle = new(RatConfig.NameScan.CooldownMs);

    public TarkovTrackerDB TarkovTrackerDB;

    internal RatEyeEngine RatEyeEngine;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ItemQueue ItemScans = new();

    private RatScannerMain()
    {
        _instance = this;
        _scanRefreshTimer = new Timer(RefreshOverlay, null, Timeout.Infinite, Timeout.Infinite);
        ItemScans.Changed += OnItemScansChanged;

        // Remove old log
        Logger.Clear();

        Logger.LogInfo("----- " + RatConfig.FullVersionLabel + " -----");
        _ = CheckForUpdatesAsync();

        Logger.LogInfo(
            $"Screen Info: {RatConfig.ScreenWidth}x{RatConfig.ScreenHeight} at {RatConfig.ScreenScale * 100}%"
        );

        Logger.LogInfo("Initializing TarkovDev API...");

        // Try to load from offline cache first for faster startup. Start the network
        // refresh only after RatEye is listening for cache updates below.
        bool cacheRefreshNeeded;
        if (TarkovDevAPI.TryInitializeCacheFromOffline())
        {
            if (TarkovDevAPI.AnyCacheExpired())
            {
                Logger.LogInfo("Offline cache loaded but stale, refreshing in background...");
                cacheRefreshNeeded = true;
            }
            else
            {
                Logger.LogInfo("Offline cache loaded and fresh, skipping background refresh.");
                cacheRefreshNeeded = false;
            }
        }
        else
        {
            // No offline cache available, wait for network requests
            Logger.LogWarning("No complete offline cache available, fetching from network...");
            cacheRefreshNeeded = true;
        }

        SeedInitialItem();

        Logger.LogInfo("Initializing tarkov tracker database");
        TarkovTrackerDB = new TarkovTrackerDB();
        // Configure synchronously so the UI's first render sees Untested (token
        // present) or NotConfigured (no token) instead of the constructor default
        // NotConfigured — preventing a false "not connected" banner flash before
        // InitializeRuntimeAsync validates the key ~1s later.
        ConfigureActiveTracker(RatConfig.GameMode);

        Logger.LogInfo("Initializing hotkey manager...");
        HotkeyManager = new HotkeyManager(this);
        HotkeyManager.UnregisterHotkeys();

        Logger.LogInfo("UI Ready!");

        Logger.LogInfo("Initializing RatEye...");
        SetupRatEye();
        TarkovDevAPI.ItemsCacheUpdated += OnItemsCacheUpdated;

        if (cacheRefreshNeeded)
            _ = RefreshApiCacheAsync();
        _ = InitializeRuntimeAsync(_lifetimeCancellation.Token);
    }

    private static async Task RefreshApiCacheAsync()
    {
        try
        {
            await TarkovDevAPI.InitializeCache().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // API initialization is a degraded mode: cached data can still be used and
            // later requests will retry after their backoff period.
            Logger.LogWarning("Unable to initialize the tarkov.dev cache.", e);
        }
    }

    private async Task InitializeRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1000, cancellationToken);
            Logger.LogInfo("Loading TarkovTracker data...");
            await ActivateTrackerModeAsync(RatConfig.GameMode, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            Logger.LogInfo("Setting up timer routines...");
            lock (_tarkovTrackerTimerLock)
            {
                if (!_disposed)
                {
                    _tarkovTrackerDBRefreshTimer = new Timer(
                        RefreshTarkovTrackerDB,
                        null,
                        RatConfig.Tracking.TarkovTracker.RefreshTime,
                        Timeout.Infinite
                    );
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Logger.LogInfo("Enabling hotkeys...");
            HotkeyManager.RegisterHotkeys();
            Logger.LogInfo("Ready!");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger.LogWarning("Runtime initialization failed; RatScanner will continue in degraded mode.", e);
        }
    }

    private static async Task CheckForUpdatesAsync()
    {
        Logger.LogInfo("Checking the maintained fork for updates...");
        GitHubUpdateService.LatestRelease? release = await GitHubUpdateService
            .TryGetLatestReleaseAsync()
            .ConfigureAwait(false);
        if (release == null || !GitHubUpdateService.IsNewerVersion(RatConfig.Version, release.Version))
            return;
        Logger.LogInfo("A new version is available: " + release.Version);

        string message = "Version " + release.Version + " is available!\n";
        message += "You are using: " + RatConfig.FullVersionLabel + "\n\n";
        message += "Do you want to install it now?";
        MessageBoxResult result = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(
                message,
                Constants.Branding.Name + " update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information
            )
        );
        if (result != MessageBoxResult.Yes)
            return;

        bool started = await Task.Run(() => GitHubUpdateService.TryApplyUpdate(release)).ConfigureAwait(false);
        if (started)
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                System.Windows.Application.Current.Shutdown()
            );
    }

    internal TrackerConfigurationHandle ConfigureActiveTracker(GameMode mode)
    {
        lock (_trackerConfigurationLock)
        {
            (string token, string endpoint) = GetActiveTrackerConfiguration(mode);
            long generation = TarkovTrackerDB.Configure(token, endpoint, mode);
            return new TrackerConfigurationHandle(generation);
        }
    }

    internal async Task ActivateTrackerModeAsync(GameMode mode, CancellationToken cancellationToken = default)
    {
        ConfigureActiveTracker(mode);
        if (string.IsNullOrWhiteSpace(TarkovTrackerDB.Token))
        {
            OnPropertyChanged(nameof(TarkovTrackerDB));
            return;
        }

        await TarkovTrackerDB.InitAsync(cancellationToken).ConfigureAwait(false);
        OnPropertyChanged(nameof(TarkovTrackerDB));
    }

    internal Task<TrackerValidationResult> ValidateTarkovTrackerOrgKeyAsync(
        GameMode mode,
        string token,
        CancellationToken cancellationToken = default
    ) =>
        TarkovTrackerDB.ValidateCandidateAsync(
            token,
            RatConfig.Tracking.TarkovTracker.OrgEndpoint,
            mode,
            cancellationToken
        );

    internal Task<TrackerValidationResult> ValidateTarkovTrackerIoKeyAsync(
        string token,
        CancellationToken cancellationToken = default
    ) =>
        TarkovTrackerDB.ValidateCandidateAsync(
            token,
            RatConfig.Tracking.TarkovTracker.IoEndpoint,
            GameMode.Regular,
            cancellationToken
        );

    private static (string Token, string Endpoint) GetActiveTrackerConfiguration(GameMode mode)
    {
        if (mode == GameMode.Regular && RatConfig.Tracking.TarkovTracker.PvpSource == PvpSource.Io)
        {
            string ioToken = RatConfig.Tracking.TarkovTracker.IoToken;
            if (!string.IsNullOrWhiteSpace(ioToken))
                return (ioToken, RatConfig.Tracking.TarkovTracker.IoEndpoint);
            // Configured for Io but no Io token: fall back to org PvP if present
            // so a misconfigured source never silently disables tracking.
        }
        string orgToken = RatConfig.Tracking.TarkovTracker.TokenForMode(mode);
        if (!string.IsNullOrWhiteSpace(orgToken))
            return (orgToken, RatConfig.Tracking.TarkovTracker.OrgEndpoint);
        return ("", RatConfig.Tracking.TarkovTracker.OrgEndpoint);
    }

    [MemberNotNull(nameof(RatEyeEngine))]
    internal void SetupRatEye()
    {
        lock (_ratEyeSetupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Config.LogDebug = RatConfig.LogDebug;
            Config.Path.LogFile = "RatEyeLog.txt";
            Config.Path.TesseractLibSearchPath = AppDomain.CurrentDomain.BaseDirectory;
            Database database = RatStashDatabaseFromTarkovDev(out bool hasItems);
            RatEyeEngine replacement = new(GetRatEyeConfig(), database);
            RatEyeEngine? previous = null;
            lock (NameScanLock)
            {
                lock (IconScanLock)
                {
                    if (RatEyeEngine is not null)
                        previous = RatEyeEngine;
                    RatEyeEngine = replacement;
                    _ratEyeReady = hasItems;
                }
            }
            previous?.Dispose();
        }
    }

    private static RatEye.Config GetRatEyeConfig(bool highlighted = true)
    {
        return new Config()
        {
            PathConfig = new Config.Path()
            {
                TrainedData = RatConfig.Paths.TrainedData,
                StaticIcons = RatConfig.Paths.StaticIcon,
            },
            ProcessingConfig = new Config.Processing()
            {
                UseCache = RatConfig.IconScan.UseCachedIcons,
                Scale = Config.Processing.Resolution2Scale(RatConfig.ScreenWidth, RatConfig.ScreenHeight),
                Language = RatConfig.NameScan.Language,
                IconConfig = new Config.Processing.Icon()
                {
                    UseStaticIcons = true,
                    ScanMode = Config.Processing.Icon.ScanModes.TemplateMatching,
                    ScanRotatedIcons = RatConfig.IconScan.ScanRotatedIcons,
                },
                InventoryConfig = new Config.Processing.Inventory() { OptimizeHighlighted = highlighted },
            },
        };
    }

    private static Database RatStashDatabaseFromTarkovDev(out bool hasItems)
    {
        List<RatStash.Item> rsItems = [];
        if (!TarkovDevAPI.TryGetCachedItems(out TarkovItem[] items) || items.Length == 0)
        {
            hasItems = false;
            Logger.LogWarning("Items cache not ready; initializing RatEye with empty item database.");
            return RatStash.Database.FromItems(rsItems);
        }

        hasItems = true;
        foreach (TarkovItem i in items)
            rsItems.Add(ToRatStashItem(i));
        return RatStash.Database.FromItems(rsItems);
    }

    internal static RatStash.Item ToRatStashItem(TarkovItem item)
    {
        _ = Enum.TryParse(item.BackgroundColor, ignoreCase: true, out TaxonomyColor backgroundColor);
        return new RatStash.Item
        {
            Id = item.Id,
            Name = item.Name,
            ShortName = item.ShortName,
            Width = Math.Max(1, item.Width),
            Height = Math.Max(1, item.Height),
            BackgroundColor = backgroundColor,
        };
    }

    private void SeedInitialItem()
    {
        if (TarkovDevAPI.TryGetCachedItems(out TarkovItem[] items) && items.Length > 0)
        {
            ItemScans.Enqueue(new DefaultItemScan(items[Random.Shared.Next(items.Length)], isSeed: true));
            return;
        }

        ItemScans.Enqueue(new DefaultItemScan(CreatePlaceholderItem(), isSeed: true));
    }

    private void OnItemsCacheUpdated(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        try
        {
            Logger.LogInfo("Items cache updated; reinitializing RatEye...");
            SetupRatEye();

            if (
                TarkovDevAPI.TryGetCachedItems(out TarkovItem[] items)
                && items.Length > 0
                && ItemScans.LastOrDefault()?.Item.Id == "loading"
            )
            {
                ItemScans.Enqueue(new DefaultItemScan(items[Random.Shared.Next(items.Length)], isSeed: true));
            }
        }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to apply the updated item cache to RatEye.", exception);
        }
    }

    internal void RefreshItemsForGameMode()
    {
        if (!TarkovDevAPI.TryGetCachedItems(out TarkovItem[] items) || items.Length == 0)
            return;

        Dictionary<string, TarkovItem> byId = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (ItemScan scan in ItemScans)
        {
            if (byId.TryGetValue(scan.Item.Id, out TarkovItem? currentItem))
                scan.Item = currentItem;
        }
        OnPropertyChanged(nameof(ItemScans));
    }

    private static TarkovItem CreatePlaceholderItem()
    {
        return new TarkovItem
        {
            Id = "loading",
            Name = "Loading...",
            ShortName = "Loading...",
            Avg24HPrice = 0,
            Width = 1,
            Height = 1,
            Updated = DateTime.UtcNow.ToString("O"),
            IconLink = "https://assets.tarkov.dev/unknown-item-grid-image.jpg",
            BaseImageLink = "https://assets.tarkov.dev/unknown-item-grid-image.jpg",
            Link = "https://tarkov.dev/",
            WikiLink = "",
        };
    }

    /// <summary>
    /// Perform a name scan at the give position
    /// </summary>
    /// <param name="position">Position on the screen at which to perform the scan</param>
    internal void NameScan(Vector2 position)
    {
        if (!_scanThrottle.TryAcquire(Environment.TickCount64))
        {
            Logger.LogDebug("NameScan: skipped (scan cooldown active)");
            return;
        }

        Logger.LogDebug($"NameScan: ENTER pos={position} _ratEyeReady={_ratEyeReady} _disposed={_disposed}");
        RefreshGameDisplayForScan();
        Logger.LogDebug("NameScan: acquiring NameScanLock...");
        lock (NameScanLock)
        {
            Logger.LogDebug("NameScan: NameScanLock acquired");
            if (_disposed || !_ratEyeReady)
            {
                Logger.LogDebug($"NameScan: early return (_disposed={_disposed} _ratEyeReady={_ratEyeReady})");
                return;
            }

            Logger.LogDebug("Name scanning at: " + position);
            // Wait for game ui to update the click
            Thread.Sleep(50);

            // Get raw screenshot which includes the icon and text
            int markerScanSize = RatConfig.NameScan.MarkerScanSize;
            int sizeWidth = markerScanSize + RatConfig.NameScan.TextWidth;
            int sizeHeight = markerScanSize;

            position -= new Vector2(markerScanSize / 2, markerScanSize / 2);

            using Bitmap screenshot = GetScreenshot(position, new Size(sizeWidth, sizeHeight));

            // Scan the item
            RatEye.Processing.Inspection inspection = RatEyeEngine.NewInspection(screenshot);
            Item? detectedItem = inspection.Item;
            _scanDiagnostics.Record(
                "inspection",
                screenshot,
                position,
                new Vector2(markerScanSize / 2, markerScanSize / 2),
                [new(detectedItem?.Id, detectedItem?.Name, inspection.ItemConfidence)],
                inspection.Timings.Snapshot(),
                RatEyeEngine.Config,
                RatConfig.GameDisplayConfiguration,
                RatConfig.VersionDisplay
            );

            if (!inspection.ContainsMarker || detectedItem == null)
            {
                Logger.LogDebug(
                    $"NameScan: no marker or item (ContainsMarker={inspection.ContainsMarker} Item={inspection.Item != null})"
                );
                return;
            }

            float scale = RatEyeEngine.Config.ProcessingConfig.Scale;
            Bitmap marker = RatEyeEngine.Config.ProcessingConfig.InspectionConfig.Marker;
            float markerItemScale = RatEyeEngine.Config.ProcessingConfig.InspectionConfig.MarkerItemScale;
            Vector2 toolTipPosition = inspection.MarkerPosition;
            toolTipPosition += new Vector2(
                -(int)(marker.Width * markerItemScale * scale),
                (int)(marker.Height * markerItemScale * scale)
            );
            toolTipPosition += position;

            ItemNameScan tempNameScan = new(inspection, toolTipPosition, RatConfig.ToolTip.Duration);

            ItemScans.Enqueue(tempNameScan);
            Logger.LogDebug($"NameScan: enqueued scan for item={inspection.Item?.Name}");
        }
        Logger.LogDebug("NameScan: EXIT (lock released)");
    }

    /// <summary>
    /// Perform a name scan over the entire active screen
    /// </summary>
    internal void NameScanScreen(object? _ = null)
    {
        RefreshGameDisplayForScan();
        lock (NameScanLock)
        {
            if (_disposed || !_ratEyeReady)
                return;

            Logger.LogDebug("Name scanning screen");
            Rectangle bounds = RatConfig.GameDisplayConfiguration.CaptureBounds;
            bool usedFallbackBounds = bounds.Width <= 0 || bounds.Height <= 0;
            if (usedFallbackBounds)
            {
                Vector2 mousePosition = UserActivityHelper.GetMousePosition();
                Screen? screen =
                    Screen.AllScreens.FirstOrDefault(candidate => candidate.Bounds.Contains(mousePosition))
                    ?? Screen.PrimaryScreen
                    ?? Screen.AllScreens.FirstOrDefault();
                if (screen is null)
                    return;
                bounds = screen.Bounds;
            }
            double displayScale = RatConfig.GameDisplayConfiguration.DisplayScale;
            if (usedFallbackBounds)
                displayScale = WindowsGameDisplayService.GetDpiScale(bounds).Scale;
            GameDisplayConfiguration diagnosticDisplay = RatConfig.GameDisplayConfiguration with
            {
                CaptureBounds = bounds,
                DisplayScale = displayScale,
            };

            Vector2 position = new(bounds.X, bounds.Y);
            using Bitmap screenshot = GetScreenshot(position, bounds.Size);

            // Scan the item
            RatEye.Processing.MultiInspection multiInspection = RatEyeEngine.NewMultiInspection(screenshot);
            List<RatEye.Processing.Inspection> inspections = multiInspection.Inspections;
            List<ScanDiagnosticDetection> detections = [];
            Dictionary<string, double> stageMilliseconds = new(multiInspection.Timings.Snapshot());
            for (int index = 0; index < inspections.Count; index++)
            {
                RatEye.Processing.Inspection inspection = inspections[index];
                Item? detectedItem = inspection.Item;
                detections.Add(new(detectedItem?.Id, detectedItem?.Name, inspection.ItemConfidence));
                AddTimings(stageMilliseconds, inspection.Timings.Snapshot(), $"inspection[{index}].");
            }
            _scanDiagnostics.Record(
                "multi-inspection",
                screenshot,
                position,
                null,
                detections,
                stageMilliseconds,
                RatEyeEngine.Config,
                diagnosticDisplay,
                RatConfig.VersionDisplay
            );

            if (inspections.Count == 0)
                return;

            foreach (RatEye.Processing.Inspection? inspection in inspections)
            {
                if (inspection.Item is null)
                    continue;

                float scale = RatEyeEngine.Config.ProcessingConfig.Scale;
                Vector2 toolTipPosition = inspection.MarkerPosition;
                toolTipPosition += position;
                Bitmap marker = RatEyeEngine.Config.ProcessingConfig.InspectionConfig.Marker;
                float markerItemScale = RatEyeEngine.Config.ProcessingConfig.InspectionConfig.MarkerItemScale;
                toolTipPosition += new Vector2(0, (int)(marker.Height * markerItemScale * scale));

                ItemNameScan tempNameScan = new(inspection, toolTipPosition, RatConfig.ToolTip.Duration);

                ItemScans.Enqueue(tempNameScan);
            }
        }
    }

    /// <summary>
    /// Perform a icon scan at the given position
    /// </summary>
    /// <param name="position">Position on the screen at which to perform the scan</param>
    /// <returns><see langword="true"/> if a item was scanned successfully</returns>
    internal void IconScan(Vector2 position)
    {
        if (!_scanThrottle.TryAcquire(Environment.TickCount64))
        {
            Logger.LogDebug("IconScan: skipped (scan cooldown active)");
            return;
        }

        Logger.LogDebug($"IconScan: ENTER pos={position} _ratEyeReady={_ratEyeReady} _disposed={_disposed}");
        RefreshGameDisplayForScan();
        Logger.LogDebug("IconScan: acquiring IconScanLock...");
        lock (IconScanLock)
        {
            Logger.LogDebug("IconScan: IconScanLock acquired");
            if (_disposed || !_ratEyeReady)
            {
                Logger.LogDebug($"IconScan: early return (_disposed={_disposed} _ratEyeReady={_ratEyeReady})");
                return;
            }

            Logger.LogDebug("Icon scanning at: " + position);
            int x = position.X - RatConfig.IconScan.ScanWidth / 2;
            int y = position.Y - RatConfig.IconScan.ScanHeight / 2;

            Vector2 screenshotPosition = new(x, y);
            Size size = new(RatConfig.IconScan.ScanWidth, RatConfig.IconScan.ScanHeight);
            using Bitmap screenshot = GetScreenshot(screenshotPosition, size);

            // Scan the item
            using RatEye.Processing.Inventory inventory = RatEyeEngine.NewInventory(screenshot);
            RatEye.Processing.Icon? icon = inventory.LocateIcon();
            Item? detectedItem = icon?.Item;
            Dictionary<string, double> stageMilliseconds = new(inventory.Timings.Snapshot());
            if (icon is not null)
                AddTimings(stageMilliseconds, icon.Timings.Snapshot());
            _scanDiagnostics.Record(
                "inventory",
                screenshot,
                screenshotPosition,
                new Vector2(size.Width / 2, size.Height / 2),
                icon is null ? [] : [new(detectedItem?.Id, detectedItem?.Name, icon.DetectionConfidence)],
                stageMilliseconds,
                RatEyeEngine.Config,
                RatConfig.GameDisplayConfiguration,
                RatConfig.VersionDisplay
            );

            if (detectedItem == null || icon!.DetectionConfidence < RatConfig.IconScan.MinAcceptConfidence)
            {
                if (icon?.Item != null)
                {
                    Logger.LogDebug(
                        $"Icon scan rejected: best match {icon.Item.Name} at "
                            + $"{icon.DetectionConfidence:F3} is below the acceptance threshold. "
                            + "Equipment-slot panels render items scaled to fit fixed boxes and "
                            + "produce low-confidence garbage matches."
                    );
                }
                else
                {
                    Logger.LogDebug(
                        $"IconScan: no icon found (icon={icon != null} confidence={icon?.DetectionConfidence:F3})"
                    );
                }
                return;
            }

            Vector2 toolTipPosition = position;
            toolTipPosition += icon.Position + icon.ItemPosition;
            toolTipPosition -= new Vector2(RatConfig.IconScan.ScanWidth, RatConfig.IconScan.ScanHeight) / 2;

            ItemIconScan tempIconScan = new(icon, toolTipPosition, RatConfig.ToolTip.Duration);

            ItemScans.Enqueue(tempIconScan);
            Logger.LogDebug(
                $"IconScan: enqueued scan for item={icon.Item?.Name} confidence={icon.DetectionConfidence:F3}"
            );
        }
        Logger.LogDebug("IconScan: EXIT (lock released)");
    }

    internal ScanDiagnosticExportResult ExportLastScanDiagnostics() => _scanDiagnostics.Export();

    private static void AddTimings(
        IDictionary<string, double> destination,
        IReadOnlyDictionary<string, double> source,
        string prefix = ""
    )
    {
        foreach ((string stage, double milliseconds) in source)
            destination[prefix + stage] = milliseconds;
    }

    // Returns the ruff screenshot
    private static Bitmap GetScreenshot(Vector2 vector2, Size size)
    {
        Bitmap bmp = new(size.Width, size.Height, PixelFormat.Format24bppRgb);

        try
        {
            using Graphics gfx = Graphics.FromImage(bmp);
            gfx.CopyFromScreen(vector2.X, vector2.Y, 0, 0, size, CopyPixelOperation.SourceCopy);
        }
        catch (Exception e)
        {
            Logger.LogWarning("Unable to capture screenshot", e);
        }

        return bmp;
    }

    private void RefreshGameDisplayForScan()
    {
        bool viewportChanged = RatConfig.RefreshGameDisplayConfiguration();
        Logger.LogDebug($"RefreshGameDisplayForScan: viewportChanged={viewportChanged}");
        if (viewportChanged)
        {
            Logger.LogDebug("RefreshGameDisplayForScan: calling SetupRatEye (may block on locks)...");
            SetupRatEye();
            Logger.LogDebug("RefreshGameDisplayForScan: SetupRatEye complete");
        }
    }

    private void RefreshTarkovTrackerDB(object? o = null) => _ = RefreshTarkovTrackerDBAsync();

    private async Task RefreshTarkovTrackerDBAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _trackerRefreshInProgress, 1) != 0)
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(TarkovTrackerDB.Token))
                return;

            Logger.LogInfo("Refreshing TarkovTracker DB...");
            await TarkovTrackerDB.RefreshProgressAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            OnPropertyChanged(nameof(TarkovTrackerDB));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to refresh TarkovTracker data.", exception);
        }
        finally
        {
            Interlocked.Exchange(ref _trackerRefreshInProgress, 0);
            lock (_tarkovTrackerTimerLock)
            {
                if (!_disposed)
                    _tarkovTrackerDBRefreshTimer?.Change(
                        RatConfig.Tracking.TarkovTracker.RefreshTime,
                        Timeout.Infinite
                    );
            }
        }
    }

    private void OnItemScansChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ItemScans));
        ScheduleNextOverlayRefresh();
    }

    private void ScheduleNextOverlayRefresh()
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        long? nextExpiration = ItemScans.GetNextExpiration(now);
        int dueTime = nextExpiration is long expiration
            ? (int)Math.Clamp(expiration - now, 1, int.MaxValue)
            : Timeout.Infinite;

        lock (_scanRefreshTimerLock)
        {
            _scanRefreshTimer?.Change(dueTime, Timeout.Infinite);
        }
    }

    private void RefreshOverlay(object? o = null)
    {
        ItemScans.PruneExpired(DateTimeOffset.Now.ToUnixTimeMilliseconds());
        OnPropertyChanged(nameof(ItemScans));
        ScheduleNextOverlayRefresh();
    }

    private void OnPropertyChanged(string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        lock (_ratEyeSetupLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            TarkovDevAPI.ItemsCacheUpdated -= OnItemsCacheUpdated;
            _lifetimeCancellation.Cancel();

            lock (_scanRefreshTimerLock)
            {
                _scanRefreshTimer?.Dispose();
                _scanRefreshTimer = null;
            }
            lock (_tarkovTrackerTimerLock)
            {
                _tarkovTrackerDBRefreshTimer?.Dispose();
                _tarkovTrackerDBRefreshTimer = null;
            }

            ItemScans.Changed -= OnItemScansChanged;
            HotkeyManager.Dispose();
            TarkovTrackerDB.Dispose();
            lock (NameScanLock)
            {
                lock (IconScanLock)
                {
                    RatEyeEngine?.Dispose();
                    _ratEyeReady = false;
                }
            }
            _scanDiagnostics.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }
}
