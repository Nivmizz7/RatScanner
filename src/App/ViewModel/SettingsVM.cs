using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.Display;
using RatScanner.TarkovDev;
using RatStash;

namespace RatScanner.ViewModel;

internal class SettingsVM : INotifyPropertyChanged, IDisposable
{
    public sealed record GameDisplayOption(string Id, string Label);

    private readonly LocalizationService _localizationService;
    private readonly SettingsPersistenceService _persistence;
    private readonly SemaphoreSlim _displaySaveLock = new(1, 1);
    private readonly SynchronizationContext? _synchronizationContext;
    private bool _disposed;

    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }
    public float ScreenScale { get; private set; }
    public IReadOnlyList<GameDisplayOption> GameDisplayOptions { get; private set; } = [];

    private GameDisplayConfiguration _displayPreview = GameDisplayConfiguration.Empty;
    private string _selectedGameDisplayId = "";
    private bool _useCustomGameResolution;
    private string _customGameWidthText = "1920";
    private string _customGameHeightText = "1080";
    private bool _useCustomDisplayScale;
    private string _customDisplayScaleText = "100";
    private string? _displayPersistenceError;

    internal SettingsVM(LocalizationService localizationService, SettingsPersistenceService persistence)
    {
        _localizationService = localizationService;
        _persistence = persistence;
        _synchronizationContext = SynchronizationContext.Current;
        RatConfig.GameDisplayConfigurationChanged += OnGameDisplayConfigurationChanged;
        LoadDisplaySettings();
    }

    public string SelectedGameDisplayId
    {
        get => _selectedGameDisplayId;
        set
        {
            value ??= "";
            if (string.Equals(_selectedGameDisplayId, value, StringComparison.Ordinal))
                return;
            _selectedGameDisplayId = value;
            UpdateDisplayPreview();
        }
    }

    public bool UseCustomGameResolution
    {
        get => _useCustomGameResolution;
        set
        {
            if (_useCustomGameResolution == value)
                return;
            _useCustomGameResolution = value;
            if (!value)
                _useCustomDisplayScale = false;
            UpdateDisplayPreview();
        }
    }

    public string CustomGameWidthText
    {
        get => _customGameWidthText;
        set
        {
            value ??= "";
            if (_customGameWidthText == value)
                return;
            _customGameWidthText = value;
            UpdateDisplayPreview();
        }
    }

    public string CustomGameHeightText
    {
        get => _customGameHeightText;
        set
        {
            value ??= "";
            if (_customGameHeightText == value)
                return;
            _customGameHeightText = value;
            UpdateDisplayPreview();
        }
    }

    public bool UseCustomDisplayScale
    {
        get => _useCustomDisplayScale;
        set
        {
            if (_useCustomDisplayScale == value)
                return;
            _useCustomDisplayScale = value;
            UpdateDisplayPreview();
        }
    }

    public string CustomDisplayScaleText
    {
        get => _customDisplayScaleText;
        set
        {
            value ??= "";
            if (_customDisplayScaleText == value)
                return;
            _customDisplayScaleText = value;
            UpdateDisplayPreview();
        }
    }

    public int CustomGameWidth => TryParsePositiveInt(CustomGameWidthText, out int value) ? value : 0;
    public int CustomGameHeight => TryParsePositiveInt(CustomGameHeightText, out int value) ? value : 0;
    public float CustomDisplayScale =>
        TryParseDisplayScalePercentage(CustomDisplayScaleText, out float value) ? value : float.NaN;

    public string? DisplaySelectionError =>
        GameDisplayOptions.Count == 0 || GameDisplayOptions.All(option => option.Id != SelectedGameDisplayId)
            ? _localizationService["GameDisplayUnavailableError"]
            : null;

    public string? CustomResolutionError
    {
        get
        {
            if (!UseCustomGameResolution)
                return null;
            if (
                !TryParsePositiveInt(CustomGameWidthText, out int width)
                || !TryParsePositiveInt(CustomGameHeightText, out int height)
            )
                return _localizationService["CustomResolutionNumbersValidation"];
            if (!GameDisplayValidation.IsValidResolution(width, height))
            {
                return _localizationService.Format(
                    "CustomResolutionValidation",
                    GameDisplayValidation.MinimumWidth,
                    GameDisplayValidation.MinimumHeight,
                    GameDisplayValidation.MaximumWidth,
                    GameDisplayValidation.MaximumHeight
                );
            }

            GameDisplayInfo? display = SelectedDisplay;
            if (display is not null && (width > display.PhysicalBounds.Width || height > display.PhysicalBounds.Height))
            {
                return _localizationService.Format(
                    "CustomResolutionDisplayValidation",
                    display.PhysicalBounds.Width,
                    display.PhysicalBounds.Height
                );
            }
            return null;
        }
    }

    public string? CustomDisplayScaleError =>
        UseCustomDisplayScale
        && (
            !TryParseDisplayScalePercentage(CustomDisplayScaleText, out float scale)
            || !GameDisplayValidation.IsValidScale(scale)
        )
            ? _localizationService.Format(
                "CustomScaleValidation",
                (int)(GameDisplayValidation.MinimumScale * 100),
                (int)(GameDisplayValidation.MaximumScale * 100)
            )
            : null;

    public string? DisplayPersistenceError => _displayPersistenceError;
    public bool CanPersistDisplaySettings =>
        DisplaySelectionError is null && CustomResolutionError is null && CustomDisplayScaleError is null;

    public GameDisplayStatusKind GameDisplayStatusKind => _displayPreview.StatusKind;
    public string GameDisplayStatus => FormatGameDisplayStatus(_displayPreview);
    public string PhysicalDisplayResolution => FormatSize(_displayPreview.ActiveDisplay?.PhysicalResolution);
    public string LogicalDisplayResolution => FormatSize(_displayPreview.ActiveDisplay?.LogicalResolution);
    public string GameViewportResolution => FormatSize(_displayPreview.GameViewport);
    public string DisplayScaling => $"{Math.Round(_displayPreview.DisplayScale * 100):0}%";
    public string CaptureRegion => FormatCaptureBounds(_displayPreview.CaptureBounds);
    public string DisplayDetectionMode =>
        _displayPreview.UsesCustomGameResolution || _displayPreview.UsesCustomDisplayScale
            ? _localizationService["CustomMode"]
            : _localizationService["AutomaticMode"];

    internal Task<SettingSaveResult> SetEnableNameScanAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.NameScan.Enable),
            "name scanner",
            value,
            () => RatConfig.NameScan.Enable,
            v => RatConfig.NameScan.Enable = v,
            // ActiveHotkey copies Enable by value at construction; rebuild so the live flag is honored.
            _ => RatScannerMain.Instance.HotkeyManager.RegisterHotkeys()
        );

    internal Task<SettingSaveResult> SetEnableAutoNameScanAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.NameScan.EnableAuto),
            "automatic name scanning",
            value,
            () => RatConfig.NameScan.EnableAuto,
            v => RatConfig.NameScan.EnableAuto = v
        );

    internal Task<SettingSaveResult> SetNameScanLanguageAsync(int value) =>
        SaveAsync(
            nameof(RatConfig.NameScan.Language),
            "name-scan language",
            (Language)value,
            () => RatConfig.NameScan.Language,
            v => RatConfig.NameScan.Language = v,
            _ => RatScannerMain.Instance.SetupRatEye()
        );

    internal Task<SettingSaveResult> SetEnableIconScanAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.IconScan.Enable),
            "icon scanner",
            value,
            () => RatConfig.IconScan.Enable,
            v => RatConfig.IconScan.Enable = v,
            // ActiveHotkey copies Enable by value at construction; rebuild so the live flag is honored.
            _ => RatScannerMain.Instance.HotkeyManager.RegisterHotkeys()
        );

    internal Task<SettingSaveResult> SetScanRotatedIconsAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.IconScan.ScanRotatedIcons),
            "rotated icon scanning",
            value,
            () => RatConfig.IconScan.ScanRotatedIcons,
            v => RatConfig.IconScan.ScanRotatedIcons = v,
            _ => RatScannerMain.Instance.SetupRatEye()
        );

    internal Task<SettingSaveResult> SetUseCachedIconsAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.IconScan.UseCachedIcons),
            "cached icon usage",
            value,
            () => RatConfig.IconScan.UseCachedIcons,
            v => RatConfig.IconScan.UseCachedIcons = v,
            _ => RatScannerMain.Instance.SetupRatEye()
        );

    internal Task<SettingSaveResult> SetIconScanHotkeyAsync(Hotkey value) =>
        SaveAsync(
            nameof(RatConfig.IconScan.Hotkey),
            "icon-scan hotkey",
            new Hotkey(value),
            () => new Hotkey(RatConfig.IconScan.Hotkey),
            v => RatConfig.IconScan.Hotkey = new Hotkey(v),
            _ => RatScannerMain.Instance.HotkeyManager.RegisterHotkeys()
        );

    internal Task<SettingSaveResult> SetUiLanguageAsync(UiLanguage value) =>
        SaveAsync(
            nameof(RatConfig.UserInterface.Language),
            "interface language",
            value,
            () => RatConfig.UserInterface.Language,
            v => RatConfig.UserInterface.Language = v,
            _ => _localizationService.SetLanguage(value)
        );

    internal Task<SettingSaveResult> SetTooltipDurationAsync(int value) =>
        SaveValidatedAsync(
            nameof(RatConfig.ToolTip.Duration),
            "tooltip delay",
            value,
            () => RatConfig.ToolTip.Duration,
            v => RatConfig.ToolTip.Duration = v,
            static v => v is 0 or 500 or 1500 or 3000 ? null : "Unsupported tooltip delay."
        );

    internal Task<SettingSaveResult> SetAlwaysOnTopAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.AlwaysOnTop),
            "always-on-top",
            value,
            () => RatConfig.AlwaysOnTop,
            v => RatConfig.AlwaysOnTop = v,
            v => PageSwitcher.Instance.Topmost = v
        );

    internal Task<SettingSaveResult> SetLogDebugAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.LogDebug),
            "debug logging",
            value,
            () => RatConfig.LogDebug,
            v => RatConfig.LogDebug = v,
            v => RatEye.Config.LogDebug = v
        );

    internal Task<SettingSaveResult> SetShowNonFirNeedsAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.Tracking.ShowNonFIRNeeds),
            "non-FIR requirement display",
            value,
            () => RatConfig.Tracking.ShowNonFIRNeeds,
            v => RatConfig.Tracking.ShowNonFIRNeeds = v
        );

    internal Task<SettingSaveResult> SetShowKappaNeedsAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.Tracking.ShowKappaNeeds),
            "Kappa requirement display",
            value,
            () => RatConfig.Tracking.ShowKappaNeeds,
            v => RatConfig.Tracking.ShowKappaNeeds = v
        );

    internal Task<SettingSaveResult> SetTarkovTrackerOrgTokenAsync(GameMode mode, string value) =>
        SaveAsync(
            $"TarkovTracker.Org.{mode}",
            $"{mode} TarkovTracker.org API key",
            value,
            () => RatConfig.Tracking.TarkovTracker.TokenForMode(mode),
            token => RatConfig.Tracking.TarkovTracker.SetTokenForMode(mode, token)
        );

    internal Task<SettingSaveResult> SetTarkovTrackerIoTokenAsync(string value) =>
        SaveAsync(
            "TarkovTracker.IO.PvP",
            "TarkovTracker.io API key",
            value,
            () => RatConfig.Tracking.TarkovTracker.IoToken,
            token => RatConfig.Tracking.TarkovTracker.IoToken = token
        );

    internal Task<SettingSaveResult> SetTarkovTrackerPvpSourceAsync(PvpSource value) =>
        SaveAsync(
            "TarkovTracker.PvpSource",
            "PvP tracker source",
            value,
            () => RatConfig.Tracking.TarkovTracker.PvpSource,
            source => RatConfig.Tracking.TarkovTracker.PvpSource = source,
            _ => RefreshTrackerInBackground("Unable to refresh tracker progress after changing the PvP tracker source.")
        );

    internal Task<SettingSaveResult> SetShowTarkovTrackerTeamAsync(bool value) =>
        SaveAsync(
            nameof(RatConfig.Tracking.TarkovTracker.ShowTeam),
            "TarkovTracker team progress",
            value,
            () => RatConfig.Tracking.TarkovTracker.ShowTeam,
            v => RatConfig.Tracking.TarkovTracker.ShowTeam = v,
            _ => RefreshTrackerInBackground("Unable to refresh tracker progress after changing team visibility.")
        );

    // The tracker database only re-reads the active token/endpoint on activation, so
    // runtime-affecting changes (PvP source, team visibility) must re-activate it
    // explicitly instead of waiting for the periodic refresh.
    //
    // Fire-and-forget is safe here: TarkovTrackerDB.Configure assigns a monotonically
    // increasing generation to each configuration, and stale async operations check
    // IsCurrent before committing state. If the save later fails and
    // SettingsPersistenceService rolls back via applyRuntime(previous), the rollback
    // activation gets a newer generation and wins — so the runtime always converges on
    // the persisted value even when the optimistic change and rollback race.
    private static void RefreshTrackerInBackground(string failureMessage)
    {
        _ = RatScannerMain
            .Instance.ActivateTrackerModeAsync(RatConfig.GameMode)
            .ContinueWith(t => Logger.LogWarning(failureMessage, t.Exception), TaskContinuationOptions.OnlyOnFaulted);
    }

    internal Task<SettingSaveResult> SetMinimalUiAsync(string key, bool value) =>
        key switch
        {
            nameof(RatConfig.MinimalUi.ShowName) => SaveAsync(
                key,
                "minimal UI name",
                value,
                () => RatConfig.MinimalUi.ShowName,
                v => RatConfig.MinimalUi.ShowName = v
            ),
            nameof(RatConfig.MinimalUi.ShowAvgDayPrice) => SaveAsync(
                key,
                "minimal UI average price",
                value,
                () => RatConfig.MinimalUi.ShowAvgDayPrice,
                v => RatConfig.MinimalUi.ShowAvgDayPrice = v
            ),
            nameof(RatConfig.MinimalUi.ShowPricePerSlot) => SaveAsync(
                key,
                "minimal UI price per slot",
                value,
                () => RatConfig.MinimalUi.ShowPricePerSlot,
                v => RatConfig.MinimalUi.ShowPricePerSlot = v
            ),
            nameof(RatConfig.MinimalUi.ShowTraderPrice) => SaveAsync(
                key,
                "minimal UI trader price",
                value,
                () => RatConfig.MinimalUi.ShowTraderPrice,
                v => RatConfig.MinimalUi.ShowTraderPrice = v
            ),
            nameof(RatConfig.MinimalUi.ShowUpdated) => SaveAsync(
                key,
                "minimal UI update time",
                value,
                () => RatConfig.MinimalUi.ShowUpdated,
                v => RatConfig.MinimalUi.ShowUpdated = v
            ),
            nameof(RatConfig.MinimalUi.ShowKappa) => SaveAsync(
                key,
                "minimal UI Kappa status",
                value,
                () => RatConfig.MinimalUi.ShowKappa,
                v => RatConfig.MinimalUi.ShowKappa = v
            ),
            nameof(RatConfig.MinimalUi.ShowQuestHideoutTracker) => SaveAsync(
                key,
                "minimal UI personal tracking",
                value,
                () => RatConfig.MinimalUi.ShowQuestHideoutTracker,
                v => RatConfig.MinimalUi.ShowQuestHideoutTracker = v
            ),
            nameof(RatConfig.MinimalUi.ShowQuestHideoutTeamTracker) => SaveAsync(
                key,
                "minimal UI team tracking",
                value,
                () => RatConfig.MinimalUi.ShowQuestHideoutTeamTracker,
                v => RatConfig.MinimalUi.ShowQuestHideoutTeamTracker = v
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

    internal Task<SettingSaveResult> SetOpacityAsync(int value) =>
        SaveValidatedAsync(
            nameof(RatConfig.MinimalUi.Opacity),
            "minimal UI opacity",
            value,
            () => RatConfig.MinimalUi.Opacity,
            v => RatConfig.MinimalUi.Opacity = v,
            static v => v is >= 0 and <= 100 ? null : "Opacity must be between 0 and 100."
        );

    internal async Task<SettingSaveResult> PersistDisplaySettingsAsync()
    {
        if (!CanPersistDisplaySettings)
            return new SettingSaveResult(
                false,
                CustomResolutionError ?? CustomDisplayScaleError ?? DisplaySelectionError
            );

        await _displaySaveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            GameDisplayPreferences next = CreateDraftDisplayPreferences();
            SettingSaveResult result = await SaveAsync(
                    "GameDisplayPreferences",
                    "game display configuration",
                    next,
                    RatConfig.GetGameDisplayPreferences,
                    RatConfig.SetGameDisplayPreferences,
                    _ =>
                    {
                        bool changed = RatConfig.RefreshGameDisplayConfiguration(force: true);
                        ApplyDisplayConfiguration(RatConfig.GameDisplayConfiguration, resetDraft: false);
                        if (changed)
                            RatScannerMain.Instance.SetupRatEye();
                    }
                )
                .ConfigureAwait(false);
            _displayPersistenceError = result.Succeeded ? null : _localizationService["SettingSaveFailed"];
            OnPropertyChanged();
            return result;
        }
        finally
        {
            _displaySaveLock.Release();
        }
    }

    internal void ResetDisplayDraftToDetected()
    {
        GameDisplayConfiguration automatic = RatConfig.DetectGameDisplayConfiguration(GameDisplayPreferences.Automatic);
        _selectedGameDisplayId = automatic.ActiveDisplay?.StableId ?? "";
        _useCustomGameResolution = false;
        _useCustomDisplayScale = false;
        _customGameWidthText = automatic.GameViewport.Width.ToString(CultureInfo.CurrentCulture);
        _customGameHeightText = automatic.GameViewport.Height.ToString(CultureInfo.CurrentCulture);
        _customDisplayScaleText = FormatDisplayScalePercentage((float)automatic.DisplayScale);
        UpdateDisplayPreview();
    }

    public void RefreshGameDisplays()
    {
        bool updateResolution = RatConfig.RefreshGameDisplayConfiguration(force: true);
        LoadDisplaySettings();
        if (updateResolution)
            RatScannerMain.Instance.SetupRatEye();
    }

    public async System.Threading.Tasks.Task RefreshGameDisplaysAsync()
    {
        // Detection can be expensive, but all view-model draft mutations must
        // resume on the Blazor renderer context to avoid racing user edits/rendering.
        bool updateResolution = await System.Threading.Tasks.Task.Run(static () =>
            RatConfig.RefreshGameDisplayConfiguration(force: true)
        );
        LoadDisplaySettings();
        if (updateResolution)
            RatScannerMain.Instance.SetupRatEye();
    }

    internal static bool TryParseDisplayScalePercentage(string? text, out float scale)
    {
        string value = text?.Trim().TrimEnd('%').Trim() ?? "";
        if (
            !float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out float percentage)
            && !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out percentage)
        )
        {
            scale = default;
            return false;
        }

        scale = percentage / 100f;
        return float.IsFinite(scale);
    }

    internal static bool TryParsePositiveInt(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value > 0;

    private Task<SettingSaveResult> SaveAsync<T>(
        string key,
        string description,
        T value,
        Func<T> read,
        Action<T> apply,
        Action<T>? applyRuntime = null
    ) => _persistence.SaveImmediateAsync(key, description, value, read, apply, applyRuntime);

    private Task<SettingSaveResult> SaveValidatedAsync<T>(
        string key,
        string description,
        T value,
        Func<T> read,
        Action<T> apply,
        Func<T, string?> validate,
        Action<T>? applyRuntime = null
    ) => _persistence.SaveValidatedAsync(key, description, value, read, apply, validate, applyRuntime);

    private void LoadDisplaySettings()
    {
        GameDisplayPreferences preferences = RatConfig.GetGameDisplayPreferences();
        ApplyDisplayConfiguration(RatConfig.GameDisplayConfiguration, resetDraft: true);
        _useCustomGameResolution = preferences.UseCustomGameResolution;
        _customGameWidthText = preferences.CustomGameWidth.ToString(CultureInfo.CurrentCulture);
        _customGameHeightText = preferences.CustomGameHeight.ToString(CultureInfo.CurrentCulture);
        _useCustomDisplayScale = preferences.UseCustomDisplayScale;
        _customDisplayScaleText = FormatDisplayScalePercentage((float)preferences.CustomDisplayScale);
        UpdateDisplayPreview();
    }

    private static string FormatDisplayScalePercentage(float scale) =>
        (scale * 100).ToString("0.##", CultureInfo.CurrentCulture);

    private void ApplyDisplayConfiguration(GameDisplayConfiguration configuration, bool resetDraft)
    {
        _displayPreview = configuration;
        GameDisplayOptions = configuration
            .Displays.Select(display => new GameDisplayOption(display.StableId, FormatGameDisplayOption(display)))
            .ToArray();
        if (resetDraft)
        {
            string preferredId = RatConfig.PreferredGameDisplayId;
            _selectedGameDisplayId = GameDisplayOptions.Any(option => option.Id == preferredId)
                ? preferredId
                : configuration.ActiveDisplay?.StableId ?? "";
        }
        ScreenWidth = configuration.GameViewport.Width;
        ScreenHeight = configuration.GameViewport.Height;
        ScreenScale = (float)configuration.DisplayScale;
    }

    private void UpdateDisplayPreview()
    {
        GameDisplayPreferences preferences = CreateDraftDisplayPreferences();
        _displayPreview = RatConfig.DetectGameDisplayConfiguration(preferences);
        GameDisplayOptions = _displayPreview
            .Displays.Select(display => new GameDisplayOption(display.StableId, FormatGameDisplayOption(display)))
            .ToArray();
        ScreenWidth = _displayPreview.GameViewport.Width;
        ScreenHeight = _displayPreview.GameViewport.Height;
        ScreenScale = (float)_displayPreview.DisplayScale;
        OnPropertyChanged();
    }

    private GameDisplayInfo? SelectedDisplay =>
        _displayPreview.Displays.FirstOrDefault(display =>
            string.Equals(display.StableId, SelectedGameDisplayId, StringComparison.OrdinalIgnoreCase)
        );

    private GameDisplayPreferences CreateDraftDisplayPreferences()
    {
        GameDisplayInfo? selectedDisplay = SelectedDisplay;
        int width = TryParsePositiveInt(CustomGameWidthText, out int parsedWidth) ? parsedWidth : 0;
        int height = TryParsePositiveInt(CustomGameHeightText, out int parsedHeight) ? parsedHeight : 0;
        float scale = TryParseDisplayScalePercentage(CustomDisplayScaleText, out float parsedScale)
            ? parsedScale
            : float.NaN;
        return new GameDisplayPreferences(
            selectedDisplay?.StableId ?? SelectedGameDisplayId,
            selectedDisplay?.DeviceName ?? "",
            selectedDisplay?.PhysicalBounds,
            UseCustomGameResolution,
            width,
            height,
            UseCustomDisplayScale,
            scale
        );
    }

    private string FormatGameDisplayOption(GameDisplayInfo display)
    {
        string primarySuffix = display.IsPrimary ? _localizationService["PrimaryDisplaySuffix"] : "";
        string friendlyName = string.IsNullOrWhiteSpace(display.FriendlyName) ? "" : $" — {display.FriendlyName}";
        return _localizationService.Format(
            "GameDisplayOption",
            display.DisplayNumber,
            display.PhysicalResolution.Width,
            display.PhysicalResolution.Height,
            Math.Round(display.DpiScale * 100),
            primarySuffix + friendlyName
        );
    }

    private string FormatGameDisplayStatus(GameDisplayConfiguration configuration)
    {
        string display = configuration.ActiveDisplay is null
            ? _localizationService["UnknownDisplay"]
            : FormatGameDisplayOption(configuration.ActiveDisplay);
        return configuration.StatusCode switch
        {
            GameDisplayStatusCode.GameWindowDetected => _localizationService.Format(
                "GameDisplayStatusDetected",
                display
            ),
            GameDisplayStatusCode.SavedDisplay => _localizationService.Format("GameDisplayStatusSaved", display),
            GameDisplayStatusCode.PrimaryFallback => _localizationService.Format("GameDisplayStatusPrimary", display),
            GameDisplayStatusCode.FirstAvailableFallback => _localizationService.Format(
                "GameDisplayStatusFallback",
                display
            ),
            GameDisplayStatusCode.SavedDisplayUnavailable => _localizationService.Format(
                "GameDisplayStatusUnavailable",
                display
            ),
            GameDisplayStatusCode.GameWindowOnDifferentDisplay => _localizationService.Format(
                "GameDisplayStatusMoved",
                display
            ),
            GameDisplayStatusCode.GameWindowSpansDisplays => _localizationService.Format(
                "GameDisplayStatusSpanning",
                display
            ),
            GameDisplayStatusCode.DpiUnavailable => _localizationService.Format("GameDisplayStatusDpi", display),
            GameDisplayStatusCode.InvalidCustomConfiguration => _localizationService["GameDisplayStatusInvalidCustom"],
            _ => _localizationService["GameDisplayStatusNone"],
        };
    }

    private static string FormatSize(Size? size) =>
        size is { Width: > 0, Height: > 0 } value ? $"{value.Width} × {value.Height}" : "—";

    private static string FormatCaptureBounds(Rectangle bounds) =>
        bounds.Width > 0 && bounds.Height > 0 ? $"{bounds.Width} × {bounds.Height} @ ({bounds.X}, {bounds.Y})" : "—";

    private void OnGameDisplayConfigurationChanged(GameDisplayConfiguration configuration)
    {
        void ApplyChange()
        {
            if (_disposed)
                return;
            ApplyDisplayConfiguration(configuration, resetDraft: false);
            OnPropertyChanged();
        }

        if (_synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
            ApplyChange();
        else
            _synchronizationContext.Post(_ => ApplyChange(), null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal virtual void OnPropertyChanged(string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        RatConfig.GameDisplayConfigurationChanged -= OnGameDisplayConfigurationChanged;
        _displaySaveLock.Dispose();
    }
}
