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
/// Fetches and caches bulk game data from <c>json.tarkov.dev</c> (static JSON API).
/// GraphQL is intentionally not used for catalog/tasks/hideout — field selection
/// was less valuable than dropping the generator surface and heavy POST query composition.
///
/// Prices (avg24h / trader sells) ship with the items document and share MediumTTL.
/// Crafts/barters load with LongTTL into product indexes for acquisition hints.
/// Maps are optional (not on the cold-start critical path) because
/// <c>/maps</c> is a multi-megabyte blob mostly of mob/loot placement data.
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"RatScanner-TT/{RatConfig.Version}");
        }
        catch (Exception e)
        {
            Logger.LogWarning(
                "Failed to set user-agent header; falling back to default RatScanner-TT user-agent.",
                e
            );
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RatScanner-TT");
        }
        return client;
    }

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
        TypeNameHandling = TypeNameHandling.None,
    };

    private static string GameModePath(GameMode mode) =>
        mode == GameMode.Pve ? "pve" : "regular";

    private static string LocaleCode() => RatConfig.NameScan.Language.ToTarkovDevLocale();

    #region HTTP

    private static async Task<string> GetJsonString(string path)
    {
        string url = $"{JsonApiBase}/{path.TrimStart('/')}";
        using HttpResponseMessage response = await HttpClient.GetAsync(url).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            string trimmed = TrimBody(responseBody);
            throw new RateLimitedException(
                GetRetryAfter(response),
                $"json.tarkov.dev rate limited (429). Body: {trimmed}"
            );
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            string trimmed = TrimBody(responseBody);
            throw new Exception(
                $"json.tarkov.dev request failed ({(int)response.StatusCode} {response.ReasonPhrase}) for {url}. Body: {trimmed}"
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

    private static bool TryLoadFromOfflineCache(string baseQueryKey, long ttl, Func<string, object?> materialize)
    {
        if (Cache.ContainsKey(baseQueryKey))
            return true;

        if (RatConfig.ReadFromCache(baseQueryKey, out string cachedResponse, out DateTimeOffset lastWriteUtc))
        {
            try
            {
                object? results = materialize(cachedResponse);
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

    private static T[] GetCached<T>(string baseQueryKey, Func<Task<object>> fetchAndMaterialize, long ttl, bool isItems = false)
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

        bool itemsLoaded = TryLoadFromOfflineCache(
            ItemsQueryKey(),
            RatConfig.MediumTTL,
            json => JsonConvert.DeserializeObject<Item[]>(json, JsonSettings)
        );
        bool tasksLoaded = TryLoadFromOfflineCache(
            TasksQueryKey(),
            RatConfig.LongTTL,
            json => JsonConvert.DeserializeObject<TTask[]>(json, JsonSettings)
        );
        bool hideoutLoaded = TryLoadFromOfflineCache(
            HideoutStationsQueryKey(),
            RatConfig.LongTTL,
            json => JsonConvert.DeserializeObject<HideoutStation[]>(json, JsonSettings)
        );
        // Maps are optional: projected offline files are tiny, but first network fetch
        // of json.tarkov.dev/.../maps is ~9MB of mostly unused placement data.
        bool mapsLoaded = TryLoadFromOfflineCache(
            MapsQueryKey(),
            RatConfig.LongTTL,
            json => JsonConvert.DeserializeObject<Map[]>(json, JsonSettings)
        );
        bool craftsLoaded = TryLoadFromOfflineCache(
            CraftsQueryKey(),
            RatConfig.LongTTL,
            json =>
            {
                Craft[]? crafts = JsonConvert.DeserializeObject<Craft[]>(json, JsonSettings);
                if (crafts != null)
                    RebuildCraftIndex(crafts);
                return crafts;
            }
        );
        bool bartersLoaded = TryLoadFromOfflineCache(
            BartersQueryKey(),
            RatConfig.LongTTL,
            json =>
            {
                Barter[]? barters = JsonConvert.DeserializeObject<Barter[]>(json, JsonSettings);
                if (barters != null)
                    RebuildBarterIndex(barters);
                return barters;
            }
        );

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
        // Maps excluded: they are lazy/background and must not force a 9MB fetch on every stale-cache cycle.
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
        GetCached<Item>(ItemsQueryKey(locale, gameMode), () => FetchItemsAsync(locale, gameMode), RatConfig.MediumTTL, isItems: true);

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

    public static int CraftRecipeCount(string? itemId) =>
        string.IsNullOrEmpty(itemId) ? 0 : CraftProductIds.Contains(itemId) ? CraftCounts.GetValueOrDefault(itemId) : 0;

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

    private static string TasksQueryKey(string locale, GameMode gameMode) => $"tasks_{locale}_{gameMode}";

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

        var itemsEnvelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<JsonApiModels.ItemsPayload>>(
            itemsTask.Result,
            JsonSettings
        );
        Dictionary<string, string>? itemLocale = ParseLocaleMap(localeTask.Result);
        Dictionary<string, JsonApiModels.RawTrader> traders = ParseTraderMap(tradersTask.Result);
        Dictionary<string, string>? traderLocale = ParseLocaleMap(tradersLocaleTask.Result);

        Dictionary<string, JsonApiModels.RawItem>? rawItems = itemsEnvelope?.Data?.Items;
        if (rawItems == null || rawItems.Count == 0)
            throw new Exception("Items JSON contained no data");

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
                        ResolveLocale(traderLocale, trader?.Name)
                        ?? trader?.NormalizedName
                        ?? offer.Trader;
                    sellFor.Add(
                        new ItemSellPrice
                        {
                            PriceRub = offer.PriceRub ?? offer.Price,
                            Vendor = new TraderOffer
                            {
                                Name = traderName,
                                NormalizedName = trader?.NormalizedName,
                                Trader = new Trader
                                {
                                    Id = offer.Trader,
                                    ImageLink = trader?.ImageLink,
                                },
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

        var envelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<JsonApiModels.TasksPayload>>(
            tasksTask.Result,
            JsonSettings
        );
        Dictionary<string, string>? taskLocale = ParseLocaleMap(localeTask.Result);
        Dictionary<string, JsonApiModels.RawTrader> traders = ParseTraderMap(tradersTask.Result);

        Dictionary<string, JsonApiModels.RawTask>? rawTasks = envelope?.Data?.Tasks;
        if (rawTasks == null || rawTasks.Count == 0)
            throw new Exception("Tasks JSON contained no data");

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
                        itemIds = arr.Select(t => t.Type == JTokenType.String ? t.Value<string>() : t["id"]?.Value<string>())
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
                            Description = ResolveLocale(taskLocale, o.Value<string>("description"))
                                ?? o.Value<string>("description"),
                            Count = o.Value<int?>("count") ?? 1,
                            FoundInRaid = o.Value<bool?>("foundInRaid") ?? false,
                            ItemIds = itemIds,
                            MarkerItemId = markerItem,
                            BuildItemId = buildItem,
                        }
                    );
                }
            }

            string? traderImage = null;
            if (!string.IsNullOrEmpty(raw.TraderId) && traders.TryGetValue(raw.TraderId, out JsonApiModels.RawTrader? trader))
                traderImage = trader.ImageLink;

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

        var envelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<Dictionary<string, JsonApiModels.RawHideoutStation>>>(
            dataTask.Result,
            JsonSettings
        );
        Dictionary<string, string>? localeMap = ParseLocaleMap(localeTask.Result);
        Dictionary<string, JsonApiModels.RawHideoutStation>? rawStations = envelope?.Data;
        if (rawStations == null || rawStations.Count == 0)
            throw new Exception("Hideout JSON contained no data");

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
        string mode = GameModePath(gameMode);
        // The /maps document is ~9MB raw (mostly mobs/loot). Stream-project only the maps map.
        Task<string> dataTask = GetJsonString($"{mode}/maps");
        Task<string> localeTask = GetJsonString($"{mode}/maps_{locale}");
        await Task.WhenAll(dataTask, localeTask).ConfigureAwait(false);

        Dictionary<string, string>? localeMap = ParseLocaleMap(localeTask.Result);
        Dictionary<string, JsonApiModels.RawMap>? rawMaps = ExtractMapsDictionary(dataTask.Result);
        if (rawMaps == null || rawMaps.Count == 0)
            throw new Exception("Maps JSON contained no data");

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
    /// Walks the JSON token stream until <c>data.maps</c> and deserializes only that object graph,
    /// so sibling keys (mobs, loot containers, …) are skipped without materializing into dictionaries.
    /// </summary>
    internal static Dictionary<string, JsonApiModels.RawMap>? ExtractMapsDictionary(string json)
    {
        using StringReader stringReader = new(json);
        using JsonTextReader reader = new(stringReader);
        JsonSerializer serializer = JsonSerializer.Create(JsonSettings);

        while (reader.Read())
        {
            if (reader.TokenType != JsonToken.PropertyName || !string.Equals(reader.Value as string, "maps", StringComparison.Ordinal))
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
        var envelope = JsonConvert.DeserializeObject<
            JsonApiModels.Envelope<Dictionary<string, JsonApiModels.RawTrader>>
        >(json, JsonSettings);
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
    internal static async Task<Craft[]> FetchCraftsAsync(GameMode gameMode = GameMode.Regular)
    {
        string json = await GetJsonString($"{GameModePath(gameMode)}/crafts").ConfigureAwait(false);
        var envelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<List<JObject>>>(json, JsonSettings);
        List<JObject>? list = envelope?.Data;
        if (list == null)
            return Array.Empty<Craft>();

        return list.Select(o => new Craft
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
            })
            .ToArray();
    }

    /// <summary>Loads barters used to index barterable product item ids.</summary>
    internal static async Task<Barter[]> FetchBartersAsync(GameMode gameMode = GameMode.Regular)
    {
        string json = await GetJsonString($"{GameModePath(gameMode)}/barters").ConfigureAwait(false);
        var envelope = JsonConvert.DeserializeObject<JsonApiModels.Envelope<List<JObject>>>(json, JsonSettings);
        List<JObject>? list = envelope?.Data;
        if (list == null)
            return Array.Empty<Barter>();

        return list.Select(o => new Barter
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
            })
            .ToArray();
    }

    #endregion
}
