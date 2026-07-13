using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TarkovDevMap = RatScanner.TarkovDev.Map;

namespace RatScanner;

public static class MapDataLoader
{
    private static Dictionary<string, InteractiveMapData.Map>? _mapsByIdCache;

    /// <summary>
    /// Loads and caches the maps.json data
    /// </summary>
    public static Dictionary<string, InteractiveMapData.Map>? GetMapsData()
    {
        if (_mapsByIdCache != null)
            return _mapsByIdCache;

        try
        {
            string mapsJsonPath = Path.Combine(RatConfig.Paths.Data, "maps.json");
            if (!File.Exists(mapsJsonPath))
            {
                // LogWarning: map overlay data is optional. LogError would call Environment.Exit.
                Logger.LogWarning($"maps.json not found at {mapsJsonPath}; interactive maps will be unavailable.");
                _mapsByIdCache = new();
                return _mapsByIdCache;
            }

            string json = File.ReadAllText(mapsJsonPath);
            var mapsData =
                JsonConvert.DeserializeObject<List<InteractiveMapData>>(json) ?? new List<InteractiveMapData>();

            // Build the map ID cache. Empty catalog = not ready yet; keep retryable until loaded.
            if (!TryBuildMapIdCache(mapsData, out Dictionary<string, InteractiveMapData.Map> cache))
                return cache;

            _mapsByIdCache = cache;
            Logger.LogInfo($"Loaded {_mapsByIdCache.Count} maps from maps.json");
            return _mapsByIdCache;
        }
        catch (Exception e)
        {
            // LogWarning: corrupt or unreadable map data must not terminate the process.
            Logger.LogWarning("Failed to load maps.json; interactive maps will be unavailable.", e);
            _mapsByIdCache = new();
            return _mapsByIdCache;
        }
    }

    /// <summary>
    /// Builds a dictionary mapping map IDs to their InteractiveMapData.
    /// Returns false when the Tarkov.dev map catalog is not loaded yet so callers do not pin an empty cache.
    /// </summary>
    private static bool TryBuildMapIdCache(
        List<InteractiveMapData> mapsData,
        out Dictionary<string, InteractiveMapData.Map> cache
    )
    {
        cache = new();
        TarkovDevMap[] tarkovDevMaps = TarkovDevAPI.GetMaps();
        // Maps are loaded lazily/background; empty catalog means "not ready", not "missing map".
        if (tarkovDevMaps.Length == 0)
        {
            Logger.LogInfo("Tarkov.dev map catalog not loaded yet; interactive map id matching deferred.");
            return false;
        }

        foreach (InteractiveMapData mapData in mapsData)
        {
            if (mapData.Maps == null)
                continue;

            foreach (InteractiveMapData.Map map in mapData.Maps)
            {
                if (string.IsNullOrEmpty(map.Key))
                    continue;
                if (map.Projection != "interactive")
                    continue;

                TarkovDevMap? tMap = tarkovDevMaps.FirstOrDefault(m => m.NormalizedName == mapData.NormalizedName);
                if (tMap == null || string.IsNullOrEmpty(tMap.Id))
                {
                    Logger.LogWarning($"No TarkovDev map match for normalized name: {mapData.NormalizedName}");
                    continue;
                }
                cache[tMap.Id] = map;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the dictionary mapping map IDs to InteractiveMapData
    /// </summary>
    public static Dictionary<string, InteractiveMapData.Map> GetMapsById()
    {
        if (_mapsByIdCache != null)
            return _mapsByIdCache;

        // Trigger loading if not already loaded
        GetMapsData();

        return _mapsByIdCache ?? new();
    }

    /// <summary>
    /// Gets the SVG URL for a given map by its ID or normalized name
    /// </summary>
    public static string? GetMapSvgUrl(string? mapId)
    {
        if (string.IsNullOrEmpty(mapId))
        {
            Logger.LogWarning($"GetMapSvgUrl called with null or empty mapId");
            return null;
        }

        Dictionary<string, InteractiveMapData.Map> mapsById = GetMapsById();

        if (!mapsById.TryGetValue(mapId, out InteractiveMapData.Map? mapData))
        {
            Logger.LogWarning($"No map data found for ID: {mapId}");
            return null;
        }

        return mapData?.SvgPath;
    }
}
