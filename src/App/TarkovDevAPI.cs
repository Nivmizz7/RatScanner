using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RatScanner.TarkovDev;
using Task = System.Threading.Tasks.Task;
using TTask = RatScanner.TarkovDev.Task;

namespace RatScanner;

/// <summary>
/// Fetches and caches bulk game data from tarkov.dev backends.
/// Catalog/tasks/hideout/crafts/barters use <c>json.tarkov.dev</c> GET documents
/// (no GraphQL schema generator). Prices ship with the items document (MediumTTL).
///
/// Regular and PvE maps use a slim GraphQL selection on <c>api.tarkov.dev</c> to avoid
/// the ~9MB json.tarkov.dev maps placement blob for id/name/normalizedName only. Seasonal
/// maps use the JSON fallback because the GraphQL GameMode enum does not support them.
/// Maps stay off the cold-start critical path (background + offline projected cache).
/// </summary>
public static class TarkovDevAPI
{
    private sealed class RateLimitedException : Exception
    {
        public TimeSpan? RetryAfter { get; }

        public RateLimitedException(TimeSpan? retryAfter, string message)
            : base(message)
        {
            RetryAfter = retryAfter;
        }
    }

    private const string JsonApiBase = "https://json.tarkov.dev";
    private const string GraphqlApiUrl = "https://api.tarkov.dev/graphql";

    // Keep field selection locked to the three app-facing Map properties.
    private const string SlimMapsQuery = """
        query RatScannerMaps($lang: LanguageCode, $gameMode: GameMode) {
          maps(lang: $lang, gameMode: $gameMode) {
            id
            name
            normalizedName
          }
        }
        """;

    private static readonly ConcurrentDictionary<string, (long expire, object response)> Cache = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlightRequests = new();
    private static readonly ConcurrentDictionary<string, long> BackoffUntil = new();

