#nullable enable

using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.Runtime;
using RatScanner.Scan;
using GameMode = RatScanner.TarkovDev.GameMode;

namespace RatScanner.Tests;

/// <summary>
/// Shared test doubles for the App-owned scan/tracker contracts used by MenuVM and
/// application-boundary tests. Kept in one place so suites exercise the same fake
/// behavior instead of drifting private copies.
/// </summary>
internal sealed class FakeScanOrchestrator : IScanOrchestrator
{
    public ItemQueue ItemScans { get; } = new();

    public int RebuildCount { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Task RebuildEngineAsync(CancellationToken cancellationToken = default)
    {
        RebuildCount++;
        return Task.CompletedTask;
    }

    public ScanDiagnosticExportResult ExportLastScanDiagnostics() => default;

    internal void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

internal sealed class FakeTrackerService : ITrackerService
{
    public TrackerStateSnapshot State { get; set; } =
        new(Array.Empty<FetchModels.TarkovTracker.UserProgress>(), "", null, TrackerConnectionState.NotConfigured);

    public Task ActivateModeAsync(GameMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<TrackerValidationResult> ValidateOrgKeyAsync(
        GameMode mode,
        string token,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(TrackerValidationResult.Success);
}
