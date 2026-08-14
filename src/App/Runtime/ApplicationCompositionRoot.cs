using System;

namespace RatScanner.Runtime;

/// <summary>
/// Owns the application runtime contracts shared by the native WPF and Blazor UI stacks.
/// </summary>
internal sealed class ApplicationCompositionRoot
{
    private static readonly object InstanceLock = new();
    private static ApplicationCompositionRoot? _instance;
    private static bool _shutdownStarted;

    private ApplicationCompositionRoot(RatScannerMain runtime)
    {
        ScanOrchestrator = runtime;
        TrackerService = runtime;
        HotkeyRegistrar = runtime;
    }

    internal static ApplicationCompositionRoot Current
    {
        get
        {
            lock (InstanceLock)
            {
                ObjectDisposedException.ThrowIf(_shutdownStarted, typeof(ApplicationCompositionRoot));
                return _instance ??= new ApplicationCompositionRoot(RatScannerMain.Instance);
            }
        }
    }

    internal IScanOrchestrator ScanOrchestrator { get; }

    internal ITrackerService TrackerService { get; }

    internal IHotkeyRegistrar HotkeyRegistrar { get; }

    internal static void DisposeCurrent()
    {
        lock (InstanceLock)
        {
            if (_shutdownStarted)
                return;
            _shutdownStarted = true;
            _instance = null;
        }

        RatScannerMain.DisposeInstance();
    }
}
