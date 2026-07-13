using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RatScanner.TarkovDev.GraphQL;
using Task = System.Threading.Tasks.Task;
using TTask = RatScanner.TarkovDev.GraphQL.Task;

namespace RatScanner;

public static class TarkovDevAPI
{
    private class ResponseData<T>
    {
        [JsonProperty("data")]
        public ResponseDataInner<T>? Data { get; set; }
    }

    private class ResponseDataInner<T>
    {
        [JsonProperty("data")]
        public T? Data { get; set; }
    }

    private sealed class RateLimitedException : Exception
    {
        public TimeSpan? RetryAfter { get; }

        public RateLimitedException(TimeSpan? retryAfter, string message)
            : base(message)
        {
            RetryAfter = retryAfter;
        }
    }

    const string ApiEndpoint = "https://api.tarkov.dev/graphql";

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
        client.Timeout = TimeSpan.FromSeconds(30);
        // Some upstreams reject requests without a user-agent.
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

    private static async Task<string> GetResponseString(string query)
    {
        Dictionary<string, string> body = new() { { "query", query } };
        using HttpResponseMessage response = await HttpClient.PostAsJsonAsync(ApiEndpoint, body).ConfigureAwait(false);

        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            string trimmed = responseBody;
            if (trimmed.Length > 512)
                trimmed = trimmed.Substring(0, 512) + "...";
            throw new RateLimitedException(
                GetRetryAfter(response),
                $"Tarkov.dev API rate limited (429). Body: {trimmed}"
            );
        }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            string trimmed = responseBody;
            if (trimmed.Length > 512)
                trimmed = trimmed.Substring(0, 512) + "...";
            throw new Exception(
                $"Tarkov.dev API request failed ({(int)response.StatusCode} {response.ReasonPhrase}). Body: {trimmed}"
            );
        }
        return responseBody;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta != null)
        {
            return response.Headers.RetryAfter.Delta;
        }
        if (response.Headers.RetryAfter?.Date != null)
        {
            TimeSpan delta = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        return null;
    }

    /// <summary>
    /// Tries to load data from offline cache
    /// </summary>
    /// <returns>True if cache was loaded successfully</returns>
    private static bool TryLoadFromOfflineCache<T>(string baseQueryKey, long ttl)
        where T : class
    {
        if (Cache.ContainsKey(baseQueryKey))
            return true;

        if (RatConfig.ReadFromCache(baseQueryKey, out string cachedResponse, out DateTimeOffset lastWriteUtc))
        {
            try
            {
                ResponseData<T[]>? neededResponse = JsonConvert.DeserializeObject<ResponseData<T[]>?>(
                    cachedResponse,
                    JsonSettings
                );
                if (neededResponse?.Data?.Data != null)
                {
                    long time = DateTimeOffset.Now.ToUnixTimeSeconds();
                    long expire = time - 1;
                    if (ttl > 0 && lastWriteUtc != DateTimeOffset.MinValue)
                    {
                        long ageSeconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - lastWriteUtc).TotalSeconds);
                        if (ageSeconds < ttl)
                        {
                            expire = time + (ttl - ageSeconds);
                        }
                    }
                    Cache[baseQueryKey] = (expire, neededResponse.Data.Data);
                    Logger.LogInfo(
                        $"Loaded {neededResponse.Data.Data.Length} items from offline cache for: \"{baseQueryKey}\""
                    );
                    return true;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Failed to load offline cache for: \"{baseQueryKey}\"", e);
            }
        }
        return false;
    }

    /// <summary>
    /// Fetches all data in a single request
    /// </summary>
    private static async Task QueueRequestInternal<T>(string baseQueryKey, Func<string> queryBuilder, long ttl)
        where T : class
    {
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            string query = queryBuilder();
            Logger.LogInfo($"Fetching data for: \"{baseQueryKey}\"");

            // Read raw response for caching
            string rawResponse = await GetResponseString(query).ConfigureAwait(false);

            // Parse the response
            ResponseData<T[]>? neededResponse = JsonConvert.DeserializeObject<ResponseData<T[]>>(
                rawResponse,
                JsonSettings
            );
            if (neededResponse?.Data?.Data == null)
                throw new Exception("Failed to deserialize response");

            T[] results = neededResponse.Data.Data;

            // Store results in cache
            long time = DateTimeOffset.Now.ToUnixTimeSeconds();
            Cache[baseQueryKey] = (time + ttl, results);
            BackoffUntil.TryRemove(baseQueryKey, out _);

            // Cache raw response for offline use
            try
            {
                RatConfig.WriteToCache(baseQueryKey, rawResponse);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Unable to persist API cache for: \"{baseQueryKey}\".", e);
            }

            NotifyItemsCacheUpdated<T>();

            Logger.LogInfo(
                $"Completed fetch in {sw.ElapsedMilliseconds}ms: {results.Length} total items for \"{baseQueryKey}\""
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

            // If we have existing cached data, extend its TTL to prevent rapid retries
            if (Cache.TryGetValue(baseQueryKey, out var existingCache))
            {
                long time = DateTimeOffset.Now.ToUnixTimeSeconds();
                long expire = time + RatConfig.SuperShortTTL;
                if (BackoffUntil.TryGetValue(baseQueryKey, out long until))
                {
                    expire = Math.Max(expire, until);
                }
                Cache[baseQueryKey] = (expire, existingCache.response);
                Logger.LogInfo($"Extended cache TTL for: \"{baseQueryKey}\" to prevent rapid retries");
                return;
            }

            // Try to load from offline cache
            if (RatConfig.ReadFromCache(baseQueryKey, out string cachedResponse))
            {
                Logger.LogInfo($"Read from offline cache for: \"{baseQueryKey}\"");
                try
                {
                    ResponseData<T[]>? neededResponse = JsonConvert.DeserializeObject<ResponseData<T[]>?>(
                        cachedResponse,
                        JsonSettings
                    );
                    if (neededResponse?.Data?.Data != null)
                    {
                        long time = DateTimeOffset.Now.ToUnixTimeSeconds();
                        long expire = time + RatConfig.SuperShortTTL;
                        if (BackoffUntil.TryGetValue(baseQueryKey, out long until))
                            expire = Math.Max(expire, until);
                        Cache[baseQueryKey] = (expire, neededResponse.Data.Data);
                        NotifyItemsCacheUpdated<T>();
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

    private static void NotifyItemsCacheUpdated<T>()
        where T : class
    {
        if (typeof(T) != typeof(Item))
            return;

        try
        {
            ItemsCacheUpdated?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception e)
        {
            Logger.LogWarning("An item-cache update listener failed.", e);
        }
    }

    private static Task QueueRequest<T>(string baseQueryKey, Func<string> queryBuilder, long ttl)
        where T : class
    {
        if (InFlightRequests.TryGetValue(baseQueryKey, out Lazy<Task>? existingLazy))
        {
            return existingLazy.Value;
        }
        if (IsInBackoff(baseQueryKey))
        {
            return Task.CompletedTask;
        }

        Lazy<Task> newLazy = new(() => QueueRequestInternal<T>(baseQueryKey, queryBuilder, ttl));
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

    private static T[] GetCached<T>(string baseQueryKey, Func<string> queryBuilder, long ttl)
        where T : class
    {
        try
        {
            if (!Cache.TryGetValue(baseQueryKey, out (long expire, object response) value))
            {
                if (IsInBackoff(baseQueryKey))
                {
                    return Array.Empty<T>();
                }
                if (InFlightRequests.ContainsKey(baseQueryKey))
                {
                    return Array.Empty<T>();
                }

                Logger.LogInfo($"Cache miss for: \"{baseQueryKey}\", queuing fetch.");
                try
                {
                    _ = QueueRequest<T>(baseQueryKey, queryBuilder, ttl);
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Failed to queue request for: \"{baseQueryKey}\", returning empty.", e);
                }
                return Array.Empty<T>();
            }

            // Queue request if cache is expired and no request is already pending
            long time = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (time > value.expire && !IsInBackoff(baseQueryKey) && !InFlightRequests.ContainsKey(baseQueryKey))
            {
                _ = QueueRequest<T>(baseQueryKey, queryBuilder, ttl);
            }

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
            {
                return true;
            }
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
        {
            Cache[baseQueryKey] = (Math.Max(existingCache.expire, until), existingCache.response);
        }
        string reason = rateLimited ? "Rate limited" : "Request failed";
        Logger.LogInfo($"{reason} for: \"{baseQueryKey}\". Backing off for {backoffSeconds}s.");
    }

    /// <summary>
    /// Initializes cache from offline storage first, then queues background refresh.
    /// Returns true if all caches were loaded from offline storage.
    /// </summary>
    public static bool TryInitializeCacheFromOffline()
    {
        Logger.LogInfo("Attempting to load API cache from offline storage...");

        bool itemsLoaded = TryLoadFromOfflineCache<Item>(ItemsQueryKey(), RatConfig.MediumTTL);
        bool tasksLoaded = TryLoadFromOfflineCache<TTask>(TasksQueryKey(), RatConfig.LongTTL);
        bool hideoutLoaded = TryLoadFromOfflineCache<HideoutStation>(HideoutStationsQueryKey(), RatConfig.LongTTL);
        bool mapsLoaded = TryLoadFromOfflineCache<Map>(MapsQueryKey(), RatConfig.LongTTL);

        bool allLoaded = itemsLoaded && tasksLoaded && hideoutLoaded && mapsLoaded;

        if (allLoaded)
        {
            Logger.LogInfo("All API caches loaded from offline storage");
        }
        else
        {
            Logger.LogWarning(
                $"Offline cache status - Items: {itemsLoaded}, Tasks: {tasksLoaded}, Hideout: {hideoutLoaded}, Maps: {mapsLoaded}"
            );
        }

        return allLoaded;
    }

    internal static bool AnyCacheExpired()
    {
        long time = DateTimeOffset.Now.ToUnixTimeSeconds();
        return IsCacheExpired(ItemsQueryKey(), time)
            || IsCacheExpired(TasksQueryKey(), time)
            || IsCacheExpired(HideoutStationsQueryKey(), time)
            || IsCacheExpired(MapsQueryKey(), time);
    }

    private static bool IsCacheExpired(string baseQueryKey, long time)
    {
        if (!Cache.TryGetValue(baseQueryKey, out (long expire, object response) cached))
        {
            return true;
        }
        return time > cached.expire;
    }

    /// <summary>
    /// Full cache initialization - waits for all requests to complete
    /// </summary>
    public static async Task InitializeCache()
    {
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        List<Task> refreshes = [];

        if (IsCacheExpired(ItemsQueryKey(), now))
            refreshes.Add(QueueRequest<Item>(ItemsQueryKey(), ItemsQuery, RatConfig.MediumTTL));
        if (IsCacheExpired(TasksQueryKey(), now))
            refreshes.Add(QueueRequest<TTask>(TasksQueryKey(), TasksQuery, RatConfig.LongTTL));
        if (IsCacheExpired(HideoutStationsQueryKey(), now))
            refreshes.Add(
                QueueRequest<HideoutStation>(HideoutStationsQueryKey(), HideoutStationsQuery, RatConfig.LongTTL)
            );
        if (IsCacheExpired(MapsQueryKey(), now))
            refreshes.Add(QueueRequest<Map>(MapsQueryKey(), MapsQuery, RatConfig.LongTTL));

        if (refreshes.Count > 0)
            await Task.WhenAll(refreshes).ConfigureAwait(false);
    }

    public static Item[] GetItems(LanguageCode language, GameMode gameMode) =>
        GetCached<Item>(ItemsQueryKey(language, gameMode), () => ItemsQuery(language, gameMode), RatConfig.MediumTTL);

    public static Item[] GetItems() => GetCached<Item>(ItemsQueryKey(), ItemsQuery, RatConfig.MediumTTL);

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

    public static TTask[] GetTasks(LanguageCode language, GameMode gameMode) =>
        GetCached<TTask>(TasksQueryKey(language, gameMode), () => TasksQuery(language, gameMode), RatConfig.LongTTL);

    public static TTask[] GetTasks() => GetCached<TTask>(TasksQueryKey(), TasksQuery, RatConfig.LongTTL);

    public static HideoutStation[] GetHideoutStations(LanguageCode language, GameMode gameMode) =>
        GetCached<HideoutStation>(
            HideoutStationsQueryKey(language, gameMode),
            () => HideoutStationsQuery(language, gameMode),
            RatConfig.LongTTL
        );

    public static HideoutStation[] GetHideoutStations() =>
        GetCached<HideoutStation>(HideoutStationsQueryKey(), HideoutStationsQuery, RatConfig.LongTTL);

    public static Map[] GetMaps(LanguageCode language, GameMode gameMode) =>
        GetCached<Map>(MapsQueryKey(language, gameMode), () => MapsQuery(language, gameMode), RatConfig.LongTTL);

    public static Map[] GetMaps() => GetCached<Map>(MapsQueryKey(), MapsQuery, RatConfig.LongTTL);

    #region Items Query

    private static string ItemsQueryKey() =>
        ItemsQueryKey(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);

    private static string ItemsQueryKey(LanguageCode language, GameMode gameMode) => $"items_{language}_{gameMode}";

    private static string ItemsQuery() => ItemsQuery(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);

    internal static string ItemsQuery(LanguageCode language, GameMode gameMode)
    {
        return new QueryQueryBuilder()
            .WithItems(
                new ItemQueryBuilder()
                    .WithId()
                    .WithName()
                    .WithShortName()
                    .WithUpdated()
                    .WithWidth()
                    .WithHeight()
                    .WithWikiLink()
                    .WithLink()
                    .WithIconLink()
                    .WithBaseImageLink()
                    .WithAvg24HPrice()
                    .WithBackgroundColor()
                    .WithTypes()
                    .WithProperties(
                        new ItemPropertiesQueryBuilder().WithItemPropertiesAmmoFragment(
                            new ItemPropertiesAmmoQueryBuilder()
                                .WithCaliber()
                                .WithDamage()
                                .WithPenetrationPower()
                                .WithFragmentationChance()
                        )
                    )
                    .WithSellFor(
                        new ItemPriceQueryBuilder()
                            .WithPriceRub()
                            .WithVendor(
                                new VendorQueryBuilder().WithTraderOfferFragment(
                                    new TraderOfferQueryBuilder()
                                        .WithName()
                                        .WithNormalizedName()
                                        .WithTrader(new TraderQueryBuilder().WithId().WithImageLink())
                                )
                            )
                    ),
                alias: "data",
                lang: language,
                gameMode: gameMode
            )
            .Build();
    }

    #endregion

    #region Tasks Query

    private static string TasksQueryKey() =>
        TasksQueryKey(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);

    private static string TasksQueryKey(LanguageCode language, GameMode gameMode) => $"tasks_{language}_{gameMode}";

    private static string TasksQuery() => TasksQuery(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);

    internal static string TasksQuery(LanguageCode language, GameMode gameMode)
    {
        return new QueryQueryBuilder()
            .WithTasks(
                new TaskQueryBuilder()
                    .WithId()
                    .WithName()
                    .WithWikiLink()
                    .WithTaskImageLink()
                    .WithKappaRequired()
                    .WithTrader(new TraderQueryBuilder().WithImageLink())
                    .WithObjectives(
                        new TaskObjectiveQueryBuilder()
                            .WithId()
                            .WithType()
                            .WithDescription()
                            .WithTaskObjectiveBasicFragment(new TaskObjectiveBasicQueryBuilder().WithAllScalarFields())
                            .WithTaskObjectiveBuildItemFragment(
                                new TaskObjectiveBuildItemQueryBuilder()
                                    .WithAllScalarFields()
                                    .WithItem(new ItemQueryBuilder().WithId())
                            )
                            .WithTaskObjectiveExperienceFragment(
                                new TaskObjectiveExperienceQueryBuilder().WithAllScalarFields()
                            )
                            .WithTaskObjectiveExtractFragment(
                                new TaskObjectiveExtractQueryBuilder().WithAllScalarFields()
                            )
                            .WithTaskObjectiveItemFragment(
                                new TaskObjectiveItemQueryBuilder()
                                    .WithAllScalarFields()
                                    .WithItems(new ItemQueryBuilder().WithId())
                            )
                            .WithTaskObjectiveMarkFragment(
                                new TaskObjectiveMarkQueryBuilder()
                                    .WithAllScalarFields()
                                    .WithMarkerItem(new ItemQueryBuilder().WithId())
                            )
                            .WithTaskObjectivePlayerLevelFragment(
                                new TaskObjectivePlayerLevelQueryBuilder().WithAllScalarFields()
                            )
                            .WithTaskObjectiveQuestItemFragment(
                                new TaskObjectiveQuestItemQueryBuilder().WithAllScalarFields()
                            )
                            .WithTaskObjectiveShootFragment(new TaskObjectiveShootQueryBuilder().WithAllScalarFields())
                            .WithTaskObjectiveSkillFragment(new TaskObjectiveSkillQueryBuilder().WithAllScalarFields())
                            .WithTaskObjectiveTaskStatusFragment(
                                new TaskObjectiveTaskStatusQueryBuilder().WithAllScalarFields()
                            )
                            .WithTaskObjectiveTraderLevelFragment(
                                new TaskObjectiveTraderLevelQueryBuilder().WithAllScalarFields()
                            )
                            .WithTaskObjectiveTraderStandingFragment(
                                new TaskObjectiveTraderStandingQueryBuilder().WithAllScalarFields()
                            )
                            .WithTaskObjectiveUseItemFragment(
                                new TaskObjectiveUseItemQueryBuilder().WithAllScalarFields()
                            )
                    ),
                alias: "data",
                lang: language,
                gameMode: gameMode
            )
            .Build();
    }

    #endregion

    #region HideoutStations Query

    private static string HideoutStationsQueryKey() =>
        HideoutStationsQueryKey(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);

    private static string HideoutStationsQueryKey(LanguageCode language, GameMode gameMode) =>
        $"hideout_{language}_{gameMode}";

    private static string HideoutStationsQuery() =>
        HideoutStationsQuery(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);

    internal static string HideoutStationsQuery(LanguageCode language, GameMode gameMode)
    {
        return new QueryQueryBuilder()
            .WithHideoutStations(
                new HideoutStationQueryBuilder().WithLevels(
                    new HideoutStationLevelQueryBuilder()
                        .WithId()
                        .WithItemRequirements(
                            new RequirementItemQueryBuilder()
                                .WithId()
                                .WithCount()
                                .WithItem(new ItemQueryBuilder().WithId())
                        )
                ),
                alias: "data",
                lang: language,
                gameMode: gameMode
            )
            .Build();
    }

    #endregion

    #region Maps Query

    private static string MapsQueryKey() =>
        MapsQueryKey(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);

    private static string MapsQueryKey(LanguageCode language, GameMode gameMode) => $"maps_{language}_{gameMode}";

    private static string MapsQuery() => MapsQuery(RatConfig.NameScan.Language.ToTarkovDevType(), RatConfig.GameMode);

    internal static string MapsQuery(LanguageCode language, GameMode gameMode)
    {
        return new QueryQueryBuilder()
            .WithMaps(
                new MapQueryBuilder().WithId().WithName().WithNormalizedName(),
                alias: "data",
                lang: language,
                gameMode: gameMode
            )
            .Build();
    }

    #endregion
}
