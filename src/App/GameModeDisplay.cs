using System;
using RatScanner.TarkovDev;

namespace RatScanner;

internal static class GameModeDisplay
{
    internal static string GetLabel(GameMode mode, LocalizationService localizer) =>
        mode switch
        {
            GameMode.Regular => localizer["PvpMode"],
            GameMode.Pve => localizer["PveMode"],
            GameMode.Seasonal => localizer["SeasonalMode"],
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported game mode."),
        };
}
