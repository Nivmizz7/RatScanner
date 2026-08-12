using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RatScanner.Diagnostics;
using Xunit;

namespace RatScanner.Tests;

/// <summary>
/// Covers the store's correlation and retention rules. The important guarantee is
/// that a late async reporter (overlay paint, main-window render) can never be
/// attributed to the wrong scan — otherwise a report would blame the wrong stage.
/// </summary>
/// <remarks>
/// <see cref="PerfTraceStore"/> is static process state, so these tests reset it
/// and are marked non-parallel against each other via the collection attribute.
/// </remarks>
[Collection(nameof(PerfTraceStoreTests))]
[CollectionDefinition(nameof(PerfTraceStoreTests), DisableParallelization = true)]
public sealed class PerfTraceStoreTests : IDisposable
{
    public PerfTraceStoreTests() => PerfTraceStore.ResetForTests();

    public void Dispose() => PerfTraceStore.ResetForTests();

    [Fact]
    public void BeginScan_assigns_increasing_sequences()
    {
        PerfTrace first = PerfTraceStore.BeginScan("name-scan");
        PerfTrace second = PerfTraceStore.BeginScan("icon-scan");

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(2, PerfTraceStore.CurrentScanSequence);
    }

    [Fact]
    public void Starting_a_scan_finalizes_the_previous_one()
    {
        PerfTraceStore.BeginScan("name-scan");
        PerfTraceStore.BeginScan("name-scan");

        // The first trace is retained; the second is still open and gets finalized
        // by the retrieval call itself.
        Assert.Equal(2, PerfTraceStore.RecentScanSnapshots().Count);
    }

    [Fact]
    public void RecordScanStage_attaches_to_the_matching_sequence()
    {
        PerfTrace trace = PerfTraceStore.BeginScan("name-scan");
        PerfTraceStore.RecordScanStage(trace.Sequence, "overlay.visible", 120);

        PerfTraceSnapshot snapshot = Assert.Single(PerfTraceStore.RecentScanSnapshots());
        Assert.Contains(snapshot.Stages, stage => stage.Name == "overlay.visible" && stage.DurationMs == 120);
    }

    [Fact]
    public void RecordScanStage_ignores_a_stale_sequence()
    {
        PerfTrace stale = PerfTraceStore.BeginScan("name-scan");
        PerfTraceStore.BeginScan("name-scan");

        // A render that belonged to the previous scan must not pollute the new one.
        PerfTraceStore.RecordScanStage(stale.Sequence, "overlay.visible", 999);

        IReadOnlyList<PerfTraceSnapshot> scans = PerfTraceStore.RecentScanSnapshots();
        Assert.All(scans, scan => Assert.DoesNotContain(scan.Stages, stage => stage.DurationMs == 999));
    }

    [Fact]
    public void NoteScan_and_MarkScan_ignore_a_stale_sequence()
    {
        PerfTrace stale = PerfTraceStore.BeginScan("name-scan");
        PerfTrace current = PerfTraceStore.BeginScan("name-scan");

        PerfTraceStore.NoteScan(stale.Sequence, "item", "wrong");
        PerfTraceStore.MarkScan(stale.Sequence, "wrong.mark");
        PerfTraceStore.NoteScan(current.Sequence, "item", "right");

        PerfTraceSnapshot latest = PerfTraceStore.RecentScanSnapshots().Last();
        Assert.Equal("right", latest.Notes["item"]);
        Assert.DoesNotContain(latest.Stages, stage => stage.Name == "wrong.mark");
    }

    [Fact]
    public void CompleteScan_closes_the_trace_so_a_later_stage_is_dropped()
    {
        PerfTrace trace = PerfTraceStore.BeginScan("name-scan");
        PerfTraceStore.CompleteScan(trace.Sequence);
        PerfTraceStore.RecordScanStage(trace.Sequence, "too.late", 50);

        PerfTraceSnapshot snapshot = Assert.Single(PerfTraceStore.RecentScanSnapshots());
        Assert.DoesNotContain(snapshot.Stages, stage => stage.Name == "too.late");
        Assert.Equal(0, PerfTraceStore.CurrentScanSequence);
    }

    [Fact]
    public void CompleteScan_is_idempotent()
    {
        PerfTrace trace = PerfTraceStore.BeginScan("name-scan");
        PerfTraceStore.CompleteScan(trace.Sequence);
        PerfTraceStore.CompleteScan(trace.Sequence);

        Assert.Single(PerfTraceStore.RecentScanSnapshots());
    }

