#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.Runtime;
using Xunit;

namespace RatScanner.Tests;

public sealed class RebuildCoordinatorTests
{
    [Fact]
    public async Task Single_request_runs_the_rebuild_once()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        int rebuildCount = 0;
        using RebuildCoordinator coordinator = new(_ =>
        {
            rebuildCount++;
            return Task.CompletedTask;
        });

        await coordinator.RequestAsync(testCancellation);

        Assert.Equal(1, rebuildCount);
    }

    [Fact]
    public async Task Concurrent_requests_coalesce_into_one_followup_rebuild()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        int rebuildCount = 0;
        TaskCompletionSource firstRebuildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstRebuild = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Rebuild(CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref rebuildCount);
            if (current == 1)
            {
                firstRebuildStarted.TrySetResult();
                await releaseFirstRebuild.Task.ConfigureAwait(false);
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        using RebuildCoordinator coordinator = new(Rebuild);
        Task first = coordinator.RequestAsync(testCancellation);

        // Wait until the first rebuild is provably inside the delegate.
        await firstRebuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        // Queue additional requests while the first rebuild is still in flight.
        Task second = coordinator.RequestAsync(testCancellation);
        Task third = coordinator.RequestAsync(testCancellation);

        // Release the first rebuild; the dirty flag must trigger exactly one
        // follow-up, not one rebuild per queued request.
        releaseFirstRebuild.TrySetResult();

        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        Assert.Equal(2, rebuildCount);
    }

    [Fact]
    public async Task Cancelled_request_throws_without_running_the_rebuild()
    {
        int rebuildCount = 0;
        using RebuildCoordinator coordinator = new(_ =>
        {
            rebuildCount++;
            return Task.CompletedTask;
        });
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RequestAsync(cts.Token));

        Assert.Equal(0, rebuildCount);
    }
}