    internal static event EventHandler? ItemsCacheUpdated;

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new(
            new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            }
        );
        client.Timeout = TimeSpan.FromSeconds(60);
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"RatScanner/{RatConfig.Version}");
        }
        catch (Exception e)
        {
            Logger.LogWarning("Failed to set user-agent header; falling back to default RatScanner user-agent.", e);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RatScanner");
        }
        return client;
    }

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
        TypeNameHandling = TypeNameHandling.None,
    };

    internal static string GameModePath(GameMode mode) =>
        mode switch
        {
            GameMode.Regular => "regular",
            GameMode.Pve => "pve",
            GameMode.Seasonal => "pvp-season",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported game mode."),
        };

    internal static string? GraphqlGameMode(GameMode mode) =>
        mode switch
        {
            GameMode.Regular => "regular",
            GameMode.Pve => "pve",
            GameMode.Seasonal => null,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported game mode."),
        };

    private static string LocaleCode() => RatConfig.NameScan.Language.ToTarkovDevLocale();

    #region HTTP

    private static async Task<string> GetJsonString(string path)
    {
        string url = $"{JsonApiBase}/{path.TrimStart('/')}";
        using HttpResponseMessage response = await HttpClient.GetAsync(url).ConfigureAwait(false);
        return await ReadSuccessBodyAsync(response, url).ConfigureAwait(false);
    }

    private static async Task<string> PostGraphqlAsync(string query, object variables)
    {
        string payload = JsonConvert.SerializeObject(new { query, variables }, JsonSettings);
        using StringContent content = new(payload, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await HttpClient.PostAsync(GraphqlApiUrl, content).ConfigureAwait(false);
        return await ReadSuccessBodyAsync(response, GraphqlApiUrl).ConfigureAwait(false);
    }

    private static async Task<string> ReadSuccessBodyAsync(HttpResponseMessage response, string url)
    {
        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            string trimmed = TrimBody(responseBody);
            throw new RateLimitedException(
                GetRetryAfter(response),
                $"tarkov.dev rate limited (429) for {url}. Body: {trimmed}"
            );
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            string trimmed = TrimBody(responseBody);
            throw new HttpRequestException(
                $"tarkov.dev request failed ({(int)response.StatusCode} {response.ReasonPhrase}) for {url}. Body: {trimmed}",
                null,
                response.StatusCode
            );
        }

        return responseBody;
    }

    private static string TrimBody(string responseBody) =>
        responseBody.Length > 512 ? responseBody.Substring(0, 512) + "..." : responseBody;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta != null)
            return response.Headers.RetryAfter.Delta;
        if (response.Headers.RetryAfter?.Date != null)
        {
            TimeSpan delta = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        return null;
    }

    #endregion

    #region Cache plumbing

    private static bool TryLoadFromOfflineCache(string baseQueryKey, long ttl)
    {
        if (Cache.ContainsKey(baseQueryKey))
            return true;

        if (RatConfig.ReadFromCache(baseQueryKey, out string cachedResponse, out DateTimeOffset lastWriteUtc))
        {
            try
            {
                object? results = MaterializeCachedArray(baseQueryKey, cachedResponse);
                if (results == null)
                    return false;

                long time = DateTimeOffset.Now.ToUnixTimeSeconds();
                long expire = time - 1;
                if (ttl > 0 && lastWriteUtc != DateTimeOffset.MinValue)
                {
                    long ageSeconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - lastWriteUtc).TotalSeconds);
                    if (ageSeconds < ttl)
                        expire = time + (ttl - ageSeconds);
                }
                Cache[baseQueryKey] = (expire, results);
                int count = results is Array arr ? arr.Length : 0;
                Logger.LogInfo($"Loaded {count} items from offline cache for: \"{baseQueryKey}\"");
                return true;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Failed to load offline cache for: \"{baseQueryKey}\"", e);
            }
        }
        return false;
    }

    private static async Task QueueRequestInternal(
        string baseQueryKey,
        Func<Task<object>> fetchAndMaterialize,
        long ttl,
        bool isItems
    )
    {
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            Logger.LogInfo($"Fetching data for: \"{baseQueryKey}\"");

            object results = await fetchAndMaterialize().ConfigureAwait(false);

            long time = DateTimeOffset.Now.ToUnixTimeSeconds();
            Cache[baseQueryKey] = (time + ttl, results);
            BackoffUntil.TryRemove(baseQueryKey, out _);

            // Persist projected app models so offline restarts skip locale re-merge issues.
            try
            {
                string serialized = JsonConvert.SerializeObject(results, JsonSettings);
                RatConfig.WriteToCache(baseQueryKey, serialized);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Unable to persist API cache for: \"{baseQueryKey}\".", e);
            }

            if (isItems)
                NotifyItemsCacheUpdated();

            int count = results is Array arr ? arr.Length : 0;
            Logger.LogInfo(
                $"Completed fetch in {sw.ElapsedMilliseconds}ms: {count} total items for \"{baseQueryKey}\""
            );
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Failed request for: \"{baseQueryKey}\".", e);

            ApplyBackoff(
                baseQueryKey,
                e is RateLimitedException rateLimited ? rateLimited.RetryAfter : null,
                e is RateLimitedException
            );

            if (Cache.TryGetValue(baseQueryKey, out var existingCache))
            {
                long time = DateTimeOffset.Now.ToUnixTimeSeconds();
                long expire = time + RatConfig.SuperShortTTL;
                if (BackoffUntil.TryGetValue(baseQueryKey, out long until))
                    expire = Math.Max(expire, until);
                Cache[baseQueryKey] = (expire, existingCache.response);
                Logger.LogInfo($"Extended cache TTL for: \"{baseQueryKey}\" to prevent rapid retries");
                return;
            }

            if (RatConfig.ReadFromCache(baseQueryKey, out string cachedResponse))
            {
                Logger.LogInfo($"Read from offline cache for: \"{baseQueryKey}\"");
                try
                {
                    object? recovered = MaterializeCachedArray(baseQueryKey, cachedResponse);
                    if (recovered != null)
                    {
                        long time = DateTimeOffset.Now.ToUnixTimeSeconds();
                        long expire = time + RatConfig.SuperShortTTL;
                        if (BackoffUntil.TryGetValue(baseQueryKey, out long until))
                            expire = Math.Max(expire, until);
                        Cache[baseQueryKey] = (expire, recovered);
                        if (isItems)
                            NotifyItemsCacheUpdated();
                        return;
                    }
                }
                catch (Exception cacheException)
                {
                    Logger.LogWarning($"Offline API cache is invalid for: \"{baseQueryKey}\".", cacheException);
                }
            }

            if (!Cache.ContainsKey(baseQueryKey))
                Logger.LogWarning($"No API data is available for: \"{baseQueryKey}\".");
        }
    }

    private static object? MaterializeCachedArray(string baseQueryKey, string cachedResponse)
    {
        // Projected domain arrays (new cache format).
        if (baseQueryKey.StartsWith("items_", StringComparison.Ordinal))
            return JsonConvert.DeserializeObject<Item[]>(cachedResponse, JsonSettings);
        if (baseQueryKey.StartsWith("tasks_", StringComparison.Ordinal))
            return JsonConvert.DeserializeObject<TTask[]>(cachedResponse, JsonSettings);
        if (baseQueryKey.StartsWith("hideout_", StringComparison.Ordinal))
            return JsonConvert.DeserializeObject<HideoutStation[]>(cachedResponse, JsonSettings);
        if (baseQueryKey.StartsWith("maps_", StringComparison.Ordinal))
            return JsonConvert.DeserializeObject<Map[]>(cachedResponse, JsonSettings);
        if (baseQueryKey.StartsWith("crafts_", StringComparison.Ordinal))
        {
            Craft[]? crafts = JsonConvert.DeserializeObject<Craft[]>(cachedResponse, JsonSettings);
            if (crafts != null)
                RebuildCraftIndex(crafts);
            return crafts;
        }
        if (baseQueryKey.StartsWith("barters_", StringComparison.Ordinal))
        {
            Barter[]? barters = JsonConvert.DeserializeObject<Barter[]>(cachedResponse, JsonSettings);
            if (barters != null)
                RebuildBarterIndex(barters);
            return barters;
        }
        return null;
    }

    private static void NotifyItemsCacheUpdated()
    {
        try
        {
            ItemsCacheUpdated?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception e)
        {
            Logger.LogWarning("An item-cache update listener failed.", e);
        }
    }

    private static Task QueueRequest(
        string baseQueryKey,
        Func<Task<object>> fetchAndMaterialize,
        long ttl,
        bool isItems = false
    )
    {
        if (InFlightRequests.TryGetValue(baseQueryKey, out Lazy<Task>? existingLazy))
            return existingLazy.Value;
        if (IsInBackoff(baseQueryKey))
            return Task.CompletedTask;

        Lazy<Task> newLazy = new(
            () => QueueRequestInternal(baseQueryKey, fetchAndMaterialize, ttl, isItems),
            LazyThreadSafetyMode.ExecutionAndPublication
        );
        Lazy<Task> lazy = InFlightRequests.GetOrAdd(baseQueryKey, newLazy);
        Task task = lazy.Value;
        if (lazy == newLazy)
        {
            _ = task.ContinueWith(
                _ => InFlightRequests.TryRemove(baseQueryKey, out Lazy<Task>? _),
                TaskScheduler.Default
            );
        }
        return task;
    }

    private static T[] GetCached<T>(
        string baseQueryKey,
        Func<Task<object>> fetchAndMaterialize,
        long ttl,
        bool isItems = false
    )
        where T : class
    {
        try
        {
            if (!Cache.TryGetValue(baseQueryKey, out (long expire, object response) value))
            {
                if (IsInBackoff(baseQueryKey) || InFlightRequests.ContainsKey(baseQueryKey))
                    return Array.Empty<T>();

                Logger.LogInfo($"Cache miss for: \"{baseQueryKey}\", queuing fetch.");
                try
                {
                    _ = QueueRequest(baseQueryKey, fetchAndMaterialize, ttl, isItems);
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Failed to queue request for: \"{baseQueryKey}\", returning empty.", e);
                }
                return Array.Empty<T>();
            }

            long time = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (time > value.expire && !IsInBackoff(baseQueryKey) && !InFlightRequests.ContainsKey(baseQueryKey))
                _ = QueueRequest(baseQueryKey, fetchAndMaterialize, ttl, isItems);

            return (T[])value.response;
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Failed to get cached data for: \"{baseQueryKey}\", returning empty.", e);
            return Array.Empty<T>();
        }
    }

    private static bool IsInBackoff(string baseQueryKey)
    {
        long time = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (BackoffUntil.TryGetValue(baseQueryKey, out long until))
        {
            if (until > time)
                return true;
            BackoffUntil.TryRemove(baseQueryKey, out _);
        }
        return false;
    }

    private static void ApplyBackoff(string baseQueryKey, TimeSpan? retryAfter, bool rateLimited)
    {
        double delaySeconds = retryAfter?.TotalSeconds ?? RatConfig.SuperShortTTL;
        long backoffSeconds = (long)Math.Ceiling(Math.Max(delaySeconds, RatConfig.SuperShortTTL));
        long time = DateTimeOffset.Now.ToUnixTimeSeconds();
        long until = time + backoffSeconds;
        BackoffUntil[baseQueryKey] = until;

        if (Cache.TryGetValue(baseQueryKey, out var existingCache))
            Cache[baseQueryKey] = (Math.Max(existingCache.expire, until), existingCache.response);

        string reason = rateLimited ? "Rate limited" : "Request failed";
        Logger.LogInfo($"{reason} for: \"{baseQueryKey}\". Backing off for {backoffSeconds}s.");
    }

    #endregion

    #region Public API

    public static bool TryInitializeCacheFromOffline()
    {
        Logger.LogInfo("Attempting to load API cache from offline storage...");

        bool itemsLoaded = TryLoadFromOfflineCache(ItemsQueryKey(), RatConfig.MediumTTL);
        bool tasksLoaded = TryLoadFromOfflineCache(TasksQueryKey(), RatConfig.LongTTL);
        bool hideoutLoaded = TryLoadFromOfflineCache(HideoutStationsQueryKey(), RatConfig.LongTTL);
        // Maps are optional and off the cold-start path. Offline projected Map[] is tiny;
        // network refresh uses slim GraphQL with json blob fallback.
        bool mapsLoaded = TryLoadFromOfflineCache(MapsQueryKey(), RatConfig.LongTTL);
        bool craftsLoaded = TryLoadFromOfflineCache(CraftsQueryKey(), RatConfig.LongTTL);
        bool bartersLoaded = TryLoadFromOfflineCache(BartersQueryKey(), RatConfig.LongTTL);

        // Scanner-critical caches only. Maps are deferred; craft/barter chips degrade if missing.
        bool allLoaded = itemsLoaded && tasksLoaded && hideoutLoaded;
        if (allLoaded)
            Logger.LogInfo(
                $"Core API caches loaded from offline storage (maps: {mapsLoaded}, crafts: {craftsLoaded}, barters: {bartersLoaded})"
            );
        else
            Logger.LogWarning(
                $"Offline cache status - Items: {itemsLoaded}, Tasks: {tasksLoaded}, Hideout: {hideoutLoaded}, Maps: {mapsLoaded}, Crafts: {craftsLoaded}, Barters: {bartersLoaded}"
            );

        return allLoaded;
    }

    internal static bool AnyCacheExpired()
    {
        long time = DateTimeOffset.Now.ToUnixTimeSeconds();
        // Maps excluded: lazy/background only; must not force a network fetch for map overlay data.
        return IsCacheExpired(ItemsQueryKey(), time)
            || IsCacheExpired(TasksQueryKey(), time)
            || IsCacheExpired(HideoutStationsQueryKey(), time)
            || IsCacheExpired(CraftsQueryKey(), time)
            || IsCacheExpired(BartersQueryKey(), time);
    }

    private static bool IsCacheExpired(string baseQueryKey, long time)
    {
        if (!Cache.TryGetValue(baseQueryKey, out (long expire, object response) cached))
            return true;
        return time > cached.expire;
    }

    public static async Task InitializeCache()
    {
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        List<Task> coreRefreshes = [];

        if (IsCacheExpired(ItemsQueryKey(), now))
            coreRefreshes.Add(QueueRequest(ItemsQueryKey(), FetchItemsAsync, RatConfig.MediumTTL, isItems: true));
        if (IsCacheExpired(TasksQueryKey(), now))
            coreRefreshes.Add(QueueRequest(TasksQueryKey(), FetchTasksAsync, RatConfig.LongTTL));
        if (IsCacheExpired(HideoutStationsQueryKey(), now))
            coreRefreshes.Add(QueueRequest(HideoutStationsQueryKey(), FetchHideoutAsync, RatConfig.LongTTL));
        if (IsCacheExpired(CraftsQueryKey(), now))
            coreRefreshes.Add(QueueRequest(CraftsQueryKey(), FetchAndIndexCraftsAsync, RatConfig.LongTTL));
        if (IsCacheExpired(BartersQueryKey(), now))
            coreRefreshes.Add(QueueRequest(BartersQueryKey(), FetchAndIndexBartersAsync, RatConfig.LongTTL));

        if (coreRefreshes.Count > 0)
            await Task.WhenAll(coreRefreshes).ConfigureAwait(false);

        // Maps only matter for MapDataLoader / (disabled) map overlay. Never block cold start.
        if (IsCacheExpired(MapsQueryKey(), DateTimeOffset.Now.ToUnixTimeSeconds()))
            _ = QueueRequest(MapsQueryKey(), FetchMapsAsync, RatConfig.LongTTL);
    }

    public static Item[] GetItems(string locale, GameMode gameMode) =>
        GetCached<Item>(
            ItemsQueryKey(locale, gameMode),
            () => FetchItemsAsync(locale, gameMode),
            RatConfig.MediumTTL,
            isItems: true
        );

    public static Item[] GetItems() =>
        GetCached<Item>(ItemsQueryKey(), FetchItemsAsync, RatConfig.MediumTTL, isItems: true);

    public static bool TryGetCachedItems(out Item[] items)
    {
        if (Cache.TryGetValue(ItemsQueryKey(), out (long expire, object response) cached))
        {
            items = (Item[])cached.response;
            return true;
        }
        items = Array.Empty<Item>();
        return false;
    }

    public static TTask[] GetTasks(string locale, GameMode gameMode) =>
        GetCached<TTask>(TasksQueryKey(locale, gameMode), () => FetchTasksAsync(locale, gameMode), RatConfig.LongTTL);

    public static TTask[] GetTasks() => GetCached<TTask>(TasksQueryKey(), FetchTasksAsync, RatConfig.LongTTL);

    public static HideoutStation[] GetHideoutStations(string locale, GameMode gameMode) =>
        GetCached<HideoutStation>(
            HideoutStationsQueryKey(locale, gameMode),
            () => FetchHideoutAsync(locale, gameMode),
            RatConfig.LongTTL
        );

    public static HideoutStation[] GetHideoutStations() =>
        GetCached<HideoutStation>(HideoutStationsQueryKey(), FetchHideoutAsync, RatConfig.LongTTL);

    public static Map[] GetMaps(string locale, GameMode gameMode) =>
        GetCached<Map>(MapsQueryKey(locale, gameMode), () => FetchMapsAsync(locale, gameMode), RatConfig.LongTTL);

    public static Map[] GetMaps() => GetCached<Map>(MapsQueryKey(), FetchMapsAsync, RatConfig.LongTTL);

    public static Craft[] GetCrafts() =>
        GetCached<Craft>(CraftsQueryKey(), FetchAndIndexCraftsAsync, RatConfig.LongTTL);

    public static Barter[] GetBarters() =>
        GetCached<Barter>(BartersQueryKey(), FetchAndIndexBartersAsync, RatConfig.LongTTL);

    /// <summary>True if this item is a product of any hideout craft (output is always FIR in-game).</summary>
    public static bool IsCraftProduct(string? itemId) =>
        !string.IsNullOrEmpty(itemId) && CraftProductIds.Contains(itemId);

    /// <summary>True if any trader barter offers this item.</summary>
    public static bool IsBarterProduct(string? itemId) =>
        !string.IsNullOrEmpty(itemId) && BarterProductIds.Contains(itemId);

    public static int CraftRecipeCount(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
            return 0;
        // Read both craft indexes under the same lock RebuildCraftIndex publishes them with,
        // so a concurrent rebuild cannot expose a mismatched product-id/count snapshot.
        lock (IndexLock)
            return CraftProductIds.Contains(itemId) ? CraftCounts.GetValueOrDefault(itemId) : 0;
    }

    public static int BarterOfferCount(string? itemId) =>
        string.IsNullOrEmpty(itemId) ? 0 : BarterCounts.GetValueOrDefault(itemId);

    #endregion

    #region Craft / barter indexes

    private static readonly object IndexLock = new();
    private static HashSet<string> CraftProductIds = new(StringComparer.Ordinal);
    private static HashSet<string> BarterProductIds = new(StringComparer.Ordinal);
    private static Dictionary<string, int> CraftCounts = new(StringComparer.Ordinal);
    private static Dictionary<string, int> BarterCounts = new(StringComparer.Ordinal);

    private static void RebuildCraftIndex(Craft[] crafts)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (Craft craft in crafts)
        {
            if (string.IsNullOrEmpty(craft.ProductItemId))
                continue;
            ids.Add(craft.ProductItemId);
            counts[craft.ProductItemId] = counts.GetValueOrDefault(craft.ProductItemId) + 1;
        }
        lock (IndexLock)
        {
            CraftProductIds = ids;
            CraftCounts = counts;
        }
    }

    private static void RebuildBarterIndex(Barter[] barters)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (Barter barter in barters)
        {
            if (string.IsNullOrEmpty(barter.OfferedItemId))
                continue;
            ids.Add(barter.OfferedItemId);
            counts[barter.OfferedItemId] = counts.GetValueOrDefault(barter.OfferedItemId) + 1;
        }
        lock (IndexLock)
        {
            BarterProductIds = ids;
            BarterCounts = counts;
        }
    }

    private static async Task<object> FetchAndIndexCraftsAsync()
    {
        Craft[] crafts = await FetchCraftsAsync(RatConfig.GameMode).ConfigureAwait(false);
        RebuildCraftIndex(crafts);
        return crafts;
    }

    private static async Task<object> FetchAndIndexBartersAsync()
    {
        Barter[] barters = await FetchBartersAsync(RatConfig.GameMode).ConfigureAwait(false);
        RebuildBarterIndex(barters);
        return barters;
    }

    #endregion

    #region Keys

    private static string ItemsQueryKey() => ItemsQueryKey(LocaleCode(), RatConfig.GameMode);

    private static string ItemsQueryKey(string locale, GameMode gameMode) => $"items_{locale}_{gameMode}";

    private static string TasksQueryKey() => TasksQueryKey(LocaleCode(), RatConfig.GameMode);

    // v2: task gate fields (level, prerequisites, trader requirements, objective optional
    // flags) now drive requirement classification; stale v1 projected caches must be ignored.
    private static string TasksQueryKey(string locale, GameMode gameMode) => $"tasks_v2_{locale}_{gameMode}";

    private static string HideoutStationsQueryKey() => HideoutStationsQueryKey(LocaleCode(), RatConfig.GameMode);

    private static string HideoutStationsQueryKey(string locale, GameMode gameMode) => $"hideout_{locale}_{gameMode}";

    private static string MapsQueryKey() => MapsQueryKey(LocaleCode(), RatConfig.GameMode);

    private static string MapsQueryKey(string locale, GameMode gameMode) => $"maps_{locale}_{gameMode}";

    private static string CraftsQueryKey() => $"crafts_{RatConfig.GameMode}";

    private static string BartersQueryKey() => $"barters_{RatConfig.GameMode}";

    #endregion

    #region Fetch + project

    private static Task<object> FetchItemsAsync() => FetchItemsAsync(LocaleCode(), RatConfig.GameMode);

    private static async Task<object> FetchItemsAsync(string locale, GameMode gameMode)
    {
        string mode = GameModePath(gameMode);
        Task<string> itemsTask = GetJsonString($"{mode}/items");
        Task<string> localeTask = GetJsonString($"{mode}/items_{locale}");
        Task<string> tradersTask = GetJsonString($"{mode}/traders");
        Task<string> tradersLocaleTask = GetJsonString($"{mode}/traders_{locale}");
        await Task.WhenAll(itemsTask, localeTask, tradersTask, tradersLocaleTask).ConfigureAwait(false);

        string itemsJson = await itemsTask.ConfigureAwait(false);
        string itemLocaleJson = await localeTask.ConfigureAwait(false);
        string tradersJson = await tradersTask.ConfigureAwait(false);
        string tradersLocaleJson = await tradersLocaleTask.ConfigureAwait(false);

        var itemsEnvelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<JsonApiModels.ItemsPayload>>(
            itemsJson,
            JsonSettings
        );
        Dictionary<string, string>? itemLocale = ParseLocaleMap(itemLocaleJson);
        Dictionary<string, JsonApiModels.RawTrader> traders = ParseTraderMap(tradersJson);
        Dictionary<string, string>? traderLocale = ParseLocaleMap(tradersLocaleJson);

        Dictionary<string, JsonApiModels.RawItem>? rawItems = itemsEnvelope?.Data?.Items;
        if (rawItems == null || rawItems.Count == 0)
            throw new InvalidOperationException("Items JSON contained no data");

        List<Item> projected = new(rawItems.Count);
        foreach (JsonApiModels.RawItem raw in rawItems.Values)
        {
            if (string.IsNullOrEmpty(raw.Id))
                continue;

            string? name = ResolveLocale(itemLocale, raw.Name) ?? raw.Name;
            string? shortName = ResolveLocale(itemLocale, raw.ShortName) ?? raw.ShortName;

            List<ItemSellPrice>? sellFor = null;
            if (raw.SellToTrader is { Count: > 0 })
            {
                sellFor = new List<ItemSellPrice>(raw.SellToTrader.Count);
                foreach (JsonApiModels.RawTraderPrice offer in raw.SellToTrader)
                {
                    if (string.IsNullOrEmpty(offer.Trader))
                        continue;
                    traders.TryGetValue(offer.Trader, out JsonApiModels.RawTrader? trader);
                    string? traderName =
                        ResolveLocale(traderLocale, trader?.Name) ?? trader?.NormalizedName ?? offer.Trader;
                    sellFor.Add(
                        new ItemSellPrice
                        {
                            PriceRub = offer.PriceRub ?? offer.Price,
                            Vendor = new TraderOffer
                            {
                                Name = traderName,
                                NormalizedName = trader?.NormalizedName,
                                Trader = new Trader { Id = offer.Trader, ImageLink = trader?.ImageLink },
                            },
                        }
                    );
                }
            }

            ItemProperties? props = null;
            if (raw.Properties != null)
            {
                props = new ItemProperties
                {
                    PropertiesType = raw.Properties.PropertiesType,
                    Caliber = raw.Properties.Caliber,
                    Damage = raw.Properties.Damage,
                    PenetrationPower = raw.Properties.PenetrationPower,
                    FragmentationChance = raw.Properties.FragmentationChance,
                };
            }

            projected.Add(
                new Item
                {
                    Id = raw.Id,
                    Name = name,
                    ShortName = shortName,
                    Updated = raw.Updated,
                    Width = raw.Width,
                    Height = raw.Height,
                    WikiLink = raw.WikiLink,
                    Link = raw.Link,
                    IconLink = raw.IconLink,
                    BaseImageLink = raw.BaseImageLink,
                    Avg24HPrice = raw.Avg24HPrice,
                    BackgroundColor = raw.BackgroundColor,
                    Types = raw.Types,
                    Properties = props,
                    SellFor = sellFor,
                }
            );
        }

        return projected.ToArray();
    }

    private static Task<object> FetchTasksAsync() => FetchTasksAsync(LocaleCode(), RatConfig.GameMode);

    private static async Task<object> FetchTasksAsync(string locale, GameMode gameMode)
    {
        string mode = GameModePath(gameMode);
        Task<string> tasksTask = GetJsonString($"{mode}/tasks");
        Task<string> localeTask = GetJsonString($"{mode}/tasks_{locale}");
        Task<string> tradersTask = GetJsonString($"{mode}/traders");
        await Task.WhenAll(tasksTask, localeTask, tradersTask).ConfigureAwait(false);

        string tasksJson = await tasksTask.ConfigureAwait(false);
        string taskLocaleJson = await localeTask.ConfigureAwait(false);
        string tradersJson = await tradersTask.ConfigureAwait(false);

        var envelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<JsonApiModels.TasksPayload>>(
            tasksJson,
            JsonSettings
        );
        Dictionary<string, string>? taskLocale = ParseLocaleMap(taskLocaleJson);
        Dictionary<string, JsonApiModels.RawTrader> traders = ParseTraderMap(tradersJson);

        Dictionary<string, JsonApiModels.RawTask>? rawTasks = envelope?.Data?.Tasks;
        if (rawTasks == null || rawTasks.Count == 0)
            throw new InvalidOperationException("Tasks JSON contained no data");

        List<TTask> projected = new(rawTasks.Count);
        foreach (JsonApiModels.RawTask raw in rawTasks.Values)
        {
            if (string.IsNullOrEmpty(raw.Id))
                continue;

            List<TaskObjective>? objectives = null;
            if (raw.Objectives is { Count: > 0 })
            {
                objectives = new List<TaskObjective>(raw.Objectives.Count);
                foreach (JObject o in raw.Objectives)
                {
                    string? type = o.Value<string>("type");
                    if (type is not ("giveItem" or "plantItem" or "mark" or "buildWeapon" or "findItem"))
                        continue;

                    List<string>? itemIds = null;
                    JToken? itemsTok = o["items"];
                    if (itemsTok is JArray arr)
                    {
                        itemIds = arr.Select(t =>
                                t.Type == JTokenType.String ? t.Value<string>() : t["id"]?.Value<string>()
                            )
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Cast<string>()
                            .ToList();
                    }

                    string? markerItem = o.Value<string>("markerItem");
                    string? buildItem = o.Value<string>("item");

                    objectives.Add(
                        new TaskObjective
                        {
                            Id = o.Value<string>("id"),
                            Type = type,
                            Description =
                                ResolveLocale(taskLocale, o.Value<string>("description"))
                                ?? o.Value<string>("description"),
                            Count = o.Value<int?>("count") ?? 1,
                            FoundInRaid = o.Value<bool?>("foundInRaid") ?? false,
                            Optional = o.Value<bool?>("optional") ?? false,
                            ItemIds = itemIds,
                            MarkerItemId = markerItem,
                            BuildItemId = buildItem,
                        }
                    );
                }
            }

            string? traderImage = null;
            if (
                !string.IsNullOrEmpty(raw.TraderId)
                && traders.TryGetValue(raw.TraderId, out JsonApiModels.RawTrader? trader)
            )
                traderImage = trader.ImageLink;

            List<TaskPrerequisite>? prerequisites = null;
            if (raw.TaskRequirements is { Count: > 0 })
            {
                prerequisites = new List<TaskPrerequisite>(raw.TaskRequirements.Count);
                foreach (JObject r in raw.TaskRequirements)
                {
                    // Upstream emits either a bare task id string or an object with `id`.
                    JToken? taskTok = r["task"];
                    string? taskId =
                        taskTok?.Type == JTokenType.String ? taskTok.Value<string>() : taskTok?["id"]?.Value<string>();
                    if (string.IsNullOrEmpty(taskId))
                        continue;
                    string[] statuses = r["status"] is JArray statusArr
                        ? statusArr.Values<string>().Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToArray()
                        : [];
                    prerequisites.Add(new TaskPrerequisite { TaskId = taskId, Statuses = statuses });
                }
                if (prerequisites.Count == 0)
                    prerequisites = null;
            }

            List<TaskTraderRequirement>? traderRequirements = null;
            if (raw.TraderRequirements is { Count: > 0 })
            {
                traderRequirements = raw
                    .TraderRequirements.Select(r => new TaskTraderRequirement
                    {
                        RequirementType = r.RequirementType,
                        CompareMethod = r.CompareMethod,
                        Value = r.Value,
                        TraderId = r.Trader,
                    })
                    .ToList();
            }

            bool hasUnmodeled =
                raw.OtherRequirements is { Count: > 0 }
                || raw.AvailableDelaySecondsMax is > 0
                || raw.LightkeeperRequired == true;

            projected.Add(
                new TTask
                {
                    Id = raw.Id,
                    Name = ResolveLocale(taskLocale, raw.Name) ?? raw.Name,
                    WikiLink = raw.WikiLink,
                    TaskImageLink = raw.TaskImageLink,
                    KappaRequired = raw.KappaRequired,
                    TraderImageLink = traderImage,
                    Objectives = objectives,
                    MinPlayerLevel = raw.MinPlayerLevel,
                    FactionName = raw.FactionName,
                    TaskRequirements = prerequisites,
                    TraderRequirements = traderRequirements,
                    HasUnmodeledRequirements = hasUnmodeled,
                }
            );
        }

        return projected.ToArray();
    }

    private static Task<object> FetchHideoutAsync() => FetchHideoutAsync(LocaleCode(), RatConfig.GameMode);

    private static async Task<object> FetchHideoutAsync(string locale, GameMode gameMode)
    {
        string mode = GameModePath(gameMode);
        Task<string> dataTask = GetJsonString($"{mode}/hideout");
        Task<string> localeTask = GetJsonString($"{mode}/hideout_{locale}");
        await Task.WhenAll(dataTask, localeTask).ConfigureAwait(false);

        string hideoutJson = await dataTask.ConfigureAwait(false);
        string hideoutLocaleJson = await localeTask.ConfigureAwait(false);

        var envelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<
            Dictionary<string, JsonApiModels.RawHideoutStation>
        >>(hideoutJson, JsonSettings);
        Dictionary<string, string>? localeMap = ParseLocaleMap(hideoutLocaleJson);
        Dictionary<string, JsonApiModels.RawHideoutStation>? rawStations = envelope?.Data;
        if (rawStations == null || rawStations.Count == 0)
            throw new InvalidOperationException("Hideout JSON contained no data");

        List<HideoutStation> projected = new(rawStations.Count);
        foreach (JsonApiModels.RawHideoutStation raw in rawStations.Values)
        {
            if (string.IsNullOrEmpty(raw.Id))
                continue;

            List<HideoutStationLevel>? levels = null;
            if (raw.Levels is { Count: > 0 })
            {
                levels = new List<HideoutStationLevel>(raw.Levels.Count);
                foreach (JsonApiModels.RawHideoutLevel level in raw.Levels)
                {
                    if (string.IsNullOrEmpty(level.Id))
                        continue;
                    List<RequirementItem>? reqs = null;
                    if (level.ItemRequirements is { Count: > 0 })
                    {
                        reqs = level
                            .ItemRequirements.Where(r => !string.IsNullOrEmpty(r.Item))
                            .Select(r => new RequirementItem
                            {
                                Id = r.Id,
                                Count = r.Count,
                                ItemId = r.Item,
                                FoundInRaid = r.Attributes?.FoundInRaid ?? false,
                            })
                            .ToList();
                    }
                    levels.Add(new HideoutStationLevel { Id = level.Id, ItemRequirements = reqs });
                }
            }

            projected.Add(
                new HideoutStation
                {
                    Id = raw.Id,
                    Name = ResolveLocale(localeMap, raw.Name) ?? raw.Name,
                    Levels = levels,
                }
            );
        }

        return projected.ToArray();
    }

    private static Task<object> FetchMapsAsync() => FetchMapsAsync(LocaleCode(), RatConfig.GameMode);

    private static async Task<object> FetchMapsAsync(string locale, GameMode gameMode)
    {
        // Prefer slim GraphQL (~1.5KB for 16 maps) when its GameMode enum supports the mode.
        string? graphqlGameMode = GraphqlGameMode(gameMode);
        if (graphqlGameMode is not null)
        {
            try
            {
                string body = await PostGraphqlAsync(SlimMapsQuery, new { lang = locale, gameMode = graphqlGameMode })
                    .ConfigureAwait(false);

                Map[] projected = ProjectMapsFromGraphql(body);
                if (projected.Length > 0)
                    return projected;

                Logger.LogWarning(
                    "Slim maps GraphQL returned no maps; falling back to json.tarkov.dev stream extract."
                );
            }
            catch (Exception e)
            {
                Logger.LogWarning("Slim maps GraphQL failed; falling back to json.tarkov.dev stream extract.", e);
            }
        }

        return await FetchMapsFromJsonBlobAsync(locale, gameMode).ConfigureAwait(false);
    }

    private static async Task<object> FetchMapsFromJsonBlobAsync(string locale, GameMode gameMode)
    {
        string mode = GameModePath(gameMode);
        Task<string> dataTask = GetJsonString($"{mode}/maps");
        Task<string> localeTask = GetJsonString($"{mode}/maps_{locale}");
        await Task.WhenAll(dataTask, localeTask).ConfigureAwait(false);

        string mapsJson = await dataTask.ConfigureAwait(false);
        string mapsLocaleJson = await localeTask.ConfigureAwait(false);

        Dictionary<string, string>? localeMap = ParseLocaleMap(mapsLocaleJson);
        Dictionary<string, JsonApiModels.RawMap>? rawMaps = ExtractMapsDictionary(mapsJson);
        if (rawMaps == null || rawMaps.Count == 0)
            throw new InvalidOperationException("Maps JSON contained no data");

        List<Map> projected = new(rawMaps.Count);
        foreach (JsonApiModels.RawMap raw in rawMaps.Values)
        {
            if (string.IsNullOrEmpty(raw.Id))
                continue;
            projected.Add(
                new Map
                {
                    Id = raw.Id,
                    Name = ResolveLocale(localeMap, raw.Name) ?? raw.Name,
                    NormalizedName = raw.NormalizedName,
                }
            );
        }

        return projected.ToArray();
    }

    /// <summary>
    /// Projects the slim maps GraphQL payload into app <see cref="Map"/> models.
    /// </summary>
    internal static Map[] ProjectMapsFromGraphql(string json)
    {
        JObject root = JObject.Parse(json);
        if (root["errors"] is JArray { Count: > 0 } errors)
            throw new InvalidOperationException($"maps GraphQL errors: {errors.First}");

        JArray? maps = root["data"]?["maps"] as JArray;
        if (maps == null || maps.Count == 0)
            return Array.Empty<Map>();

        List<Map> projected = new(maps.Count);
        foreach (JToken token in maps)
        {
            string? id = token.Value<string>("id");
            if (string.IsNullOrEmpty(id))
                continue;
            projected.Add(
                new Map
                {
                    Id = id,
                    Name = token.Value<string>("name"),
                    NormalizedName = token.Value<string>("normalizedName"),
                }
            );
        }
        return projected.ToArray();
    }

    /// <summary>
    /// Walks the JSON token stream until <c>data.maps</c> and deserializes only that object graph,
    /// so sibling keys (mobs, loot containers, …) are skipped without materializing into dictionaries.
    /// Used as fallback when GraphQL is unavailable.
    /// </summary>
    internal static Dictionary<string, JsonApiModels.RawMap>? ExtractMapsDictionary(string json)
    {
        using StringReader stringReader = new(json);
        using JsonTextReader reader = new(stringReader);
        JsonSerializer serializer = JsonSerializer.Create(JsonSettings);

        while (reader.Read())
        {
            if (
                reader.TokenType != JsonToken.PropertyName
                || !string.Equals(reader.Value as string, "maps", StringComparison.Ordinal)
            )
                continue;

            // Require depth 2: { "data": { "maps": { … } } }
            if (reader.Depth != 2)
                continue;

            if (!reader.Read())
                return null;

            if (reader.TokenType == JsonToken.Null)
                return null;

            return serializer.Deserialize<Dictionary<string, JsonApiModels.RawMap>>(reader);
        }

        return null;
    }

    private static Dictionary<string, string>? ParseLocaleMap(string json)
    {
        var envelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<Dictionary<string, string>>>(
            json,
            JsonSettings
        );
        return envelope?.Data;
    }

    private static Dictionary<string, JsonApiModels.RawTrader> ParseTraderMap(string json)
    {
        var envelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<
            Dictionary<string, JsonApiModels.RawTrader>
        >>(json, JsonSettings);
        return envelope?.Data ?? new Dictionary<string, JsonApiModels.RawTrader>();
    }

    private static string? ResolveLocale(Dictionary<string, string>? map, string? key)
    {
        if (map == null || string.IsNullOrEmpty(key))
            return null;
        return map.TryGetValue(key, out string? value) ? value : null;
    }

    #endregion

    #region Craft / barter document fetch

    /// <summary>Loads craft recipes used to index craftable product item ids.</summary>
    internal static Task<Craft[]> FetchCraftsAsync(GameMode gameMode = GameMode.Regular) =>
        FetchJObjectListAsync(
            $"{GameModePath(gameMode)}/crafts",
            "Crafts",
            o => new Craft
            {
                Id = o.Value<string>("id") ?? string.Empty,
                StationId = o.Value<string>("station"),
                Level = o.Value<int?>("level") ?? 0,
                DurationSeconds = o.Value<int?>("duration") ?? 0,
                ProductItemId = o["productItem"]?["item"]?.Value<string>(),
                ProductCount = o["productItem"]?["count"]?.Value<int>() ?? 1,
                RequiredItems = (o["requiredItems"] as JArray)
                    ?.Select(r => new CraftIngredient
                    {
                        ItemId = r["item"]?.Value<string>(),
                        Count = r["count"]?.Value<int>() ?? 0,
                    })
                    .ToList(),
            }
        );

    private static async Task<T[]> FetchJObjectListAsync<T>(string path, string name, Func<JObject, T> map)
    {
        string json = await GetJsonString(path).ConfigureAwait(false);
        var envelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<List<JObject>>>(json, JsonSettings);
        List<JObject>? list = envelope?.Data;
        // A missing data envelope indicates a transient/malformed response; throw so the request
        // layer retries with backoff instead of caching emptiness. An explicit empty list is a
        // valid answer and still yields Array.Empty below.
        if (list == null)
            throw new InvalidOperationException($"{name} response contained no data envelope.");

        return list.Select(map).ToArray();
    }

    /// <summary>Loads barters used to index barterable product item ids.</summary>
    internal static Task<Barter[]> FetchBartersAsync(GameMode gameMode = GameMode.Regular) =>
        FetchJObjectListAsync(
            $"{GameModePath(gameMode)}/barters",
            "Barters",
            o => new Barter
            {
                Id = o.Value<string>("id") ?? string.Empty,
                TraderId = o.Value<string>("trader"),
                MinTraderLevel = o.Value<int?>("minTraderLevel") ?? 0,
                OfferedItemId = o["offeredItem"]?["item"]?.Value<string>(),
                OfferedCount = o["offeredItem"]?["count"]?.Value<int>() ?? 1,
                RequiredItems = (o["requiredItems"] as JArray)
                    ?.Select(r => new CraftIngredient
                    {
                        ItemId = r["item"]?.Value<string>(),
                        Count = r["count"]?.Value<int>() ?? 0,
                    })
                    .ToList(),
            }
        );

    #endregion
}
