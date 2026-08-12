using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RatScanner.Diagnostics;

/// <summary>
/// Owns the application's performance traces: one startup timeline, a rolling
/// window of recent scan timelines, and monotonic counters for the repeated
/// events that cost GPU/CPU over time (overlay show/hide, renderer suspend and
/// resume, content-fit window resizes, scan-engine rebuilds).
/// </summary>
/// <remarks>
/// <para>
/// Design intent: a user who hits a performance problem should be able to attach
/// their normal log — or one diagnostics export — and have that be sufficient to
/// identify the slow stage without a local reproduction. Collection is therefore
/// always on; only log verbosity depends on <see cref="RatConfig.LogDebug"/>.
/// </para>
/// <para>
/// A scan's timeline does not end when the scan method returns: the overlay
/// resumes and paints, and the main window re-renders, on other threads
/// afterwards. The in-flight trace therefore stays open and is finalized by
/// whichever comes first — the next scan, a report request, or the finalize
/// timer. Reporters pass the sequence number they observed so a late arrival
/// cannot be attributed to the following scan.
/// </para>
/// </remarks>
internal static class PerfTraceStore
{
    /// <summary>Scan timelines kept for export. Bounded so memory stays flat in long sessions.</summary>
    private const int RecentCapacity = 32;

    /// <summary>
    /// A scan slower than this gets its full timeline written to the log even when
    /// debug logging is off, so outliers are captured the first time they happen.
    /// </summary>
    private const double ScanBudgetMs = 400;

    /// <summary>How long to wait for async tail stages (overlay paint, UI render) before finalizing.</summary>
    private static readonly TimeSpan ScanFinalizeDelay = TimeSpan.FromMilliseconds(2500);

    /// <summary>Scans between counter blocks in the log.</summary>
    private const int CounterLogInterval = 10;

    private static readonly object Sync = new();
    private static readonly Queue<PerfTraceSnapshot> RecentScans = new(RecentCapacity);
    private static readonly ConcurrentDictionary<string, long> Counters = new(StringComparer.Ordinal);

    private static PerfTrace? _startupTrace;
    private static PerfTraceSnapshot? _startupSnapshot;
    private static PerfTrace? _scanTrace;
    private static long _scanSequence;
    private static long _finalizedScans;
    private static Timer? _finalizeTimer;

    /// <summary>
    /// The startup timeline. Created on first access, which happens in
    /// <c>App.OnStartup</c> before any other timed work.
    /// </summary>
    internal static PerfTrace Startup
    {
        get
        {
            lock (Sync)
                return _startupTrace ??= PerfTrace.Start("startup");
        }
    }

    /// <summary>
    /// Closes the startup timeline and logs it. Called once the first UI frame is
    /// on screen; later calls are ignored so the value reflects time-to-usable.
    /// </summary>
    internal static void CompleteStartup()
    {
        PerfTraceSnapshot snapshot;
        lock (Sync)
        {
            if (_startupTrace is null || _startupSnapshot is not null)
                return;
            snapshot = _startupTrace.Complete();
            _startupSnapshot = snapshot;
        }

        // Startup happens once per session and is a common complaint, so always
        // log the full breakdown rather than only the summary line.
        foreach (string line in snapshot.ToDetailLines())
            Logger.LogInfo(line);

        // Environment goes into the ordinary log too. A scan timeline is not
        // interpretable without knowing the display layout, refresh rates and
        // virtual-screen size, and the log is what users actually attach to a
        // report. Captured off the UI thread because it enumerates processes.
        _ = Task.Run(LogEnvironment);
    }

    private static void LogEnvironment()
    {
        try
        {
            PerfEnvironmentSnapshot machine = PerfEnvironment.Capture();
            foreach (
                string line in PerfReportBuilder
                    .DescribeMachine(machine)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            )
                Logger.LogInfo("perf env " + line.TrimEnd());
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to capture the performance environment snapshot.", exception);
        }
    }

    internal static PerfTraceSnapshot? StartupSnapshot
    {
        get
        {
            lock (Sync)
                return _startupSnapshot;
        }
    }

    /// <summary>
    /// Opens a scan timeline, finalizing any previous one that is still waiting on
    /// async tail stages.
    /// </summary>
    /// <param name="kind">Scan entry point, for example <c>name-scan</c>.</param>
    internal static PerfTrace BeginScan(string kind)
    {
        PerfTrace? previous;
        PerfTrace trace;
        lock (Sync)
        {
            previous = _scanTrace;
            _scanTrace = trace = PerfTrace.Start(kind, ++_scanSequence);
        }

        if (previous is not null)
            Finalize(previous);

        ArmFinalizeTimer();
        return trace;
    }

    /// <summary>Sequence of the scan currently collecting stages, or 0 when none is open.</summary>
    internal static long CurrentScanSequence
    {
        get
        {
            lock (Sync)
                return _scanTrace?.Sequence ?? 0;
        }
    }

