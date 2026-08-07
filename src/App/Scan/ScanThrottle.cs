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

    // 0 is a safe "never acquired" sentinel: real epoch-millisecond timestamps
    // are far above any cooldown value, so the first acquire always passes and
    // the addition cannot overflow for any realistic timestamp.
    private long _lastAcceptedMs;

    internal ScanThrottle(long cooldownMs)
    {
        if (cooldownMs < 0)
            throw new ArgumentOutOfRangeException(nameof(cooldownMs));
        _cooldownMs = cooldownMs;
    }

    /// <summary>
    /// Returns true when a scan may start now. Passing the wall-clock time in
    /// keeps the class deterministic and unit-testable.
    /// </summary>
    internal bool TryAcquire(long nowMs)
    {
        long last = Interlocked.Read(ref _lastAcceptedMs);
        if (nowMs < last + _cooldownMs)
            return false;
        return Interlocked.CompareExchange(ref _lastAcceptedMs, nowMs, last) == last;
    }
}
