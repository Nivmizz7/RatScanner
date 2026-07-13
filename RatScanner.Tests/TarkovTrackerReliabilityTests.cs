#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.FetchModels.TarkovTracker;
using Xunit;

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
    public void Get_maps_unauthorized_and_rate_limit_statuses()
    {
        using HttpClient unauthorizedClient = CreateStatusClient(HttpStatusCode.Unauthorized);
        using HttpClient rateLimitedClient = CreateStatusClient(HttpStatusCode.TooManyRequests);

        Assert.Throws<UnauthorizedTokenException>(() =>
            APIClient.Get(unauthorizedClient, "https://example.test/resource", "token")
        );
        Assert.Throws<RateLimitExceededException>(() =>
            APIClient.Get(rateLimitedClient, "https://example.test/resource", "token")
        );
    }

    [Fact]
    public void Get_rejects_other_non_success_statuses()
    {
        using HttpClient client = CreateStatusClient(HttpStatusCode.ServiceUnavailable);

        HttpRequestException exception = Assert.Throws<HttpRequestException>(() =>
            APIClient.Get(client, "https://example.test/resource", "token")
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
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
    private const string TokenResponse = """
        {"token":"abc","permissions":["GP"]}
        """;

    private const string ProgressResponse = """
        {
          "data": {
            "userId": "self",
            "displayName": "Me",
            "tasksProgress": [],
            "taskObjectivesProgress": [],
            "hideoutModulesProgress": [],
            "hideoutPartsProgress": []
          },
          "meta": {"self":"self"}
        }
        """;

    [Fact]
    public void Clearing_configured_token_clears_runtime_state_immediately()
    {
        TarkovTrackerDB database = new(
            (url, _) => url.EndsWith("/token", StringComparison.Ordinal) ? TokenResponse : ProgressResponse
        );
        database.Token = "abc";
        Assert.True(database.Init());
        Assert.Single(database.Progress);
        Assert.Equal("self", database.Self);

        database.Token = "";

        Assert.Empty(database.Progress);
        Assert.Equal("", database.Self);
        Assert.Null(database.SoloProgressAvailable);
        Assert.Null(database.TeamProgressAvailable);
        Assert.False(database.Init());
    }

    [Fact]
    public void Init_contains_transient_token_failures_for_later_retry()
    {
        TarkovTrackerDB database = new((_, _) => throw new HttpRequestException("Unavailable")) { Token = "abc" };
        bool initialized = false;

        Exception? exception = Record.Exception(() => initialized = database.Init());

        Assert.Null(exception);
        Assert.True(initialized);
        Assert.Empty(database.Progress);
        Assert.Equal(TokenValidationResult.Unavailable, database.ValidateToken("abc"));
    }

    [Fact]
    public void Init_contains_malformed_token_responses_for_later_retry()
    {
        TarkovTrackerDB database = new((_, _) => "not-json") { Token = "abc" };
        bool initialized = false;

        Exception? exception = Record.Exception(() => initialized = database.Init());

        Assert.Null(exception);
        Assert.True(initialized);
        Assert.False(database.TestToken("abc"));
    }

    [Fact]
    public void Init_retains_last_good_progress_when_refresh_is_malformed()
    {
        bool malformed = false;
        TarkovTrackerDB database = new(
            (url, _) =>
            {
                if (url.EndsWith("/token", StringComparison.Ordinal))
                    return TokenResponse;
                return malformed ? "not-json" : ProgressResponse;
            }
        )
        {
            Token = "abc",
        };
        Assert.True(database.Init());
        Assert.Single(database.Progress);

        malformed = true;
        Exception? exception = Record.Exception(() => database.Init());

        Assert.Null(exception);
        Assert.Single(database.Progress);
        Assert.Equal("self", database.Self);
    }

    [Fact]
    public void Init_marks_only_explicit_unauthorized_responses_as_invalid()
    {
        TarkovTrackerDB database = new((_, _) => throw new UnauthorizedTokenException()) { Token = "abc" };

        Assert.False(database.Init());
        Assert.Empty(database.Progress);
        Assert.Equal(TokenValidationResult.Invalid, database.ValidateToken("abc"));
    }

    [Fact]
    public void Backend_change_invalidates_old_state_and_retries_an_unavailable_validation()
    {
        int newBackendTokenRequests = 0;
        TarkovTrackerDB database = new(
            (url, _) =>
            {
                if (url.StartsWith("https://old.example", StringComparison.Ordinal))
                    throw new UnauthorizedTokenException();
                if (url.EndsWith("/token", StringComparison.Ordinal) && ++newBackendTokenRequests == 1)
                    throw new HttpRequestException("Temporarily unavailable");
                return url.EndsWith("/token", StringComparison.Ordinal) ? TokenResponse : ProgressResponse;
            }
        );
        database.Configure("abc", "https://old.example");
        Assert.False(database.Init());

        database.Configure("abc", "https://new.example");

        Assert.True(database.Init());
        Assert.Null(database.SoloProgressAvailable);
        Assert.True(database.Init());
        Assert.True(database.SoloProgressAvailable);
        Assert.Single(database.Progress);
        Assert.Equal(2, newBackendTokenRequests);
    }

    [Fact]
    public async Task Stale_token_validation_cannot_overwrite_a_newer_configuration()
    {
        using ManualResetEventSlim oldRequestStarted = new(false);
        using ManualResetEventSlim releaseOldRequest = new(false);
        TarkovTrackerDB database = new(
            (url, token) =>
            {
                if (url.StartsWith("https://old.example", StringComparison.Ordinal))
                {
                    oldRequestStarted.Set();
                    releaseOldRequest.Wait(TimeSpan.FromSeconds(5));
                    throw new UnauthorizedTokenException();
                }

                if (url.EndsWith("/token", StringComparison.Ordinal))
                    return $$"""{"token":"{{token}}","permissions":["GP"]}""";
                return ProgressResponse;
            }
        );
        database.Configure("old", "https://old.example");
        Task<TokenValidationResult> oldValidation = Task.Run(database.UpdateToken);
        Assert.True(oldRequestStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        database.Configure("new", "https://new.example");
        Assert.Equal(TokenValidationResult.Valid, database.UpdateToken());
        releaseOldRequest.Set();

        Assert.Equal(TokenValidationResult.Unavailable, await oldValidation);
        Assert.Equal("new", database.Token);
        Assert.True(database.SoloProgressAvailable);
        Assert.True(database.Init());
        Assert.Single(database.Progress);
    }

    [Fact]
    public async Task Superseded_init_is_not_reported_as_an_invalid_current_token()
    {
        using ManualResetEventSlim oldRequestStarted = new(false);
        using ManualResetEventSlim releaseOldRequest = new(false);
        TarkovTrackerDB database = new(
            (url, token) =>
            {
                if (url.StartsWith("https://old.example", StringComparison.Ordinal))
                {
                    oldRequestStarted.Set();
                    releaseOldRequest.Wait(TimeSpan.FromSeconds(5));
                    throw new UnauthorizedTokenException();
                }

                if (url.EndsWith("/token", StringComparison.Ordinal))
                    return $$"""{"token":"{{token}}","permissions":["GP"]}""";
                return ProgressResponse;
            }
        );
        long oldGeneration = database.Configure("old", "https://old.example");
        Task<bool> oldInitialization = Task.Run(database.Init);
        Assert.True(oldRequestStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        long newGeneration = database.Configure("new", "https://new.example");
        Assert.Equal(TokenValidationResult.Valid, database.UpdateToken());
        releaseOldRequest.Set();

        Assert.True(await oldInitialization);
        Assert.False(database.IsCurrentConfiguration(oldGeneration));
        Assert.True(database.IsCurrentConfiguration(newGeneration));
        Assert.Equal("new", database.Token);
        Assert.True(database.SoloProgressAvailable);
    }
}
