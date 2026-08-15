using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RatScanner;

internal class SimpleConfig
{
    internal string Path;
    internal string Section;
    internal string EnumerableSeparator = ";";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(string section, string key, string? val, string filePath);

    // GetPrivateProfileString writes a variable-length null-terminated value into this buffer.
#pragma warning disable CA1838
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileString(
        string section,
        string key,
        string def,
        StringBuilder retVal,
        uint size,
        string filePath
    );
#pragma warning restore CA1838

    internal SimpleConfig(string configPath, string section = "default")
    {
        Path = configPath;
        Section = section;
    }

    internal void WriteString(string key, string value)
    {
        if (!WritePrivateProfileString(Section, key.ToLowerInvariant(), value, Path))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to write configuration file '{Path}'.");
    }

    internal void WriteSecureString(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteString(key, value);
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] encryptedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        string hexString = Convert.ToHexString(encryptedBytes);
        WriteString(key, hexString);
    }

    internal void RemoveValue(string key)
    {
        if (!WritePrivateProfileString(Section, key.ToLowerInvariant(), null, Path))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to update configuration file '{Path}'.");
    }

    internal void WriteInt(string key, int value)
    {
        WriteString(key, value.ToString(CultureInfo.InvariantCulture));
    }

    internal void WriteFloat(string key, float value)
    {
        WriteString(key, value.ToString(CultureInfo.InvariantCulture));
    }

    internal void WriteBool(string key, bool value)
    {
        WriteString(key, value.ToString(CultureInfo.InvariantCulture));
    }

    internal void WriteEnumerableEnum<T>(string key, IEnumerable<T> value)
        where T : struct, IConvertible
    {
        if (value == null || !value.Any())
        {
            WriteString(key, "null");
            return;
        }
        WriteString(key, string.Join(EnumerableSeparator, value));
    }

    internal void WriteHotkey(string key, Hotkey value)
    {
        WriteEnumerableEnum(key + "Keyboard", value.KeyboardKeys);
        WriteEnumerableEnum(key + "Mouse", value.MouseButtons);
    }

    private string ReadStringInternal(string key)
    {
        const string def = "RatScanner.Config.Default.Exception";
        for (int capacity = 1024; capacity <= short.MaxValue; capacity *= 2)
        {
            StringBuilder temp = new(capacity);
            uint length = GetPrivateProfileString(
                Section,
                key.ToLowerInvariant(),
                def,
                temp,
                (uint)temp.Capacity,
                Path
            );
            string result = temp.ToString();
            if (result == def)
                throw new KeyNotFoundException(def);
            if (length < temp.Capacity - 1)
                return result;
        }

        throw new InvalidDataException($"Configuration value '{Section}.{key}' is too long.");
    }

    private static T ReadOrDefault<T>(Func<T> read, T defaultValue)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return defaultValue;
        }
    }

    internal string ReadString(string key, string defaultValue) =>
        ReadOrDefault(() => ReadStringInternal(key), defaultValue);

    internal string ReadSecureString(string key, string defaultValue) =>
        ReadOrDefault(
            () =>
            {
                string hexString = ReadStringInternal(key);
                if (string.IsNullOrEmpty(hexString))
                    return "";
                byte[] encryptedBytes = Convert.FromHexString(hexString);
                byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decryptedBytes);
            },
            defaultValue
        );

    internal int ReadInt(string key, int defaultValue) =>
        ReadOrDefault(() => int.Parse(ReadStringInternal(key), CultureInfo.InvariantCulture), defaultValue);

    internal float ReadFloat(string key, float defaultValue) =>
        ReadOrDefault(() => float.Parse(ReadStringInternal(key), CultureInfo.InvariantCulture), defaultValue);

    internal bool ReadBool(string key, bool defaultValue) =>
        ReadOrDefault(() => bool.Parse(ReadStringInternal(key)), defaultValue);

    internal IEnumerable<TEnum> ReadEnumerableEnum<TEnum>(string key, IEnumerable<TEnum> defaultValue)
        where TEnum : struct, Enum =>
        ReadOrDefault(
            () =>
            {
                string[]? readStrings = ReadStringInternal(key)?.Split(EnumerableSeparator);
                if (readStrings == null || readStrings.Length == 0)
                    throw new InvalidOperationException("No enum values found.");
                if (readStrings[0] == "null")
                    return Enumerable.Empty<TEnum>();
                if (readStrings.Length == 1 && readStrings[0] == "")
                    throw new InvalidOperationException("No enum values found.");
                return readStrings.Select(Enum.Parse<TEnum>);
            },
            defaultValue
        );

    internal Hotkey ReadHotkey(string key, Hotkey? defaultValue)
    {
        defaultValue ??= new Hotkey();
        IEnumerable<System.Windows.Input.Key> keyboardKeys = ReadEnumerableEnum(
            key + "Keyboard",
            defaultValue.KeyboardKeys
        );
        IEnumerable<System.Windows.Input.MouseButton> mouseButtons = ReadEnumerableEnum(
            key + "Mouse",
            defaultValue.MouseButtons
        );
        return new Hotkey(keyboardKeys.ToList(), mouseButtons.ToList());
    }
}
