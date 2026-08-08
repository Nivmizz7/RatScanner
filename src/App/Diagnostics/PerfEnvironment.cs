using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using RatScanner.Display;

namespace RatScanner.Diagnostics;

/// <summary>One attached display, as reported in a performance export.</summary>
internal sealed record PerfDisplayInfo(
    string DeviceName,
    string FriendlyName,
    bool IsPrimary,
    int Width,
    int Height,
    double DpiScale,
    bool IsDpiReliable,
    int RefreshHz
);

/// <summary>
/// Machine and process context for a performance report. Without this a timeline
/// is not diagnosable remotely: the same scan cost means very different things on
/// a single 1080p60 display than across three high-refresh 4K displays, because
/// the tooltip overlay spans the whole virtual screen.
/// </summary>
internal sealed record PerfEnvironmentSnapshot(
    string ApplicationVersion,
    string RuntimeVersion,
    string OperatingSystem,
    string ProcessArchitecture,
    int ProcessorCount,
    long TotalPhysicalMemoryBytes,
    string WebView2Runtime,
    int WpfRenderTier,
    int VirtualScreenWidth,
    int VirtualScreenHeight,
    double VirtualScreenMegapixels,
    IReadOnlyList<PerfDisplayInfo> Displays,
    IReadOnlyList<string> GraphicsAdapters,
    long ProcessWorkingSetBytes,
    int WebView2ProcessCount,
    long WebView2WorkingSetBytes
);

internal static class PerfEnvironment
{
    /// <summary>
    /// Captures the current environment. Called only when building a report, not on
    /// the scan path — process enumeration and display queries are too slow to run
    /// per scan.
    /// </summary>
    internal static PerfEnvironmentSnapshot Capture()
    {
        Rectangle virtualScreen = GetVirtualScreenBounds();
        (int webViewCount, long webViewWorkingSet) = MeasureWebViewProcesses();

        return new PerfEnvironmentSnapshot(
            RatConfig.VersionDisplay,
            Environment.Version.ToString(),
            Environment.OSVersion.VersionString,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            TryGetTotalPhysicalMemory(),
            TryGetWebView2Version(),
            TryGetRenderTier(),
            virtualScreen.Width,
            virtualScreen.Height,
            Math.Round(virtualScreen.Width * (double)virtualScreen.Height / 1_000_000d, 2),
            DescribeDisplays(),
            DescribeAdapters(),
            TryGetProcessWorkingSet(),
            webViewCount,
            webViewWorkingSet
        );
    }

