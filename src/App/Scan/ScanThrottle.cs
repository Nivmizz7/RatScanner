using System;
using System.Threading;

namespace RatScanner.Scan;

/// <summary>
/// Rate-limits scan requests so hotkey spam cannot drive the OCR pipeline and
/// the overlay compositor at unbounded rate. Only successful acquisitions
/// advance the timestamp, so blocked (spam) attempts do not extend the window.
/// Thread-safe: the CompareExchange guarantees a single winner per window even
/// when multiple hotkey handlers race.
/// </summary>
internal sealed class ScanThrottle
{
    private readonly long _cooldownMs;

    // A dedicated sentinel lets the first request pass even immediately after
    // system startup, when the monotonic clock can still be below the cooldown.
    private long _lastAcceptedMs = long.MinValue;

    internal ScanThrottle(long cooldownMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cooldownMs);
        _cooldownMs = cooldownMs;
    }

    /// <summary>
    /// Returns true when a scan may start now. <paramref name="nowMs"/> must be
    /// elapsed milliseconds from a monotonic source such as
    /// <see cref="Environment.TickCount64"/>. Supplying the timestamp keeps the
    /// class deterministic in tests.
    /// </summary>
    internal bool TryAcquire(long nowMs)
    {
        long last = Interlocked.Read(ref _lastAcceptedMs);
        if (last != long.MinValue && (nowMs < last || nowMs - last < _cooldownMs))
            return false;
        return Interlocked.CompareExchange(ref _lastAcceptedMs, nowMs, last) == last;
    }
}
