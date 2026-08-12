using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace RatScanner.Diagnostics;

/// <summary>
/// A single measured stage inside a <see cref="PerfTrace"/>. <c>OffsetMs</c> is
/// relative to the trace start, so an ordered stage list reads as a timeline
/// instead of an unordered bag of durations — that is what makes a user-supplied
/// log usable without reproducing the problem locally.
/// </summary>
/// <param name="Name">Dot-separated stage name, for example <c>scan.screenshot</c>.</param>
/// <param name="OffsetMs">Milliseconds from trace start to the start of this stage.</param>
/// <param name="DurationMs">Stage duration in milliseconds; zero for an instant mark.</param>
/// <param name="IsMark"><see langword="true"/> when this is an instant with no duration.</param>
internal readonly record struct PerfStage(string Name, double OffsetMs, double DurationMs, bool IsMark)
{
    internal double EndMs => OffsetMs + DurationMs;
}

/// <summary>
/// Immutable result of a completed <see cref="PerfTrace"/>. Safe to keep,
/// serialize, and hand to another thread.
/// </summary>
internal sealed record PerfTraceSnapshot(
    string Operation,
    long Sequence,
    DateTimeOffset StartedAtUtc,
    double TotalMs,
    IReadOnlyList<PerfStage> Stages,
    IReadOnlyDictionary<string, string> Notes
)
{
    /// <summary>
    /// Stages that are worth a human's attention, in timeline order. Sub-millisecond
    /// stages are noise in a one-line summary but are kept in the full detail view.
    /// </summary>
    private const double SummaryThresholdMs = 0.5;

    /// <summary>The longest non-mark stage, or <see langword="null"/> when nothing was measured.</summary>
    internal PerfStage? Slowest =>
        Stages
            .Where(stage => !stage.IsMark)
            .OrderByDescending(stage => stage.DurationMs)
            .Cast<PerfStage?>()
            .FirstOrDefault();

    /// <summary>
    /// One dense line describing the whole operation. This is what gets written to
    /// the normal log on every scan so a user's routine log already answers
    /// "which stage was slow?".
    /// </summary>
    internal string ToSummaryLine()
    {
        System.Text.StringBuilder builder = new();
        builder.Append("perf ").Append(Operation);
        if (Sequence > 0)
            builder.Append(" #").Append(Sequence.ToString(CultureInfo.InvariantCulture));
        builder.Append(" total=").Append(Format(TotalMs)).Append("ms");

        foreach (PerfStage stage in Stages)
        {
            if (stage.IsMark || stage.DurationMs < SummaryThresholdMs)
                continue;
            builder.Append(" | ").Append(stage.Name).Append(' ').Append(Format(stage.DurationMs));
        }

        foreach (KeyValuePair<string, string> note in Notes)
            builder.Append(' ').Append(note.Key).Append('=').Append(note.Value);

        return builder.ToString();
    }

    /// <summary>
    /// Full aligned timeline, one stage per line. Written when debug logging is on
    /// or when the operation breached its budget, and always included in an export.
    /// </summary>
    internal IEnumerable<string> ToDetailLines()
    {
        yield return ToSummaryLine();
        foreach (PerfStage stage in Stages)
        {
            string window = stage.IsMark
                ? $"{Format(stage.OffsetMs), 9}          "
                : $"{Format(stage.OffsetMs), 9} +{Format(stage.DurationMs), 8}";
            yield return $"    {window}  {stage.Name}";
        }
    }

    private static string Format(double milliseconds) => milliseconds.ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>
/// Collects a timeline for one logical operation (a scan, application startup).
/// Thread-safe: scan work starts on a hotkey thread pool thread and finishes on
/// the WPF dispatcher and inside Blazor render callbacks, so stages arrive from
/// several threads and a lock is required.
/// </summary>
/// <remarks>
/// Cost is a QPC read plus a list insert per stage, so tracing stays on
/// unconditionally. Only the verbosity of the log output is configurable —
/// collection itself must not be opt-in, otherwise the data is never there when
/// a user actually hits the problem.
/// </remarks>
internal sealed class PerfTrace
{
    private readonly object _sync = new();
    private readonly List<PerfStage> _stages = new(24);
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);
    private readonly List<string> _openScopes = new(4);
    private readonly Func<double> _nowMs;
    private readonly double _originMs;
    private double? _completedAtMs;

    internal string Operation { get; }
    internal long Sequence { get; }
    internal DateTimeOffset StartedAtUtc { get; }

    private PerfTrace(string operation, long sequence, Func<double> nowMs)
    {
        Operation = operation;
        Sequence = sequence;
        _nowMs = nowMs;
        _originMs = nowMs();
        StartedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Starts a trace. <paramref name="nowMs"/> exists so tests can drive a
    /// deterministic clock; production always uses the monotonic
    /// <see cref="Stopwatch"/> timestamp (never wall clock, which can step
    /// backwards and produce negative durations).
    /// </summary>
    internal static PerfTrace Start(string operation, long sequence = 0, Func<double>? nowMs = null) =>
        new(operation, sequence, nowMs ?? MonotonicMs);

    internal static double MonotonicMs() => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

    internal bool IsCompleted
    {
        get
        {
            lock (_sync)
                return _completedAtMs is not null;
        }
    }

    /// <summary>Milliseconds since the trace started, or its total once completed.</summary>
    internal double ElapsedMs
    {
        get
        {
            lock (_sync)
                return _completedAtMs ?? _nowMs() - _originMs;
        }
    }

    /// <summary>
    /// Measures a stage for the lifetime of the returned scope. Scopes may nest
    /// and may overlap across threads; each captures its own start offset.
    /// </summary>
    internal PerfScope Measure(string stage)
    {
        lock (_sync)
            _openScopes.Add(stage);
        return new PerfScope(this, stage, _nowMs() - _originMs);
    }

    /// <summary>Records an instant with no duration, for example a state transition.</summary>
    internal void Mark(string stage) => Add(new PerfStage(stage, _nowMs() - _originMs, 0, true));

    /// <summary>
    /// Records a stage measured elsewhere, treating it as having just finished.
    /// Used for work timed by another component (Blazor render callbacks, RatEye).
    /// </summary>
    internal void Record(string stage, double durationMs)
    {
        double end = _nowMs() - _originMs;
        Add(new PerfStage(stage, Math.Max(0, end - durationMs), Math.Max(0, durationMs), false));
    }

    /// <summary>Records a stage with an explicit position on the timeline.</summary>
    internal void RecordAt(string stage, double offsetMs, double durationMs) =>
        Add(new PerfStage(stage, Math.Max(0, offsetMs), Math.Max(0, durationMs), false));

    /// <summary>
    /// Folds engine-internal timings (<see cref="RatEye.ProcessingTimings.Snapshot"/>)
    /// into this timeline so App and engine costs appear side by side.
    /// </summary>
    internal void Merge(string prefix, IReadOnlyDictionary<string, double>? stageMilliseconds)
    {
        if (stageMilliseconds is null)
            return;
        double end = _nowMs() - _originMs;
        foreach (KeyValuePair<string, double> stage in stageMilliseconds.OrderByDescending(entry => entry.Value))
            Add(new PerfStage(prefix + stage.Key, Math.Max(0, end - stage.Value), Math.Max(0, stage.Value), false));
    }

    /// <summary>Attaches context that explains the numbers (item name, whether the engine rebuilt).</summary>
    internal void Note(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        lock (_sync)
            _notes[key] = value;
    }

    /// <summary>
    /// Stops the trace and returns its snapshot. Completing twice is harmless and
    /// returns the same total, because late async reporters can race the finalizer.
    /// </summary>
    internal PerfTraceSnapshot Complete()
    {
        lock (_sync)
        {
            _completedAtMs ??= _nowMs() - _originMs;
            // A stage still running at completion never records, which previously made
            // a stalled operation look like unexplained dead time. Name the culprit.
            if (_openScopes.Count > 0)
                _notes["truncated"] = string.Join(",", _openScopes);
            List<PerfStage> ordered = _stages.OrderBy(stage => stage.OffsetMs).ThenBy(stage => stage.Name).ToList();
            return new PerfTraceSnapshot(
                Operation,
                Sequence,
                StartedAtUtc,
                _completedAtMs.Value,
                new ReadOnlyCollection<PerfStage>(ordered),
                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_notes, StringComparer.Ordinal))
            );
        }
    }

    private void Add(PerfStage stage)
    {
        lock (_sync)
        {
            // Bound the list so a pathological caller cannot grow it without limit.
            if (_stages.Count >= 512)
                return;
            _stages.Add(stage);
        }
    }

    /// <summary>Scope returned by <see cref="Measure"/>; records on dispose.</summary>
    internal readonly struct PerfScope : IDisposable
    {
        private readonly PerfTrace? _trace;
        private readonly string _stage;
        private readonly double _startOffsetMs;

        internal PerfScope(PerfTrace trace, string stage, double startOffsetMs)
        {
            _trace = trace;
            _stage = stage;
            _startOffsetMs = startOffsetMs;
        }

        public void Dispose()
        {
            if (_trace is null)
                return;
            double end = _trace._nowMs() - _trace._originMs;
            lock (_trace._sync)
                _trace._openScopes.Remove(_stage);
            _trace.Add(new PerfStage(_stage, _startOffsetMs, Math.Max(0, end - _startOffsetMs), false));
        }
    }
}
