#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RatScanner.Runtime;
using RatScanner.Scan;
using RatScanner.TarkovDev;
using RatScanner.ViewModel;
using Xunit;

namespace RatScanner.Tests;

// CA1711: xUnit convention names collection-definition classes "*Collection".
#pragma warning disable CA1711
[CollectionDefinition(nameof(MenuVMDerivedCacheTests), DisableParallelization = true)]
public sealed class MenuVMDerivedCacheTestCollection;
#pragma warning restore CA1711

// Seeding TarkovDevAPI.Cache and reading RatConfig are global static state: run this
// class exclusively so no parallel test can change the locale/game mode or the shared
// cache between the seed and the classification read.
[Collection(nameof(MenuVMDerivedCacheTests))]
public sealed class MenuVMDerivedCacheTests
{
    private static DefaultItemScan CreateScan(string itemId)
    {
        return new DefaultItemScan(
            new Item
            {
                Id = itemId,
                Name = itemId,
                ShortName = itemId,
                Width = 1,
                Height = 1,
            }
        );
    }

    private static TrackerStateSnapshot CreateSnapshot(
        string self = "self",
        IReadOnlyList<FetchModels.TarkovTracker.UserProgress>? teamMembers = null
    )
    {
        List<FetchModels.TarkovTracker.UserProgress> progress = [new() { UserId = self, DisplayName = "Solo" }];
        if (teamMembers != null)
            progress.AddRange(teamMembers);
        return new TrackerStateSnapshot(progress, self, null, TrackerConnectionState.Connected);
    }

    [Fact]
    public void Derived_state_is_reused_while_scan_and_tracker_snapshot_are_unchanged()
    {
        using IDisposable seededCatalog = SeedTarkovDevCatalogWithHideoutNeed("item-a");
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new();
        orchestrator.ItemScans.Enqueue(CreateScan("item-a"));
        MenuVM viewModel = new(orchestrator, tracker);

        MenuVM.DerivedScanState first = viewModel.Derived;
        MenuVM.DerivedScanState second = viewModel.Derived;

        Assert.Same(first, second);
    }

    [Fact]
    public void Derived_state_is_recomputed_when_a_new_scan_is_enqueued()
    {
        using IDisposable seededCatalog = SeedTarkovDevCatalogWithHideoutNeed("item-a");
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new();
        orchestrator.ItemScans.Enqueue(CreateScan("item-a"));
        MenuVM viewModel = new(orchestrator, tracker);

        MenuVM.DerivedScanState first = viewModel.Derived;
        orchestrator.ItemScans.Enqueue(CreateScan("item-b"));

        Assert.NotSame(first, viewModel.Derived);
    }

    [Fact]
    public void Derived_state_is_recomputed_when_the_tracker_snapshot_changes()
    {
        using IDisposable seededCatalog = SeedTarkovDevCatalogWithHideoutNeed("item-a");
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new();
        orchestrator.ItemScans.Enqueue(CreateScan("item-a"));
        MenuVM viewModel = new(orchestrator, tracker);

        MenuVM.DerivedScanState first = viewModel.Derived;

        // Same content, different record instance: the reference-based change
        // detection must still treat this as a tracker data change.
        tracker.State = CreateSnapshot();

        Assert.NotSame(first, viewModel.Derived);
    }

    [Fact]
    public void Derived_state_is_reused_for_the_stable_placeholder_scan()
    {
        using IDisposable seededCatalog = SeedTarkovDevCatalogWithHideoutNeed("item-a");
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new();
        MenuVM viewModel = new(orchestrator, tracker);

        Assert.Same(viewModel.LastItemScan, viewModel.LastItemScan);
        Assert.Same(viewModel.Derived, viewModel.Derived);
    }

    [Fact]
    public void Derived_state_is_recomputed_when_the_scan_item_is_swapped_in_place()
    {
        using IDisposable seededCatalog = SeedTarkovDevCatalogWithHideoutNeed("item-a");
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new();
        DefaultItemScan scan = CreateScan("item-a");
        orchestrator.ItemScans.Enqueue(scan);
        MenuVM viewModel = new(orchestrator, tracker);

        MenuVM.DerivedScanState first = viewModel.Derived;

        // RefreshItemsForGameMode replaces Item on already-enqueued scans (same id,
        // fresh catalog instance); the cache must not keep serving classification
        // computed against the previous catalog.
        scan.Item = CreateScan("item-a").Item;

        Assert.NotSame(first, viewModel.Derived);
    }

    [Fact]
    public void Derived_state_is_recomputed_when_show_non_fir_needs_changes()
    {
        using IDisposable seededCatalog = SeedTarkovDevCatalogWithHideoutNeed("item-a");
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new();
        orchestrator.ItemScans.Enqueue(CreateScan("item-a"));
        MenuVM viewModel = new(orchestrator, tracker);

        bool original = RatConfig.Tracking.ShowNonFIRNeeds;
        try
        {
            MenuVM.DerivedScanState first = viewModel.Derived;
            RatConfig.Tracking.ShowNonFIRNeeds = !original;

            // The classifier reads this preference, so a toggle must invalidate the
            // cached classification even when scan and tracker snapshot are unchanged.
            Assert.NotSame(first, viewModel.Derived);
        }
        finally
        {
            RatConfig.Tracking.ShowNonFIRNeeds = original;
        }
    }

