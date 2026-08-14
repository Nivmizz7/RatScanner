using System;

namespace RatScanner.Runtime;

/// <summary>
/// Serializes resource construction while allowing shutdown to stop publication without
/// waiting for a construction already in progress. A replacement completed after stop
/// is disposed instead of becoming visible to callers.
/// </summary>
internal sealed class EngineLifecycleGate<T> : IDisposable
    where T : class, IDisposable
{
    private readonly object _buildLock = new();
    private readonly object _stateLock = new();
    private bool _stopping;

    internal bool BuildAndPublish(Func<T> build, Func<T, T?> publish)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(publish);

        lock (_buildLock)
        {
            lock (_stateLock)
                ObjectDisposedException.ThrowIf(_stopping, this);

            T replacement = build();
            T? displaced = null;
            bool wasPublished = false;
            try
            {
                lock (_stateLock)
                {
                    if (!_stopping)
                    {
                        displaced = publish(replacement);
                        wasPublished = true;
                    }
                }
            }
            finally
            {
                if (!wasPublished)
                    replacement.Dispose();
            }

            displaced?.Dispose();

            return wasPublished;
        }
    }

    internal bool Stop(Action disposePublished)
    {
        ArgumentNullException.ThrowIfNull(disposePublished);

        lock (_stateLock)
        {
            if (_stopping)
                return false;

            _stopping = true;
            disposePublished();
            return true;
        }
    }

    public void Dispose() => Stop(static () => { });
}
