using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RatEye.Diagnostics;
using RatScanner.Display;

namespace RatScanner;

internal readonly record struct ScanDiagnosticDetection(string? ItemId, string? ItemName, float Confidence);

internal readonly record struct ScanDiagnosticExportResult(bool Succeeded, string? Directory, string? Error);

internal sealed class ScanDiagnosticStore : IDisposable
{
    private readonly object _sync = new();
    private Bitmap? _capture;
    private ScanReplayManifest? _manifest;
    private bool _disposed;

    internal void Record(
        string scanType,
        Bitmap capture,
        RatEye.Vector2 capturePosition,
        RatEye.Vector2? cursorPosition,
        IReadOnlyCollection<ScanDiagnosticDetection> detections,
        IReadOnlyDictionary<string, double> stageMilliseconds,
        RatEye.Config ratEyeConfig,
        GameDisplayConfiguration display,
        string applicationVersion
    )
    {
        ArgumentNullException.ThrowIfNull(capture);

        DateTime capturedAtUtc = DateTime.UtcNow;
        string captureId = $"ratscanner-{capturedAtUtc:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        Rectangle displayBounds = display.CaptureBounds;
        ScanReplayManifest manifest = new()
        {
            Id = captureId,
            ScanType = scanType,
            ImageFile = "capture.png",
            Configuration = new ScanReplayConfiguration
            {
                Scale = ratEyeConfig.ProcessingConfig.Scale,
                Language = ratEyeConfig.ProcessingConfig.Language.ToString(),
                OptimizeHighlighted = ratEyeConfig.ProcessingConfig.InventoryConfig.OptimizeHighlighted,
                UseStaticIcons = ratEyeConfig.ProcessingConfig.IconConfig.UseStaticIcons,
                ScanRotatedIcons = ratEyeConfig.ProcessingConfig.IconConfig.ScanRotatedIcons,
            },
            Context = new ScanReplayContext
            {
                CapturedAtUtc = capturedAtUtc,
                ApplicationVersion = applicationVersion,
                CaptureX = capturePosition.X,
                CaptureY = capturePosition.Y,
                CaptureWidth = capture.Width,
                CaptureHeight = capture.Height,
                DisplayX = displayBounds.X,
                DisplayY = displayBounds.Y,
                DisplayWidth = displayBounds.Width,
                DisplayHeight = displayBounds.Height,
                DpiScale = (float)display.DisplayScale,
                CursorX = cursorPosition?.X,
                CursorY = cursorPosition?.Y,
            },
            Observed = new ScanReplayObservedResult
            {
                ItemIds = detections
                    .Where(detection => !string.IsNullOrWhiteSpace(detection.ItemId))
                    .Select(detection => detection.ItemId!)
                    .ToList(),
                ItemNames = detections
                    .Where(detection => !string.IsNullOrWhiteSpace(detection.ItemName))
                    .Select(detection => detection.ItemName!)
                    .ToList(),
                Confidences = detections.Select(detection => detection.Confidence).ToList(),
                StageMilliseconds = new Dictionary<string, double>(stageMilliseconds),
            },
        };
        Bitmap clonedCapture = new(capture);

        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _capture?.Dispose();
                _capture = clonedCapture;
                _manifest = manifest;
            }
        }
        catch
        {
            clonedCapture.Dispose();
            throw;
        }
    }

    internal ScanDiagnosticExportResult Export()
    {
        Bitmap capture;
        ScanReplayManifest manifest;
        lock (_sync)
        {
            if (_disposed)
                return new(false, null, "Diagnostic storage is no longer available.");
            if (_capture is null || _manifest is null)
                return new(false, null, null);

            capture = new Bitmap(_capture);
            manifest = _manifest;
        }

        try
        {
            string directory = Path.Combine(
                RatConfig.Paths.Debug,
                "ScanDiagnostics",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(directory);
            capture.Save(Path.Combine(directory, manifest.ImageFile), ImageFormat.Png);

            JsonSerializerSettings serializerSettings = new()
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.Indented,
            };
            File.WriteAllText(
                Path.Combine(directory, "scan.ratdiag.json"),
                JsonConvert.SerializeObject(manifest, serializerSettings)
            );
            return new(true, directory, null);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to export scan diagnostics.", exception);
            return new(false, null, exception.Message);
        }
        finally
        {
            capture.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _capture?.Dispose();
            _capture = null;
            _manifest = null;
            _disposed = true;
        }
    }
}
