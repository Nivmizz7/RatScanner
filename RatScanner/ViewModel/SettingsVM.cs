using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using RatStash;

namespace RatScanner.ViewModel;

internal class SettingsVM : INotifyPropertyChanged
{
    public bool EnableNameScan { get; set; }
    public bool EnableAutoNameScan { get; set; }
    public int NameScanLanguage { get; set; }

    public bool EnableIconScan { get; set; }
    public bool ScanRotatedIcons { get; set; }
    public bool UseCachedIcons { get; set; }
    public Hotkey IconScanHotkey { get; set; } = new Hotkey();

    public string ToolTipDuration { get; set; } = "";
    public int ToolTipMilli { get; set; }
    public UiLanguage UiLanguage { get; set; }

    public bool ShowName { get; set; }
    public bool ShowAvgDayPrice { get; set; }
    public bool ShowPricePerSlot { get; set; }
    public bool ShowTraderPrice { get; set; }
    public bool ShowUpdated { get; set; }
    public bool ShowKappa { get; set; }
    public bool ShowQuestHideoutTracker { get; set; }
    public bool ShowQuestHideoutTeamTracker { get; set; }
    public int Opacity { get; set; }

    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public float ScreenScale { get; set; }
    public TarkovDev.GraphQL.GameMode GameMode { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool LogDebug { get; set; }

    // Progress Tracking Settings
    public bool ShowNonFIRNeeds { get; set; }

    public bool ShowKappaNeeds { get; set; }

    // TarkovTracker Specific Tracking Settings
    public string TarkovTrackerToken { get; set; } = "";

    public bool ShowTarkovTrackerTeam { get; set; }

    public RatConfig.TarkovTrackerBackend TarkovTrackerBackend { get; set; }

    // Interactable Overlay
    public bool EnableIneractableOverlay { get; set; }
    public bool BlurBehindSearch { get; set; }
    public Hotkey InteractableOverlayHotkey { get; set; } = new Hotkey();

    private readonly LocalizationService _localizationService;

    internal SettingsVM(LocalizationService localizationService)
    {
        _localizationService = localizationService;
        LoadSettings();
    }

    public void LoadSettings()
    {
        EnableNameScan = RatConfig.NameScan.Enable;
        EnableAutoNameScan = RatConfig.NameScan.EnableAuto;
        NameScanLanguage = (int)RatConfig.NameScan.Language;

        EnableIconScan = RatConfig.IconScan.Enable;
        ScanRotatedIcons = RatConfig.IconScan.ScanRotatedIcons;
        UseCachedIcons = RatConfig.IconScan.UseCachedIcons;
        IconScanHotkey = new Hotkey(RatConfig.IconScan.Hotkey);

        ToolTipDuration = RatConfig.ToolTip.Duration.ToString();
        ToolTipMilli = RatConfig.ToolTip.Duration;
        UiLanguage = RatConfig.UserInterface.Language;

        ShowName = RatConfig.MinimalUi.ShowName;
        ShowAvgDayPrice = RatConfig.MinimalUi.ShowAvgDayPrice;
        ShowPricePerSlot = RatConfig.MinimalUi.ShowPricePerSlot;
        ShowTraderPrice = RatConfig.MinimalUi.ShowTraderPrice;
        ShowKappa = RatConfig.MinimalUi.ShowKappa;
        ShowQuestHideoutTracker = RatConfig.MinimalUi.ShowQuestHideoutTracker;
        ShowQuestHideoutTeamTracker = RatConfig.MinimalUi.ShowQuestHideoutTeamTracker;
        ShowUpdated = RatConfig.MinimalUi.ShowUpdated;
        Opacity = RatConfig.MinimalUi.Opacity;

        ScreenWidth = RatConfig.ScreenWidth;
        ScreenHeight = RatConfig.ScreenHeight;
        ScreenScale = RatConfig.ScreenScale;
        GameMode = RatConfig.GameMode;
        MinimizeToTray = RatConfig.MinimizeToTray;
        AlwaysOnTop = RatConfig.AlwaysOnTop;
        LogDebug = RatConfig.LogDebug;

        ShowNonFIRNeeds = RatConfig.Tracking.ShowNonFIRNeeds;
        ShowKappaNeeds = RatConfig.Tracking.ShowKappaNeeds;

        TarkovTrackerToken = RatConfig.Tracking.TarkovTracker.Token;
        ShowTarkovTrackerTeam = RatConfig.Tracking.TarkovTracker.ShowTeam;
        TarkovTrackerBackend = RatConfig.Tracking.TarkovTracker.Backend;

        EnableIneractableOverlay = RatConfig.Overlay.Search.Enable;
        BlurBehindSearch = RatConfig.Overlay.Search.BlurBehind;
        InteractableOverlayHotkey = new Hotkey(RatConfig.Overlay.Search.Hotkey);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public async Task SaveSettings()
    {
        bool updateTarkovTrackerToken = TarkovTrackerToken != RatConfig.Tracking.TarkovTracker.Token;
        bool updateTarkovTrackerBackend = TarkovTrackerBackend != RatConfig.Tracking.TarkovTracker.Backend;
        bool updateResolution = ScreenWidth != RatConfig.ScreenWidth || ScreenHeight != RatConfig.ScreenHeight;
        bool updateLanguage = RatConfig.NameScan.Language != (Language)NameScanLanguage;
        bool updateUiLanguage = RatConfig.UserInterface.Language != UiLanguage;
        bool updateGameMode = GameMode != RatConfig.GameMode;
        bool updateCachedIcons = UseCachedIcons != RatConfig.IconScan.UseCachedIcons;
        bool updateRotatedIcons = ScanRotatedIcons != RatConfig.IconScan.ScanRotatedIcons;
        ConfigSnapshot previousConfiguration = new();

        try
        {
            // Stage the requested values in the runtime configuration. The prior
            // state is restored if any fallible apply or persistence step fails.
            RatConfig.NameScan.Enable = EnableNameScan;
            RatConfig.NameScan.EnableAuto = EnableAutoNameScan;
            RatConfig.NameScan.Language = (Language)NameScanLanguage;

            RatConfig.IconScan.Enable = EnableIconScan;
            RatConfig.IconScan.ScanRotatedIcons = ScanRotatedIcons;
            RatConfig.IconScan.UseCachedIcons = UseCachedIcons;
            RatConfig.IconScan.Hotkey = new Hotkey(IconScanHotkey);

            RatConfig.ToolTip.Duration = ToolTipMilli;
            RatConfig.UserInterface.Language = UiLanguage;

            RatConfig.MinimalUi.ShowName = ShowName;
            RatConfig.MinimalUi.ShowAvgDayPrice = ShowAvgDayPrice;
            RatConfig.MinimalUi.ShowPricePerSlot = ShowPricePerSlot;
            RatConfig.MinimalUi.ShowTraderPrice = ShowTraderPrice;
            RatConfig.MinimalUi.ShowKappa = ShowKappa;
            RatConfig.MinimalUi.ShowQuestHideoutTracker = ShowQuestHideoutTracker;
            RatConfig.MinimalUi.ShowQuestHideoutTeamTracker = ShowQuestHideoutTeamTracker;
            RatConfig.MinimalUi.ShowUpdated = ShowUpdated;
            RatConfig.MinimalUi.Opacity = Opacity;

            RatConfig.Tracking.ShowNonFIRNeeds = ShowNonFIRNeeds;
            RatConfig.Tracking.ShowKappaNeeds = ShowKappaNeeds;

            RatConfig.Tracking.TarkovTracker.ShowTeam = ShowTarkovTrackerTeam;
            TrackerConfigurationHandle trackerConfiguration = RatScannerMain.Instance.ApplyTarkovTrackerConfiguration(
                TarkovTrackerToken.Trim(),
                TarkovTrackerBackend
            );

            RatConfig.Overlay.Search.Enable = EnableIneractableOverlay;
            RatConfig.Overlay.Search.BlurBehind = BlurBehindSearch;
            RatConfig.Overlay.Search.Hotkey = new Hotkey(InteractableOverlayHotkey);

            RatConfig.ScreenWidth = ScreenWidth;
            RatConfig.ScreenHeight = ScreenHeight;
            RatConfig.ScreenScale = ScreenScale;
            RatConfig.GameMode = GameMode;
            RatConfig.MinimizeToTray = MinimizeToTray;
            RatConfig.AlwaysOnTop = AlwaysOnTop;
            RatConfig.LogDebug = LogDebug;

            // Apply config
            PageSwitcher.Instance.Topmost = RatConfig.AlwaysOnTop;
            PageSwitcher.Instance.ResetWindowSize();
            await TarkovDevAPI.InitializeCache();
            if (updateTarkovTrackerToken || updateTarkovTrackerBackend)
                await UpdateTarkovTrackerTokenAsync(trackerConfiguration);
            if (updateUiLanguage)
                _localizationService.SetLanguage(UiLanguage);
            if (updateResolution || updateLanguage || updateGameMode || updateCachedIcons || updateRotatedIcons)
                RatScannerMain.Instance.SetupRatEye();

            RatEye.Config.LogDebug = RatConfig.LogDebug;
            RatScannerMain.Instance.HotkeyManager.RegisterHotkeys();

            // Save config to file only after every runtime apply step succeeds.
            Logger.LogInfo("Saving config...");
            RatConfig.SaveConfig();
            Logger.LogInfo("Config saved!");
        }
        catch
        {
            previousConfiguration.Restore();
            RatScannerMain.Instance.ApplyTarkovTrackerConfiguration(
                previousConfiguration.TrackerToken,
                previousConfiguration.TrackerBackend
            );
            RestoreRuntimeAfterFailedSave();
            LoadSettings();
            throw;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void RestoreRuntimeAfterFailedSave()
    {
        TryRestore(() => PageSwitcher.Instance.Topmost = RatConfig.AlwaysOnTop, "window topmost state");
        TryRestore(PageSwitcher.Instance.ResetWindowSize, "window dimensions");
        TryRestore(() => _localizationService.SetLanguage(RatConfig.UserInterface.Language), "UI language");
        TryRestore(RatScannerMain.Instance.SetupRatEye, "RatEye configuration");
        RatEye.Config.LogDebug = RatConfig.LogDebug;
        TryRestore(RatScannerMain.Instance.HotkeyManager.RegisterHotkeys, "hotkeys");
    }

    private static void TryRestore(System.Action action, string description)
    {
        try
        {
            action();
        }
        catch (System.Exception exception)
        {
            Logger.LogWarning($"Unable to restore the previous {description} after a settings failure.", exception);
        }
    }

    private async Task UpdateTarkovTrackerTokenAsync(TrackerConfigurationHandle configuration)
    {
        string token = RatConfig.Tracking.TarkovTracker.Token;
        var db = RatScannerMain.Instance.TarkovTrackerDB;
        if (string.IsNullOrWhiteSpace(token))
            return;

        TokenValidationResult result = await Task.Run(db.UpdateToken);
        if (result == TokenValidationResult.Valid)
            return;

        if (result == TokenValidationResult.Unavailable)
        {
            Logger.ShowWarning(
                "RatScanner could not reach TarkovTracker to validate the token. The token was kept and will be retried later."
            );
            return;
        }

        int visibleLength = (int)(token.Length * 0.25);
        token = token[..visibleLength] + string.Concat(Enumerable.Repeat(" *", token.Length - visibleLength));
        if (!RatScannerMain.Instance.TryClearTarkovTrackerConfiguration(configuration))
            return;

        TarkovTrackerToken = "";
        Logger.ShowWarning($"The TarkovTracker API Token does not seem to work.\n\n{token}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal virtual void OnPropertyChanged(string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class ConfigSnapshot
    {
        private readonly bool _enableNameScan = RatConfig.NameScan.Enable;
        private readonly bool _enableAutoNameScan = RatConfig.NameScan.EnableAuto;
        private readonly Language _nameScanLanguage = RatConfig.NameScan.Language;
        private readonly bool _enableIconScan = RatConfig.IconScan.Enable;
        private readonly bool _scanRotatedIcons = RatConfig.IconScan.ScanRotatedIcons;
        private readonly bool _useCachedIcons = RatConfig.IconScan.UseCachedIcons;
        private readonly Hotkey _iconScanHotkey = new(RatConfig.IconScan.Hotkey);
        private readonly int _toolTipDuration = RatConfig.ToolTip.Duration;
        private readonly UiLanguage _uiLanguage = RatConfig.UserInterface.Language;
        private readonly bool _showName = RatConfig.MinimalUi.ShowName;
        private readonly bool _showAvgDayPrice = RatConfig.MinimalUi.ShowAvgDayPrice;
        private readonly bool _showPricePerSlot = RatConfig.MinimalUi.ShowPricePerSlot;
        private readonly bool _showTraderPrice = RatConfig.MinimalUi.ShowTraderPrice;
        private readonly bool _showUpdated = RatConfig.MinimalUi.ShowUpdated;
        private readonly bool _showKappa = RatConfig.MinimalUi.ShowKappa;
        private readonly bool _showQuestHideoutTracker = RatConfig.MinimalUi.ShowQuestHideoutTracker;
        private readonly bool _showQuestHideoutTeamTracker = RatConfig.MinimalUi.ShowQuestHideoutTeamTracker;
        private readonly int _opacity = RatConfig.MinimalUi.Opacity;
        private readonly bool _showNonFirNeeds = RatConfig.Tracking.ShowNonFIRNeeds;
        private readonly bool _showKappaNeeds = RatConfig.Tracking.ShowKappaNeeds;
        private readonly string _trackerToken = RatConfig.Tracking.TarkovTracker.Token;
        private readonly bool _showTrackerTeam = RatConfig.Tracking.TarkovTracker.ShowTeam;
        private readonly RatConfig.TarkovTrackerBackend _trackerBackend = RatConfig.Tracking.TarkovTracker.Backend;
        private readonly bool _enableOverlay = RatConfig.Overlay.Search.Enable;
        private readonly bool _blurBehindSearch = RatConfig.Overlay.Search.BlurBehind;
        private readonly Hotkey _overlayHotkey = new(RatConfig.Overlay.Search.Hotkey);
        private readonly int _screenWidth = RatConfig.ScreenWidth;
        private readonly int _screenHeight = RatConfig.ScreenHeight;
        private readonly float _screenScale = RatConfig.ScreenScale;
        private readonly TarkovDev.GraphQL.GameMode _gameMode = RatConfig.GameMode;
        private readonly bool _minimizeToTray = RatConfig.MinimizeToTray;
        private readonly bool _alwaysOnTop = RatConfig.AlwaysOnTop;
        private readonly bool _logDebug = RatConfig.LogDebug;

        internal string TrackerToken => _trackerToken;
        internal RatConfig.TarkovTrackerBackend TrackerBackend => _trackerBackend;

        internal void Restore()
        {
            RatConfig.NameScan.Enable = _enableNameScan;
            RatConfig.NameScan.EnableAuto = _enableAutoNameScan;
            RatConfig.NameScan.Language = _nameScanLanguage;
            RatConfig.IconScan.Enable = _enableIconScan;
            RatConfig.IconScan.ScanRotatedIcons = _scanRotatedIcons;
            RatConfig.IconScan.UseCachedIcons = _useCachedIcons;
            RatConfig.IconScan.Hotkey = new Hotkey(_iconScanHotkey);
            RatConfig.ToolTip.Duration = _toolTipDuration;
            RatConfig.UserInterface.Language = _uiLanguage;
            RatConfig.MinimalUi.ShowName = _showName;
            RatConfig.MinimalUi.ShowAvgDayPrice = _showAvgDayPrice;
            RatConfig.MinimalUi.ShowPricePerSlot = _showPricePerSlot;
            RatConfig.MinimalUi.ShowTraderPrice = _showTraderPrice;
            RatConfig.MinimalUi.ShowUpdated = _showUpdated;
            RatConfig.MinimalUi.ShowKappa = _showKappa;
            RatConfig.MinimalUi.ShowQuestHideoutTracker = _showQuestHideoutTracker;
            RatConfig.MinimalUi.ShowQuestHideoutTeamTracker = _showQuestHideoutTeamTracker;
            RatConfig.MinimalUi.Opacity = _opacity;
            RatConfig.Tracking.ShowNonFIRNeeds = _showNonFirNeeds;
            RatConfig.Tracking.ShowKappaNeeds = _showKappaNeeds;
            RatConfig.Tracking.TarkovTracker.ShowTeam = _showTrackerTeam;
            RatConfig.Overlay.Search.Enable = _enableOverlay;
            RatConfig.Overlay.Search.BlurBehind = _blurBehindSearch;
            RatConfig.Overlay.Search.Hotkey = new Hotkey(_overlayHotkey);
            RatConfig.ScreenWidth = _screenWidth;
            RatConfig.ScreenHeight = _screenHeight;
            RatConfig.ScreenScale = _screenScale;
            RatConfig.GameMode = _gameMode;
            RatConfig.MinimizeToTray = _minimizeToTray;
            RatConfig.AlwaysOnTop = _alwaysOnTop;
            RatConfig.LogDebug = _logDebug;
        }
    }
}
