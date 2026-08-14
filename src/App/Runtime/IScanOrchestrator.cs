using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.Scan;

namespace RatScanner.Runtime;

internal interface IScanOrchestrator : INotifyPropertyChanged
{
    ItemQueue ItemScans { get; }

    /// <summary>
    /// Rebuilds the RatEye engine asynchronously and coalesces concurrent requests, so
    /// display/scan-setting changes never block the UI thread on the full engine build.
    /// </summary>
    Task RebuildEngineAsync(CancellationToken cancellationToken = default);

    ScanDiagnosticExportResult ExportLastScanDiagnostics();
}