    /// <summary>
    /// Bounds of the union of all screens — the size the tooltip overlay window is
    /// stretched to, and therefore the size of the surface it composites.
    /// </summary>
    internal static Rectangle GetVirtualScreenBounds()
    {
        int left = 0;
        int top = 0;
        int right = 0;
        int bottom = 0;
        foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
        {
            Rectangle bounds = screen.Bounds;
            left = Math.Min(left, bounds.Left);
            top = Math.Min(top, bounds.Top);
            right = Math.Max(right, bounds.Right);
            bottom = Math.Max(bottom, bounds.Bottom);
        }
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static IReadOnlyList<PerfDisplayInfo> DescribeDisplays()
    {
        List<PerfDisplayInfo> displays = [];
        try
        {
            // Reuse the display configuration the scanner already maintains so the
            // report agrees with the geometry the scan pipeline actually used.
            IReadOnlyList<GameDisplayInfo> known = RatConfig.GameDisplayConfiguration.Displays;
            foreach (GameDisplayInfo display in known)
            {
                displays.Add(
                    new PerfDisplayInfo(
                        display.DeviceName,
                        display.FriendlyName,
                        display.IsPrimary,
                        display.PhysicalBounds.Width,
                        display.PhysicalBounds.Height,
                        Math.Round(display.DpiScale, 3),
                        display.IsDpiReliable,
                        TryGetRefreshRate(display.DeviceName)
                    )
                );
            }
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to describe displays for the performance report.", exception);
        }
        return displays;
    }

    private static IReadOnlyList<string> DescribeAdapters()
    {
        List<string> adapters = [];
        try
        {
            for (uint index = 0; index < 16; index++)
            {
                NativeDisplayDevice device = NativeDisplayDevice.Create();
                if (!NativeMethods.EnumDisplayDevices(null, index, ref device, 0))
                    break;
                // Only adapters actually driving a desktop matter for compositing cost.
                if ((device.StateFlags & NativeMethods.DisplayDeviceAttachedToDesktop) == 0)
                    continue;
                if (!string.IsNullOrWhiteSpace(device.DeviceString) && !adapters.Contains(device.DeviceString))
                    adapters.Add(device.DeviceString);
            }
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Unable to enumerate graphics adapters for the performance report.", exception);
        }
        return adapters;
    }

    private static int TryGetRefreshRate(string deviceName)
    {
        try
        {
            NativeDeviceMode mode = NativeDeviceMode.Create();
            return NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.EnumCurrentSettings, ref mode)
                ? mode.DisplayFrequency
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string TryGetWebView2Version()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "unknown";
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }

    /// <summary>
    /// WPF render tier: 0 means software rendering, 2 means full hardware
    /// acceleration. Relevant because a layered (per-pixel alpha) window is
    /// composited on the CPU regardless of tier.
    /// </summary>
    private static int TryGetRenderTier()
    {
        try
        {
            return System.Windows.Media.RenderCapability.Tier >> 16;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static long TryGetProcessWorkingSet()
    {
        try
        {
            using Process current = Process.GetCurrentProcess();
            return current.WorkingSet64;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Counts WebView2 host processes and their combined working set. This is
    /// machine-wide, not just this application's children: attributing children to
    /// a parent needs a slow WMI query, and the aggregate is enough to spot a
    /// runaway renderer.
    /// </summary>
    private static (int Count, long WorkingSetBytes) MeasureWebViewProcesses()
    {
        try
        {
            Process[] processes = Process.GetProcessesByName("msedgewebview2");
            try
            {
                long workingSet = 0;
                foreach (Process process in processes)
                {
                    try
                    {
                        workingSet += process.WorkingSet64;
                    }
                    catch (Exception)
                    {
                        // The process can exit between enumeration and inspection.
                    }
                }
                return (processes.Length, workingSet);
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    private static long TryGetTotalPhysicalMemory()
    {
        try
        {
            NativeMemoryStatus status = NativeMemoryStatus.Create();
            return NativeMethods.GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhysical : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    internal static string FormatBytes(long bytes) =>
        bytes <= 0 ? "0 MB" : (bytes / 1024d / 1024d).ToString("F0", CultureInfo.InvariantCulture) + " MB";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDisplayDevice
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

        internal static NativeDisplayDevice Create() => new() { Cb = Marshal.SizeOf<NativeDisplayDevice>() };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDeviceMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;

        internal ushort SpecVersion;
        internal ushort DriverVersion;
        internal ushort Size;
        internal ushort DriverExtra;
        internal uint Fields;
        internal int PositionX;
        internal int PositionY;
        internal uint DisplayOrientation;
        internal uint DisplayFixedOutput;
        internal short Color;
        internal short Duplex;
        internal short YResolution;
        internal short TrueTypeOption;
        internal short Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string FormName;

        internal ushort LogPixels;
        internal uint BitsPerPixel;
        internal uint PixelsWidth;
        internal uint PixelsHeight;
        internal uint DisplayFlags;
        internal int DisplayFrequency;
        internal uint IcmMethod;
        internal uint IcmIntent;
        internal uint MediaType;
        internal uint DitherType;
        internal uint Reserved1;
        internal uint Reserved2;
        internal uint PanningWidth;
        internal uint PanningHeight;

        internal static NativeDeviceMode Create() =>
            new()
            {
                DeviceName = string.Empty,
                FormName = string.Empty,
                Size = (ushort)Marshal.SizeOf<NativeDeviceMode>(),
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;

        internal static NativeMemoryStatus Create() => new() { Length = (uint)Marshal.SizeOf<NativeMemoryStatus>() };
    }

    private static class NativeMethods
    {
        internal const int EnumCurrentSettings = -1;
        internal const int DisplayDeviceAttachedToDesktop = 0x00000001;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayDevices(
            string? device,
            uint deviceNumber,
            ref NativeDisplayDevice displayDevice,
            uint flags
        );

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref NativeDeviceMode mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref NativeMemoryStatus buffer);
    }
}
