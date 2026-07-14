using System.Drawing;

namespace RatScanner.Display;

internal static class GameDisplayPreferencesStore
{
    private const string SectionName = "GameDisplay";
    private const string MissingSetting = "RatScanner.Config.Missing.GameDisplay";

    internal static bool TryRead(
        SimpleConfig config,
        int defaultWidth,
        int defaultHeight,
        double defaultScale,
        out GameDisplayPreferences preferences
    )
    {
        config.Section = SectionName;
        if (config.ReadString(nameof(RatConfig.PreferredGameDisplayId), MissingSetting) == MissingSetting)
        {
            preferences = GameDisplayPreferences.Automatic;
            return false;
        }

        int boundsWidth = config.ReadInt(nameof(RatConfig.PreferredGameDisplayBoundsWidth), 0);
        int boundsHeight = config.ReadInt(nameof(RatConfig.PreferredGameDisplayBoundsHeight), 0);
        Rectangle? bounds =
            boundsWidth > 0 && boundsHeight > 0
                ? new Rectangle(
                    config.ReadInt(nameof(RatConfig.PreferredGameDisplayBoundsX), 0),
                    config.ReadInt(nameof(RatConfig.PreferredGameDisplayBoundsY), 0),
                    boundsWidth,
                    boundsHeight
                )
                : null;

        preferences = new GameDisplayPreferences(
            config.ReadString(nameof(RatConfig.PreferredGameDisplayId), ""),
            config.ReadString(nameof(RatConfig.PreferredGameDisplayDeviceName), ""),
            bounds,
            config.ReadBool(nameof(RatConfig.UseCustomGameResolution), false),
            config.ReadInt(nameof(RatConfig.CustomGameWidth), defaultWidth),
            config.ReadInt(nameof(RatConfig.CustomGameHeight), defaultHeight),
            config.ReadBool(nameof(RatConfig.UseCustomDisplayScale), false),
            config.ReadFloat(nameof(RatConfig.CustomDisplayScale), (float)defaultScale)
        );
        return true;
    }

    internal static void Write(SimpleConfig config, GameDisplayPreferences preferences)
    {
        config.Section = SectionName;
        config.WriteString(nameof(RatConfig.PreferredGameDisplayId), preferences.PreferredStableId);
        config.WriteString(nameof(RatConfig.PreferredGameDisplayDeviceName), preferences.PreferredDeviceName);
        config.WriteInt(nameof(RatConfig.PreferredGameDisplayBoundsX), preferences.LastPhysicalBounds?.X ?? 0);
        config.WriteInt(nameof(RatConfig.PreferredGameDisplayBoundsY), preferences.LastPhysicalBounds?.Y ?? 0);
        config.WriteInt(nameof(RatConfig.PreferredGameDisplayBoundsWidth), preferences.LastPhysicalBounds?.Width ?? 0);
        config.WriteInt(
            nameof(RatConfig.PreferredGameDisplayBoundsHeight),
            preferences.LastPhysicalBounds?.Height ?? 0
        );
        config.WriteBool(nameof(RatConfig.UseCustomGameResolution), preferences.UseCustomGameResolution);
        config.WriteInt(nameof(RatConfig.CustomGameWidth), preferences.CustomGameWidth);
        config.WriteInt(nameof(RatConfig.CustomGameHeight), preferences.CustomGameHeight);
        config.WriteBool(nameof(RatConfig.UseCustomDisplayScale), preferences.UseCustomDisplayScale);
        config.WriteFloat(nameof(RatConfig.CustomDisplayScale), (float)preferences.CustomDisplayScale);
    }
}
