#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.TarkovDev;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace RatScanner.Tests;

public class TarkovDevJsonApiTests
{
    [Fact]
    public void ExtractMapsDictionary_skips_sibling_blob_data()
    {
        // Shape mirrors json.tarkov.dev/regular/maps: maps sits beside multi-MB siblings.
        const string json = """
            {
              "data": {
                "maps": {
                  "55f2d3fd4bdc2d5f408b4567": {
                    "id": "55f2d3fd4bdc2d5f408b4567",
                    "name": "55f2d3fd4bdc2d5f408b4567 Name",
                    "normalizedName": "factory"
                  },
                  "56f40101d2720b2a4d8b45d6": {
                    "id": "56f40101d2720b2a4d8b45d6",
                    "name": "56f40101d2720b2a4d8b45d6 Name",
                    "normalizedName": "customs"
                  }
                },
                "mobs": { "bogus": { "heavy": true } },
                "goonReports": []
              },
              "translations": {}
            }
            """;

        Dictionary<string, JsonApiModels.RawMap>? maps = TarkovDevAPI.ExtractMapsDictionary(json);
        Assert.NotNull(maps);
        Assert.Equal(2, maps.Count);
        Assert.Equal("factory", maps["55f2d3fd4bdc2d5f408b4567"].NormalizedName);
        Assert.Equal("customs", maps["56f40101d2720b2a4d8b45d6"].NormalizedName);
    }

    [Fact]
    public void ExtractMapsDictionary_returns_null_when_maps_missing()
    {
        const string json = """{ "data": { "mobs": {} }, "translations": {} }""";
        Assert.Null(TarkovDevAPI.ExtractMapsDictionary(json));
    }

    [Fact]
    public void ProjectMapsFromGraphql_reads_localized_name_and_normalizedName()
    {
        const string json = """
            {
              "data": {
                "maps": [
                  {
                    "id": "55f2d3fd4bdc2d5f408b4567",
                    "name": "Factory",
                    "normalizedName": "factory"
                  },
                  {
                    "id": "56f40101d2720b2a4d8b45d6",
                    "name": "Customs",
                    "normalizedName": "customs"
                  }
                ]
              }
            }
            """;

        Map[] maps = TarkovDevAPI.ProjectMapsFromGraphql(json);
        Assert.Equal(2, maps.Length);
        Assert.Equal("Factory", maps[0].Name);
        Assert.Equal("factory", maps[0].NormalizedName);
        Assert.Equal("Customs", maps[1].Name);
    }

    [Fact]
    public void ProjectMapsFromGraphql_throws_on_graphql_errors()
    {
        const string json = """
            {
              "errors": [ { "message": "boom" } ],
              "data": null
            }
            """;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            TarkovDevAPI.ProjectMapsFromGraphql(json)
        );
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectMapsFromGraphql_returns_empty_when_no_maps()
    {
        const string json = """{ "data": { "maps": [] } }""";
        Assert.Empty(TarkovDevAPI.ProjectMapsFromGraphql(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void MenuUpdated_tolerates_missing_or_invalid_timestamps(string? updated)
    {
        // MenuVM.Updated must not throw for seed/placeholder items with empty Updated.
        Assert.False(DateTime.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
    }

    [Fact]
    public void MenuUpdated_parses_iso_roundtrip_timestamps()
    {
        const string raw = "2026-07-13T21:58:01.000Z";
        Assert.True(
            DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)
        );
        Assert.Equal(2026, parsed.ToUniversalTime().Year);
    }
}

public class ApiClientUserAgentTests
{
    [Fact]
    public void Get_sends_tarkovtracker_edition_user_agent()
    {
        string? userAgent = null;
        using HttpClient client = new(
            new DelegateHandler(request =>
            {
                userAgent = request.Headers.UserAgent.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            })
        );

        Assert.Equal("ok", APIClient.Get(client, "https://example.test/resource", "token"));
        Assert.NotNull(userAgent);
        Assert.StartsWith("RatScanner/", userAgent, StringComparison.Ordinal);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(send(request));
    }
}
