using System;
using RatEye;
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

    [Fact]
    public void GetNextExpiration_returns_null_when_no_live_scan_remains()
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        ItemQueue queue = new();
        queue.Enqueue(new TestItemScan(now - 1));

        Assert.Null(queue.GetNextExpiration(now));
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

public class SimpleConfigTests
{
    [Fact]
    public void Config_round_trips_values_larger_than_the_initial_native_buffer()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RatScanner-{Guid.NewGuid():N}.cfg");
        try
        {
            SimpleConfig config = new(path, "Test");
            string expected = new('x', 4_096);

            config.WriteString("LongValue", expected);

            Assert.Equal(expected, config.ReadString("LongValue", string.Empty));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
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

public class RatEyeProcessingTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("item", "", 0)]
    [InlineData("", "item", 0)]
    [InlineData("item", "item", 1)]
    public void Normalized_similarity_handles_empty_input(string source, string target, float expected) =>
        Assert.Equal(expected, source.NormedLevenshteinDistance(target));

    [Fact]
    public void Crop_clamps_to_the_image_and_rejects_disjoint_regions()
    {
        using System.Drawing.Bitmap source = new(20, 20);
        using System.Drawing.Bitmap cropped = source.Crop(-5, -5, 10, 10);

        Assert.Equal(5, cropped.Width);
        Assert.Equal(5, cropped.Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Crop(30, 30, 5, 5));
    }

    [Fact]
    public void Multi_inspection_suppresses_duplicate_peaks_and_preserves_confidence()
    {
        using System.Drawing.Bitmap marker = CreateMarker();
        using System.Drawing.Bitmap source = new(100, 60);
        using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(source))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(25, 27, 27));
            graphics.DrawImageUnscaled(marker, 10, 10);
            graphics.DrawImageUnscaled(marker, 65, 35);
        }

        RatEye.Config.Processing.Inspection inspectionConfig = new() { MarkerItemScale = 1, MarkerThreshold = 0.8f };
        inspectionConfig.Marker.Dispose();
        inspectionConfig.Marker = new System.Drawing.Bitmap(marker);
        RatEye.Config config = new()
        {
            ProcessingConfig = new RatEye.Config.Processing { Scale = 1, InspectionConfig = inspectionConfig },
        };

        using RatEye.RatEyeEngine engine = new(config, RatStash.Database.FromItems(Array.Empty<RatStash.Item>()));
        RatEye.Processing.MultiInspection result = engine.NewMultiInspection(source);

        Assert.Equal(2, result.Inspections.Count);
        Assert.All(result.Inspections, inspection => Assert.True(inspection.MarkerConfidence > 0.99f));
    }

    [Fact]
    public void Marker_peak_extraction_suppresses_adjacent_peaks_but_keeps_separated_matches()
    {
        using OpenCvSharp.Mat response = new(5, 15, OpenCvSharp.MatType.CV_32FC1, OpenCvSharp.Scalar.All(0));
        response.Set(2, 2, 0.99f);
        response.Set(2, 3, 0.98f);
        response.Set(2, 12, 0.97f);

        var matches = RatEye.Processing.MultiInspection.ExtractMarkerPeaks(
            response,
            new System.Drawing.Size(5, 5),
            0.8f
        );

        Assert.Equal(2, matches.Count);
        Assert.Equal(new RatEye.Vector2(2, 2), matches[0].position);
        Assert.Equal(0.99f, matches[0].confidence);
        Assert.Equal(new RatEye.Vector2(12, 2), matches[1].position);
        Assert.Equal(0.97f, matches[1].confidence);
    }

    private static System.Drawing.Bitmap CreateMarker()
    {
        System.Drawing.Bitmap marker = new(9, 9);
        using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(marker);
        graphics.Clear(System.Drawing.Color.FromArgb(25, 27, 27));
        using System.Drawing.Pen pen = new(System.Drawing.Color.White, 2);
        graphics.DrawLine(pen, 1, 1, 7, 7);
        graphics.DrawLine(pen, 7, 1, 1, 7);
        marker.SetPixel(4, 1, System.Drawing.Color.Red);
        return marker;
    }
}

public class TarkovDevApiTests
{
    [Fact]
    public void ItemsQuery_requests_only_fields_used_by_the_application()
    {
        string query = TarkovDevAPI.ItemsQuery(LanguageCode.En, GameMode.Regular);

        Assert.Contains("avg24hPrice", query);
        Assert.Contains("backgroundColor", query);
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
        Assert.Contains("normalizedName", query);
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

    [Fact]
    public void Scan_result_mapping_uses_the_scanned_items_metadata()
    {
        Item item = new()
        {
            Id = "historical-item",
            Name = "Historical Item",
            ShortName = "History",
            Avg24HPrice = 12_000,
            Width = 2,
            Height = 1,
            WikiLink = "https://example.test/historical-item",
        };
        DefaultItemScan scan = new(item);

        ScanResultViewModel result = ScanResultAdapter.Map(scan, questRemaining: 2, hideoutRemaining: 0, true);

        Assert.Equal(item.Id, result.Item.Id);
        Assert.Equal(item.WikiLink, result.Item.WikiUrl);
        Assert.Equal(2, result.Quests.RemainingRequired);
        Assert.Equal(RecommendationType.KeepForQuest, result.Recommendation.Type);
    }
}
