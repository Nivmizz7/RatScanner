using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace RatScanner.Display;

internal enum GameDisplaySelectionSource
{
    GameWindow,
    SavedStableId,
    SavedDeviceName,
    SavedBounds,
    PrimaryFallback,
    FirstAvailableFallback,
    None,
}

internal enum GameDisplayStatusCode
{
    GameWindowDetected,
    SavedDisplay,
    PrimaryFallback,
    FirstAvailableFallback,
    SavedDisplayUnavailable,
    GameWindowOnDifferentDisplay,
    GameWindowSpansDisplays,
    DpiUnavailable,
    InvalidCustomConfiguration,
    NoDisplays,
}

internal enum GameDisplayStatusKind
{
    Success,
    Information,
    Warning,
    Error,
}

internal sealed record GameDisplayInfo(
    string StableId,
    string DeviceName,
    string FriendlyName,
    Rectangle PhysicalBounds,
    bool IsPrimary,
    double DpiScale,
    bool IsDpiReliable,
    int DisplayNumber
)
{
    internal Size PhysicalResolution => PhysicalBounds.Size;

    internal Size LogicalResolution => DisplayCoordinateConverter.PhysicalToLogical(PhysicalResolution, DpiScale);
}

internal sealed record GameDisplayPreferences(
    string PreferredStableId,
    string PreferredDeviceName,
    Rectangle? LastPhysicalBounds,
    bool UseCustomGameResolution,
    int CustomGameWidth,
    int CustomGameHeight,
    bool UseCustomDisplayScale,
    double CustomDisplayScale
)
{
    internal bool HasSavedDisplay =>
        !string.IsNullOrWhiteSpace(PreferredStableId) || !string.IsNullOrWhiteSpace(PreferredDeviceName);

    internal static GameDisplayPreferences Automatic { get; } = new("", "", null, false, 1920, 1080, false, 1);
}

internal sealed record GameDisplaySelection(
    GameDisplayInfo? Display,
    GameDisplaySelectionSource Source,
    bool SavedDisplayUnavailable,
    bool GameWindowSpansDisplays,
    bool GameWindowDiffersFromSavedDisplay
);

internal sealed record GameDisplayConfiguration(
    IReadOnlyList<GameDisplayInfo> Displays,
    GameDisplayInfo? ActiveDisplay,
    Rectangle? GameClientBounds,
    Size GameViewport,
    Rectangle CaptureBounds,
    double DisplayScale,
    GameDisplaySelectionSource SelectionSource,
    GameDisplayStatusCode StatusCode,
    GameDisplayStatusKind StatusKind,
    bool RequiresAttention,
    bool UsesCustomGameResolution,
    bool UsesCustomDisplayScale
)
{
    internal static GameDisplayConfiguration Empty { get; } = new(
        Array.Empty<GameDisplayInfo>(),
        null,
        null,
        new Size(1920, 1080),
        Rectangle.Empty,
        1,
        GameDisplaySelectionSource.None,
        GameDisplayStatusCode.NoDisplays,
        GameDisplayStatusKind.Error,
        true,
        false,
        false
    );

    internal string NotificationKey => $"{StatusCode}:{ActiveDisplay?.StableId}";
}

