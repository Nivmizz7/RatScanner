using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using RatEye;
using RatScanner.Scan;
using RatStash;
using MessageBox = System.Windows.MessageBox;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Size = System.Drawing.Size;
using TarkovItem = RatScanner.TarkovDev.GraphQL.Item;
using Timer = System.Threading.Timer;

namespace RatScanner;

public class RatScannerMain : INotifyPropertyChanged
{
    private static RatScannerMain _instance = null!;
    internal static RatScannerMain Instance => _instance ??= new RatScannerMain();

    internal readonly HotkeyManager HotkeyManager;

    private Timer? _tarkovTrackerDBRefreshTimer;
    private Timer? _scanRefreshTimer;
    private readonly object _scanRefreshTimerLock = new();

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

    public TarkovTrackerDB TarkovTrackerDB;

    internal RatEyeEngine RatEyeEngine;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ItemQueue ItemScans = new();

    public RatScannerMain()
    {
        _instance = this;
        _scanRefreshTimer = new Timer(RefreshOverlay, null, Timeout.Infinite, Timeout.Infinite);
        ItemScans.Changed += OnItemScansChanged;

        // Remove old log
        Logger.Clear();

        Logger.LogInfo("----- RatScanner " + RatConfig.Version + " -----");
        _ = CheckForUpdatesAsync();

        Logger.LogInfo(
            $"Screen Info: {RatConfig.ScreenWidth}x{RatConfig.ScreenHeight} at {RatConfig.ScreenScale * 100}%"
        );

        Logger.LogInfo("Initializing TarkovDev API...");

        // Try to load from offline cache first for faster startup
        if (TarkovDevAPI.TryInitializeCacheFromOffline())
        {
            if (TarkovDevAPI.AnyCacheExpired())
            {
                Logger.LogInfo("Offline cache loaded but stale, refreshing in background...");
                _ = TarkovDevAPI.InitializeCache();
            }
            else
            {
                Logger.LogInfo("Offline cache loaded and fresh, skipping background refresh.");
            }
        }
        else
        {
            // No offline cache available, wait for network requests
            Logger.LogWarning("No complete offline cache available, fetching from network...");
            _ = TarkovDevAPI.InitializeCache();
        }

        SeedInitialItem();

        Logger.LogInfo("Initializing tarkov tracker database");
        TarkovTrackerDB = new TarkovTrackerDB();

        Logger.LogInfo("Initializing hotkey manager...");
        HotkeyManager = new HotkeyManager();
        HotkeyManager.UnregisterHotkeys();

        Logger.LogInfo("UI Ready!");

        Logger.LogInfo("Initializing RatEye...");
        SetupRatEye();

        new Thread(() =>
        {
            Thread.Sleep(1000);
            Logger.LogInfo("Loading TarkovTracker data...");
            if (RatConfig.Tracking.TarkovTracker.Enable)
            {
                TarkovTrackerDB.Token = RatConfig.Tracking.TarkovTracker.Token;
                Logger.LogInfo("Loading TarkovTracker...");
                if (!TarkovTrackerDB.Init())
                {
                    Logger.ShowWarning("TarkovTracker API Token invalid!\n\nPlease provide a new token.");
                    RatConfig.Tracking.TarkovTracker.Token = "";
                    RatConfig.SaveConfig();
                }
            }

            Logger.LogInfo("Setting up timer routines...");
            _tarkovTrackerDBRefreshTimer = new Timer(
                RefreshTarkovTrackerDB,
                null,
                RatConfig.Tracking.TarkovTracker.RefreshTime,
                Timeout.Infinite
            );
            Logger.LogInfo("Enabling hotkeys...");
            HotkeyManager.RegisterHotkeys();

            Logger.LogInfo("Ready!");
        }).Start();
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
        message += "You are using: " + RatConfig.Version + "\n\n";
        message += "Do you want to install it now?";
        MessageBoxResult result = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(message, "RatScanner update", MessageBoxButton.YesNo, MessageBoxImage.Information)
        );
        if (result != MessageBoxResult.Yes)
            return;

