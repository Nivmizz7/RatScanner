using System;
using System.Collections.Generic;
using System.Linq;
using RatScanner.Diagnostics;
using Xunit;

namespace RatScanner.Tests;

/// <summary>
/// Covers the performance-trace primitive with a driven clock. These are the
/// guarantees the diagnostic feature depends on: stages land on the timeline in
/// order, durations are never negative, and a snapshot is a stable copy.
/// </summary>
public sealed class PerfTraceTests
{
    /// <summary>Manually advanced millisecond clock so tests never depend on real time.</summary>
    private sealed class FakeClock
    {
        internal double NowMs { get; private set; }

        internal double Read() => NowMs;

        internal void Advance(double milliseconds) => NowMs += milliseconds;
    }

    [Fact]
    public void Measure_records_stage_duration_and_offset()
    {
        FakeClock clock = new();
        clock.Advance(1_000);
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);

        clock.Advance(5);
        using (trace.Measure("stage.a"))
            clock.Advance(20);

        PerfTraceSnapshot snapshot = trace.Complete();

        PerfStage stage = Assert.Single(snapshot.Stages);
        Assert.Equal("stage.a", stage.Name);
        Assert.Equal(5, stage.OffsetMs);
        Assert.Equal(20, stage.DurationMs);
        Assert.False(stage.IsMark);
    }

    [Fact]
    public void Total_is_measured_from_trace_start()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);
        clock.Advance(42);

        Assert.Equal(42, trace.Complete().TotalMs);
    }

    [Fact]
    public void Completing_twice_keeps_the_first_total()
    {
        // Async reporters can race the store's finalize timer; the total must not
        // creep upward each time somebody asks for it.
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);
        clock.Advance(10);
        double first = trace.Complete().TotalMs;
        clock.Advance(500);

        Assert.Equal(first, trace.Complete().TotalMs);
    }

    [Fact]
    public void Mark_records_an_instant_with_no_duration()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);
        clock.Advance(7);
        trace.Mark("stage.reached");

        PerfStage stage = Assert.Single(trace.Complete().Stages);
        Assert.True(stage.IsMark);
        Assert.Equal(7, stage.OffsetMs);
        Assert.Equal(0, stage.DurationMs);
    }

    [Fact]
    public void Record_places_an_externally_timed_stage_before_now()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);
        clock.Advance(100);
        trace.Record("overlay.visible", 30);

        PerfStage stage = Assert.Single(trace.Complete().Stages);
        Assert.Equal(70, stage.OffsetMs);
        Assert.Equal(30, stage.DurationMs);
        Assert.Equal(100, stage.EndMs);
    }

    [Fact]
    public void Record_clamps_a_duration_longer_than_the_trace()
    {
        // A reporter's clock can start before the trace does; the timeline must not
        // gain a negative offset from it.
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);
        clock.Advance(10);
        trace.Record("overlay.visible", 999);

        Assert.Equal(0, Assert.Single(trace.Complete().Stages).OffsetMs);
    }

    [Fact]
    public void Stages_are_ordered_by_offset_regardless_of_arrival_order()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);
        clock.Advance(50);
        trace.Record("late", 1); // offset 49
        trace.RecordAt("early", 2, 3);

        string[] names = trace.Complete().Stages.Select(stage => stage.Name).ToArray();
        Assert.Equal(["early", "late"], names);
    }

    [Fact]
    public void Merge_folds_engine_timings_in_with_a_prefix()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);
        clock.Advance(200);
        trace.Merge("ratEye.", new Dictionary<string, double> { ["inspection.ocr"] = 120 });

        PerfStage stage = Assert.Single(trace.Complete().Stages);
        Assert.Equal("ratEye.inspection.ocr", stage.Name);
        Assert.Equal(120, stage.DurationMs);
    }

    [Fact]
    public void Merge_tolerates_a_null_snapshot()
    {
        PerfTrace trace = PerfTrace.Start("scan", 1, new FakeClock().Read);
        trace.Merge("ratEye.", null);

        Assert.Empty(trace.Complete().Stages);
    }

    [Fact]
    public void Notes_are_captured_and_empty_values_are_ignored()
    {
        PerfTrace trace = PerfTrace.Start("scan", 1, new FakeClock().Read);
        trace.Note("item", "Army Crackers");
        trace.Note("outcome", "");
        trace.Note("confidence", null);

        IReadOnlyDictionary<string, string> notes = trace.Complete().Notes;
        Assert.Equal("Army Crackers", notes["item"]);
        Assert.False(notes.ContainsKey("outcome"));
        Assert.False(notes.ContainsKey("confidence"));
    }

    [Fact]
    public void Snapshot_is_not_affected_by_later_writes()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);
        trace.Mark("first");
        PerfTraceSnapshot snapshot = trace.Complete();

        trace.Mark("second");
        trace.Note("item", "added later");

        Assert.Single(snapshot.Stages);
        Assert.Empty(snapshot.Notes);
    }

    [Fact]
    public void Slowest_identifies_the_dominant_stage_and_ignores_marks()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("scan", 1, clock.Read);
        trace.RecordAt("fast", 0, 5);
        trace.RecordAt("slow", 5, 300);
        trace.Mark("boundary");

        Assert.Equal("slow", trace.Complete().Slowest?.Name);
    }

    [Fact]
    public void Summary_line_names_the_operation_sequence_total_and_notes()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("name-scan", 12, clock.Read);
        trace.RecordAt("scan.inspect", 0, 341.5);
        clock.Advance(400);
        trace.Note("item", "Army Crackers");

        string line = trace.Complete().ToSummaryLine();

        Assert.Contains("perf name-scan #12", line, StringComparison.Ordinal);
        Assert.Contains("total=400.0ms", line, StringComparison.Ordinal);
        Assert.Contains("scan.inspect 341.5", line, StringComparison.Ordinal);
        Assert.Contains("item=Army Crackers", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_line_omits_sub_millisecond_noise_but_detail_keeps_it()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("name-scan", 1, clock.Read);
        trace.RecordAt("scan.trivial", 0, 0.05);
        trace.RecordAt("scan.real", 1, 25);

        PerfTraceSnapshot snapshot = trace.Complete();

        Assert.DoesNotContain("scan.trivial", snapshot.ToSummaryLine(), StringComparison.Ordinal);
        Assert.Contains(snapshot.ToDetailLines(), line => line.Contains("scan.trivial", StringComparison.Ordinal));
    }

    [Fact]
    public void Detail_lines_start_with_the_summary_then_one_line_per_stage()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("name-scan", 1, clock.Read);
        trace.RecordAt("a", 0, 1);
        trace.RecordAt("b", 1, 2);

        string[] lines = trace.Complete().ToDetailLines().ToArray();

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("perf name-scan", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_unfinished_stage_marks_the_trace_as_truncated()
    {
        // The first icon scan of a session stalled inside LocateIcon and the finalize
        // timer closed the trace, leaving unexplained dead time. Name the open stage
        // so that situation is diagnosable from the log alone.
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("icon-scan", 1, clock.Read);
        PerfTrace.PerfScope unfinished = trace.Measure("scan.locate_icon");
        clock.Advance(2_000);

        PerfTraceSnapshot snapshot = trace.Complete();

        Assert.Equal("scan.locate_icon", snapshot.Notes["truncated"]);
        Assert.Contains("truncated=scan.locate_icon", snapshot.ToSummaryLine(), StringComparison.Ordinal);
        unfinished.Dispose();
    }

    [Fact]
    public void A_completed_stage_does_not_mark_the_trace_as_truncated()
    {
        FakeClock clock = new();
        PerfTrace trace = PerfTrace.Start("icon-scan", 1, clock.Read);
        using (trace.Measure("scan.locate_icon"))
            clock.Advance(200);

        Assert.False(trace.Complete().Notes.ContainsKey("truncated"));
    }

    [Fact]
    public void Monotonic_clock_does_not_move_backwards()
    {
        // The cooldown and every trace rely on this; wall clock would not guarantee it.
        double first = PerfTrace.MonotonicMs();
        double second = PerfTrace.MonotonicMs();

        Assert.True(second >= first);
    }
}
