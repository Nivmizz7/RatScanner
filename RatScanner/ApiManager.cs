using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RatScanner.FetchModels;
using static RatScanner.OAuth2;

namespace RatScanner;

public static class ApiManager
{
    static readonly HttpClient HttpClient = new(
        new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }
    );

    public enum ResourceType
    {
        ClientVersion,
        ClientForceUpdateVersions,
        DownloadLink,
        PatreonLink,
        GithubLink,
        DiscordLink,
        FAQLink,
        UpdaterLink,
    }

    private static readonly Dictionary<ResourceType, string> ResCache = new();

    // Official RatScanner API URL
    private const string BaseUrl = "https://api.ratscanner.com/v3";

    internal static async Task<OAuth2.Token?> ExchangeRefreshTokenForTokensAsync(Client client, Token token)
    {
        Logger.LogInfo("Exchanging refresh token for tokens...");

        JsonContent content = JsonContent.Create(new { client_id = client.Id, refresh_token = token.RefreshToken });
        HttpRequestMessage request = new()
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri($"{BaseUrl}/oauth/refresh"),
            Content = content,
        };

        HttpResponseMessage response = await HttpClient.SendAsync(request);
        string responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning($"STATUS CODE: {response.StatusCode}");
            Logger.LogInfo($"Content: {responseText}");
            return null;
        }

        Dictionary<string, string>? tokenEndpointDecoded = JsonConvert.DeserializeObject<Dictionary<string, string>>(
            responseText
        );

        if (
            tokenEndpointDecoded == null
            || !tokenEndpointDecoded.TryGetValue("access_token", out string? accessToken)
            || !tokenEndpointDecoded.TryGetValue("refresh_token", out string? refreshToken)
        )
        {
            Logger.LogWarning("Failed to decode token endpoint response.");
            return null;
        }

        return new Token() { AccessToken = accessToken, RefreshToken = refreshToken };
    }

    public static string GetResource(ResourceType resource)
    {
        if (ResCache.ContainsKey(resource))
            return ResCache[resource];

        string resPath = resource.GetResourcePath();

        try
        {
            Logger.LogInfo($"Loading resource \"{resPath}\"...");
            string json = GetString($"{BaseUrl}/res/{resPath}");
            string value = JsonConvert.DeserializeObject<Resource>(json)?.Value ?? throw new NullReferenceException();
            ResCache.Add(resource, value);
            return value;
        }
        catch (Exception e)
        {
            Logger.LogError($"Loading of resource \"{resPath}\" failed.", e);
            return "[Loading failed]";
        }
    }

    public static void DownloadFile(string url, string destination)
    {
        try
        {
            Logger.LogInfo($"Downloading file \"{url}\"...");
            byte[] contents = GetBytes(url);
            File.WriteAllBytes(destination, contents);
        }
        catch (Exception e)
        {
            Logger.LogError($"Downloading of file \"{url}\" failed.", e);
        }
    }

    private static HttpRequestMessage CreateRequest(string url, string? bearerToken = null)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"RatScanner-Client/{RatConfig.Version}");
        if (bearerToken != null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                bearerToken
            );
        return request;
    }

    private static byte[] GetBytes(string url, string? bearerToken = null)
    {
        using HttpRequestMessage request = CreateRequest(url, bearerToken);
        using HttpResponseMessage response = HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using Stream stream = response.Content.ReadAsStream();
        using MemoryStream memoryStream = new();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static string GetString(string url, string? bearerToken = null)
    {
        using HttpRequestMessage request = CreateRequest(url, bearerToken);
        using HttpResponseMessage response = HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using Stream stream = response.Content.ReadAsStream();
        string? charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        StreamReader reader = new(stream, encoding);
        return reader.ReadToEnd();
    }

    public static string GetResourcePath(this ResourceType resourceType)
    {
        return resourceType switch
        {
            ResourceType.ClientVersion => "RSClientVersion",
            ResourceType.ClientForceUpdateVersions => "RSClientForceUpdateVersions",
            ResourceType.DownloadLink => "RSDownloadLink",
            ResourceType.PatreonLink => "RSPatreonLink",
            ResourceType.GithubLink => "RSGithubLink",
            ResourceType.DiscordLink => "RSDiscordLink",
            ResourceType.FAQLink => "RSFAQLink",
            ResourceType.UpdaterLink => "RSUpdaterLink",
            _ => throw new NotImplementedException(),
        };
    }
}
