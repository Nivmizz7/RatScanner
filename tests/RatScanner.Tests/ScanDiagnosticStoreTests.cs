using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RatEye.Diagnostics;
using RatScanner.Display;
using Xunit;

namespace RatScanner.Tests;

[Collection(RatConfigCollection.Name)]
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
            GameDisplayConfiguration display = GameDisplayConfiguration.Empty with
            {
                CaptureBounds = new Rectangle(100, 200, 32, 24),
                DisplayScale = 1.5,
            };
            store.Record(
                "inventory",
                capture,
                new RatEye.Vector2(100, 200),
                new RatEye.Vector2(16, 12),
                [new("item-id", "Fixture item", 0.95f), new(null, null, 0.25f)],
                new Dictionary<string, double> { ["inventory.grid_parse"] = 4.5 },
                config,
                display,
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
            var exportedManifest = JsonConvert.DeserializeObject<ScanReplayManifest>(manifest);
            Assert.NotNull(exportedManifest);
            JObject manifestJson = JObject.Parse(manifest);
            var observedJson = manifestJson["observed"] as JObject;
            var stageMillisecondsJson = observedJson?["stageMilliseconds"] as JObject;
            var contextJson = manifestJson["context"] as JObject;
            Assert.NotNull(observedJson);
            Assert.NotNull(stageMillisecondsJson);
            Assert.NotNull(contextJson);
            Assert.Equal(1, manifestJson.Value<int>("schemaVersion"));
            Assert.Equal("item-id", observedJson["itemIds"]?[0]?.Value<string>());
            Assert.Equal(4.5, stageMillisecondsJson.Value<double>("inventory.grid_parse"));
            Assert.Equal(100, contextJson.Value<int>("displayX"));
            Assert.Equal(200, contextJson.Value<int>("displayY"));
            Assert.Equal(32, contextJson.Value<int>("displayWidth"));
            Assert.Equal(24, contextJson.Value<int>("displayHeight"));
            Assert.Equal(1.5, contextJson.Value<double>("dpiScale"));
            Assert.Equal(new[] { "item-id", string.Empty }, exportedManifest.Observed.ItemIds);
            Assert.Equal(new[] { "Fixture item", string.Empty }, exportedManifest.Observed.ItemNames);
            Assert.Collection(
                exportedManifest.Observed.Confidences,
                confidence => Assert.Equal(0.95f, confidence, precision: 4),
                confidence => Assert.Equal(0.25f, confidence, precision: 4)
            );
        }
        finally
        {
            RatConfig.Paths.Debug = previousDebugPath;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