    [Fact]
    public void Recent_scans_are_capped_so_a_long_session_stays_bounded()
    {
        for (int index = 0; index < 60; index++)
            PerfTraceStore.CompleteScan(PerfTraceStore.BeginScan("name-scan").Sequence);

        IReadOnlyList<PerfTraceSnapshot> scans = PerfTraceStore.RecentScanSnapshots();

        Assert.Equal(32, scans.Count);
        // Oldest entries are evicted, so the newest scan is always present.
        Assert.Equal(60, scans.Last().Sequence);
    }

    [Fact]
    public void Counters_accumulate_and_gauges_overwrite()
    {
        PerfTraceStore.Increment("overlay.shown");
        PerfTraceStore.Increment("overlay.shown");
        PerfTraceStore.Increment("scan.throttled", 5);
        PerfTraceStore.SetGauge("overlay.surface_px", 1000);
        PerfTraceStore.SetGauge("overlay.surface_px", 8_294_400);

        IReadOnlyDictionary<string, long> counters = PerfTraceStore.CounterSnapshot();

        Assert.Equal(2, counters["overlay.shown"]);
        Assert.Equal(5, counters["scan.throttled"]);
        Assert.Equal(8_294_400, counters["overlay.surface_px"]);
    }

    [Fact]
    public void RecentScanSnapshots_finalizes_the_open_trace()
    {
        // The scan a user just performed is usually the interesting one, so a report
        // must never omit it just because its async tail has not landed yet.
        PerfTrace trace = PerfTraceStore.BeginScan("name-scan");
        trace.Mark("scan.enqueue");

        Assert.Single(PerfTraceStore.RecentScanSnapshots());
        Assert.Equal(0, PerfTraceStore.CurrentScanSequence);
    }

    [Fact]
    public void Report_includes_machine_startup_counters_and_scans()
    {
        PerfTraceStore.Startup.Mark("startup.began");
        PerfTraceStore.CompleteStartup();
        PerfTraceStore.Increment("window.fit_resize", 15);
        PerfTrace scan = PerfTraceStore.BeginScan("name-scan");
        scan.RecordAt("scan.inspect", 0, 250);
        PerfTraceStore.CompleteScan(scan.Sequence);

        PerfReport report = PerfReportBuilder.Build();
        string text = PerfReportBuilder.ToText(report);

        Assert.NotNull(report.Startup);
        Assert.Equal(15, report.Counters["window.fit_resize"]);
        Assert.Single(report.RecentScans);
        Assert.Contains("virtual screen:", text, StringComparison.Ordinal);
        Assert.Contains("window.fit_resize = 15", text, StringComparison.Ordinal);
        Assert.Contains("scan.inspect", text, StringComparison.Ordinal);
        Assert.Contains("-- scan stage totals", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_text_is_stable_when_nothing_was_recorded()
    {
        string text = PerfReportBuilder.ToText(PerfReportBuilder.Build());

        Assert.Contains("startup timeline not captured", text, StringComparison.Ordinal);
        Assert.Contains("no scans recorded this session", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_report_limits_how_many_scans_it_lists()
    {
        for (int index = 0; index < 8; index++)
            PerfTraceStore.CompleteScan(PerfTraceStore.BeginScan("name-scan").Sequence);

        string compact = PerfReportBuilder.ToCompactText(PerfReportBuilder.Build(), recentScanLimit: 3);

        Assert.Equal(3, compact.Split('\n').Count(line => line.StartsWith("perf name-scan", StringComparison.Ordinal)));
        // The newest scans are the ones kept.
        Assert.Contains("#8", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTo_emits_both_a_json_and_a_text_report()
    {
        PerfTrace scan = PerfTraceStore.BeginScan("icon-scan");
        scan.RecordAt("scan.locate_icon", 0, 180);
        PerfTraceStore.CompleteScan(scan.Sequence);

        string directory = Path.Combine(Path.GetTempPath(), "RatScannerPerfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            PerfReportBuilder.WriteTo(directory, PerfReportBuilder.Build());

            string jsonPath = Path.Combine(directory, PerfReportBuilder.JsonFileName);
            string textPath = Path.Combine(directory, PerfReportBuilder.TextFileName);
            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(textPath));

            // The JSON form has to be machine-readable for triage tooling, and camel
            // cased like the sibling scan manifest.
            JObject parsed = JObject.Parse(File.ReadAllText(jsonPath));
            Assert.NotNull(parsed["machine"]);
            Assert.NotNull(parsed["recentScans"]);
            Assert.Contains("scan.locate_icon", File.ReadAllText(textPath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(new double[] { 5 }, 5)]
    [InlineData(new double[] { 1, 3 }, 2)]
    [InlineData(new double[] { 9, 1, 5 }, 5)]
    [InlineData(new double[] { }, 0)]
    public void Median_reports_the_middle_sample(double[] values, double expected)
    {
        Assert.Equal(expected, PerfReportBuilder.Median(values));
    }
}