internal static class DisplayCoordinateConverter
{
    internal static Size PhysicalToLogical(Size physicalSize, double dpiScale)
    {
        double scale = NormalizeScale(dpiScale);
        return new Size(
            Math.Max(1, (int)Math.Round(physicalSize.Width / scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(physicalSize.Height / scale, MidpointRounding.AwayFromZero))
        );
    }

    internal static Size LogicalToPhysical(Size logicalSize, double dpiScale)
    {
        double scale = NormalizeScale(dpiScale);
        return new Size(
            Math.Max(1, (int)Math.Round(logicalSize.Width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(logicalSize.Height * scale, MidpointRounding.AwayFromZero))
        );
    }

    private static double NormalizeScale(double dpiScale) =>
        double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
}

internal static class GameDisplayValidation
{
    internal const int MinimumWidth = 640;
    internal const int MinimumHeight = 360;
    internal const int MaximumWidth = 16384;
    internal const int MaximumHeight = 8640;
    internal const double MinimumScale = 0.5;
    internal const double MaximumScale = 5;

    internal static bool IsValidResolution(int width, int height)
    {
        if (width < MinimumWidth || width > MaximumWidth || height < MinimumHeight || height > MaximumHeight)
            return false;

        double aspectRatio = (double)width / height;
        return aspectRatio is >= 0.25 and <= 4;
    }

    internal static bool IsValidScale(double scale) =>
        double.IsFinite(scale) && scale is >= MinimumScale and <= MaximumScale;
}

internal static class GameDisplaySelectionPolicy
{
    internal static GameDisplaySelection Select(
        IReadOnlyList<GameDisplayInfo> displays,
        Rectangle? gameClientBounds,
        GameDisplayPreferences preferences
    )
    {
        GameDisplayInfo? savedDisplay = FindSavedDisplay(displays, preferences, out GameDisplaySelectionSource savedSource);
        bool savedDisplayUnavailable = preferences.HasSavedDisplay && savedDisplay is null;

        if (gameClientBounds is { Width: > 0, Height: > 0 } gameBounds)
        {
            var overlaps = displays
                .Select(display => new
                {
                    Display = display,
                    Area = GetIntersectionArea(display.PhysicalBounds, gameBounds),
                })
                .Where(candidate => candidate.Area > 0)
                .OrderByDescending(candidate => candidate.Area)
                .ThenByDescending(candidate => candidate.Display.IsPrimary)
                .ThenBy(candidate => candidate.Display.DisplayNumber)
                .ToArray();

            if (overlaps.Length > 0)
            {
                GameDisplayInfo gameDisplay = overlaps[0].Display;
                return new GameDisplaySelection(
                    gameDisplay,
                    GameDisplaySelectionSource.GameWindow,
                    savedDisplayUnavailable,
                    overlaps.Length > 1,
                    savedDisplay is not null
                        && !string.Equals(savedDisplay.StableId, gameDisplay.StableId, StringComparison.OrdinalIgnoreCase)
                );
            }
        }

        if (savedDisplay is not null)
            return new GameDisplaySelection(savedDisplay, savedSource, false, false, false);

        GameDisplayInfo? primary = displays.FirstOrDefault(display => display.IsPrimary);
        if (primary is not null)
            return new GameDisplaySelection(
                primary,
                GameDisplaySelectionSource.PrimaryFallback,
                savedDisplayUnavailable,
                false,
                false
            );

        GameDisplayInfo? first = displays.OrderBy(display => display.DisplayNumber).FirstOrDefault();
        return new GameDisplaySelection(
            first,
            first is null ? GameDisplaySelectionSource.None : GameDisplaySelectionSource.FirstAvailableFallback,
            savedDisplayUnavailable,
            false,
            false
        );
    }

    private static GameDisplayInfo? FindSavedDisplay(
        IReadOnlyList<GameDisplayInfo> displays,
        GameDisplayPreferences preferences,
        out GameDisplaySelectionSource source
    )
    {
        GameDisplayInfo? match = displays.FirstOrDefault(display =>
            !string.IsNullOrWhiteSpace(preferences.PreferredStableId)
            && string.Equals(display.StableId, preferences.PreferredStableId, StringComparison.OrdinalIgnoreCase)
        );
        if (match is not null)
        {
            source = GameDisplaySelectionSource.SavedStableId;
            return match;
        }

        match = displays.FirstOrDefault(display =>
            !string.IsNullOrWhiteSpace(preferences.PreferredDeviceName)
            && string.Equals(display.DeviceName, preferences.PreferredDeviceName, StringComparison.OrdinalIgnoreCase)
        );
        if (match is not null)
        {
            source = GameDisplaySelectionSource.SavedDeviceName;
            return match;
        }

        if (preferences.LastPhysicalBounds is { Width: > 0, Height: > 0 } savedBounds)
        {
            GameDisplayInfo[] boundsMatches = displays.Where(display => display.PhysicalBounds == savedBounds).ToArray();
            if (boundsMatches.Length == 1)
            {
                source = GameDisplaySelectionSource.SavedBounds;
                return boundsMatches[0];
            }
        }

        source = GameDisplaySelectionSource.None;
        return null;
    }

    private static long GetIntersectionArea(Rectangle first, Rectangle second)
    {
        Rectangle intersection = Rectangle.Intersect(first, second);
        return intersection.IsEmpty ? 0 : (long)intersection.Width * intersection.Height;
    }
}

internal static class GameDisplayConfigurationBuilder
{
    internal static GameDisplayConfiguration Build(
        IReadOnlyList<GameDisplayInfo> displays,
        Rectangle? gameClientBounds,
        Size? graphicsViewport,
        GameDisplayPreferences preferences
    )
    {
        GameDisplaySelection selection = GameDisplaySelectionPolicy.Select(displays, gameClientBounds, preferences);
        GameDisplayInfo? display = selection.Display;

        Size automaticViewport = GetAutomaticViewport(display, gameClientBounds, graphicsViewport);
        bool validCustomResolution = GameDisplayValidation.IsValidResolution(
            preferences.CustomGameWidth,
            preferences.CustomGameHeight
        );
        bool useCustomResolution = preferences.UseCustomGameResolution && validCustomResolution;
        Size gameViewport = useCustomResolution
            ? new Size(preferences.CustomGameWidth, preferences.CustomGameHeight)
            : automaticViewport;

        bool validCustomScale = GameDisplayValidation.IsValidScale(preferences.CustomDisplayScale);
        bool useCustomScale = preferences.UseCustomDisplayScale && validCustomScale;
        double displayScale = useCustomScale ? preferences.CustomDisplayScale : display?.DpiScale ?? 1;
        if (!GameDisplayValidation.IsValidScale(displayScale))
            displayScale = 1;

        Rectangle captureBounds = gameClientBounds is { Width: > 0, Height: > 0 } clientBounds
            ? clientBounds
            : display?.PhysicalBounds ?? Rectangle.Empty;

        bool invalidCustomConfiguration =
            (preferences.UseCustomGameResolution && !validCustomResolution)
            || (preferences.UseCustomDisplayScale && !validCustomScale);

        (GameDisplayStatusCode statusCode, GameDisplayStatusKind statusKind, bool requiresAttention) = GetStatus(
            selection,
            display,
            invalidCustomConfiguration
        );

        return new GameDisplayConfiguration(
            displays,
            display,
            gameClientBounds,
            gameViewport,
            captureBounds,
            displayScale,
            selection.Source,
            statusCode,
            statusKind,
            requiresAttention,
            useCustomResolution,
            useCustomScale
        );
    }

    private static Size GetAutomaticViewport(
        GameDisplayInfo? display,
        Rectangle? gameClientBounds,
        Size? graphicsViewport
    )
    {
        if (gameClientBounds is { Width: > 0, Height: > 0 } gameBounds)
            return gameBounds.Size;
        if (graphicsViewport is { Width: > 0, Height: > 0 } configuredViewport
            && GameDisplayValidation.IsValidResolution(configuredViewport.Width, configuredViewport.Height))
            return configuredViewport;
        return display?.PhysicalResolution ?? new Size(1920, 1080);
    }

    private static (GameDisplayStatusCode Code, GameDisplayStatusKind Kind, bool RequiresAttention) GetStatus(
        GameDisplaySelection selection,
        GameDisplayInfo? display,
        bool invalidCustomConfiguration
    )
    {
        if (display is null)
            return (GameDisplayStatusCode.NoDisplays, GameDisplayStatusKind.Error, true);
        if (invalidCustomConfiguration)
            return (GameDisplayStatusCode.InvalidCustomConfiguration, GameDisplayStatusKind.Error, true);
        if (selection.SavedDisplayUnavailable)
            return (GameDisplayStatusCode.SavedDisplayUnavailable, GameDisplayStatusKind.Warning, true);
        if (selection.GameWindowSpansDisplays)
            return (GameDisplayStatusCode.GameWindowSpansDisplays, GameDisplayStatusKind.Warning, true);
        if (selection.GameWindowDiffersFromSavedDisplay)
            return (GameDisplayStatusCode.GameWindowOnDifferentDisplay, GameDisplayStatusKind.Warning, true);
        if (!display.IsDpiReliable)
            return (GameDisplayStatusCode.DpiUnavailable, GameDisplayStatusKind.Warning, true);
        if (selection.Source == GameDisplaySelectionSource.GameWindow)
            return (GameDisplayStatusCode.GameWindowDetected, GameDisplayStatusKind.Success, false);
        if (selection.Source is GameDisplaySelectionSource.SavedStableId
            or GameDisplaySelectionSource.SavedDeviceName
            or GameDisplaySelectionSource.SavedBounds)
            return (GameDisplayStatusCode.SavedDisplay, GameDisplayStatusKind.Information, false);
        if (selection.Source == GameDisplaySelectionSource.PrimaryFallback)
            return (GameDisplayStatusCode.PrimaryFallback, GameDisplayStatusKind.Information, false);
        return (GameDisplayStatusCode.FirstAvailableFallback, GameDisplayStatusKind.Warning, true);
    }
}

internal static class GameDisplayMigration
{
    internal static GameDisplayPreferences FromLegacy(
        int legacyWidth,
        int legacyHeight,
        double legacyScale,
        GameDisplayConfiguration automaticConfiguration
    )
    {
        GameDisplayInfo? display = automaticConfiguration.ActiveDisplay;
        bool preserveResolution =
            GameDisplayValidation.IsValidResolution(legacyWidth, legacyHeight)
            && (legacyWidth != automaticConfiguration.GameViewport.Width
                || legacyHeight != automaticConfiguration.GameViewport.Height);
        bool preserveScale =
            GameDisplayValidation.IsValidScale(legacyScale)
            && Math.Abs(legacyScale - automaticConfiguration.DisplayScale) > 0.01;

        return new GameDisplayPreferences(
            display?.StableId ?? "",
            display?.DeviceName ?? "",
            display?.PhysicalBounds,
            preserveResolution,
            GameDisplayValidation.IsValidResolution(legacyWidth, legacyHeight) ? legacyWidth : 1920,
            GameDisplayValidation.IsValidResolution(legacyWidth, legacyHeight) ? legacyHeight : 1080,
            preserveScale,
            GameDisplayValidation.IsValidScale(legacyScale) ? legacyScale : 1
        );
    }
}
