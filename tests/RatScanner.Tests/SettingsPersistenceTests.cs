#nullable enable

using System;
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
    public async Task Failed_persistence_restores_a_null_reference_value()
    {
        string? runtime = null;
        using SettingsPersistenceService service = new(_ => throw new InvalidOperationException("disk full"));

        SettingSaveResult result = await service.SaveImmediateAsync<string?>(
            "nullable",
            "nullable setting",
            "candidate",
            () => runtime,
            value => runtime = value
        );

        Assert.False(result.Succeeded);
        Assert.Null(runtime);
    }

    [Fact]
    public async Task Failed_persistence_restores_a_null_nullable_value()
    {
        int? runtime = null;
        using SettingsPersistenceService service = new(_ => throw new InvalidOperationException("disk full"));

        SettingSaveResult result = await service.SaveImmediateAsync<int?>(
            "nullable-number",
            "nullable numeric setting",
            42,
            () => runtime,
            value => runtime = value
        );

        Assert.False(result.Succeeded);
        Assert.Null(runtime);
    }

    [Fact]
    public async Task Rapid_changes_keep_the_final_value_when_an_older_save_fails()
    {
        bool runtime = false;
        int call = 0;
        using ManualResetEventSlim firstStarted = new(false);
        using ManualResetEventSlim releaseFirst = new(false);
        using SettingsPersistenceService service = new(token =>
        {
            int current = Interlocked.Increment(ref call);
            if (current == 1)
            {
                firstStarted.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(5), token);
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
    public async Task Successful_older_save_keeps_the_value_written_to_disk_for_later_rollback()
    {
        bool runtime = false;
        int call = 0;
        using ManualResetEventSlim firstStarted = new(false);
        using ManualResetEventSlim releaseFirst = new(false);
        using SettingsPersistenceService service = new(token =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                firstStarted.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(5), token);
                return Task.CompletedTask;
            }

            throw new InvalidOperationException("disk full");
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

        Assert.True((await first).Succeeded);
        Assert.False((await second).Succeeded);
        Assert.True(runtime);
    }

    [Fact]
    public async Task Disposing_during_persistence_returns_a_failed_result_instead_of_cancellation()
    {
        using ManualResetEventSlim started = new(false);
        SettingsPersistenceService service = new(async cancellationToken =>
        {
            started.Set();
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        });

        Task<SettingSaveResult> save = service.SaveImmediateAsync("toggle", "test toggle", true, () => false, _ => { });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        service.Dispose();
        SettingSaveResult result = await save;

        Assert.False(result.Succeeded);
        service.Dispose();
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
