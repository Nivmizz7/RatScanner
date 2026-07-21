using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace RatScanner;

internal class ActiveHotkey : Hotkey, IDisposable
{
    private event UserActivityHelper.KeyUpEventHandler HotkeyPressedEventHandler;
    private readonly Func<KeyUpEventArgs, bool>? _canHandle;

    /// <summary>
    /// <see langword="true"/> if the hotkey should not be forwarded down
    /// </summary>
    internal bool SuppressHotkey = false;

    internal bool Enabled = true;

    /// <summary>
    /// Create a new active hotkey which will notify the event handler, when the hotkey is pressed
    /// </summary>
    /// <param name="hotkey">The hotkey which will be listened for</param>
    /// <param name="hotkeyPressedEventHandler">The event handler which will be notified</param>
    /// <param name="suppressHotkey"><see langword="true"/> if the hotkey should not be forwarded down the chain</param>
    internal ActiveHotkey(
        Hotkey hotkey,
        UserActivityHelper.KeyUpEventHandler hotkeyPressedEventHandler,
        bool suppressHotkey = false
    )
        : base(hotkey.KeyboardKeys, hotkey.MouseButtons)
    {
        HotkeyPressedEventHandler += hotkeyPressedEventHandler;
        SuppressHotkey = suppressHotkey;
        RegisterEventListeners();
    }

    /// <summary>
    /// Create a new active hotkey which will notify the event handler, when the hotkey is pressed
    /// </summary>
    /// <param name="hotkey">The hotkey which will be listened for</param>
    /// <param name="hotkeyPressedEventHandler">The event handler which will be notified</param>
    /// <param name="enabled"><see langword="false"/> to disable the active hotkey</param>
    /// <param name="suppressHotkey"><see langword="true"/> if the hotkey should not be forwarded down the chain</param>
    internal ActiveHotkey(
        Hotkey hotkey,
        UserActivityHelper.KeyUpEventHandler hotkeyPressedEventHandler,
        bool enabled,
        bool suppressHotkey = false,
        Func<KeyUpEventArgs, bool>? canHandle = null
    )
        : base(hotkey.KeyboardKeys, hotkey.MouseButtons)
    {
        HotkeyPressedEventHandler += hotkeyPressedEventHandler;
        Enabled = enabled;
        SuppressHotkey = suppressHotkey;
        _canHandle = canHandle;
        RegisterEventListeners();
    }

    /// <summary>
    /// Create a new active hotkey which will notify the event handler, when the hotkey is pressed
    /// </summary>
    /// <param name="keyboardKeys">The keyboard keys of the hotkey which will be listened for</param>
    /// <param name="mouseButtons">The mouse buttons of the hotkey which will be listened for</param>
    /// <param name="hotkeyPressedEventHandler">The event handler which will be notified</param>
    /// <param name="suppressHotkey"><see langword="true"/> if the hotkey should not be forwarded down the chain</param>
    internal ActiveHotkey(
        List<Key> keyboardKeys,
        List<MouseButton> mouseButtons,
        UserActivityHelper.KeyUpEventHandler hotkeyPressedEventHandler,
        bool suppressHotkey = false
    )
        : base(keyboardKeys, mouseButtons)
    {
        HotkeyPressedEventHandler += hotkeyPressedEventHandler;
        SuppressHotkey = suppressHotkey;
        RegisterEventListeners();
    }

    /// <summary>
    /// Create a new active hotkey which will notify the event handler, when the hotkey is pressed
    /// </summary>
    /// <param name="keyboardKeys">The keyboard keys of the hotkey which will be listened for</param>
    /// <param name="mouseButtons">The mouse buttons of the hotkey which will be listened for</param>
    /// <param name="hotkeyPressedEventHandler">The event handler which will be notified</param>
    /// <param name="enabled"><see langword="false"/> to disable the active hotkey</param>
    /// <param name="suppressHotkey"><see langword="true"/> if the hotkey should not be forwarded down the chain</param>
    internal ActiveHotkey(
        List<Key> keyboardKeys,
        List<MouseButton> mouseButtons,
        UserActivityHelper.KeyUpEventHandler hotkeyPressedEventHandler,
        bool enabled,
        bool suppressHotkey = false
    )
        : base(keyboardKeys, mouseButtons)
    {
        HotkeyPressedEventHandler += hotkeyPressedEventHandler;
        SuppressHotkey = suppressHotkey;
        Enabled = enabled;
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