        bool started = await Task.Run(() => GitHubUpdateService.TryApplyUpdate(release)).ConfigureAwait(false);
        if (started)
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                System.Windows.Application.Current.Shutdown()
            );
    }

    [MemberNotNull(nameof(RatEyeEngine))]
    internal void SetupRatEye()
    {
        Config.LogDebug = RatConfig.LogDebug;
        Config.Path.LogFile = "RatEyeLog.txt";
        Config.Path.TesseractLibSearchPath = AppDomain.CurrentDomain.BaseDirectory;
        RatEyeEngine replacement = new(GetRatEyeConfig(), RatStashDatabaseFromTarkovDev());
        RatEyeEngine? previous = null;
        lock (NameScanLock)
        {
            lock (IconScanLock)
            {
                if (RatEyeEngine is not null)
                    previous = RatEyeEngine;
                RatEyeEngine = replacement;
            }
        }
        previous?.Dispose();
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
                },
                InventoryConfig = new Config.Processing.Inventory() { OptimizeHighlighted = highlighted },
            },
        };
    }

    private static Database RatStashDatabaseFromTarkovDev()
    {
        List<Item> rsItems = [];
        if (!TarkovDevAPI.TryGetCachedItems(out TarkovDev.GraphQL.Item[] items) || items.Length == 0)
        {
            Logger.LogWarning("Items cache not ready; initializing RatEye with empty item database.");
            return RatStash.Database.FromItems(rsItems);
        }

        foreach (TarkovDev.GraphQL.Item i in items)
            rsItems.Add(ToRatStashItem(i));
        return RatStash.Database.FromItems(rsItems);
    }

    internal static RatStash.Item ToRatStashItem(TarkovDev.GraphQL.Item item)
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
        _ = Task.Run(() => WaitForItemsAndSeedAsync(TimeSpan.FromSeconds(30)));
    }

    private async Task WaitForItemsAndSeedAsync(TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (TarkovDevAPI.TryGetCachedItems(out TarkovItem[] items) && items.Length > 0)
            {
                ItemScans.Enqueue(new DefaultItemScan(items[Random.Shared.Next(items.Length)], isSeed: true));
                Logger.LogInfo("Items cache ready; reinitializing RatEye...");
                SetupRatEye();
                return;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        Logger.LogWarning("Timed out waiting for items cache to populate.");
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
        lock (NameScanLock)
        {
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

            if (!inspection.ContainsMarker || inspection.Item == null)
                return;

            float scale = RatEyeEngine.Config.ProcessingConfig.Scale;
            Bitmap marker = RatEyeEngine.Config.ProcessingConfig.InspectionConfig.Marker;
            Vector2 toolTipPosition = inspection.MarkerPosition;
            toolTipPosition += new Vector2(-(int)(marker.Width * scale), (int)(marker.Height * scale));
            toolTipPosition += position;

            ItemNameScan tempNameScan = new(inspection, toolTipPosition, RatConfig.ToolTip.Duration);

            ItemScans.Enqueue(tempNameScan);
        }
    }

    /// <summary>
    /// Perform a name scan over the entire active screen
    /// </summary>
    internal void NameScanScreen(object? _ = null)
    {
        lock (NameScanLock)
        {
            Logger.LogDebug("Name scanning screen");
            Vector2 mousePosition = UserActivityHelper.GetMousePosition();
            Rectangle bounds = Screen.AllScreens.First(screen => screen.Bounds.Contains(mousePosition)).Bounds;

            Vector2 position = new(bounds.X, bounds.Y);
            using Bitmap screenshot = GetScreenshot(position, bounds.Size);

            // Scan the item
            RatEye.Processing.MultiInspection multiInspection = RatEyeEngine.NewMultiInspection(screenshot);

            if (multiInspection.Inspections.Count == 0)
                return;

            foreach (RatEye.Processing.Inspection? inspection in multiInspection.Inspections)
            {
                float scale = RatEyeEngine.Config.ProcessingConfig.Scale;
                Vector2 toolTipPosition = inspection.MarkerPosition;
                toolTipPosition += position;
                Bitmap marker = RatEyeEngine.Config.ProcessingConfig.InspectionConfig.Marker;
                toolTipPosition += new Vector2(0, (int)(marker.Height * scale));

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
        lock (IconScanLock)
        {
            Logger.LogDebug("Icon scanning at: " + position);
            int x = position.X - RatConfig.IconScan.ScanWidth / 2;
            int y = position.Y - RatConfig.IconScan.ScanHeight / 2;

            Vector2 screenshotPosition = new(x, y);
            Size size = new(RatConfig.IconScan.ScanWidth, RatConfig.IconScan.ScanHeight);
            using Bitmap screenshot = GetScreenshot(screenshotPosition, size);

            // Scan the item
            using RatEye.Processing.Inventory inventory = RatEyeEngine.NewInventory(screenshot);
            RatEye.Processing.Icon? icon = inventory.LocateIcon();

            if (icon?.DetectionConfidence <= 0 || icon?.Item == null)
                return;

            Vector2 toolTipPosition = position;
            toolTipPosition += icon.Position + icon.ItemPosition;
            toolTipPosition -= new Vector2(RatConfig.IconScan.ScanWidth, RatConfig.IconScan.ScanHeight) / 2;

            ItemIconScan tempIconScan = new(icon, toolTipPosition, RatConfig.ToolTip.Duration);

            ItemScans.Enqueue(tempIconScan);
        }
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

    private void RefreshTarkovTrackerDB(object? o = null)
    {
        Logger.LogInfo("Refreshing TarkovTracker DB...");
        TarkovTrackerDB.Init();
        OnPropertyChanged(nameof(TarkovTrackerDB));
        _tarkovTrackerDBRefreshTimer?.Change(RatConfig.Tracking.TarkovTracker.RefreshTime, Timeout.Infinite);
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

    protected virtual void OnPropertyChanged(string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