    /// <summary>
    /// Adds a stage measured by a downstream component (overlay paint, main-window
    /// render). Ignored when the sequence no longer matches the open trace, which
    /// keeps a late render from being blamed on the next scan.
    /// </summary>
    internal static void RecordScanStage(long sequence, string stage, double durationMs)
    {
        lock (Sync)
        {
            if (_scanTrace is null || _scanTrace.Sequence != sequence)
                return;
            _scanTrace.Record(stage, durationMs);
        }
    }

    /// <summary>Marks an instant on the open scan timeline.</summary>
    internal static void MarkScan(long sequence, string stage)
    {
        lock (Sync)
        {
            if (_scanTrace is null || _scanTrace.Sequence != sequence)
                return;
            _scanTrace.Mark(stage);
        }
    }

    /// <summary>Attaches context to the open scan timeline.</summary>
    internal static void NoteScan(long sequence, string key, string? value)
    {
        lock (Sync)
        {
            if (_scanTrace is null || _scanTrace.Sequence != sequence)
                return;
            _scanTrace.Note(key, value);
        }
    }

    /// <summary>
    /// Finalizes the open scan timeline immediately. Used by the last expected
    /// reporter so the log line appears promptly instead of after the timer.
    /// </summary>
    internal static void CompleteScan(long sequence)
    {
        PerfTrace? trace;
        lock (Sync)
        {
            if (_scanTrace is null || _scanTrace.Sequence != sequence)
                return;
            trace = _scanTrace;
            _scanTrace = null;
        }
        Finalize(trace);
    }

    /// <summary>Increments a named event counter.</summary>
    internal static void Increment(string counter, long by = 1) =>
        Counters.AddOrUpdate(counter, by, (_, existing) => existing + by);

    /// <summary>Records the most recent value of a named gauge (last write wins).</summary>
    internal static void SetGauge(string gauge, long value) => Counters[gauge] = value;

    internal static IReadOnlyDictionary<string, long> CounterSnapshot() =>
        Counters.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    /// <summary>
    /// Writes the counter/gauge block to the log. Counters answer "how often", which
    /// per-scan timelines cannot: overlay show/hide churn, renderer resumes, window
    /// resizes driven by content fit, throttled scans. Called on a cadence and at
    /// shutdown so an ordinary log is self-sufficient without an export.
    /// </summary>
    internal static void LogCounters(string reason)
    {
        IReadOnlyDictionary<string, long> counters = CounterSnapshot();
        if (counters.Count == 0)
            return;

        System.Text.StringBuilder builder = new();
        builder.Append("perf counters (").Append(reason).Append(')');
        foreach (KeyValuePair<string, long> counter in counters.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            builder.Append(' ').Append(counter.Key).Append('=').Append(counter.Value);
        Logger.LogInfo(builder.ToString());
    }

    internal static IReadOnlyList<PerfTraceSnapshot> RecentScanSnapshots()
    {
        // Finalize the open trace so a report is never missing the scan the user
        // just performed — which is usually the interesting one.
        PerfTrace? open;
        lock (Sync)
        {
            open = _scanTrace;
            _scanTrace = null;
        }
        if (open is not null)
            Finalize(open);

        lock (Sync)
            return RecentScans.ToList();
    }

    /// <summary>Clears collected traces and counters. Test-only seam.</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _finalizeTimer?.Dispose();
            _finalizeTimer = null;
            _startupTrace = null;
            _startupSnapshot = null;
            _scanTrace = null;
            _scanSequence = 0;
            _finalizedScans = 0;
            RecentScans.Clear();
        }
        Counters.Clear();
    }

    private static void Finalize(PerfTrace trace)
    {
        PerfTraceSnapshot snapshot = trace.Complete();

        bool logCounters;
        lock (Sync)
        {
            RecentScans.Enqueue(snapshot);
            while (RecentScans.Count > RecentCapacity)
                RecentScans.Dequeue();
            // Periodic rather than per-scan: counters are cumulative, so one block per
            // batch of scans is enough to see churn without flooding the log.
            logCounters = ++_finalizedScans % CounterLogInterval == 0;
        }

        // Always leave a one-line record in the log; expand to the full timeline
        // when debugging is on or when the scan blew its budget.
        if (RatConfig.LogDebug || snapshot.TotalMs > ScanBudgetMs)
        {
            foreach (string line in snapshot.ToDetailLines())
                Logger.LogInfo(line);
        }
        else
        {
            Logger.LogInfo(snapshot.ToSummaryLine());
        }

        if (logCounters)
            LogCounters("periodic");
    }

    private static void ArmFinalizeTimer()
    {
        lock (Sync)
        {
            _finalizeTimer ??= new Timer(OnFinalizeTimer, null, Timeout.Infinite, Timeout.Infinite);
            _finalizeTimer.Change(ScanFinalizeDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private static void OnFinalizeTimer(object? state)
    {
        PerfTrace? trace;
        lock (Sync)
        {
            trace = _scanTrace;
            _scanTrace = null;
        }
        if (trace is not null)
            Finalize(trace);
    }
}
