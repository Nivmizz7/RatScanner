using System;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.Scan;
using Xunit;

namespace RatScanner.Tests;

public sealed class ScanThrottleTests
{
    [Fact]
    public void First_acquire_is_allowed()
    {
        ScanThrottle throttle = new(300);
        Assert.True(throttle.TryAcquire(1_000));
    }

    [Fact]
    public void Acquire_within_cooldown_is_blocked()
    {
        ScanThrottle throttle = new(300);
        Assert.True(throttle.TryAcquire(1_000));
        Assert.False(throttle.TryAcquire(1_299));
    }

    [Fact]
    public void Acquire_exactly_at_cooldown_boundary_is_allowed()
    {
        ScanThrottle throttle = new(300);
        Assert.True(throttle.TryAcquire(1_000));
        Assert.True(throttle.TryAcquire(1_300));
    }

    [Fact]
    public void Acquire_after_cooldown_is_allowed()
    {
        ScanThrottle throttle = new(300);
        Assert.True(throttle.TryAcquire(1_000));
        Assert.True(throttle.TryAcquire(2_000));
    }

    [Fact]
    public void Blocked_attempts_do_not_extend_the_window()
    {
        // Spam at 100 ms intervals must not keep pushing the next allowed scan
        // into the future; the window is anchored to the last accepted acquire.
        ScanThrottle throttle = new(300);
        Assert.True(throttle.TryAcquire(1_000));

        Assert.False(throttle.TryAcquire(1_100));
        Assert.False(throttle.TryAcquire(1_200));
        Assert.False(throttle.TryAcquire(1_250));

        // Next scan lands 300 ms after the accepted one, not after the spam.
        Assert.True(throttle.TryAcquire(1_300));
    }

    [Fact]
    public void Zero_cooldown_allows_every_acquire()
    {
        ScanThrottle throttle = new(0);
        Assert.True(throttle.TryAcquire(1_000));
        Assert.True(throttle.TryAcquire(1_001));
    }

    [Fact]
    public void Negative_cooldown_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanThrottle(-1));
    }

    [Fact]
    public void Concurrent_acquires_have_a_single_winner()
    {
        // Multiple hotkey handlers can race; exactly one may acquire per window.
        const int participants = 8;
        const long now = 1_000_000;
        ScanThrottle throttle = new(300);
        using Barrier barrier = new(participants);
        int winners = 0;

        Parallel.For(0, participants, _ =>
        {
            barrier.SignalAndWait();
            if (throttle.TryAcquire(now))
                Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners);
    }
}
