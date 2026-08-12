using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace RatScanner.Diagnostics;

/// <summary>
/// A complete, self-contained performance report: machine context, the startup
/// timeline, recent scan timelines, and lifetime event counters.
/// </summary>
/// <remarks>
/// This is the artifact a user attaches to an issue. It is designed so a
/// maintainer can identify the slow stage without reproducing the problem, and it
/// deliberately contains no screenshots, item data, tokens, or paths beyond the
/// export directory itself.
/// </remarks>
internal sealed record PerfReport(
    DateTimeOffset GeneratedAtUtc,
    PerfEnvironmentSnapshot Machine,
    PerfTraceSnapshot? Startup,
    IReadOnlyList<PerfTraceSnapshot> RecentScans,
    IReadOnlyDictionary<string, long> Counters
);

internal static class PerfReportBuilder
{
    internal const string JsonFileName = "performance.json";
    internal const string TextFileName = "performance.txt";

    internal static PerfReport Build() =>
        new(
            DateTimeOffset.UtcNow,
            PerfEnvironment.Capture(),
            PerfTraceStore.StartupSnapshot,
            PerfTraceStore.RecentScanSnapshots(),
            PerfTraceStore.CounterSnapshot()
        );

    /// <summary>
    /// Writes <see cref="JsonFileName"/> and <see cref="TextFileName"/> into an
    /// existing directory. The text form exists because it is readable inline in a
    /// GitHub issue; the JSON form exists because it is diffable and machine-readable.
    /// </summary>
    internal static void WriteTo(string directory, PerfReport report)
    {
        JsonSerializerSettings serializerSettings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.Indented,
        };
        File.WriteAllText(
            Path.Combine(directory, JsonFileName),
            JsonConvert.SerializeObject(report, serializerSettings),
            Encoding.UTF8
        );
        File.WriteAllText(Path.Combine(directory, TextFileName), ToText(report), Encoding.UTF8);
    }

    /// <summary>Human-readable rendering of a report.</summary>
    internal static string ToText(PerfReport report)
    {
        StringBuilder builder = new();
        builder.Append("RatScanner performance report\n");
        builder
            .Append("generated: ")
            .Append(report.GeneratedAtUtc.ToString("O", CultureInfo.InvariantCulture))
            .Append('\n');
        AppendMachine(builder, report.Machine);

        builder.Append("\n-- startup --\n");
        if (report.Startup is null)
            builder.Append("  (startup timeline not captured)\n");
        else
            AppendTrace(builder, report.Startup);

        builder.Append("\n-- counters --\n");
        if (report.Counters.Count == 0)
        {
            builder.Append("  (none)\n");
        }
        else
        {
            foreach (
                KeyValuePair<string, long> counter in report.Counters.OrderBy(
                    entry => entry.Key,
                    StringComparer.Ordinal
                )
            )
                builder.Append("  ").Append(counter.Key).Append(" = ").Append(counter.Value).Append('\n');
        }

        builder.Append("\n-- recent scans (oldest first) --\n");
        if (report.RecentScans.Count == 0)
        {
            builder.Append("  (no scans recorded this session)\n");
        }
        else
        {
            foreach (PerfTraceSnapshot scan in report.RecentScans)
                AppendTrace(builder, scan);
            AppendScanStatistics(builder, report.RecentScans);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Compact form for embedding in a crash/issue body, where space is limited.
    /// Keeps machine context, startup total, and the most recent scans.
    /// </summary>
    internal static string ToCompactText(PerfReport report, int recentScanLimit = 5)
    {
        StringBuilder builder = new();
        AppendMachine(builder, report.Machine);
        if (report.Startup is not null)
            builder.Append("startup: ").Append(report.Startup.ToSummaryLine()).Append('\n');
        foreach (PerfTraceSnapshot scan in report.RecentScans.TakeLast(recentScanLimit))
            builder.Append(scan.ToSummaryLine()).Append('\n');
        foreach (
            KeyValuePair<string, long> counter in report.Counters.OrderBy(entry => entry.Key, StringComparer.Ordinal)
        )
            builder.Append(counter.Key).Append('=').Append(counter.Value).Append(' ');
        return builder.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Machine context as text. Exposed so the ordinary log can carry it too, not
    /// only an export.
    /// </summary>
    internal static string DescribeMachine(PerfEnvironmentSnapshot machine)
    {
        StringBuilder builder = new();
        AppendMachine(builder, machine);
        return builder.ToString();
    }

    private static void AppendMachine(StringBuilder builder, PerfEnvironmentSnapshot machine)
    {
        builder
            .Append("version: ")
            .Append(machine.ApplicationVersion)
            .Append(" (")
            .Append(machine.ProcessArchitecture)
            .Append(")\n");
        builder
            .Append("os: ")
            .Append(machine.OperatingSystem)
            .Append(" | .NET ")
            .Append(machine.RuntimeVersion)
            .Append('\n');
        builder
            .Append("cpu: ")
            .Append(machine.ProcessorCount)
            .Append(" logical | ram: ")
            .Append(PerfEnvironment.FormatBytes(machine.TotalPhysicalMemoryBytes))
            .Append('\n');
        builder
            .Append("webview2: ")
            .Append(machine.WebView2Runtime)
            .Append(" | wpf render tier: ")
            .Append(machine.WpfRenderTier)
            .Append('\n');
        builder
            .Append("virtual screen: ")
            .Append(machine.VirtualScreenWidth)
            .Append('x')
            .Append(machine.VirtualScreenHeight)
            .Append(" (")
            .Append(machine.VirtualScreenMegapixels.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" MP overlay surface)\n");
        foreach (PerfDisplayInfo display in machine.Displays)
        {
            builder
                .Append("  display ")
                .Append(display.DeviceName)
                .Append(display.IsPrimary ? " [primary]" : "")
                .Append(": ")
                .Append(display.Width)
                .Append('x')
                .Append(display.Height)
                .Append(" @ ")
                .Append(display.RefreshHz)
                .Append("Hz, dpi ")
                .Append(display.DpiScale.ToString("F2", CultureInfo.InvariantCulture))
                .Append(display.IsDpiReliable ? "" : " (unreliable)")
                .Append(" — ")
                .Append(display.FriendlyName)
                .Append('\n');
        }
        foreach (string adapter in machine.GraphicsAdapters)
            builder.Append("  adapter: ").Append(adapter).Append('\n');
        builder
            .Append("memory: process ")
            .Append(PerfEnvironment.FormatBytes(machine.ProcessWorkingSetBytes))
            .Append(" | webview2 hosts ")
            .Append(machine.WebView2ProcessCount)
            .Append(" using ")
            .Append(PerfEnvironment.FormatBytes(machine.WebView2WorkingSetBytes))
            .Append(" (machine-wide)\n");
    }

    private static void AppendTrace(StringBuilder builder, PerfTraceSnapshot trace)
    {
        foreach (string line in trace.ToDetailLines())
            builder.Append("  ").Append(line).Append('\n');
    }

    /// <summary>
    /// Aggregate view across recent scans. A median plus worst case separates
    /// "always slow" from "occasionally stalls", which need different fixes.
    /// </summary>
    private static void AppendScanStatistics(StringBuilder builder, IReadOnlyList<PerfTraceSnapshot> scans)
    {
        builder.Append("\n-- scan stage totals across ").Append(scans.Count).Append(" scan(s) --\n");

        Dictionary<string, List<double>> byStage = new(StringComparer.Ordinal);
        foreach (PerfTraceSnapshot scan in scans)
        {
            foreach (PerfStage stage in scan.Stages)
            {
                if (stage.IsMark)
                    continue;
                if (!byStage.TryGetValue(stage.Name, out List<double>? samples))
                    byStage[stage.Name] = samples = [];
                samples.Add(stage.DurationMs);
            }
        }

        IEnumerable<KeyValuePair<string, List<double>>> ranked = byStage.OrderByDescending(entry =>
            Median(entry.Value)
        );
        foreach (KeyValuePair<string, List<double>> entry in ranked)
        {
            builder
                .Append("  ")
                .Append(entry.Key.PadRight(34))
                .Append(" median ")
                .Append(Median(entry.Value).ToString("F1", CultureInfo.InvariantCulture).PadLeft(8))
                .Append(" ms | max ")
                .Append(entry.Value.Max().ToString("F1", CultureInfo.InvariantCulture).PadLeft(8))
                .Append(" ms | n=")
                .Append(entry.Value.Count)
                .Append('\n');
        }

        double[] totals = scans.Select(scan => scan.TotalMs).OrderBy(value => value).ToArray();
        builder
            .Append("  TOTAL".PadRight(36))
            .Append(" median ")
            .Append(Median(totals).ToString("F1", CultureInfo.InvariantCulture).PadLeft(8))
            .Append(" ms | max ")
            .Append(totals.Max().ToString("F1", CultureInfo.InvariantCulture).PadLeft(8))
            .Append(" ms | n=")
            .Append(totals.Length)
            .Append('\n');
    }

    internal static double Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
            return 0;
        double[] sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2d;
    }
}
