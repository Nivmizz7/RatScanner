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

        // Release the first rebuild; the pending batch must trigger exactly one
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

    [Fact]
    public async Task Cancelled_waiter_does_not_trigger_an_extra_followup_rebuild()
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
        await firstRebuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        // A second request waits on the gate, then its token is cancelled while the
        // first rebuild is still in flight. Its pending contribution must be removed so
        // the in-flight loop does not run a follow-up rebuild for a cancelled request.
        using CancellationTokenSource cts = new();
        Task cancelledWaiter = coordinator.RequestAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);

        releaseFirstRebuild.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        Assert.Equal(1, rebuildCount);
    }

    [Fact]
    public async Task Cancelled_holder_preserves_a_waiting_request_for_the_next_loop()
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
        using CancellationTokenSource holderCts = new();
        Task holder = coordinator.RequestAsync(holderCts.Token);
        await firstRebuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        // A second request arrives while the first (holder) rebuild is in flight.
        Task waiter = coordinator.RequestAsync(testCancellation);

        // Cancel the holder while it is still inside rebuild #1. It must exit without
        // consuming the waiter's pending count so the waiter runs the rebuild itself.
        holderCts.Cancel();
        releaseFirstRebuild.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holder);
        await waiter.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        Assert.Equal(2, rebuildCount);
    }

    [Fact]
    public async Task Cancelled_claimed_waiter_does_not_drop_a_later_request()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        int rebuildCount = 0;
        TaskCompletionSource firstRebuildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondRebuildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstRebuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecondRebuild = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Rebuild(CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref rebuildCount);
            if (current == 1)
            {
                firstRebuildStarted.TrySetResult();
                await releaseFirstRebuild.Task.ConfigureAwait(false);
            }
            else if (current == 2)
            {
                secondRebuildStarted.TrySetResult();
                await releaseSecondRebuild.Task.ConfigureAwait(false);
            }
        }

        using RebuildCoordinator coordinator = new(Rebuild);
        Task first = coordinator.RequestAsync(testCancellation);
        await firstRebuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        using CancellationTokenSource claimedWaiterCts = new();
        Task claimedWaiter = coordinator.RequestAsync(claimedWaiterCts.Token);
        releaseFirstRebuild.TrySetResult();
        await secondRebuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        // The second request's batch is already claimed. Cancelling its wait must not
        // mutate pending work, and a request made during rebuild #2 must run rebuild #3.
        claimedWaiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => claimedWaiter);
        Task laterRequest = coordinator.RequestAsync(testCancellation);

        releaseSecondRebuild.TrySetResult();
        await Task.WhenAll(first, laterRequest).WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        Assert.Equal(3, rebuildCount);
    }
}
