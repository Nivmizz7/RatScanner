using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using RatScanner.Display;
using RatScanner.TarkovDev;
using RatStash;
using Key = System.Windows.Input.Key;

namespace RatScanner;

internal static class RatConfig
{
    private static readonly object GameDisplayLock = new();
    private static readonly WindowsGameDisplayService GameDisplayService = new();
    private static readonly TimeSpan GameDisplayRefreshInterval = TimeSpan.FromSeconds(5);
    private static DateTimeOffset _lastGameDisplayRefresh = DateTimeOffset.MinValue;

    /// <summary>
    /// Numeric / informational product version from assembly metadata (csproj <c>Version</c>).
    /// TarkovTracker Edition uses its own major line (4.x+) — never matches upstream 3.x tags.
    /// </summary>
    public static string Version =>
        Process.GetCurrentProcess().MainModule?.FileVersionInfo.ProductVersion?.Trim() ?? "Unknown";

    /// <summary>Compact display form, e.g. <c>v4.0.0</c>.</summary>
    public static string VersionDisplay => Version.StartsWith('v') || Version.StartsWith('V') ? Version : "v" + Version;

    /// <summary>Full branded label for logs/dialogs, e.g. <c>RatScanner TarkovTracker Edition v4.0.0</c>.</summary>
    public static string FullVersionLabel => $"{Constants.Branding.ProductName} {VersionDisplay}";

    public const string SINGLE_INSTANCE_GUID = "{a057bb64-c126-4ef4-a4ed-3037c2e7bc89}";

    // Paths
    internal static class Paths
    {
        internal static string Base = AppDomain.CurrentDomain.BaseDirectory;
        internal static string Data = Path.Combine(Base, "Data");
        internal static string StaticIcon = Path.Combine(Data, "icons");
        internal static string Locales = Path.Combine(Data, "locales");

        private const string EftTempDir = "Battlestate Games\\EscapeFromTarkov\\";
        private static readonly string EftTemp = Path.Combine(Path.GetTempPath(), EftTempDir);
        private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "RatScanner");
        internal static readonly string CacheDir = Path.Combine(TempDir, "Cache");
        internal static string DynamicIcon = Path.Combine(EftTemp, "Icon Cache");
        internal static string StaticCorrelation = Path.Combine(StaticIcon, "correlation.json");
        internal static string DynamicCorrelation = Path.Combine(DynamicIcon, "index.json");
        internal static string ItemData = Path.Combine(Data, "items.json");
        internal static string TrainedData = Path.Combine(Data, "traineddata");
        internal static string UnknownIcon = Path.Combine(Data, "unknown.png");
        internal static string ConfigFile = Path.Combine(Base, "config.cfg");
        internal static string Debug = Path.Combine(Base, "Debug");
        internal static string LogFile = Path.Combine(Base, "Log.txt");

        internal static string I18nDir => Path.Combine(Base, "i18n");
    }

    // Name Scan options
    internal static class NameScan
    {
        internal static bool Enable = true;
        internal static bool EnableAuto = false;
        internal static Language Language = Language.English;
        internal static float ConfWarnThreshold = 0.85f;
        internal static int MarkerScanSize => (int)(50 * GameScale);
        internal static int TextWidth => (int)(600 * GameScale);
    }

    // Icon Scan options
    internal static class IconScan
    {
        internal static bool Enable = true;
        internal static float ConfWarnThreshold = 0.8f;
        internal static bool ScanRotatedIcons = true;
        internal static int ScanWidth => (int)(GameScale * 896);
        internal static int ScanHeight => (int)(GameScale * 896);
        internal static Hotkey Hotkey = new([Key.LeftShift], [MouseButton.Left]);
        internal static bool UseCachedIcons = true;
    }

    // ToolTip options
    internal static class ToolTip
    {
        internal static string DigitGroupingSymbol = ",";
        internal static int Duration = 1500;
    }

    // UI options
    internal static class UserInterface
    {
        internal static UiLanguage Language = UiLanguage.English;
    }

    // Minimal UI
    internal static class MinimalUi
    {
        internal static bool ShowName = true;
        internal static bool ShowAvgDayPrice = true;
        internal static bool ShowPricePerSlot = true;
        internal static bool ShowTraderPrice = true;
        internal static bool ShowUpdated = false;
        internal static bool ShowKappa = false;
        internal static bool ShowQuestHideoutTracker = true;
        internal static bool ShowQuestHideoutTeamTracker = false;
        internal static int Opacity = 0;
    }

    // Progress Tracking options
    internal static class Tracking
    {
        internal static bool ShowNonFIRNeeds = true;

        internal static bool ShowKappaNeeds = false;

        internal static class TarkovTracker
        {
            internal static TarkovTrackerBackend Backend = TarkovTrackerBackend.TarkovTrackerORG;
            internal static string Endpoint =>
                Backend == TarkovTrackerBackend.TarkovTrackerIO
                    ? "https://tarkovtracker.io/api/v2"
                    : "https://api.tarkovtracker.org";
            internal static bool Enable => Token.Length > 0;

            internal static string Token = "";
            internal static bool ShowTeam = true;
            internal static int RefreshTime = 5 * 60 * 1000; // 5 minutes
        }
    }

    public enum TarkovTrackerBackend
    {
        TarkovTrackerIO,
        TarkovTrackerORG,
    }

    // Overlay options
    internal static class Overlay
    {
        internal static class Search
        {
            internal static bool Enable = true;
            internal static bool BlurBehind = true;
            internal static Hotkey Hotkey = new([Key.N, Key.M]);
        }
    }

    // Other
