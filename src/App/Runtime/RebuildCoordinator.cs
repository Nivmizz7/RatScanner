using System;
using System.Threading;
using System.Threading.Tasks;

namespace RatScanner.Runtime;

/// <summary>
/// Serializes and coalesces repeated rebuild requests. The first request acquires the
/// gate and runs the rebuild loop; requests that arrive while a rebuild is in flight
/// set the dirty flag and wait on the gate. The in-flight loop observes the flag and
/// runs exactly one follow-up rebuild that picks up the latest configuration, then the
/// waiting requests acquire the gate, find the flag already cleared, and return without
/// doing redundant work. Thread-safe.
/// </summary>
internal sealed class RebuildCoordinator : IDisposable
{
    private readonly Func<CancellationToken, Task> _rebuild;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requested;
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

        // Mark a rebuild as desired before contending for the gate. The volatile write
        // is the release half of the dirty-flag protocol; the loop below reads it with
        // Volatile.Read (acquire) so a request that lands during a rebuild is always seen.
        Volatile.Write(ref _requested, 1);
        return RunAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (Volatile.Read(ref _requested) != 0)
            {
                Volatile.Write(ref _requested, 0);
                cancellationToken.ThrowIfCancellationRequested();
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
