#nullable enable

using System.Collections.Generic;
using System.Globalization;
using RatScanner.TarkovDev;
using Xunit;

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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void MenuUpdated_tolerates_missing_or_invalid_timestamps(string? updated)
    {
        // MenuVM.Updated must not throw for seed/placeholder items with empty Updated.
        Assert.False(
            System.DateTime.TryParse(
                updated,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _
            )
        );
    }

    [Fact]
    public void MenuUpdated_parses_iso_roundtrip_timestamps()
    {
        const string raw = "2026-07-13T21:58:01.000Z";
        Assert.True(
            System.DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out System.DateTime parsed
            )
        );
        Assert.Equal(2026, parsed.ToUniversalTime().Year);
    }
}
