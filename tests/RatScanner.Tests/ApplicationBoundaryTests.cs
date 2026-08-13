#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.FetchModels.TarkovTracker;
using RatScanner.Runtime;
using RatScanner.Scan;
using RatScanner.ViewModel;
using Xunit;
using GameMode = RatScanner.TarkovDev.GameMode;

namespace RatScanner.Tests;

[CollectionDefinition(nameof(ApplicationBoundaryTests), DisableParallelization = true)]
public sealed class ApplicationBoundaryTestCollection;

[Collection(nameof(ApplicationBoundaryTests))]
public sealed class ApplicationBoundaryTests
{
    [Fact]
    public async Task Scanner_settings_apply_runtime_changes_through_injected_contracts()
    {
        bool originalNameScan = RatConfig.NameScan.Enable;
        bool originalIconScan = RatConfig.IconScan.Enable;
        bool originalUseCachedIcons = RatConfig.IconScan.UseCachedIcons;
        FakeScanOrchestrator scans = new();
        FakeHotkeyRegistrar hotkeys = new();
        using SettingsPersistenceService persistence = new(_ => Task.CompletedTask);
        using SettingsVM settings = new(
            new LocalizationService(),
            persistence,
            scans,
            new FakeTrackerService(),
            hotkeys
        );

        try
        {
            SettingSaveResult nameResult = await settings.SetEnableNameScanAsync(!originalNameScan);
            SettingSaveResult iconResult = await settings.SetEnableIconScanAsync(!originalIconScan);
            SettingSaveResult cacheResult = await settings.SetUseCachedIconsAsync(!originalUseCachedIcons);

            Assert.True(nameResult.Succeeded);
            Assert.True(iconResult.Succeeded);
            Assert.True(cacheResult.Succeeded);
            Assert.Equal(2, hotkeys.RegistrationCount);
            Assert.Equal(1, scans.RebuildCount);
        }
        finally
        {
            RatConfig.NameScan.Enable = originalNameScan;
            RatConfig.IconScan.Enable = originalIconScan;
            RatConfig.IconScan.UseCachedIcons = originalUseCachedIcons;
        }
    }

    [Fact]
    public void Menu_view_model_forwards_scan_changes_from_injected_orchestrator()
    {
        FakeScanOrchestrator scans = new();
        MenuVM menu = new(scans, new FakeTrackerService());
        int notifications = 0;
        menu.PropertyChanged += (_, _) => notifications++;

        scans.NotifyChanged();

        Assert.Same(scans.ItemScans, menu.ItemScans);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Tracker_snapshot_resolves_the_current_user_from_one_consistent_state()
    {
        UserProgress teammate = new() { UserId = "team" };
        UserProgress currentUser = new() { UserId = "self" };
        TrackerStateSnapshot snapshot = new(
            new List<UserProgress> { teammate, currentUser },
            "self",
            "token",
            TrackerConnectionState.Connected
        );

        Assert.Same(currentUser, snapshot.CurrentUser);
        Assert.Equal("token", snapshot.Token);
        Assert.Equal(TrackerConnectionState.Connected, snapshot.ConnectionState);
    }

    private sealed class FakeScanOrchestrator : IScanOrchestrator
    {
        public ItemQueue ItemScans { get; } = new();

        public int RebuildCount { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void RebuildEngine() => RebuildCount++;

        public ScanDiagnosticExportResult ExportLastScanDiagnostics() => throw new NotSupportedException();

        internal void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private sealed class FakeTrackerService : ITrackerService
    {
        public TrackerStateSnapshot State { get; } =
            new(Array.Empty<UserProgress>(), "", null, TrackerConnectionState.NotConfigured);

        public Task ActivateModeAsync(GameMode mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TrackerValidationResult> ValidateOrgKeyAsync(
            GameMode mode,
            string token,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<TrackerValidationResult> ValidateIoKeyAsync(
            string token,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
    {
        public int RegistrationCount { get; private set; }

        public void RegisterHotkeys() => RegistrationCount++;
    }
}
