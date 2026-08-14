using System;
using System.Threading;
using System.Threading.Tasks;

namespace RatScanner.Runtime;

/// <summary>
/// Serializes and coalesces repeated rebuild requests. The first request acquires the
/// gate and runs the rebuild loop; requests that arrive while a rebuild is in flight
/// bump a pending counter and wait on the gate. The in-flight loop observes the counter
/// and runs exactly one follow-up rebuild that picks up the latest configuration, then
/// the waiting requests acquire the gate, find the counter already drained, and return
/// without doing redundant work. Thread-safe.
/// </summary>
internal sealed class RebuildCoordinator : IDisposable
{
    private readonly Func<CancellationToken, Task> _rebuild;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pending;
    private bool _disposed;

    internal RebuildCoordinator(Func<CancellationToken, Task> rebuild)
    {
        _rebuild = rebuild ?? throw new ArgumentNullException(nameof(rebuild));
    }

    /// <summary>
    /// Requests a rebuild, coalescing with any rebuild already in progress.
    /// The returned task completes when this request's rebuild has finished.
    /// </summary>
    internal Task RequestAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Count the request before contending for the gate so the in-flight loop can
        // observe it. A counter (rather than a single dirty bit) lets a request that is
        // cancelled while waiting remove exactly its own contribution without disturbing
        // a concurrent waiter's.
        Interlocked.Increment(ref _pending);
        return RunAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The request never entered the loop; undo its contribution so an in-flight
            // (or future) rebuild does not run a follow-up for a cancelled request.
            Interlocked.Decrement(ref _pending);
            throw;
        }

        try
        {
            while (Volatile.Read(ref _pending) > 0)
            {
                // Check cancellation before draining so a cancelled holder exits without
                // consuming a waiting request's pending count; that waiter then acquires
                // the gate and runs the rebuild itself.
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref _pending, 0);
                await _rebuild(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // Only WaitAsync is used, so the SemaphoreSlim never materializes an OS wait
        // handle and holds no unmanaged resource. Deliberately do not dispose _gate:
        // disposing it during shutdown would race an in-flight RunAsync that is about
        // to Release() and surface a spurious ObjectDisposedException on exit.
        _disposed = true;
    }
}
