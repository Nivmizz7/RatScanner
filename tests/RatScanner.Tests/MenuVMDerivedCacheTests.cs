using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using RatScanner.Runtime;
using RatScanner.Scan;
using RatScanner.TarkovDev;
using RatScanner.ViewModel;
using Xunit;

namespace RatScanner.Tests;

public sealed class MenuVMDerivedCacheTests
{
    private sealed class FakeScanOrchestrator : IScanOrchestrator
    {
        public ItemQueue ItemScans { get; } = new();

        public event PropertyChangedEventHandler PropertyChanged;

        public void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

        public System.Threading.Tasks.Task RebuildEngineAsync(CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.CompletedTask;

        public ScanDiagnosticExportResult ExportLastScanDiagnostics() => default;
    }

    private sealed class FakeTrackerService : ITrackerService
    {
        internal TrackerStateSnapshot Snapshot { get; set; } =
            new(new List<FetchModels.TarkovTracker.UserProgress>(), "", null, TrackerConnectionState.NotConfigured);

        public TrackerStateSnapshot State => Snapshot;

        public System.Threading.Tasks.Task ActivateModeAsync(
            GameMode mode,
            CancellationToken cancellationToken = default
        ) => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task<TrackerValidationResult> ValidateOrgKeyAsync(
            GameMode mode,
            string token,
            CancellationToken cancellationToken = default
        ) => System.Threading.Tasks.Task.FromResult(TrackerValidationResult.Success);
    }

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

    private static TrackerStateSnapshot CreateSnapshot(string self = "self")
    {
        return new TrackerStateSnapshot(
            new List<FetchModels.TarkovTracker.UserProgress>
            {
                new() { UserId = self, DisplayName = "Solo" },
            },
            self,
            null,
            TrackerConnectionState.Connected
        );
    }

    [Fact]
    public void Derived_state_is_reused_while_scan_and_tracker_snapshot_are_unchanged()
    {
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
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new();
        orchestrator.ItemScans.Enqueue(CreateScan("item-a"));
        MenuVM viewModel = new(orchestrator, tracker);

        MenuVM.DerivedScanState first = viewModel.Derived;

        // Same content, different record instance: the reference-based change
        // detection must still treat this as a tracker data change.
        tracker.Snapshot = CreateSnapshot();

        Assert.NotSame(first, viewModel.Derived);
    }

    [Fact]
    public void Derived_state_is_reused_for_the_stable_placeholder_scan()
    {
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new();
        MenuVM viewModel = new(orchestrator, tracker);

        Assert.Same(viewModel.LastItemScan, viewModel.LastItemScan);
        Assert.Same(viewModel.Derived, viewModel.Derived);
    }

    [Fact]
    public void Team_needs_are_part_of_the_shared_derived_state()
    {
        FakeScanOrchestrator orchestrator = new();
        FakeTrackerService tracker = new();
        orchestrator.ItemScans.Enqueue(CreateScan("item-a"));
        MenuVM viewModel = new(orchestrator, tracker);

        MenuVM.DerivedScanState derived = viewModel.Derived;

        Assert.Same(derived.TeamNeeds, viewModel.ItemTeamNeeds);
    }
}
