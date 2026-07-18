#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.FetchModels.TarkovTracker;
using Xunit;
using GameMode = RatScanner.TarkovDev.GameMode;

namespace RatScanner.Tests;

public class ApiClientReliabilityTests
{
    [Fact]
    public void Get_disposes_request_and_response_content()
    {
        TrackingContent requestContent = new("");
        TrackingContent responseContent = new("ok");
        using HttpClient client = new(
            new DelegateHandler(request =>
            {
                request.Content = requestContent;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = responseContent };
            })
        );

        Assert.Equal("ok", APIClient.Get(client, "https://example.test/resource", "token"));
        Assert.True(requestContent.Disposed);
        Assert.True(responseContent.Disposed);
    }

    [Fact]
    public void Get_maps_auth_permission_and_rate_limit_statuses()
    {
        using HttpClient unauthorizedClient = CreateStatusClient(HttpStatusCode.Unauthorized);
        using HttpClient forbiddenClient = new(
            new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":\"Missing required permission: GP\"}"),
            })
        );
        using HttpClient rateLimitedClient = CreateStatusClient(HttpStatusCode.TooManyRequests);

        Assert.Throws<UnauthorizedTokenException>(() =>
            APIClient.Get(unauthorizedClient, "https://example.test/resource", "token")
        );
        MissingPermissionException permission = Assert.Throws<MissingPermissionException>(() =>
            APIClient.Get(forbiddenClient, "https://example.test/resource", "token")
        );
        Assert.Contains("GP", permission.Message, StringComparison.Ordinal);
        Assert.Throws<RateLimitExceededException>(() =>
            APIClient.Get(rateLimitedClient, "https://example.test/resource", "token")
        );
    }

    [Fact]
    public void Get_rejects_other_non_success_statuses_without_including_the_token()
    {
        using HttpClient client = CreateStatusClient(HttpStatusCode.ServiceUnavailable);
        const string secret = "PVP_super-secret";

        HttpRequestException exception = Assert.Throws<HttpRequestException>(() =>
            APIClient.Get(client, "https://example.test/resource", secret)
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    private static HttpClient CreateStatusClient(HttpStatusCode statusCode) =>
        new(new DelegateHandler(_ => new HttpResponseMessage(statusCode)));

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(send(request));
    }

    private sealed class TrackingContent(string value) : HttpContent
    {
        private readonly byte[] _content = System.Text.Encoding.UTF8.GetBytes(value);

        internal bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_content, 0, _content.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = _content.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

public class TarkovTrackerDatabaseReliabilityTests
{
    private const string PvpTokenResponse = """
        {"token":"PVP_abc","permissions":["GP"],"gameMode":"pvp"}
        """;

    private const string PvpProgressResponse = """
        {
          "data": {
            "userId": "pvp-self",
            "displayName": "PvP",
            "tasksProgress": [],
            "taskObjectivesProgress": [],
            "hideoutModulesProgress": [],
            "hideoutPartsProgress": []
          },
          "meta": {"self":"pvp-self","gameMode":"pvp"}
        }
        """;

    private const string PveProgressResponse = """
        {
          "data": {
            "userId": "pve-self",
            "displayName": "PvE",
            "tasksProgress": [],
            "taskObjectivesProgress": [],
            "hideoutModulesProgress": [],
            "hideoutPartsProgress": []
          },
          "meta": {"self":"pve-self","gameMode":"pve"}
        }
        """;

    [Fact]
    public async Task Clearing_configured_token_clears_runtime_state_immediately()
    {
        TarkovTrackerDB database = new(
            (url, _, _) =>
                Task.FromResult(
                    url.EndsWith("/token", StringComparison.Ordinal) ? PvpTokenResponse : PvpProgressResponse
                )
        );
        database.Configure("PVP_abc", "https://api.example", GameMode.Regular);
        Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
        Assert.Single(database.Progress);

        database.Configure("", "https://api.example", GameMode.Regular);

        Assert.Empty(database.Progress);
        Assert.Equal("", database.Self);
        Assert.Equal(TrackerConnectionState.NotConfigured, database.ConnectionState);
        Assert.False(await database.InitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Validation_requires_only_progress_read_and_treats_team_as_optional()
    {
        TarkovTrackerDB database = new(
            (_, token, _) => Task.FromResult($$"""{"token":"{{token}}","permissions":["GP"],"gameMode":"pvp"}""")
        );

        TrackerValidationResult valid = await database.ValidateCandidateAsync(
            "PVP_abc",
            "https://api.example",
            GameMode.Regular,
            TestContext.Current.CancellationToken
        );
        TrackerValidationResult missing = await new TarkovTrackerDB(
            (_, token, _) => Task.FromResult($$"""{"token":"{{token}}","permissions":["TP","WP"],"gameMode":"pvp"}""")
        ).ValidateCandidateAsync(
            "PVP_abc",
            "https://api.example",
            GameMode.Regular,
            TestContext.Current.CancellationToken
        );

        Assert.True(valid.Succeeded);
        Assert.False(missing.Succeeded);
        Assert.Equal(TrackerValidationFailure.MissingPermissions, missing.Failure);
        Assert.Equal([TarkovTrackerPermissions.ReadProgress], missing.MissingPermissions);
    }

    [Theory]
    [InlineData("PVP_abc", "pve", GameMode.Regular)]
    [InlineData("PVE_abc", "pvp", GameMode.Pve)]
    public async Task Wrong_mode_key_is_rejected(string token, string apiMode, GameMode expectedMode)
    {
        TarkovTrackerDB database = new(
            (_, suppliedToken, _) =>
                Task.FromResult($$"""{"token":"{{suppliedToken}}","permissions":["GP"],"gameMode":"{{apiMode}}"}""")
        );

        TrackerValidationResult result = await database.ValidateCandidateAsync(
            token,
            "https://api.example",
            expectedMode,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.Succeeded);
        Assert.Equal(TrackerValidationFailure.WrongGameMode, result.Failure);
    }

    [Fact]
    public async Task Failed_reconfiguration_does_not_allow_old_mode_progress_to_overwrite_the_new_mode()
    {
        using ManualResetEventSlim pvpStarted = new(false);
        using ManualResetEventSlim releasePvp = new(false);
        TarkovTrackerDB database = new(
            async (url, token, cancellationToken) =>
            {
                if (token == "PVP_abc" && url.EndsWith("/progress", StringComparison.Ordinal))
                {
                    pvpStarted.Set();
                    await Task.Run(
                        () => releasePvp.Wait(TimeSpan.FromSeconds(5), cancellationToken),
                        cancellationToken
                    );
                    return PvpProgressResponse;
                }
                if (url.EndsWith("/token", StringComparison.Ordinal))
                {
                    string mode = token!.StartsWith("PVE_", StringComparison.Ordinal) ? "pve" : "pvp";
                    return $$"""{"token":"{{token}}","permissions":["GP"],"gameMode":"{{mode}}"}""";
                }
                return PveProgressResponse;
            }
        );

        database.Configure("PVP_abc", "https://api.example", GameMode.Regular);
        Task<bool> pvpInit = database.InitAsync(TestContext.Current.CancellationToken);
        Assert.True(pvpStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        database.Configure("PVE_abc", "https://api.example", GameMode.Pve);
        Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
        releasePvp.Set();
        await pvpInit;

        Assert.Equal("PVE_abc", database.Token);
        Assert.Equal("pve-self", database.Self);
        Assert.Equal("PvE", Assert.Single(database.Progress).DisplayName);
    }

    [Fact]
    public async Task Switching_back_to_a_mode_loads_its_cached_progress_before_refresh()
    {
        bool failRefresh = false;
        TarkovTrackerDB database = new(
            (url, token, _) =>
            {
                if (url.EndsWith("/token", StringComparison.Ordinal))
                {
                    string mode = token!.StartsWith("PVE_", StringComparison.Ordinal) ? "pve" : "pvp";
                    return Task.FromResult($$"""{"token":"{{token}}","permissions":["GP"],"gameMode":"{{mode}}"}""");
                }
                if (failRefresh)
                    throw new HttpRequestException("offline");
                return Task.FromResult(token == "PVE_abc" ? PveProgressResponse : PvpProgressResponse);
            }
        );

        database.Configure("PVP_abc", "https://api.example", GameMode.Regular);
        Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
        database.Configure("PVE_abc", "https://api.example", GameMode.Pve);
        Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));

        failRefresh = true;
        database.Configure("PVP_abc", "https://api.example", GameMode.Regular);

        Assert.Equal("pvp-self", database.Self);
        Assert.Equal("PvP", Assert.Single(database.Progress).DisplayName);
        Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("pvp-self", database.Self);
    }

    [Fact]
    public async Task Invalid_replacement_validation_does_not_mutate_the_configured_key()
    {
        TarkovTrackerDB database = new((_, _, _) => throw new UnauthorizedTokenException());
        database.Configure("PVP_existing", "https://api.example", GameMode.Regular);

        TrackerValidationResult result = await database.ValidateCandidateAsync(
            "PVP_replacement",
            "https://api.example",
            GameMode.Regular,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.Succeeded);
        Assert.Equal("PVP_existing", database.Token);
    }
}

public class TarkovTrackerRefreshProgressTests
{
    private const string PvpTokenResponse = """
        {"token":"PVP_abc","permissions":["GP"],"gameMode":"pvp"}
        """;

    private const string PvpProgressResponse = """
        {
          "data": {
            "userId": "pvp-self",
            "displayName": "PvP",
            "tasksProgress": [],
            "taskObjectivesProgress": [],
            "hideoutModulesProgress": [],
            "hideoutPartsProgress": []
          },
          "meta": {"self":"pvp-self","gameMode":"pvp"}
        }
        """;

    private const string PvpProgressResponseUpdated = """
        {
          "data": {
            "userId": "pvp-self",
            "displayName": "PvP-Updated",
            "tasksProgress": [],
            "taskObjectivesProgress": [],
            "hideoutModulesProgress": [],
            "hideoutPartsProgress": []
          },
          "meta": {"self":"pvp-self","gameMode":"pvp"}
        }
        """;

    [Fact]
    public async Task Steady_state_refresh_skips_token_endpoint_and_only_fetches_progress()
    {
        int tokenCallCount = 0;
        int progressCallCount = 0;
        using TarkovTrackerDB database = new(
            (url, _, _) =>
            {
                if (url.EndsWith("/token", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref tokenCallCount);
                    return Task.FromResult(PvpTokenResponse);
                }
                Interlocked.Increment(ref progressCallCount);
                return Task.FromResult(progressCallCount == 1 ? PvpProgressResponse : PvpProgressResponseUpdated);
            }
        );

        database.Configure("PVP_abc", "https://api.example", GameMode.Regular);
        Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, tokenCallCount);
        Assert.Equal(1, progressCallCount);
        Assert.Equal("PvP", Assert.Single(database.Progress).DisplayName);

        // Steady-state refresh should NOT call /token again.
        Assert.True(await database.RefreshProgressAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, tokenCallCount);
        Assert.Equal(2, progressCallCount);
        Assert.Equal("PvP-Updated", Assert.Single(database.Progress).DisplayName);
    }

    [Fact]
    public async Task Refresh_falls_back_to_full_init_when_token_was_never_validated()
    {
        int tokenCallCount = 0;
        using TarkovTrackerDB database = new(
            (url, _, _) =>
            {
                if (url.EndsWith("/token", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref tokenCallCount);
                    return Task.FromResult(PvpTokenResponse);
                }
                return Task.FromResult(PvpProgressResponse);
            }
        );

        // Configure but never call InitAsync — _token stays null.
        database.Configure("PVP_abc", "https://api.example", GameMode.Regular);
        Assert.Equal(TrackerConnectionState.Untested, database.ConnectionState);

        // RefreshProgressAsync should detect the unvalidated token and do a full init.
        Assert.True(await database.RefreshProgressAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, tokenCallCount);
        Assert.Equal(TrackerConnectionState.Connected, database.ConnectionState);
        Assert.Equal("pvp-self", database.Self);
    }

    [Fact]
    public async Task Refresh_falls_back_to_full_init_after_progress_rejects_the_key()
    {
        int tokenCallCount = 0;
        bool rejectProgress = false;
        using TarkovTrackerDB database = new(
            (url, _, _) =>
            {
                if (url.EndsWith("/token", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref tokenCallCount);
                    return Task.FromResult(PvpTokenResponse);
                }
                if (rejectProgress)
                    throw new UnauthorizedTokenException();
                return Task.FromResult(PvpProgressResponse);
            }
        );

        database.Configure("PVP_abc", "https://api.example", GameMode.Regular);
        Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TrackerConnectionState.Connected, database.ConnectionState);

        // First refresh: progress rejects the key → _token cleared, state → InvalidKey.
        rejectProgress = true;
        Assert.True(await database.RefreshProgressAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TrackerConnectionState.InvalidKey, database.ConnectionState);
        // /token was called once during InitAsync; the rejected refresh did NOT call /token.
        Assert.Equal(1, tokenCallCount);

        // Second refresh: _token is null → falls back to full init → /token called again.
        rejectProgress = false;
        Assert.True(await database.RefreshProgressAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, tokenCallCount);
        Assert.Equal(TrackerConnectionState.Connected, database.ConnectionState);
    }

    [Fact]
    public async Task Refresh_with_no_configured_token_clears_state_and_makes_no_calls()
    {
        int callCount = 0;
        using TarkovTrackerDB database = new(
            (_, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(PvpTokenResponse);
            }
        );

        database.Configure("", "https://api.example", GameMode.Regular);
        Assert.False(await database.RefreshProgressAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, callCount);
        Assert.Equal(TrackerConnectionState.NotConfigured, database.ConnectionState);
    }
}

public class TarkovTrackerResponseContractTests
{
    [Fact]
    public void Missing_solo_data_is_not_synthesized_by_deserialization()
    {
        ProgressResponse response = Newtonsoft.Json.JsonConvert.DeserializeObject<ProgressResponse>(
            "{\"meta\":{\"self\":\"pvp-self\",\"gameMode\":\"pvp\"}}"
        )!;

        Assert.Null(response.UserProgress);
        Assert.NotNull(response.Meta);
    }

    [Fact]
    public void Missing_solo_meta_is_not_synthesized_by_deserialization()
    {
        ProgressResponse response = Newtonsoft.Json.JsonConvert.DeserializeObject<ProgressResponse>(
            "{\"data\":{\"userId\":\"pvp-self\"}}"
        )!;

        Assert.NotNull(response.UserProgress);
        Assert.Null(response.Meta);
    }

    [Fact]
    public void Missing_team_hidden_teammates_is_not_synthesized_by_deserialization()
    {
        TeamProgressResponse response = Newtonsoft.Json.JsonConvert.DeserializeObject<TeamProgressResponse>(
            "{\"data\":[],\"meta\":{\"self\":\"pvp-self\"}}"
        )!;

        Assert.NotNull(response.TeamProgress);
        Assert.NotNull(response.Meta);
        Assert.Null(response.Meta!.HiddenTeammates);
    }
}

public class TarkovTrackerMalformedResponseReliabilityTests
{
    private const string PvpTokenResponse = "{\"token\":\"PVP_abc\",\"permissions\":[\"GP\"],\"gameMode\":\"pvp\"}";

    private const string PvpProgressResponse = """
        {
          "data": {
            "userId": "pvp-self",
            "displayName": "PvP",
            "tasksProgress": [],
            "taskObjectivesProgress": [],
            "hideoutModulesProgress": [],
            "hideoutPartsProgress": []
          },
          "meta": {"self":"pvp-self","gameMode":"pvp"}
        }
        """;

    private const string PvpTeamTokenResponse =
        "{\"token\":\"PVP_abc\",\"permissions\":[\"GP\",\"TP\"],\"gameMode\":\"pvp\"}";

    private const string PvpTeamProgressResponse = """
        {
          "data": [{
            "userId": "pvp-self",
            "displayName": "PvP",
            "tasksProgress": [],
            "taskObjectivesProgress": [],
            "hideoutModulesProgress": [],
            "hideoutPartsProgress": []
          }],
          "meta": {"self":"pvp-self","hiddenTeammates":[]}
        }
        """;

    [Theory]
    [InlineData("{\"meta\":{\"self\":\"pvp-self\",\"gameMode\":\"pvp\"}}")]
    [InlineData("{\"data\":null,\"meta\":{\"self\":\"pvp-self\",\"gameMode\":\"pvp\"}}")]
    [InlineData("{\"data\":{},\"meta\":{\"self\":\"pvp-self\",\"gameMode\":\"pvp\"}}")]
    [InlineData("{\"data\":{\"userId\":\"pvp-self\"}}")]
    public async Task Malformed_solo_response_retains_last_good_progress(string malformedResponse)
    {
        bool returnMalformed = false;
        using TarkovTrackerDB database = new(
            (url, _, _) =>
                Task.FromResult(
                    url.EndsWith("/token", StringComparison.Ordinal) ? PvpTokenResponse
                    : returnMalformed ? malformedResponse
                    : PvpProgressResponse
                )
        );
        database.Configure("PVP_abc", "https://api.example", GameMode.Regular);

        Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
        returnMalformed = true;

        Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("pvp-self", database.Self);
        Assert.Equal("PvP", Assert.Single(database.Progress).DisplayName);
    }

    [Fact]
    public async Task Malformed_team_response_retains_last_good_progress()
    {
        bool returnMalformed = false;
        bool previousShowTeam = RatConfig.Tracking.TarkovTracker.ShowTeam;
        RatConfig.Tracking.TarkovTracker.ShowTeam = true;
        try
        {
            using TarkovTrackerDB database = new(
                (url, _, _) =>
                    Task.FromResult(
                        url.EndsWith("/token", StringComparison.Ordinal) ? PvpTeamTokenResponse
                        : returnMalformed ? "{\"data\":[],\"meta\":{\"self\":\"pvp-self\"}}"
                        : PvpTeamProgressResponse
                    )
            );
            database.Configure("PVP_abc", "https://api.example", GameMode.Regular);

            Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
            returnMalformed = true;

            Assert.True(await database.InitAsync(TestContext.Current.CancellationToken));
            Assert.Equal("pvp-self", database.Self);
            Assert.Equal("PvP", Assert.Single(database.Progress).DisplayName);
        }
        finally
        {
            RatConfig.Tracking.TarkovTracker.ShowTeam = previousShowTeam;
        }
    }
}

public class TarkovTrackerApiContractTests
{
    [Fact]
    public void Get_sends_expected_route_bearer_token_and_fork_user_agent()
    {
        string? requestUri = null;
        string? authorizationScheme = null;
        string? authorizationParameter = null;
        string? userAgent = null;
        using HttpClient client = new(
            new CaptureHandler(request =>
            {
                requestUri = request.RequestUri?.ToString();
                authorizationScheme = request.Headers.Authorization?.Scheme;
                authorizationParameter = request.Headers.Authorization?.Parameter;
                userAgent = request.Headers.UserAgent.ToString();
            })
        );

        Assert.Equal("ok", APIClient.Get(client, "https://api.example/progress", "PVP_super-secret"));
        Assert.Equal("https://api.example/progress", requestUri);
        Assert.Equal("Bearer", authorizationScheme);
        Assert.Equal("PVP_super-secret", authorizationParameter);
        Assert.Contains("RatScanner-TT/", userAgent, StringComparison.Ordinal);
    }

    private sealed class CaptureHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }
}
