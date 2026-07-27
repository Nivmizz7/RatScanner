using System;
using System.Collections.Generic;
using RatEye;
using RatScanner.FetchModels.TarkovTracker;
using RatScanner.Presentation;
using RatScanner.Scan;
using RatScanner.TarkovDev;
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

    [Fact]
    public void HigherFleaValueUsesActionableExplanation()
    {
        RecommendationViewModel result = RecommendationSelector.Select(10000, 5000, "Mechanic", 0, 0);
        Assert.Equal(RecommendationType.SellOnFlea, result.Type);
        Assert.Equal($"{PriceFormatter.Format(5000)} more than Mechanic.", result.Explanation);
    }

    [Fact]
    public void QuestFirNeedMentionsFoundInRaidAndCraft()
    {
        RecommendationViewModel result = RecommendationSelector.Select(
            10000,
            5000,
            "Mechanic",
            new RequirementBreakdown(Total: 2, FoundInRaid: 2, NonFoundInRaid: 0),
            default,
            new AcquisitionInfo(CanCraft: true, CraftRecipeCount: 1, CanBarter: false, BarterOfferCount: 0)
        );

        Assert.Equal(RecommendationType.KeepForQuest, result.Type);
        Assert.Contains("found in raid", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Craftable", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SellRecommendationMentionsBarterWhenAvailable()
    {
        RecommendationViewModel result = RecommendationSelector.Select(
            10000,
            5000,
            "Mechanic",
            default,
            default,
            new AcquisitionInfo(CanCraft: false, CraftRecipeCount: 0, CanBarter: true, BarterOfferCount: 3)
        );

        Assert.Equal(RecommendationType.SellOnFlea, result.Type);
        Assert.Contains("bartered", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ammoBox", "Ammunition container")]
    [InlineData("gun", "Weapon")]
    [InlineData("meds", "Medical item")]
    [InlineData(null, "Item")]
    [InlineData("customThing", "Custom Thing")]
    public void ItemTypeLabelMapsDeveloperNames(string raw, string expected) =>
        Assert.Equal(expected, ItemTypeLabel.Format(raw));
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

public class SessionHistoryServiceTests
{
    [Fact]
    public void Queue_changes_are_recorded_without_a_page_component()
    {
        ItemQueue queue = new();
        using SessionHistoryService history = new(
            queue,
            scan => ScanResultAdapter.Map(scan, questRemaining: 0, hideoutRemaining: 0, false)
        );
        int changed = 0;
        history.Changed += (_, _) => changed++;

        queue.Enqueue(new DefaultItemScan(CreateItem("seed"), isSeed: true));
        queue.Enqueue(new DefaultItemScan(CreateItem("scanned")));

        ScanResultViewModel result = Assert.Single(history.Items);
        Assert.Equal("scanned", result.Item.Id);
        Assert.True(result.IsHistoricalResult);
        Assert.NotNull(result.ScannedAt);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void History_is_bounded_and_duplicate_items_move_to_the_front()
    {
        ItemQueue queue = new();
        using SessionHistoryService history = new(
            queue,
            scan => ScanResultAdapter.Map(scan, questRemaining: 0, hideoutRemaining: 0, false)
        );

        for (int index = 0; index < 55; index++)
            queue.Enqueue(new DefaultItemScan(CreateItem($"item-{index}")));

        Assert.Equal(50, history.Items.Count);
        Assert.Equal("item-54", history.Items[0].Item.Id);
        Assert.Equal("item-5", history.Items[^1].Item.Id);

        queue.Enqueue(new DefaultItemScan(CreateItem("item-10")));

        Assert.Equal(50, history.Items.Count);
        Assert.Equal("item-10", history.Items[0].Item.Id);
        Assert.Single(history.Items, result => result.Item.Id == "item-10");
    }

    private static Item CreateItem(string id) =>
        new()
        {
            Id = id,
            Name = id,
            ShortName = id,
            Width = 1,
            Height = 1,
        };
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
                RatStashDB = RatStash.Database.FromItems([
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
                ]),
            };

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
        using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            using System.Drawing.Brush brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            graphics.FillEllipse(brush, width / 4, height / 4, width / 2, height / 2);
        }
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

        using RatEye.RatEyeEngine engine = new(config, RatStash.Database.FromItems([]));
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
    public async System.Threading.Tasks.Task FetchCrafts_returns_projected_domain_models()
    {
        Craft[] crafts = await TarkovDevAPI.FetchCraftsAsync(GameMode.Regular);
        // Network may be flaky in CI; when data is returned it must be projected.
        if (crafts.Length == 0)
            return;

        Assert.All(crafts, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
        Assert.Contains(crafts, c => !string.IsNullOrWhiteSpace(c.ProductItemId));
    }

    [Fact]
    public async System.Threading.Tasks.Task FetchBarters_returns_projected_domain_models()
    {
        Barter[] barters = await TarkovDevAPI.FetchBartersAsync(GameMode.Regular);
        if (barters.Length == 0)
            return;

        Assert.All(barters, b => Assert.False(string.IsNullOrWhiteSpace(b.Id)));
        Assert.Contains(barters, b => !string.IsNullOrWhiteSpace(b.OfferedItemId));
    }

    [Theory]
    [InlineData("Gun", "Weapon")]
    [InlineData("gun", "Weapon")]
    [InlineData("Ammo", "Ammunition")]
    [InlineData("meds", "Medical item")]
    public void ItemTypeLabel_tolerates_json_and_legacy_casing(string raw, string expected) =>
        Assert.Equal(expected, ItemTypeLabel.Format(raw));
}

public class GitHubUpdateServiceTests
{
    [Theory]
    // This project uses its own 4.x line and full SemVer precedence.
    [InlineData("4.0.0", "v4.0.1", true)]
    [InlineData("4.0.0+build.1", "4.0.0+build.2", false)]
    [InlineData("4.1.0-beta.1", "4.0.9", false)]
    [InlineData("4.0.0", "4.0.1-beta.1", false)]
    [InlineData("4.0.1-alpha.1", "4.0.1-beta.1", true)]
    [InlineData("4.0.1-beta", "4.0.1-beta.1", true)]
    [InlineData("4.0.1-beta.1", "4.0.1-beta.2", true)]
    [InlineData("4.0.1-beta.2", "4.0.1-beta.11", true)]
    [InlineData("4.0.1-alpha.999999999999999999999", "4.0.1-alpha.1000000000000000000000", true)]
    [InlineData("4.0.1-ALPHA", "4.0.1-alpha", true)]
    [InlineData("4.0.1-beta.11", "4.0.1-rc.1", true)]
    [InlineData("4.0.1-rc.1", "4.0.1", true)]
    [InlineData("4.0.1-beta.2", "4.0.1-beta.1", false)]
    [InlineData("4.0.1-beta.1", "4.0.2-beta.1", true)]
    [InlineData("4.0.1", "4.0.2-beta.1", false)]
    [InlineData("unknown", "4.0.1", false)]
    // Upstream-style 3.x would always be older than a 4.x fork install (and vice versa).
    [InlineData("4.0.0", "v3.9.3", false)]
    [InlineData("3.9.3", "v4.0.0", true)]
    public void IsNewerVersion_handles_release_tag_formats(string current, string available, bool expected) =>
        Assert.Equal(expected, GitHubUpdateService.IsNewerVersion(current, available));

    [Fact]
    public void TryParseVersion_preserves_three_component_system_version_shape()
    {
        bool parsed = GitHubUpdateService.TryParseVersion("v4.0.1-beta.1", out Version version);

        Assert.True(parsed);
        Assert.Equal(new Version(4, 0, 1), version);
        Assert.Equal(-1, version.Revision);
    }

    [Fact]
    public void SelectUpdateRelease_keeps_stable_installs_on_the_latest_stable_channel()
    {
        string json = ReleaseListJson(
            ReleaseJson("v4.0.2-beta.1", prerelease: true),
            ReleaseJson("v4.0.1", prerelease: false)
        );

        GitHubUpdateService.LatestRelease release = GitHubUpdateService.SelectUpdateRelease(
            json,
            "4.0.0",
            includePrereleases: false
        );

        Assert.NotNull(release);
        Assert.Equal("4.0.1", release.Version);
    }

    [Fact]
    public void SelectUpdateRelease_allows_opted_in_prerelease_installs_to_advance()
    {
        string json = ReleaseListJson(
            ReleaseJson("v4.0.1-beta.3", prerelease: true),
            ReleaseJson("v4.0.1-beta.2", prerelease: true),
            ReleaseJson("v4.0.0", prerelease: false)
        );

        GitHubUpdateService.LatestRelease release = GitHubUpdateService.SelectUpdateRelease(
            json,
            "4.0.1-beta.1",
            includePrereleases: true
        );

        Assert.NotNull(release);
        Assert.Equal("4.0.1-beta.3", release.Version);
    }

    [Fact]
    public void SelectUpdateRelease_promotes_prerelease_installs_to_matching_stable()
    {
        string json = ReleaseListJson(
            ReleaseJson("v4.0.1", prerelease: false),
            ReleaseJson("v4.0.1-rc.1", prerelease: true)
        );

        GitHubUpdateService.LatestRelease release = GitHubUpdateService.SelectUpdateRelease(
            json,
            "4.0.1-beta.2",
            includePrereleases: true
        );

        Assert.NotNull(release);
        Assert.Equal("4.0.1", release.Version);
    }

    [Fact]
    public void SelectUpdateRelease_ignores_drafts_and_untrusted_or_missing_assets()
    {
        string json = ReleaseListJson(
            ReleaseJson("v4.0.1-beta.4", prerelease: true, draft: true),
            ReleaseJson("v4.0.1-beta.3", prerelease: true, assetUrl: "https://evil.example/RatScanner.zip"),
            ReleaseJson("v4.0.1-beta.2", prerelease: true)
        );

        GitHubUpdateService.LatestRelease release = GitHubUpdateService.SelectUpdateRelease(
            json,
            "4.0.1-beta.1",
            includePrereleases: true
        );

        Assert.NotNull(release);
        Assert.Equal("4.0.1-beta.2", release.Version);
    }

    private static string ReleaseListJson(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private static string ReleaseJson(string tag, bool prerelease, bool draft = false, string assetUrl = null)
    {
        assetUrl ??= $"https://github.com/tarkovtracker-org/RatScanner/releases/download/{tag}/RatScanner.zip";
        return $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/tarkovtracker-org/RatScanner/releases/tag/{{tag}}",
              "draft": {{draft.ToString().ToLowerInvariant()}},
              "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
              "assets": [
                {
                  "name": "RatScanner.zip",
                  "browser_download_url": "{{assetUrl}}"
                }
              ]
            }
            """;
    }

    [Theory]
    [InlineData("https://github.com/tarkovtracker-org/RatScanner/releases/download/v4.0.0/RatScanner.zip", true)]
    [InlineData(
        "https://objects.githubusercontent.com/github-production-release-asset-2e65be/123/abc?X-Amz-Algorithm=AWS4-HMAC-SHA256",
        true
    )]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/123/abc", true)]
    [InlineData("http://github.com/tarkovtracker-org/RatScanner/releases/download/v4.0.0/RatScanner.zip", false)]
    [InlineData("https://evil.example/RatScanner.zip", false)]
    [InlineData("https://github.evil.com/tarkovtracker-org/RatScanner/RatScanner.zip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not-a-url", false)]
    public void IsAllowedReleaseAssetUrl_accepts_only_https_github_asset_hosts(string url, bool expected) =>
        Assert.Equal(expected, GitHubUpdateService.IsAllowedReleaseAssetUrl(url));
}

public class ItemExtensionTests
{
    [Fact]
    public void Task_requirements_count_find_and_hand_over_as_one_item_need()
    {
        Item item = new() { Id = "ofz" };
        UserProgress progress = new();
        IReadOnlyList<TaskObjective> objectives =
        [
            new TaskObjective
            {
                Id = "find",
                Type = "findItem",
                Count = 5,
                FoundInRaid = true,
                ItemIds = [item.Id],
            },
            new TaskObjective
            {
                Id = "give",
                Type = "giveItem",
                Count = 5,
                FoundInRaid = true,
                ItemIds = [item.Id],
            },
        ];

        RequirementBreakdown result = ItemExtensions.GetTaskRequirementBreakdown(
            item,
            objectives,
            progress,
            showNonFir: true
        );

        Assert.Equal(new RequirementBreakdown(Total: 5, FoundInRaid: 5, NonFoundInRaid: 0), result);
    }

    [Fact]
    public void Task_requirements_use_the_further_progress_of_a_find_and_hand_over_pair()
    {
        Item item = new() { Id = "ofz" };
        UserProgress progress = new()
        {
            TaskObjectives = [new Progress { Id = "find", Count = 4 }, new Progress { Id = "give", Count = 2 }],
        };
        IReadOnlyList<TaskObjective> objectives =
        [
            new TaskObjective
            {
                Id = "find",
                Type = "findItem",
                Count = 5,
                FoundInRaid = true,
                ItemIds = [item.Id],
            },
            new TaskObjective
            {
                Id = "give",
                Type = "giveItem",
                Count = 5,
                FoundInRaid = true,
                ItemIds = [item.Id],
            },
        ];

        RequirementBreakdown result = ItemExtensions.GetTaskRequirementBreakdown(
            item,
            objectives,
            progress,
            showNonFir: true
        );

        Assert.Equal(new RequirementBreakdown(Total: 1, FoundInRaid: 1, NonFoundInRaid: 0), result);
    }

    [Fact]
    public void Task_requirements_keep_distinct_hand_over_objectives_for_the_same_item()
    {
        Item item = new() { Id = "armor" };
        UserProgress progress = new();
        IReadOnlyList<TaskObjective> objectives =
        [
            new TaskObjective
            {
                Id = "low-durability",
                Type = "giveItem",
                Count = 1,
                ItemIds = [item.Id],
            },
            new TaskObjective
            {
                Id = "high-durability",
                Type = "giveItem",
                Count = 1,
                ItemIds = [item.Id],
            },
        ];

        RequirementBreakdown result = ItemExtensions.GetTaskRequirementBreakdown(
            item,
            objectives,
            progress,
            showNonFir: true
        );

        Assert.Equal(new RequirementBreakdown(Total: 2, FoundInRaid: 0, NonFoundInRaid: 2), result);
    }

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
    public void GetWikiLink_prefers_api_link_and_falls_back_to_fandom()
    {
        Item withLink = new() { WikiLink = "https://tarkov.dev/item/foo", Name = "Foo" };
        Assert.Equal("https://tarkov.dev/item/foo", withLink.GetWikiLink());

        Item fallback = new() { Name = "Bolt-action rifle" };
        Assert.Equal("https://escapefromtarkov.fandom.com/wiki/Bolt-action_rifle", fallback.GetWikiLink());
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
