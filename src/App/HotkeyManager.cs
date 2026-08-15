using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using static RatScanner.RatConfig;

namespace RatScanner;

internal sealed class HotkeyManager : IDisposable
{
    private readonly RatScannerMain _owner;
    private readonly object _registrationLock = new();
    private long _last_mouse_click;
    private bool _engineReady;
    private bool _disposed;

    internal ActiveHotkey NameScanHotkey;
    internal ActiveHotkey IconScanHotkey;

    internal HotkeyManager(RatScannerMain owner)
    {
        _owner = owner;
        UserActivityHelper.Start(true, true);
        RegisterHotkeys();
    }

    ~HotkeyManager()
    {
        Dispose(false);
    }

    /// <summary>
    /// Register hotkeys so the event handlers receive hotkey presses
    /// </summary>
    /// <remarks>
    /// Called by the constructor
    /// </remarks>
    [MemberNotNull(nameof(NameScanHotkey), nameof(IconScanHotkey))]
    internal void RegisterHotkeys()
    {
        lock (_registrationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RegisterHotkeysLocked();
        }
    }

    [MemberNotNull(nameof(NameScanHotkey), nameof(IconScanHotkey))]
    private void RegisterHotkeysLocked()
    {
        // Unregister hotkeys to prevent multiple listeners for the same hotkey.
        // Settings can request a rebuild while RatEye is still initializing; keep
        // the live hooks disabled until the owner explicitly publishes readiness.
        UnregisterHotkeysLocked();
        if (!_engineReady)
        {
            CreateDisabledHotkeys();
            return;
        }

        // IMPORTANT: pass enabled/suppressHotkey explicitly by name. Without
        // the named argument, C# resolves to the 3-param constructor
        // (Hotkey, handler, bool suppressHotkey) instead of the 5-param one
        // (Hotkey, handler, bool enabled, bool suppressHotkey, Func? canHandle),
        // silently passing IconScan.Enable as suppressHotkey=true. That causes
        // the low-level hook to swallow LBUTTONUP and Shift KEYUP events —
        // the game never sees the button/key release and both appear stuck.
        IconScanHotkey = new ActiveHotkey(
            IconScan.Hotkey,
            OnIconScanHotkey,
            enabled: IconScan.Enable,
            suppressHotkey: false
        );
        Hotkey nameScanHotkey = new(null, new[] { MouseButton.Left });
        NameScanHotkey = new ActiveHotkey(
            nameScanHotkey,
            OnNameScanHotkey,
            NameScan.Enable,
            canHandle: e => !IconScanHotkey.Enabled || !IconScanHotkey.IsPressed(e)
        );
    }

    internal void SetEngineReady(bool ready)
    {
        lock (_registrationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_engineReady == ready)
                return;

            _engineReady = ready;
            RegisterHotkeysLocked();
        }
    }

    [MemberNotNull(nameof(NameScanHotkey), nameof(IconScanHotkey))]
    private void CreateDisabledHotkeys()
    {
        IconScanHotkey = new ActiveHotkey(IconScan.Hotkey, OnIconScanHotkey, enabled: false, suppressHotkey: false);
        NameScanHotkey = new ActiveHotkey(
            new Hotkey(null, new[] { MouseButton.Left }),
            OnNameScanHotkey,
            enabled: false
        );
    }

    /// <summary>
    /// Unregister hotkeys
    /// </summary>
    internal void UnregisterHotkeys()
    {
        lock (_registrationLock)
            UnregisterHotkeysLocked();
    }

    private void UnregisterHotkeysLocked()
    {
        NameScanHotkey?.Dispose();
        IconScanHotkey?.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        // Hotkey disposal and global input-hook teardown are managed and may have
        // thread affinity — only run them from an explicit Dispose(), never the finalizer.
        if (disposing)
        {
            lock (_registrationLock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                UnregisterHotkeysLocked();
                UserActivityHelper.Stop(true, true, false);
            }
            return;
        }
        _disposed = true;
    }

    private static void Wrap(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Logger.LogWarning("A RatScanner hotkey action failed.", e);
        }
    }

    private void OnNameScanHotkey(object? sender, KeyUpEventArgs e)
    {
        Wrap(() =>
        {
            Logger.LogDebug("OnNameScanHotkey: ENTER");
            _owner.NameScan(UserActivityHelper.GetMousePosition(), e.HookObservedAtMs);
            // Claim the auto-scan window atomically so concurrent Task.Run handlers from a
            // real double-click cannot both pass the debounce and both call NameScanScreen.
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            long previous = Interlocked.Exchange(ref _last_mouse_click, now);
            Logger.LogDebug($"OnNameScanHotkey: now={now} previous={previous} EnableAuto={NameScan.EnableAuto}");
            if (previous + 500 < now && NameScan.EnableAuto)
            {
                Logger.LogDebug("OnNameScanHotkey: auto-scan window open, sleeping 200ms then NameScanScreen");
                // Wait for the double click and the game UI without blocking the input
                // hook. The continuation is fire-and-forget and can still fire during
                // shutdown, so it first bails out when this manager is disposed and
                // then routes the scan through Wrap to keep failures observable.
                _ = Task.Delay(200)
                    .ContinueWith(
                        _ =>
                            Wrap(() =>
                            {
                                lock (_registrationLock)
                                {
                                    if (_disposed)
                                        return;
                                }
                                _owner.NameScanScreen();
                            }),
                        TaskScheduler.Default
                    );
            }
            Logger.LogDebug("OnNameScanHotkey: EXIT");
        });
    }

    private void OnIconScanHotkey(object? sender, KeyUpEventArgs e)
    {
        Wrap(() =>
        {
            Logger.LogDebug("OnIconScanHotkey: ENTER");
            _owner.IconScan(UserActivityHelper.GetMousePosition(), e.HookObservedAtMs);
            Logger.LogDebug("OnIconScanHotkey: EXIT");
        });
    }
}
