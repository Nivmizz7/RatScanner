using System;
using System.Threading;
using System.Threading.Tasks;

namespace RatScanner.Runtime;

/// <summary>
/// Serializes rebuilds and coalesces requests that arrive during an in-flight rebuild
/// into one follow-up batch. Each caller waits for the batch containing its request.
/// Thread-safe.
/// </summary>
internal sealed class RebuildCoordinator : IDisposable
{
    private sealed class RebuildBatch
    {
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ActiveRequests;
        internal bool IsClaimed;
    }

    private readonly Func<CancellationToken, Task> _rebuild;
    private readonly object _stateLock = new();
    private RebuildBatch? _pendingBatch;
    private bool _runnerActive;
    private bool _disposed;

    internal RebuildCoordinator(Func<CancellationToken, Task> rebuild)
    {
        _rebuild = rebuild ?? throw new ArgumentNullException(nameof(rebuild));
    }

    /// <summary>
    /// Requests a rebuild, coalescing with any rebuild already in progress.
    /// Cancellation stops this caller from waiting; it does not cancel shared work
    /// that has already been claimed for execution.
    /// </summary>
    internal Task RequestAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RebuildBatch batch;
        bool startRunner;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            batch = _pendingBatch ??= new RebuildBatch();
            batch.ActiveRequests++;
            startRunner = !_runnerActive;
            if (startRunner)
                _runnerActive = true;
        }

        if (startRunner)
            _ = RunBatchesAsync();

        return WaitForBatchAsync(batch, cancellationToken);
    }

    private async Task WaitForBatchAsync(RebuildBatch batch, CancellationToken cancellationToken)
    {
        try
        {
            await batch.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                if (!batch.IsClaimed)
                {
                    batch.ActiveRequests--;
                    if (batch.ActiveRequests == 0 && ReferenceEquals(_pendingBatch, batch))
                        _pendingBatch = null;
                }
            }

            throw;
        }
    }

    private async Task RunBatchesAsync()
    {
        while (true)
        {
            RebuildBatch batch;
            lock (_stateLock)
            {
                if (_pendingBatch is null)
                {
                    _runnerActive = false;
                    return;
                }

                batch = _pendingBatch;
                _pendingBatch = null;
                batch.IsClaimed = true;
            }

            try
            {
                await _rebuild(CancellationToken.None).ConfigureAwait(false);
                batch.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                batch.Completion.TrySetException(exception);
            }
        }
    }

    public void Dispose()
    {
        RebuildBatch? abandonedBatch;
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            abandonedBatch = _pendingBatch;
            _pendingBatch = null;
        }

        abandonedBatch?.Completion.TrySetException(new ObjectDisposedException(nameof(RebuildCoordinator)));
    }
}
