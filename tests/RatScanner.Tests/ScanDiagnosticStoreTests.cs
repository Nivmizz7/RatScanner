using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using RatScanner.Display;
using Xunit;

namespace RatScanner.Tests;

[Collection("RatConfig")]
public class ScanDiagnosticStoreTests
{
    [Fact]
    public void Export_without_a_recorded_scan_reports_no_bundle()
    {
        using ScanDiagnosticStore store = new();

        ScanDiagnosticExportResult result = store.Export();

        Assert.False(result.Succeeded);
        Assert.Null(result.Directory);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Export_writes_replayable_png_and_versioned_manifest_to_unique_directories()
    {
        string previousDebugPath = RatConfig.Paths.Debug;
        string root = Path.Combine(Path.GetTempPath(), "RatScanner-diagnostics-" + Guid.NewGuid().ToString("N"));
        RatConfig.Paths.Debug = root;

        try
        {
            using ScanDiagnosticStore store = new();
            using Bitmap capture = new(32, 24);
            RatEye.Config config = new()
            {
                ProcessingConfig = new RatEye.Config.Processing
                {
                    Scale = 1.25f,
                    UseCache = false,
                    InventoryConfig = new RatEye.Config.Processing.Inventory { OptimizeHighlighted = true },
                },
            };
            store.Record(
                "inventory",
                capture,
                new RatEye.Vector2(100, 200),
                new RatEye.Vector2(16, 12),
                [new("item-id", "Fixture item", 0.95f)],
                new Dictionary<string, double> { ["inventory.grid_parse"] = 4.5 },
                config,
                GameDisplayConfiguration.Empty,
                "v4-test"
            );

            ScanDiagnosticExportResult result = store.Export();
            ScanDiagnosticExportResult repeatedResult = store.Export();

            Assert.True(result.Succeeded, result.Error);
            Assert.NotNull(result.Directory);
            Assert.True(repeatedResult.Succeeded, repeatedResult.Error);
            Assert.NotNull(repeatedResult.Directory);
            Assert.NotEqual(result.Directory, repeatedResult.Directory);
            Assert.True(File.Exists(Path.Combine(result.Directory, "capture.png")));
            string manifestPath = Path.Combine(result.Directory, "scan.ratdiag.json");
            Assert.True(File.Exists(manifestPath));
            string manifest = File.ReadAllText(manifestPath);
            Assert.Contains("\"schemaVersion\": 1", manifest, StringComparison.Ordinal);
            Assert.Contains("\"item-id\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"inventory.grid_parse\": 4.5", manifest, StringComparison.Ordinal);
        }
        finally
        {
            RatConfig.Paths.Debug = previousDebugPath;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