    [Fact]
    public void Team_needs_are_part_of_the_shared_derived_state()
    {
        using IDisposable seededCatalog = SeedTarkovDevCatalogWithHideoutNeed("item-a");
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new()
        {
            State = CreateSnapshot(
                teamMembers: [new FetchModels.TarkovTracker.UserProgress { UserId = "mate", DisplayName = "Mate" }]
            ),
        };
        orchestrator.ItemScans.Enqueue(CreateScan("item-a"));

        MenuVM viewModel = new(orchestrator, tracker);
        MenuVM.DerivedScanState derived = viewModel.Derived;

        // The teammate has no task needs and a seeded 3-item hideout need, so the
        // derived team needs must reflect that progress rather than being null.
        Assert.NotNull(derived.TeamNeeds);
        Assert.Same(derived.TeamNeeds, viewModel.ItemTeamNeeds);
        KeyValuePair<string, KeyValuePair<int, int>> mateNeed = Assert.Single(derived.TeamNeeds);
        Assert.Equal("Mate", mateNeed.Key);
        Assert.Equal(new KeyValuePair<int, int>(0, 3), mateNeed.Value);
    }

    [Fact]
    public void Team_needs_disambiguate_duplicate_display_names_without_an_upper_bound()
    {
        // 99 members all named "Mate": the 99th needs the "#99" suffix, which the
        // previous 98-suffix loop could not produce (it capped at "#98" and added a
        // duplicate key without re-checking the final candidate).
        const int memberCount = 99;
        using IDisposable seededCatalog = SeedTarkovDevCatalogWithHideoutNeed("item-a");
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new()
        {
            State = CreateSnapshot(
                teamMembers: Enumerable
                    .Range(1, memberCount)
                    .Select(i => new FetchModels.TarkovTracker.UserProgress
                    {
                        UserId = $"mate-{i}",
                        DisplayName = "Mate",
                    })
                    .ToList()
            ),
        };
        orchestrator.ItemScans.Enqueue(CreateScan("item-a"));

        MenuVM viewModel = new(orchestrator, tracker);
        MenuVM.DerivedScanState derived = viewModel.Derived;

        Assert.NotNull(derived.TeamNeeds);
        List<string> names = derived.TeamNeeds.Select(need => need.Key).ToList();
        Assert.Equal("Mate", names[0]);
        Assert.Equal("Mate #2", names[1]);
        Assert.Equal($"Mate #{memberCount}", names[^1]);
        Assert.Equal(memberCount, names.Distinct().Count());
    }

    // Reaches into TarkovDevAPI's private in-memory cache so ComputeTeamNeeds sees a
    // hideout need for the synthetic scan item without any network access. Restores the
    // previous cache entries on dispose. Keys mirror TarkovDevAPI's private query-key
    // format.
    private static IDisposable SeedTarkovDevCatalogWithHideoutNeed(string itemId)
    {
        ConcurrentDictionary<string, (long expire, object response)> cache = TarkovDevCache();
        string locale = RatConfig.NameScan.Language.ToTarkovDevLocale();
        string mode = RatConfig.GameMode.ToString();
        string tasksKey = $"tasks_v2_{locale}_{mode}";
        string hideoutKey = $"hideout_{locale}_{mode}";
        long expire = DateTimeOffset.Now.ToUnixTimeSeconds() + RatConfig.LongTTL;

        bool hadTasks = cache.TryGetValue(tasksKey, out (long expire, object response) previousTasks);
        bool hadHideout = cache.TryGetValue(hideoutKey, out (long expire, object response) previousHideout);

        // Empty task catalog keeps task needs at zero and avoids a fire-and-forget fetch.
        cache[tasksKey] = (expire, Array.Empty<RatScanner.TarkovDev.Task>());
        cache[hideoutKey] = (
            expire,
            new HideoutStation[]
            {
                new()
                {
                    Id = "station-1",
                    Levels = new List<HideoutStationLevel>
                    {
                        new()
                        {
                            Id = "level-1",
                            ItemRequirements = new List<RequirementItem>
                            {
                                new()
                                {
                                    Id = "requirement-1",
                                    ItemId = itemId,
                                    Count = 3,
                                },
                            },
                        },
                    },
                },
            }
        );

        return new DisposeAction(() =>
        {
            if (hadTasks)
                cache[tasksKey] = previousTasks;
            else
                cache.TryRemove(tasksKey, out _);
            if (hadHideout)
                cache[hideoutKey] = previousHideout;
            else
                cache.TryRemove(hideoutKey, out _);
        });
    }

    private static readonly FieldInfo TarkovDevCacheField =
        typeof(TarkovDevAPI).GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TarkovDevAPI.Cache field is missing.");

    private static ConcurrentDictionary<string, (long expire, object response)> TarkovDevCache() =>
        (ConcurrentDictionary<string, (long expire, object response)>)TarkovDevCacheField.GetValue(null)!;

    private sealed class DisposeAction(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
