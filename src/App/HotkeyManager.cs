using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using RatScanner.View;
using static RatScanner.RatConfig;
using OverlayC = RatScanner.RatConfig.Overlay;

namespace RatScanner;

internal sealed class HotkeyManager : IDisposable
{
    private readonly RatScannerMain _owner;
    private long _last_mouse_click = 0;
    private bool _disposed;

    internal ActiveHotkey NameScanHotkey;
    internal ActiveHotkey IconScanHotkey;
    internal ActiveHotkey OpenInteractableOverlayHotkey;
    internal ActiveHotkey CloseInteractableOverlayHotkey;

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
    [MemberNotNull(
        nameof(NameScanHotkey),
        nameof(IconScanHotkey),
        nameof(OpenInteractableOverlayHotkey),
        nameof(CloseInteractableOverlayHotkey)
    )]
    internal void RegisterHotkeys()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Unregister hotkeys to prevent multiple listeners for the same hotkey
        UnregisterHotkeys();

        IconScanHotkey = new ActiveHotkey(IconScan.Hotkey, OnIconScanHotkey, ref IconScan.Enable);
        Hotkey nameScanHotkey = new(null, new[] { MouseButton.Left });
        NameScanHotkey = new ActiveHotkey(
            nameScanHotkey,
            OnNameScanHotkey,
            ref NameScan.Enable,
            canHandle: e => !IconScanHotkey.Enabled || !IconScanHotkey.IsPressed(e)
        );
        OpenInteractableOverlayHotkey = new ActiveHotkey(
            OverlayC.Search.Hotkey,
            OnOpenInteractableOverlayHotkey,
            ref OverlayC.Search.Enable
        );
        CloseInteractableOverlayHotkey = new ActiveHotkey(
            new Hotkey(new[] { Key.Escape }),
            OnCloseInteractableOverlayHotkey
        );
    }

    /// <summary>
    /// Unregister hotkeys
    /// </summary>
    internal void UnregisterHotkeys()
    {
        NameScanHotkey?.Dispose();
        IconScanHotkey?.Dispose();
        OpenInteractableOverlayHotkey?.Dispose();
        CloseInteractableOverlayHotkey?.Dispose();
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
            UnregisterHotkeys();
            UserActivityHelper.Stop(true, true, false);
        }
        _disposed = true;
    }

    private static void Wrap<T>(Func<T> func)
    {
        try
        {
            func();
        }
        catch (Exception e)
        {
            Logger.LogWarning("A RatScanner hotkey action failed.", e);
        }
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
            _owner.NameScan(UserActivityHelper.GetMousePosition());
            // Claim the auto-scan window atomically so concurrent Task.Run handlers from a
            // real double-click cannot both pass the debounce and both call NameScanScreen.
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            long previous = Interlocked.Exchange(ref _last_mouse_click, now);
            if (previous + 500 < now && NameScan.EnableAuto)
            {
                Thread.Sleep(200); // wait for double click and ui
                _owner.NameScanScreen();
            }
        });
    }

    private void OnIconScanHotkey(object? sender, KeyUpEventArgs e)
    {
        Wrap(() => _owner.IconScan(UserActivityHelper.GetMousePosition()));
    }

    private void OnOpenInteractableOverlayHotkey(object? sender, KeyUpEventArgs e)
    {
        Wrap(() =>
            Application.Current.Dispatcher.Invoke(() => Wrap(() => BlazorUI.BlazorInteractableOverlay.ShowOverlay()))
        );
    }

    private void OnCloseInteractableOverlayHotkey(object? sender, KeyUpEventArgs e)
    {
        Wrap(() =>
            Application.Current.Dispatcher.Invoke(() => Wrap(() => BlazorUI.BlazorInteractableOverlay.HideOverlay()))
        );
    }
}