#if DEBUG
    internal static bool LogDebug
    {
        get => true;
        set { }
    }
#else
    internal static bool LogDebug = false;
#endif
    internal static GameMode GameMode = GameMode.Regular;
    internal static bool MinimizeToTray = false;
    internal static bool AlwaysOnTop = true;
    internal static int SuperShortTTL = 30; // 30 seconds
    internal static int ShortTTL = 60 * 5; // 5 minutes
    internal static int MediumTTL = 60 * 60 * 1; // 1 hour
    internal static int LongTTL = 60 * 60 * 12; // 12 hours
    private static int ConfigVersion => 2;

    internal static int ScreenWidth = 1920;
    internal static int ScreenHeight = 1080;
    internal static float ScreenScale = 1f;
    internal static string PreferredGameDisplayId = "";
    internal static string PreferredGameDisplayDeviceName = "";
    internal static int PreferredGameDisplayBoundsX;
    internal static int PreferredGameDisplayBoundsY;
    internal static int PreferredGameDisplayBoundsWidth;
    internal static int PreferredGameDisplayBoundsHeight;
    internal static bool UseCustomGameResolution;
    internal static int CustomGameWidth = 1920;
    internal static int CustomGameHeight = 1080;
    internal static bool UseCustomDisplayScale;
    internal static float CustomDisplayScale = 1f;
    internal static GameDisplayConfiguration GameDisplayConfiguration { get; private set; } =
        GameDisplayConfiguration.Empty;
    internal static event Action<GameDisplayConfiguration>? GameDisplayConfigurationChanged;
    internal static int LastWindowPositionX = int.MinValue;
    internal static int LastWindowPositionY = int.MinValue;
    internal static WindowMode LastWindowMode = WindowMode.Normal;

    internal static float GameScale => RatScannerMain.Instance.RatEyeEngine.Config.ProcessingConfig.Scale;

    internal readonly record struct ConfigLoadPlan(
        bool FileExists,
        bool IsSupported,
        bool ShouldSave,
        int ExistingVersion,
        string? BackupPath
    );

    internal static ConfigLoadPlan PrepareConfigForLoad(string configPath)
    {
        if (!File.Exists(configPath))
            return new ConfigLoadPlan(false, true, true, -1, null);

        SimpleConfig config = new(configPath, "Other");
        int existingVersion = config.ReadInt(nameof(ConfigVersion), -1);
        if (existingVersion == ConfigVersion)
            return new ConfigLoadPlan(true, true, false, existingVersion, null);

        string backupPath = CreateConfigBackup(configPath, existingVersion);
        return new ConfigLoadPlan(true, false, true, existingVersion, backupPath);
    }

    private static string CreateConfigBackup(string configPath, int existingVersion)
    {
        string baseBackupPath = $"{configPath}.v{existingVersion}.bak";
        string backupPath = baseBackupPath;
        for (int suffix = 1; File.Exists(backupPath); suffix++)
            backupPath = $"{baseBackupPath}.{suffix}";

        File.Copy(configPath, backupPath, overwrite: false);
        return backupPath;
    }

    internal static void LoadConfig()
    {
        ConfigLoadPlan loadPlan;
        try
        {
            loadPlan = PrepareConfigForLoad(Paths.ConfigFile);
        }
        catch (Exception exception)
        {
            // Never rewrite an unsupported config unless its original bytes were preserved first.
            Logger.LogWarning(
                "Unable to back up the existing configuration; automatic migration was skipped.",
                exception
            );
            loadPlan = new ConfigLoadPlan(File.Exists(Paths.ConfigFile), false, false, -1, null);
        }

        bool configFileExists = loadPlan.FileExists;
        bool shouldSaveConfig = loadPlan.ShouldSave;
        if (configFileExists && !loadPlan.IsSupported)
        {
            Logger.LogWarning($"Config version ({loadPlan.ExistingVersion}) is not supported.");
            string message = "Old config version detected.\n\n";
            if (loadPlan.BackupPath is not null)
            {
                message += $"Your original settings were backed up to:\n{loadPlan.BackupPath}\n\n";
                message += "RatScanner will migrate every setting it can read and keep safe defaults for the rest.";
            }
            else
            {
                message +=
                    "RatScanner could not create a backup, so it will use readable settings for this session "
                    + "without rewriting the file. Back up config.cfg manually before saving new settings.";
            }
            Logger.ShowMessage(message);
        }

        SimpleConfig config = new(Paths.ConfigFile) { Section = nameof(NameScan) };

        NameScan.Enable = config.ReadBool(nameof(NameScan.Enable), NameScan.Enable);
        NameScan.EnableAuto = config.ReadBool(nameof(NameScan.EnableAuto), NameScan.EnableAuto);
        NameScan.Language = (Language)config.ReadInt(nameof(NameScan.Language), (int)NameScan.Language);

        config.Section = nameof(IconScan);
        IconScan.Enable = config.ReadBool(nameof(IconScan.Enable), IconScan.Enable);
        IconScan.ScanRotatedIcons = config.ReadBool(nameof(IconScan.ScanRotatedIcons), IconScan.ScanRotatedIcons);
        IconScan.Hotkey = config.ReadHotkey(nameof(IconScan.Hotkey), IconScan.Hotkey);
        IconScan.UseCachedIcons = config.ReadBool(nameof(IconScan.UseCachedIcons), IconScan.UseCachedIcons);

        config.Section = nameof(ToolTip);
        ToolTip.Duration = config.ReadInt(nameof(ToolTip.Duration), ToolTip.Duration);
        ToolTip.DigitGroupingSymbol = config.ReadString(
            nameof(ToolTip.DigitGroupingSymbol),
            ToolTip.DigitGroupingSymbol
        );

        config.Section = nameof(UserInterface);
        UserInterface.Language = (UiLanguage)
            config.ReadInt(nameof(UserInterface.Language), (int)UserInterface.Language);

        config.Section = nameof(MinimalUi);
        MinimalUi.ShowName = config.ReadBool(nameof(MinimalUi.ShowName), MinimalUi.ShowName);
        MinimalUi.ShowAvgDayPrice = config.ReadBool(nameof(MinimalUi.ShowAvgDayPrice), MinimalUi.ShowAvgDayPrice);
        MinimalUi.ShowPricePerSlot = config.ReadBool(nameof(MinimalUi.ShowPricePerSlot), MinimalUi.ShowPricePerSlot);
        MinimalUi.ShowTraderPrice = config.ReadBool(nameof(MinimalUi.ShowTraderPrice), MinimalUi.ShowTraderPrice);
        MinimalUi.ShowUpdated = config.ReadBool(nameof(MinimalUi.ShowUpdated), MinimalUi.ShowUpdated);
        MinimalUi.ShowKappa = config.ReadBool(nameof(MinimalUi.ShowKappa), MinimalUi.ShowKappa);
        MinimalUi.ShowQuestHideoutTracker = config.ReadBool(
            nameof(MinimalUi.ShowQuestHideoutTracker),
            MinimalUi.ShowQuestHideoutTracker
        );
        MinimalUi.ShowQuestHideoutTeamTracker = config.ReadBool(
            nameof(MinimalUi.ShowQuestHideoutTeamTracker),
            MinimalUi.ShowQuestHideoutTeamTracker
        );
        MinimalUi.Opacity = config.ReadInt(nameof(MinimalUi.Opacity), MinimalUi.Opacity);

        config.Section = nameof(Tracking);
        Tracking.ShowNonFIRNeeds = config.ReadBool(nameof(Tracking.ShowNonFIRNeeds), Tracking.ShowNonFIRNeeds);
        Tracking.ShowKappaNeeds = config.ReadBool(nameof(Tracking.ShowKappaNeeds), Tracking.ShowKappaNeeds);

        config.Section = nameof(Tracking.TarkovTracker);
        Tracking.TarkovTracker.Backend = (TarkovTrackerBackend)
            config.ReadInt(nameof(Tracking.TarkovTracker.Backend), (int)Tracking.TarkovTracker.Backend);
        Tracking.TarkovTracker.Token = config.ReadSecureString(
            nameof(Tracking.TarkovTracker.Token),
            Tracking.TarkovTracker.Token
        );
        Tracking.TarkovTracker.ShowTeam = config.ReadBool(
            nameof(Tracking.TarkovTracker.ShowTeam),
            Tracking.TarkovTracker.ShowTeam
        );

        config.Section = nameof(Overlay);

        config.Section = nameof(Overlay.Search);
        Overlay.Search.Enable = config.ReadBool(nameof(Overlay.Search.Enable), Overlay.Search.Enable);
        Overlay.Search.BlurBehind = config.ReadBool(nameof(Overlay.Search.BlurBehind), Overlay.Search.BlurBehind);
        Overlay.Search.Hotkey = config.ReadHotkey(nameof(Overlay.Search.Hotkey), Overlay.Search.Hotkey);

        config.Section = "Other";
        ScreenWidth = config.ReadInt(nameof(ScreenWidth), ScreenWidth);
        ScreenHeight = config.ReadInt(nameof(ScreenHeight), ScreenHeight);
        ScreenScale = config.ReadFloat(nameof(ScreenScale), ScreenScale);

        GameMode = (GameMode)config.ReadInt(nameof(GameMode), (int)GameMode);
        MinimizeToTray = config.ReadBool(nameof(MinimizeToTray), MinimizeToTray);
        AlwaysOnTop = config.ReadBool(nameof(AlwaysOnTop), AlwaysOnTop);
        LogDebug = config.ReadBool(nameof(LogDebug), LogDebug);

        LastWindowPositionX = config.ReadInt(nameof(LastWindowPositionX), LastWindowPositionX);
        LastWindowPositionY = config.ReadInt(nameof(LastWindowPositionY), LastWindowPositionY);
        LastWindowMode = (WindowMode)config.ReadInt(nameof(LastWindowMode), (int)LastWindowMode);

        if (GameDisplayPreferencesStore.TryRead(config, ScreenWidth, ScreenHeight, ScreenScale, out var preferences))
        {
            SetGameDisplayPreferences(preferences);
        }
        else
        {
            GameDisplayConfiguration automaticConfiguration = GameDisplayService.Detect(
                GameDisplayPreferences.Automatic
            );
            GameDisplayPreferences migratedPreferences = configFileExists
                ? GameDisplayMigration.FromLegacy(ScreenWidth, ScreenHeight, ScreenScale, automaticConfiguration)
                : CreateFirstRunPreferences(automaticConfiguration);
            SetGameDisplayPreferences(migratedPreferences);
            shouldSaveConfig = true;
        }

        RefreshGameDisplayConfiguration(force: true);
        if (shouldSaveConfig)
            SaveConfig();
    }

    internal static void SaveConfig()
    {
        string temporaryPath = Paths.ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.ConfigFile) ?? Paths.Base);
            if (File.Exists(Paths.ConfigFile))
                File.Copy(Paths.ConfigFile, temporaryPath);

            SimpleConfig config = new(temporaryPath) { Section = nameof(NameScan) };

            config.WriteBool(nameof(NameScan.Enable), NameScan.Enable);
            config.WriteBool(nameof(NameScan.EnableAuto), NameScan.EnableAuto);
            config.WriteInt(nameof(NameScan.Language), (int)NameScan.Language);

            config.Section = nameof(IconScan);
            config.WriteBool(nameof(IconScan.Enable), IconScan.Enable);
            config.WriteBool(nameof(IconScan.ScanRotatedIcons), IconScan.ScanRotatedIcons);
            config.WriteHotkey(nameof(IconScan.Hotkey), IconScan.Hotkey);
            config.WriteBool(nameof(IconScan.UseCachedIcons), IconScan.UseCachedIcons);

            config.Section = nameof(ToolTip);
            config.WriteInt(nameof(ToolTip.Duration), ToolTip.Duration);
            config.WriteString(nameof(ToolTip.DigitGroupingSymbol), ToolTip.DigitGroupingSymbol);

            config.Section = nameof(UserInterface);
            config.WriteInt(nameof(UserInterface.Language), (int)UserInterface.Language);

            config.Section = nameof(MinimalUi);
            config.WriteBool(nameof(MinimalUi.ShowName), MinimalUi.ShowName);
            config.WriteBool(nameof(MinimalUi.ShowAvgDayPrice), MinimalUi.ShowAvgDayPrice);
            config.WriteBool(nameof(MinimalUi.ShowPricePerSlot), MinimalUi.ShowPricePerSlot);
            config.WriteBool(nameof(MinimalUi.ShowTraderPrice), MinimalUi.ShowTraderPrice);
            config.WriteBool(nameof(MinimalUi.ShowUpdated), MinimalUi.ShowUpdated);
            config.WriteBool(nameof(MinimalUi.ShowKappa), MinimalUi.ShowKappa);
            config.WriteBool(nameof(MinimalUi.ShowQuestHideoutTracker), MinimalUi.ShowQuestHideoutTracker);
            config.WriteBool(nameof(MinimalUi.ShowQuestHideoutTeamTracker), MinimalUi.ShowQuestHideoutTeamTracker);
            config.WriteInt(nameof(MinimalUi.Opacity), MinimalUi.Opacity);

            config.Section = nameof(Tracking);
            config.WriteBool(nameof(Tracking.ShowNonFIRNeeds), Tracking.ShowNonFIRNeeds);
            config.WriteBool(nameof(Tracking.ShowKappaNeeds), Tracking.ShowKappaNeeds);

            config.Section = nameof(Tracking.TarkovTracker);
            config.WriteInt(nameof(Tracking.TarkovTracker.Backend), (int)Tracking.TarkovTracker.Backend);
            config.WriteSecureString(nameof(Tracking.TarkovTracker.Token), Tracking.TarkovTracker.Token);
            config.WriteBool(nameof(Tracking.TarkovTracker.ShowTeam), Tracking.TarkovTracker.ShowTeam);

            config.Section = nameof(Overlay);

            config.Section = nameof(Overlay.Search);
            config.WriteBool(nameof(Overlay.Search.Enable), Overlay.Search.Enable);
            config.WriteBool(nameof(Overlay.Search.BlurBehind), Overlay.Search.BlurBehind);
            config.WriteHotkey(nameof(Overlay.Search.Hotkey), Overlay.Search.Hotkey);

            config.Section = "Other";
            config.WriteInt(nameof(ScreenWidth), ScreenWidth);
            config.WriteInt(nameof(ScreenHeight), ScreenHeight);
            config.WriteFloat(nameof(ScreenScale), ScreenScale);
            config.WriteInt(nameof(GameMode), (int)GameMode);
            config.WriteBool(nameof(MinimizeToTray), MinimizeToTray);
            config.WriteBool(nameof(AlwaysOnTop), AlwaysOnTop);
            config.WriteBool(nameof(LogDebug), LogDebug);
            config.WriteInt(nameof(ConfigVersion), ConfigVersion);
            config.WriteInt(nameof(LastWindowPositionX), LastWindowPositionX);
            config.WriteInt(nameof(LastWindowPositionY), LastWindowPositionY);
            config.WriteInt(nameof(LastWindowMode), (int)LastWindowMode);

            GameDisplayPreferencesStore.Write(config, GetGameDisplayPreferences());
            File.Move(temporaryPath, Paths.ConfigFile, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Unable to delete a temporary configuration file.", exception);
            }
        }
    }

    internal static bool ReadFromCache(string key, out string value)
    {
        return ReadFromCache(key, out value, out _);
    }

    internal static bool ReadFromCache(string key, out string value, out DateTimeOffset lastWriteUtc)
    {
        string path = GetCachePath(key);
        try
        {
            if (!File.Exists(path))
            {
                value = string.Empty;
                lastWriteUtc = DateTimeOffset.MinValue;
                return false;
            }

            value = File.ReadAllText(path);
            lastWriteUtc = File.GetLastWriteTimeUtc(path);
            return value.Length > 0;
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Unable to read cache file '{path}'.", e);
            value = string.Empty;
            lastWriteUtc = DateTimeOffset.MinValue;
            return false;
        }
    }

    internal static string GetCachePath(string key)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string hash = string.Concat(Array.ConvertAll(hashBytes, b => b.ToString("X2")));
        return Path.Combine(Paths.CacheDir, hash + ".data");
    }

    internal static void WriteToCache(string key, string value)
    {
        string path = GetCachePath(key);
        Directory.CreateDirectory(Paths.CacheDir);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, value);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public enum WindowMode
    {
        Normal = 0,
        Minimal = 1,
        Minimized = 2,
    }

    internal static bool RefreshGameDisplayConfiguration(bool force = false)
    {
        GameDisplayConfiguration? changedConfiguration = null;
        bool viewportChanged;
        lock (GameDisplayLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!force && now - _lastGameDisplayRefresh < GameDisplayRefreshInterval)
                return false;

            GameDisplayConfiguration previous = GameDisplayConfiguration;
            GameDisplayConfiguration detected = GameDisplayService.Detect(GetGameDisplayPreferences());
            viewportChanged =
                previous.GameViewport != detected.GameViewport
                || Math.Abs(previous.DisplayScale - detected.DisplayScale) > 0.001;

            GameDisplayConfiguration = detected;
            ScreenWidth = detected.GameViewport.Width;
            ScreenHeight = detected.GameViewport.Height;
            ScreenScale = (float)detected.DisplayScale;
            _lastGameDisplayRefresh = now;

            if (HasMaterialDisplayChange(previous, detected))
                changedConfiguration = detected;
        }

        if (changedConfiguration is not null)
            GameDisplayConfigurationChanged?.Invoke(changedConfiguration);
        return viewportChanged;
    }

    internal static GameDisplayConfiguration DetectGameDisplayConfiguration(GameDisplayPreferences preferences) =>
        GameDisplayService.Detect(preferences);

    internal static GameDisplayPreferences GetGameDisplayPreferences()
    {
        Rectangle? bounds =
            PreferredGameDisplayBoundsWidth > 0 && PreferredGameDisplayBoundsHeight > 0
                ? new Rectangle(
                    PreferredGameDisplayBoundsX,
                    PreferredGameDisplayBoundsY,
                    PreferredGameDisplayBoundsWidth,
                    PreferredGameDisplayBoundsHeight
                )
                : null;
        return new GameDisplayPreferences(
            PreferredGameDisplayId,
            PreferredGameDisplayDeviceName,
            bounds,
            UseCustomGameResolution,
            CustomGameWidth,
            CustomGameHeight,
            UseCustomDisplayScale,
            CustomDisplayScale
        );
    }

    internal static void SetGameDisplayPreferences(GameDisplayPreferences preferences)
    {
        PreferredGameDisplayId = preferences.PreferredStableId;
        PreferredGameDisplayDeviceName = preferences.PreferredDeviceName;
        PreferredGameDisplayBoundsX = preferences.LastPhysicalBounds?.X ?? 0;
        PreferredGameDisplayBoundsY = preferences.LastPhysicalBounds?.Y ?? 0;
        PreferredGameDisplayBoundsWidth = preferences.LastPhysicalBounds?.Width ?? 0;
        PreferredGameDisplayBoundsHeight = preferences.LastPhysicalBounds?.Height ?? 0;
        UseCustomGameResolution = preferences.UseCustomGameResolution;
        CustomGameWidth = preferences.CustomGameWidth;
        CustomGameHeight = preferences.CustomGameHeight;
        UseCustomDisplayScale = preferences.UseCustomDisplayScale;
        CustomDisplayScale = (float)preferences.CustomDisplayScale;
    }

    private static GameDisplayPreferences CreateFirstRunPreferences(GameDisplayConfiguration automaticConfiguration)
    {
        GameDisplayInfo? display = automaticConfiguration.ActiveDisplay;
        return new GameDisplayPreferences(
            display?.StableId ?? "",
            display?.DeviceName ?? "",
            display?.PhysicalBounds,
            false,
            automaticConfiguration.GameViewport.Width,
            automaticConfiguration.GameViewport.Height,
            false,
            automaticConfiguration.DisplayScale
        );
    }

    private static bool HasMaterialDisplayChange(GameDisplayConfiguration previous, GameDisplayConfiguration current) =>
        previous.ActiveDisplay?.StableId != current.ActiveDisplay?.StableId
        || previous.GameClientBounds != current.GameClientBounds
        || previous.GameViewport != current.GameViewport
        || previous.CaptureBounds != current.CaptureBounds
        || Math.Abs(previous.DisplayScale - current.DisplayScale) > 0.001
        || previous.StatusCode != current.StatusCode
        || previous.Displays.Count != current.Displays.Count;
}
