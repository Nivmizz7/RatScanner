using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace RatScanner;

internal class ActiveHotkey : Hotkey, IDisposable
{
    private event UserActivityHelper.KeyUpEventHandler? HotkeyPressedEventHandler;
    private Func<KeyUpEventArgs, bool>? _canHandle;

    /// <summary>
    /// <see langword="true"/> if the hotkey should not be forwarded down
    /// </summary>
    internal bool SuppressHotkey;

    internal bool Enabled = true;

    /// <summary>
    /// Create a new active hotkey which will notify the event handler when the hotkey is pressed.
    /// </summary>
    internal ActiveHotkey(
        Hotkey hotkey,
        UserActivityHelper.KeyUpEventHandler hotkeyPressedEventHandler,
        bool enabled = true,
        bool suppressHotkey = false,
        Func<KeyUpEventArgs, bool>? canHandle = null
    )
        : base(hotkey.KeyboardKeys, hotkey.MouseButtons)
    {
        HotkeyPressedEventHandler += hotkeyPressedEventHandler;
        Initialize(enabled, suppressHotkey, canHandle);
    }

    /// <summary>
    /// Create a new active hotkey which will notify the event handler when the hotkey is pressed.
    /// </summary>
    internal ActiveHotkey(
        IEnumerable<Key> keyboardKeys,
        IEnumerable<MouseButton> mouseButtons,
        UserActivityHelper.KeyUpEventHandler hotkeyPressedEventHandler,
        bool enabled = true,
        bool suppressHotkey = false
    )
        : base(keyboardKeys, mouseButtons)
    {
        HotkeyPressedEventHandler += hotkeyPressedEventHandler;
        Initialize(enabled, suppressHotkey, null);
    }

    private void Initialize(bool enabled, bool suppressHotkey, Func<KeyUpEventArgs, bool>? canHandle)
    {
        Enabled = enabled;
        SuppressHotkey = suppressHotkey;
        _canHandle = canHandle;
        RegisterEventListeners();
    }

    private void RegisterEventListeners()
    {
        if (RequiresKeyboard)
            UserActivityHelper.OnKeyboardKeyUp += OnKeyUp;
        if (RequiresMouse)
            UserActivityHelper.OnMouseButtonUp += OnKeyUp;
    }

    private void UnregisterEventListeners()
    {
        if (RequiresKeyboard)
            UserActivityHelper.OnKeyboardKeyUp -= OnKeyUp;
        if (RequiresMouse)
            UserActivityHelper.OnMouseButtonUp -= OnKeyUp;
    }

    private void OnKeyUp(object? sender, KeyUpEventArgs e)
    {
        if (!Enabled)
        {
            Logger.LogDebug(
                $"ActiveHotkey.OnKeyUp: SKIPPED (disabled) hotkey={ToString()} device={e.Device} vk=0x{e.VKCode:X2}"
            );
            return;
        }
        if (_canHandle != null && !_canHandle(e))
        {
            Logger.LogDebug(
                $"ActiveHotkey.OnKeyUp: canHandle=false (rejected) hotkey={ToString()} device={e.Device} vk=0x{e.VKCode:X2}"
            );
            return;
        }
        bool pressed = IsPressed(e);
        Logger.LogDebug(
            $"ActiveHotkey.OnKeyUp: hotkey={ToString()} device={e.Device} vk=0x{e.VKCode:X2} IsPressed={pressed} SuppressHotkey={SuppressHotkey}"
        );
        if (pressed && HotkeyPressedEventHandler != null)
        {
            Logger.LogDebug("Pressed: " + ToString());
            e.Handled |= SuppressHotkey;
            Logger.LogDebug($"ActiveHotkey.OnKeyUp: firing handler via Task.Run, Handled={e.Handled}");
            Task.Run(() => HotkeyPressedEventHandler(sender, e));
        }
        else
        {
            Logger.LogDebug(
                $"ActiveHotkey.OnKeyUp: NOT firing (pressed={pressed} hasHandler={HotkeyPressedEventHandler != null})"
            );
        }
    }

    internal bool IsPressed(KeyUpEventArgs e)
    {
        if (e == null)
            throw new ArgumentNullException(nameof(e), "KeyUpEventArgs can not be empty!");

        bool keyInHotkey = false;

        if (RequiresKeyboard)
        {
            foreach (Key keyboardKey in KeyboardKeys)
            {
                bool isDown = UserActivityHelper.IsKeyDown(keyboardKey);
                if (!isDown)
                {
                    Logger.LogDebug($"IsPressed: keyboard key {keyboardKey} is NOT down → returning false");
                    return false;
                }
                if (e.Device == Device.Keyboard)
                    keyInHotkey |= e.Key == keyboardKey;
            }
        }

        if (RequiresMouse)
        {
            foreach (MouseButton mouseButton in MouseButtons)
            {
                bool isDown = UserActivityHelper.IsMouseButtonDown(mouseButton);
                if (!isDown)
                {
                    Logger.LogDebug($"IsPressed: mouse button {mouseButton} is NOT down → returning false");
                    return false;
                }
                if (e.Device == Device.Mouse)
                    keyInHotkey |= e.MouseButton == mouseButton;
            }
        }

        Logger.LogDebug(
            $"IsPressed: returning {keyInHotkey} (RequiresKeyboard={RequiresKeyboard} RequiresMouse={RequiresMouse} device={e.Device})"
        );
        return keyInHotkey;
    }

    public void Dispose()
    {
        UnregisterEventListeners();
    }
}
