using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace RatScanner.Display;

internal sealed class WindowsGameDisplayService
{
    private static readonly string[] TarkovProcessNames = ["EscapeFromTarkov", "EscapeFromTarkov_BE"];

    internal GameDisplayConfiguration Detect(GameDisplayPreferences preferences)
    {
        IReadOnlyList<GameDisplayInfo> displays = EnumerateDisplays();
        Rectangle? gameClientBounds = TryGetTarkovClientBounds();
        Size? graphicsViewport = GameGraphicsSettingsReader.TryReadViewport();
        return GameDisplayConfigurationBuilder.Build(displays, gameClientBounds, graphicsViewport, preferences);
    }

    private static IReadOnlyList<GameDisplayInfo> EnumerateDisplays()
    {
        try
        {
            return Screen
                .AllScreens.Select((screen, index) => CreateDisplayInfo(screen, index + 1))
                .OrderBy(display => display.DisplayNumber)
                .ToArray();
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to enumerate Windows displays.", exception);
            return Array.Empty<GameDisplayInfo>();
        }
    }

    private static GameDisplayInfo CreateDisplayInfo(Screen screen, int fallbackNumber)
    {
        string stableId = screen.DeviceName;
        string friendlyName = "";
        try
        {
            DisplayDevice monitor = DisplayDevice.Create();
            if (
                NativeMethods.EnumDisplayDevices(
                    screen.DeviceName,
                    0,
                    ref monitor,
                    NativeMethods.EddGetDeviceInterfaceName
                )
            )
            {
                stableId = FirstNonEmpty(monitor.DeviceID, monitor.DeviceKey, screen.DeviceName) ?? screen.DeviceName;
                friendlyName = monitor.DeviceString?.Trim() ?? "";
            }
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"Unable to query a stable identifier for display '{screen.DeviceName}'.", exception);
        }

        (double dpiScale, bool isDpiReliable) = GetDpiScale(screen.Bounds);
        return new GameDisplayInfo(
            stableId,
            screen.DeviceName,
            friendlyName,
            screen.Bounds,
            screen.Primary,
            dpiScale,
            isDpiReliable,
            ParseDisplayNumber(screen.DeviceName, fallbackNumber)
        );
    }

    private static (double Scale, bool IsReliable) GetDpiScale(Rectangle bounds)
    {
        try
        {
            Point point = new(bounds.Left + Math.Max(1, bounds.Width / 2), bounds.Top + Math.Max(1, bounds.Height / 2));
            nint monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
            int result = NativeMethods.GetDpiForMonitor(monitor, DpiType.Effective, out uint dpiX, out _);
            double scale = dpiX / 96.0;
            if (result == 0 && GameDisplayValidation.IsValidScale(scale))
                return (scale, true);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to determine per-monitor display scaling.", exception);
        }

        return (1, false);
    }

    private static Rectangle? TryGetTarkovClientBounds()
    {
        Rectangle? largestBounds = null;
        foreach (string processName in TarkovProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception exception)
            {
                Logger.LogWarning($"Unable to inspect the {processName} process.", exception);
                continue;
            }

            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        nint handle = process.MainWindowHandle;
                        if (handle == 0 || !NativeMethods.IsWindowVisible(handle))
                            continue;
                        if (!TryGetPhysicalClientBounds(handle, out Rectangle bounds))
                            continue;
                        if (
                            largestBounds is null
                            || (long)bounds.Width * bounds.Height
                                > (long)largestBounds.Value.Width * largestBounds.Value.Height
                        )
                            largestBounds = bounds;
                    }
                    catch (Exception exception)
                    {
                        Logger.LogWarning($"Unable to inspect a {processName} game window.", exception);
                    }
                }
            }
        }

        return largestBounds;
    }

    private static bool TryGetPhysicalClientBounds(nint window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!NativeMethods.GetClientRect(window, out NativeRectangle client))
            return false;

        NativePoint topLeft = new(client.Left, client.Top);
        NativePoint bottomRight = new(client.Right, client.Bottom);
        if (
            !NativeMethods.ClientToScreen(window, ref topLeft) || !NativeMethods.ClientToScreen(window, ref bottomRight)
        )
            return false;

        int width = bottomRight.X - topLeft.X;
        int height = bottomRight.Y - topLeft.Y;
        if (!GameDisplayValidation.IsValidResolution(width, height))
            return false;

        bounds = new Rectangle(topLeft.X, topLeft.Y, width, height);
        return true;
    }

    private static int ParseDisplayNumber(string deviceName, int fallback)
    {
        Match match = Regex.Match(deviceName, "(?<number>\\d+)$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["number"].Value, out int number) ? number : fallback;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    internal enum DpiType
    {
        Effective = 0,
        Angular = 1,
        Raw = 2,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        internal int Cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceString;

        internal int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceKey;

        internal static DisplayDevice Create() => new() { Cb = Marshal.SizeOf<DisplayDevice>() };
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRectangle(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativePoint(int X, int Y);

    private static class NativeMethods
    {
        internal const uint MonitorDefaultToNearest = 2;
        internal const uint EddGetDeviceInterfaceName = 1;

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromPoint([In] Point point, [In] uint flags);

        [DllImport("Shcore.dll")]
        internal static extern int GetDpiForMonitor(
            [In] nint monitor,
            [In] DpiType dpiType,
            [Out] out uint dpiX,
            [Out] out uint dpiY
        );

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayDevices(
            string device,
            uint deviceNumber,
            ref DisplayDevice displayDevice,
            uint flags
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint window, out NativeRectangle rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(nint window, ref NativePoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);
    }
}

internal static class GameGraphicsSettingsReader
{
    private static readonly string GraphicsPath = Environment.ExpandEnvironmentVariables(
        @"%AppData%\Battlestate Games\Escape From Tarkov\Settings\Graphics.ini"
    );

    internal static Size? TryReadViewport()
    {
        try
        {
            if (!File.Exists(GraphicsPath))
                return null;
            using FileStream file = new(GraphicsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(file, Encoding.UTF8);
            return TryParseViewport(reader.ReadToEnd(), out Size viewport) ? viewport : null;
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to query Escape From Tarkov graphic settings.", exception);
            return null;
        }
    }

    internal static bool TryParseViewport(string json, out Size viewport)
    {
        viewport = Size.Empty;
        try
        {
            JObject root = JObject.Parse(json);
            int? width = root["DisplaySettings"]?["Resolution"]?["Width"]?.ToObject<int>();
            int? height = root["DisplaySettings"]?["Resolution"]?["Height"]?.ToObject<int>();

            if (
                !(
                    width.HasValue
                    && height.HasValue
                    && GameDisplayValidation.IsValidResolution(width.Value, height.Value)
                )
            )
            {
                int? displayIndex = root["DisplaySettings"]?["Display"]?.ToObject<int>();
                JToken? stored = root["Stored"]
                    ?.Children()
                    .FirstOrDefault(entry => entry["Index"]?.ToObject<int>() == displayIndex);
                JToken? storedResolution = stored?["WindowResolution"] ?? stored?["FullScreenResolution"];
                width = storedResolution?["Width"]?.ToObject<int>();
                height = storedResolution?["Height"]?.ToObject<int>();
            }

            if (
                !(
                    width.HasValue
                    && height.HasValue
                    && GameDisplayValidation.IsValidResolution(width.Value, height.Value)
                )
            )
                return false;

            viewport = new Size(width.Value, height.Value);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
