#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RatScanner.Tests;

public sealed class SettingsPersistenceTests
{
    [Fact]
    public async Task Immediate_setting_applies_and_persists_without_a_page_save()
    {
        bool runtime = false;
        int persisted = 0;
        using SettingsPersistenceService service = new(_ =>
        {
            persisted++;
            return Task.CompletedTask;
        });

        SettingSaveResult result = await service.SaveImmediateAsync(
            "toggle",
            "test toggle",
            true,
            () => runtime,
            value => runtime = value
        );

        Assert.True(result.Succeeded);
        Assert.True(runtime);
        Assert.Equal(1, persisted);
    }

    [Fact]
    public async Task Failed_persistence_restores_the_last_persisted_value()
    {
        bool runtime = false;
        using SettingsPersistenceService service = new(_ => throw new InvalidOperationException("disk full"));

        SettingSaveResult result = await service.SaveImmediateAsync(
            "toggle",
            "test toggle",
            true,
            () => runtime,
            value => runtime = value
        );

        Assert.False(result.Succeeded);
        Assert.False(runtime);
    }

    [Fact]
    public async Task Rapid_changes_keep_the_final_value_when_an_older_save_fails()
    {
        bool runtime = false;
        int call = 0;
        using ManualResetEventSlim firstStarted = new(false);
        using ManualResetEventSlim releaseFirst = new(false);
        using SettingsPersistenceService service = new(_ =>
        {
            int current = Interlocked.Increment(ref call);
            if (current == 1)
            {
                firstStarted.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("first failed");
            }
            return Task.CompletedTask;
        });

        Task<SettingSaveResult> first = service.SaveImmediateAsync(
            "toggle",
            "test toggle",
            true,
            () => runtime,
            value => runtime = value
        );
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Task<SettingSaveResult> second = service.SaveImmediateAsync(
            "toggle",
            "test toggle",
            false,
            () => runtime,
            value => runtime = value
        );
        releaseFirst.Set();

        Assert.False((await first).Succeeded);
        Assert.True((await second).Succeeded);
        Assert.False(runtime);
    }

    [Fact]
    public async Task Invalid_numeric_value_does_not_apply_or_persist()
    {
        int runtime = 10;
        int persisted = 0;
        using SettingsPersistenceService service = new(_ =>
        {
            persisted++;
            return Task.CompletedTask;
        });

        SettingSaveResult result = await service.SaveValidatedAsync(
            "number",
            "test number",
            -1,
            () => runtime,
            value => runtime = value,
            value => value > 0 ? null : "Must be positive."
        );

        Assert.False(result.Succeeded);
        Assert.Equal("Must be positive.", result.ErrorMessage);
        Assert.Equal(10, runtime);
        Assert.Equal(0, persisted);
    }
}
