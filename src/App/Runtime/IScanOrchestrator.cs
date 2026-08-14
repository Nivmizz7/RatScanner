using System.ComponentModel;
using RatScanner.Scan;

namespace RatScanner.Runtime;

internal interface IScanOrchestrator : INotifyPropertyChanged
{
    ItemQueue ItemScans { get; }

    void RebuildEngine();

    ScanDiagnosticExportResult ExportLastScanDiagnostics();
}
