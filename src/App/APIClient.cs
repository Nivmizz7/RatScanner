using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.FetchModels.TarkovTracker;

namespace RatScanner;

internal static class APIClient
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(
            new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }
        )
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? bearerToken = null)
    {
        HttpRequestMessage request = new(method, url);
        // Identify the TarkovTracker Edition fork (matches TarkovDevAPI / GitHubUpdateService UA).
        try
        {
            request.Headers.UserAgent.ParseAdd($"RatScanner-TT/{RatConfig.Version}");
        }
        catch (Exception e)
        {
            Logger.LogWarning("Failed to set user-agent header; falling back to default RatScanner-TT user-agent.", e);
            request.Headers.UserAgent.ParseAdd("RatScanner-TT");
        }
        if (!string.IsNullOrEmpty(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        return request;
    }

    internal static string Get(string url, string? bearerToken = null) => Get(HttpClient, url, bearerToken);

    internal static string Get(HttpClient client, string url, string? bearerToken = null) =>
        GetAsync(client, url, bearerToken).GetAwaiter().GetResult();

    internal static Task<string> GetAsync(
        string url,
        string? bearerToken = null,
        CancellationToken cancellationToken = default
    ) => GetAsync(HttpClient, url, bearerToken, cancellationToken);

    internal static async Task<string> GetAsync(
        HttpClient client,
        string url,
        string? bearerToken = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url, bearerToken);
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Token was rejected by the API");
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new MissingPermissionException(ParseApiError(body));
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitExceededException("Rate limit reached for this account");
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"TarkovTracker API request failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                null,
                response.StatusCode
            );
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ParseApiError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "The API key is missing a required permission.";

        try
        {
            Newtonsoft.Json.Linq.JToken token = Newtonsoft.Json.Linq.JToken.Parse(body);
            return token.Value<string>("error") ?? "The API key is missing a required permission.";
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return "The API key is missing a required permission.";
        }
    }
}
