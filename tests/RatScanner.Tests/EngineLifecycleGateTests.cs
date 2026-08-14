#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.Runtime;
using Xunit;

namespace RatScanner.Tests;

public sealed class EngineLifecycleGateTests
{
    [Fact]
    public async Task Stop_does_not_wait_for_build_and_discards_late_replacement()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        TaskCompletionSource buildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestResource? published = null;
        TestResource replacement = new();
        using EngineLifecycleGate<TestResource> gate = new();
        TaskCompletionSource<bool> rebuildCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread rebuildThread = new(() =>
        {
            try
            {
                bool wasPublished = gate.BuildAndPublish(
                    () =>
                    {
                        buildStarted.TrySetResult();
                        releaseBuild.Task.GetAwaiter().GetResult();
                        return replacement;
                    },
                    resource =>
                    {
                        TestResource? previous = published;
                        published = resource;
                        return previous;
                    }
                );
                rebuildCompletion.TrySetResult(wasPublished);
            }
            catch (Exception exception)
            {
                rebuildCompletion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "EngineLifecycleGateTests.Build",
        };
        rebuildThread.Start();

        try
        {
            await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);
            Task stop = Task.Run(() => gate.Stop(() => published?.Dispose()), testCancellation);
            await stop.WaitAsync(TimeSpan.FromSeconds(1), testCancellation);
        }
        finally
        {
            releaseBuild.TrySetResult();
        }
        bool wasPublished = await rebuildCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);

        Assert.False(wasPublished);
        Assert.Null(published);
        Assert.True(replacement.IsDisposed);
    }

    [Fact]
    public void Published_replacement_displaces_and_disposes_previous_resource()
    {
        TestResource? published = new();
        TestResource previous = published;
        TestResource replacement = new();
        using EngineLifecycleGate<TestResource> gate = new();

        bool wasPublished = gate.BuildAndPublish(
            () => replacement,
            resource =>
            {
                TestResource? displaced = published;
                published = resource;
                return displaced;
            }
        );

        Assert.True(wasPublished);
        Assert.Same(replacement, published);
        Assert.True(previous.IsDisposed);
        Assert.False(replacement.IsDisposed);
    }

    [Fact]
    public void Publication_failure_disposes_unpublished_replacement()
    {
        TestResource replacement = new();
        InvalidOperationException failure = new("publication failed");
        using EngineLifecycleGate<TestResource> gate = new();

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            gate.BuildAndPublish(() => replacement, _ => throw failure)
        );

        Assert.Same(failure, actual);
        Assert.True(replacement.IsDisposed);
    }

    private sealed class TestResource : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
