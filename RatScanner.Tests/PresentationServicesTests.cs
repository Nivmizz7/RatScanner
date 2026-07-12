using System;
using RatScanner.Presentation;
using RatScanner.Scan;
using RatScanner.TarkovDev.GraphQL;
using Xunit;

namespace RatScanner.Tests;

public class PresentationServicesTests
{
    [Fact]
    public void QuestNeedTakesPriority() =>
        Assert.Equal(
            RecommendationType.KeepForQuest,
            RecommendationSelector.Select(10000, 5000, "Mechanic", 2, 0).Type
        );

    [Fact]
    public void HigherFleaValueRecommendsMarket() =>
        Assert.Equal(RecommendationType.SellOnFlea, RecommendationSelector.Select(10000, 5000, "Mechanic", 0, 0).Type);

    [Fact]
    public void HigherTraderValueRecommendsTrader() =>
        Assert.Equal(RecommendationType.SellToTrader, RecommendationSelector.Select(4000, 5000, "Mechanic", 0, 0).Type);

    [Fact]
    public void MissingValuesReportUnavailable() =>
        Assert.Equal(RecommendationType.PriceUnavailable, RecommendationSelector.Select(null, null, null, 0, 0).Type);

    [Fact]
    public void FreshnessUsesElapsedMinutes() =>
        Assert.Equal(
            "Updated 3 min ago",
            FreshnessFormatter.Format(
                new DateTimeOffset(2026, 1, 1, 11, 57, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
            )
        );
}

public class ItemQueueTests
{
    [Fact]
    public void Enqueue_keeps_live_scans_and_prunes_expired_scans()
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        ItemQueue queue = new();

        queue.Enqueue(new TestItemScan(now + 10_000));
        queue.Enqueue(new TestItemScan(now + 10_000));

        Assert.Equal(2, queue.Count);

        queue.Enqueue(new TestItemScan(now - 1));
        Assert.Equal(3, queue.Count);

        Assert.True(queue.PruneExpired(now + 20_000));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void PruneExpired_retains_the_latest_scan_for_result_views()
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        ItemQueue queue = new();
        queue.Enqueue(new TestItemScan(now - 1));

        Assert.False(queue.PruneExpired(now));
        Assert.Single(queue);
    }

    private sealed class TestItemScan : ItemScan
    {
        public TestItemScan(long expiresAt)
        {
            DissapearAt = expiresAt;
        }

        public override RatEye.Vector2 GetToolTipPosition() => RatEye.Vector2.Zero;
    }
}

public class RatEyeIconManagerTests
{
    [Fact]
    public void Static_icons_are_loaded_one_slot_size_at_a_time()
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "RatEye-icon-test-" + Guid.NewGuid().ToString("N")
        );
        string icons = System.IO.Path.Combine(root, "icons");
        System.IO.Directory.CreateDirectory(icons);

        try
        {
            WriteIcon(System.IO.Path.Combine(icons, "one.png"), 64, 64);
            WriteIcon(System.IO.Path.Combine(icons, "two.png"), 127, 64);

            RatEye.Config config = new()
            {
                PathConfig = new RatEye.Config.Path { StaticIcons = icons },
                ProcessingConfig = new RatEye.Config.Processing
                {
                    UseCache = false,
                    IconConfig = new RatEye.Config.Processing.Icon { UseStaticIcons = true },
                },
            };
            config.RatStashDB = RatStash.Database.FromItems(
                new RatStash.Item[]
                {
                    new()
                    {
                        Id = "one",
                        Name = "One",
                        ShortName = "One",
                        Width = 1,
                        Height = 1,
                    },
                    new()
                    {
                        Id = "two",
                        Name = "Two",
                        ShortName = "Two",
                        Width = 2,
                        Height = 1,
                    },
                }
            );

            using RatEye.IconManager manager = new(config);
            Assert.Empty(manager.StaticIcons);

            manager.EnsureStaticIconsLoaded(new RatEye.Vector2(1, 1));

            Assert.Single(manager.StaticIcons);
            Assert.Single(manager.StaticIcons[new RatEye.Vector2(1, 1)]);
            Assert.DoesNotContain(new RatEye.Vector2(2, 1), manager.StaticIcons.Keys);
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteIcon(string path, int width, int height)
    {
        using System.Drawing.Bitmap bitmap = new(width, height);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}

public class TarkovDevApiTests
{
    [Fact]
    public void ItemsQuery_requests_only_fields_used_by_the_application()
    {
        string query = TarkovDevAPI.ItemsQuery(LanguageCode.En, GameMode.Regular);

        Assert.Contains("avg24hPrice", query);
        Assert.Contains("sellFor", query);
        Assert.Contains("properties", query);
        Assert.DoesNotContain("buyFor", query);
        Assert.DoesNotContain("bartersFor", query);
        Assert.DoesNotContain("craftsUsing", query);
        Assert.DoesNotContain("historicalPrices", query);
    }

    [Fact]
    public void TasksQuery_omits_unused_nested_payloads()
    {
        string query = TarkovDevAPI.TasksQuery(LanguageCode.En, GameMode.Regular);

        Assert.Contains("taskImageLink", query);
        Assert.Contains("foundInRaid", query);
        Assert.Contains("markerItem", query);
        Assert.DoesNotContain("zones", query);
        Assert.DoesNotContain("taskRequirements", query);
        Assert.DoesNotContain("startRewards", query);
    }

    [Fact]
    public void HideoutQuery_requests_only_progress_fields()
    {
        string query = TarkovDevAPI.HideoutStationsQuery(LanguageCode.En, GameMode.Regular);

        Assert.Contains("itemRequirements", query);
        Assert.Contains("count", query);
        Assert.DoesNotContain("crafts", query);
        Assert.DoesNotContain("bonuses", query);
        Assert.DoesNotContain("stationLevelRequirements", query);
    }

    [Fact]
    public void MapsQuery_requests_only_search_and_mapping_fields()
    {
        string query = TarkovDevAPI.MapsQuery(LanguageCode.En, GameMode.Regular);

        Assert.Contains("id", query);
        Assert.Contains("name", query);
        Assert.DoesNotContain("extracts", query);
        Assert.DoesNotContain("transits", query);
        Assert.DoesNotContain("wiki", query);
    }
}

public class GitHubUpdateServiceTests
{
    [Theory]
    [InlineData("3.9.3", "v3.9.4", true)]
    [InlineData("3.9.3+build.1", "3.9.3", false)]
    [InlineData("3.10.0-beta.1", "3.9.9", false)]
    [InlineData("unknown", "3.9.4", false)]
    public void IsNewerVersion_handles_release_tag_formats(string current, string available, bool expected) =>
        Assert.Equal(expected, GitHubUpdateService.IsNewerVersion(current, available));
}

public class ItemExtensionTests
{
    [Fact]
    public void PricePerSlot_handles_missing_dimensions()
    {
        Item item = new()
        {
            Avg24HPrice = 12_000,
            Width = 0,
            Height = 0,
        };

        Assert.Equal(12_000, item.GetAvg24hMarketPricePerSlot());
    }

    [Fact]
    public void RatStash_mapping_preserves_template_matching_metadata()
    {
        Item source = new()
        {
            Id = "item",
            Name = "Item",
            ShortName = "I",
            Width = 2,
            Height = 3,
            BackgroundColor = "violet",
        };

        RatStash.Item mapped = RatScannerMain.ToRatStashItem(source);

        Assert.Equal(2, mapped.Width);
        Assert.Equal(3, mapped.Height);
        Assert.Equal(RatStash.TaxonomyColor.Violet, mapped.BackgroundColor);
    }
}
