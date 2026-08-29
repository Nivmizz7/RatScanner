using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.DXGI.ResultCode;

namespace RatScanner.Display;

/// <summary>
/// HDR-aware screen capture built on DXGI Desktop Duplication.
///
/// When Windows advanced color (HDR) is enabled, the desktop is composed in FP16 linear
/// scRGB, which legacy GDI capture cannot represent: it silently clips and mis-encodes
/// the frame, producing the classic washed-out / overbright screenshots (and failing icon
/// template matching). This backend captures the desktop duplication stream in
/// <see cref="Format.R16G16B16A16_Float"/> and tone maps it to SDR with
/// <see cref="HdrToneMapper"/> using the display's actual SDR reference white level.
///
/// Duplication sessions are cached per output so repeated scans only pay for a GPU copy +
/// tone map. Every public entry point is failure-soft: any error returns <see langword="null"/>
/// (or <see langword="false"/> for detection) so the caller can fall back to the legacy GDI
/// capture path, keeping SDR behavior byte-for-byte unchanged.
/// </summary>
internal static class HdrScreenCapture
{
    private const int AcquireFrameTimeoutMs = 200;

    // Display states are stable at display-session granularity; cache the enumeration
    // briefly so SDR scans (the hot path) do not pay DXGI enumeration on every hotkey
    // press. The cache holds per-output states, not a single bool, so mixed HDR/SDR
    // multi-monitor setups route each capture region correctly.
    private static readonly TimeSpan DetectionTtl = TimeSpan.FromSeconds(2);
    private static readonly object DetectionLock = new();
    private static DateTimeOffset _detectionCheckedAt = DateTimeOffset.MinValue;
    private static List<(Rectangle Bounds, bool IsHdr, float SdrWhiteNits)> _cachedOutputs = [];
    private static bool _detectionFailed;

    private static readonly object SyncLock = new();
    private static IDXGIFactory1? _factory;
    private static readonly List<OutputSession> Sessions = [];

    private sealed class OutputSession : IDisposable
    {
        public ID3D11Device? Device;
        public ID3D11DeviceContext? Context;
        public IDXGIOutputDuplication? Duplication;
        public ID3D11Texture2D? LastFrame;
        public ID3D11Texture2D? StagingTexture;
        public Rectangle DesktopBounds;
        public string DeviceName = "";
        public bool IsHdr;
        public float SdrWhiteLevelNits = 200f;
        public float MaxLuminanceNits;
        public bool HasFrame;

        public void Dispose()
        {
            StagingTexture?.Dispose();
            StagingTexture = null;
            LastFrame?.Dispose();
            LastFrame = null;
            Duplication?.Dispose();
            Duplication = null;
            Context?.Dispose();
            Context = null;
            Device?.Dispose();
            Device = null;
        }
    }

    /// <summary>
    /// True when any display intersecting <paramref name="rect"/> is currently in HDR
    /// (advanced color) mode, meaning GDI capture would produce incorrect colors.
    /// </summary>
    internal static bool IsHdrCaptureRequired(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return false;

        List<(Rectangle Bounds, bool IsHdr, float SdrWhiteNits)> outputs = _cachedOutputs;
        if (DateTimeOffset.UtcNow - _detectionCheckedAt >= DetectionTtl)
        {
            lock (DetectionLock)
            {
                if (DateTimeOffset.UtcNow - _detectionCheckedAt >= DetectionTtl)
                {
                    _detectionCheckedAt = DateTimeOffset.UtcNow;
                    try
                    {
                        _cachedOutputs = QueryHdrOutputs();
                        _detectionFailed = false;
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning("HDR display state query failed; assuming SDR capture path.", e);
                        _cachedOutputs = [];
                        _detectionFailed = true;
                    }
                }
                outputs = _cachedOutputs;
            }
        }

        return AnyHdrDisplayIntersects(outputs, rect);
    }

    /// <summary>True when any HDR display in <paramref name="outputs"/> intersects <paramref name="rect"/>.</summary>
    internal static bool AnyHdrDisplayIntersects(
        List<(Rectangle Bounds, bool IsHdr, float SdrWhiteNits)> outputs,
        Rectangle rect
    ) => outputs.Any(state => state.IsHdr && state.Bounds.IntersectsWith(rect));

    internal static bool DetectionFailed => _detectionFailed;

    /// <summary>Last capture failure detail, for diagnostics (null when the last capture succeeded).</summary>
    internal static string? LastCaptureError { get; private set; }

    /// <summary>True when duplication sessions are active.</summary>
    internal static bool HasSessions => Sessions.Count > 0;

    /// <summary>
    /// Captures <paramref name="rect"/> (virtual desktop coordinates) with correct HDR
    /// handling. Returns <see langword="null"/> when capture is not possible, in which case
    /// the caller should fall back to GDI.
    /// </summary>
    internal static Bitmap? CaptureRectangle(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return null;

        lock (SyncLock)
        {
            try
            {
                EnsureSessions();

                List<OutputSession> intersecting = Sessions.Where(s => s.DesktopBounds.IntersectsWith(rect)).ToList();
                if (intersecting.Count == 0)
                    return null;

                Bitmap bmp = new(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
                try
                {
                    BitmapData bmpData = bmp.LockBits(
                        new Rectangle(0, 0, rect.Width, rect.Height),
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format24bppRgb
                    );
                    try
                    {
                        foreach (OutputSession session in intersecting)
                            CaptureOutputRegion(session, rect, bmpData);
                    }
                    finally
                    {
                        bmp.UnlockBits(bmpData);
                    }
                    return bmp;
                }
                catch
                {
                    bmp.Dispose();
                    throw;
                }
            }
            catch (Exception e)
            {
                LastCaptureError = e.ToString();
                Logger.LogWarning("HDR capture failed; falling back to GDI capture.", e);
                // Sessions may hold a poisoned duplication (mode switch, HDR toggle); the next
                // capture rebuilds them from scratch.
                ResetSessions();
                return null;
            }
        }
    }

    /// <summary>Forces session teardown; used by diagnostics and tests.</summary>
    internal static void ResetSessions()
    {
        lock (SyncLock)
        {
            foreach (OutputSession session in Sessions)
                session.Dispose();
            Sessions.Clear();
            _factory?.Dispose();
            _factory = null;
        }
    }

    private static List<(Rectangle Bounds, bool IsHdr, float SdrWhiteNits)> QueryHdrOutputs()
    {
        List<(Rectangle, bool, float)> outputs = [];
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        Dictionary<string, DisplayColorInfo.AdvancedColorState> colorStates = DisplayColorInfo.GetAdvancedColorStates();

        for (
            uint adapterIndex = 0;
            factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success;
            adapterIndex++
        )
        {
            using (adapter)
            {
                for (
                    uint outputIndex = 0;
                    adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success;
                    outputIndex++
                )
                {
                    using (output)
                    using (IDXGIOutput6 output6 = output.QueryInterface<IDXGIOutput6>())
                    {
                        OutputDescription1 desc = output6.Description1;
                        if (!desc.AttachedToDesktop)
                            continue;

                        bool isHdr = IsHdrColorSpace(desc.ColorSpace);
                        float sdrWhite = 200f;
                        if (colorStates.TryGetValue(desc.DeviceName, out DisplayColorInfo.AdvancedColorState state))
                        {
                            isHdr |= state.AdvancedColorEnabled;
                            sdrWhite = state.SdrWhiteLevelNits;
                        }

                        outputs.Add(
                            (
                                new Rectangle(
                                    desc.DesktopCoordinates.Left,
                                    desc.DesktopCoordinates.Top,
                                    desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                                    desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top
                                ),
                                isHdr,
                                sdrWhite
                            )
                        );
                    }
                }
            }
        }
        return outputs;
    }

    /// <summary>HDR10 signaling color spaces (advanced color enabled on the output).</summary>
    internal static bool IsHdrColorSpace(ColorSpaceType colorSpace) =>
        colorSpace
            is ColorSpaceType.RgbFullG2084NoneP2020
                or ColorSpaceType.RgbStudioG2084NoneP2020
                or ColorSpaceType.YcbcrStudioG2084LeftP2020
                or ColorSpaceType.YcbcrStudioG2084TopLeftP2020
                or ColorSpaceType.YcbcrStudioGhlgTopLeftP2020
                or ColorSpaceType.YcbcrFullGhlgTopLeftP2020;

    private static void EnsureSessions()
    {
        if (_factory is null || !_factory.IsCurrent || Sessions.Count == 0)
        {
            ResetSessions();
            CreateSessions();
        }
    }

    private static void CreateSessions()
    {
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        Dictionary<string, DisplayColorInfo.AdvancedColorState> colorStates = DisplayColorInfo.GetAdvancedColorStates();

        for (
            uint adapterIndex = 0;
            _factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success;
            adapterIndex++
        )
        {
            using (adapter)
            {
                for (
                    uint outputIndex = 0;
                    adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success;
                    outputIndex++
                )
                {
                    using (output)
                    using (IDXGIOutput6 output6 = output.QueryInterface<IDXGIOutput6>())
                    {
                        OutputDescription1 desc = output6.Description1;
                        if (!desc.AttachedToDesktop)
                            continue;
                        if (desc.Rotation != ModeRotation.Identity)
                        {
                            Logger.LogDebug(
                                $"Skipping {desc.DeviceName}: rotated output unsupported, GDI capture covers it."
                            );
                            continue;
                        }

                        // One D3D11 device per output session rather than one shared per adapter:
                        // a DuplicateOutput failure on one output must never poison the sessions
                        // of sibling outputs. Sessions are created rarely (mode changes only), so
                        // the extra devices are a cheap trade for independent failure domains.
                        OutputSession session = new()
                        {
                            DesktopBounds = new Rectangle(
                                desc.DesktopCoordinates.Left,
                                desc.DesktopCoordinates.Top,
                                desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                                desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top
                            ),
                            DeviceName = desc.DeviceName,
                            IsHdr = IsHdrColorSpace(desc.ColorSpace),
                            MaxLuminanceNits = desc.MaxLuminance,
                        };

                        if (colorStates.TryGetValue(desc.DeviceName, out DisplayColorInfo.AdvancedColorState state))
                        {
                            session.IsHdr |= state.AdvancedColorEnabled;
                            session.SdrWhiteLevelNits = state.SdrWhiteLevelNits;
                        }

                        try
                        {
                            D3D11
                                .D3D11CreateDevice(
                                    adapter,
                                    DriverType.Unknown,
                                    DeviceCreationFlags.BgraSupport,
                                    [
                                        FeatureLevel.Level_11_1,
                                        FeatureLevel.Level_11_0,
                                        FeatureLevel.Level_10_1,
                                        FeatureLevel.Level_10_0,
                                    ],
                                    out session.Device
                                )
                                .CheckError();
                            session.Context = session.Device!.ImmediateContext;
                            session.Duplication = output6.DuplicateOutput(session.Device);
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning($"Desktop duplication unavailable for {desc.DeviceName}.", e);
                            session.Dispose();
                            continue;
                        }

                        Logger.LogDebug(
                            $"Desktop duplication session created for {desc.DeviceName}: HDR={session.IsHdr}, "
                                + $"SDRWhiteLevel={session.SdrWhiteLevelNits}nits, MaxLuminance={session.MaxLuminanceNits}nits"
                        );
                        Sessions.Add(session);
                    }
                }
            }
        }
    }

    private static unsafe void CaptureOutputRegion(OutputSession session, Rectangle rect, BitmapData bmpData)
    {
        UpdateLastFrame(session);
        if (!session.HasFrame)
            throw new InvalidOperationException($"No desktop frame available for {session.DeviceName}.");

        Rectangle intersection = Rectangle.Intersect(session.DesktopBounds, rect);
        if (intersection.Width <= 0 || intersection.Height <= 0)
            return;

        Rectangle frameRect = new(
            intersection.X - session.DesktopBounds.X,
            intersection.Y - session.DesktopBounds.Y,
            intersection.Width,
            intersection.Height
        );

        Texture2DDescription frameDesc = session.LastFrame!.Description;

        // Stage and read back only the requested region: region captures on 4K/8K HDR
        // desktops would otherwise pay a full-frame GPU copy and CPU readback.
        if (
            session.StagingTexture is null
            || session.StagingTexture.Description.Width != (uint)frameRect.Width
            || session.StagingTexture.Description.Height != (uint)frameRect.Height
            || session.StagingTexture.Description.Format != frameDesc.Format
        )
        {
            session.StagingTexture?.Dispose();
            session.StagingTexture = session.Device!.CreateTexture2D(
                new Texture2DDescription
                {
                    Width = (uint)frameRect.Width,
                    Height = (uint)frameRect.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = frameDesc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                }
            );
        }

        Box sourceBox = new(frameRect.Left, frameRect.Top, 0, frameRect.Right, frameRect.Bottom, 1);
        session.Context!.CopySubresourceRegion(session.StagingTexture, 0, 0, 0, 0, session.LastFrame, 0, sourceBox);

        MappedSubresource mapped = session.Context.Map(
            session.StagingTexture,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None
        );

        try
        {
            Rectangle sourceRect = new(0, 0, frameRect.Width, frameRect.Height);
            IntPtr destination =
                bmpData.Scan0
                + (nint)((long)(intersection.Y - rect.Y) * bmpData.Stride + (long)(intersection.X - rect.X) * 3);

            if (frameDesc.Format == Format.R16G16B16A16_Float)
            {
                HdrToneMapper.ConvertScRgbToSdr(
                    mapped.DataPointer,
                    (int)mapped.RowPitch,
                    sourceRect,
                    destination,
                    bmpData.Stride,
                    3,
                    session.SdrWhiteLevelNits,
                    session.MaxLuminanceNits
                );
            }
            else
            {
                CopyBgra(mapped.DataPointer, (int)mapped.RowPitch, sourceRect, destination, bmpData.Stride, 3);
            }
        }
        finally
        {
            session.Context.Unmap(session.StagingTexture, 0);
        }
    }

    private static unsafe void CopyBgra(
        IntPtr source,
        int sourceRowPitch,
        Rectangle sourceRect,
        IntPtr destination,
        int destinationRowPitch,
        int destinationBytesPerPixel
    )
    {
        byte* srcBase = (byte*)source;
        byte* dstBase = (byte*)destination;

        for (int y = 0; y < sourceRect.Height; y++)
        {
            byte* srcRow = srcBase + (long)(sourceRect.Y + y) * sourceRowPitch + (long)sourceRect.X * 4;
            byte* dstRow = dstBase + (long)y * destinationRowPitch;
            for (int x = 0; x < sourceRect.Width; x++)
            {
                // Desktop duplication alpha is undefined; force opaque.
                dstRow[x * destinationBytesPerPixel + 0] = srcRow[x * 4 + 0];
                dstRow[x * destinationBytesPerPixel + 1] = srcRow[x * 4 + 1];
                dstRow[x * destinationBytesPerPixel + 2] = srcRow[x * 4 + 2];
            }
        }
    }

    private static void UpdateLastFrame(OutputSession session)
    {
        // The first frame after starting duplication can take a few vsyncs to arrive,
        // especially on a static desktop, so retry before giving up.
        int attempts = session.HasFrame ? 1 : 10;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (TryAcquireFrame(session) || session.HasFrame)
                return;
        }

        if (!session.HasFrame)
            throw new InvalidOperationException($"Desktop duplication produced no frame for {session.DeviceName}.");
    }

    private static bool TryAcquireFrame(OutputSession session)
    {
        Result result = session.Duplication!.AcquireNextFrame(
            AcquireFrameTimeoutMs,
            out OutduplFrameInfo frameInfo,
            out IDXGIResource desktopResource
        );

        if (result.Success)
        {
            try
            {
                // AcquireNextFrame can succeed before the desktop has ever been presented to
                // the duplication surface (LastPresentTime == 0), in which case the texture
                // contents are undefined — typically black. Only accept surfaces that carry a
                // real desktop image, otherwise the first capture after startup is black.
                if (frameInfo.LastPresentTime == 0 && !session.HasFrame)
                    return false;

                using (ID3D11Texture2D frameTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    Texture2DDescription desc = frameTexture.Description;
                    if (
                        session.LastFrame is null
                        || session.LastFrame.Description.Width != desc.Width
                        || session.LastFrame.Description.Height != desc.Height
                        || session.LastFrame.Description.Format != desc.Format
                    )
                    {
                        session.LastFrame?.Dispose();
                        session.LastFrame = session.Device!.CreateTexture2D(
                            new Texture2DDescription
                            {
                                Width = desc.Width,
                                Height = desc.Height,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = desc.Format,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Default,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.None,
                                MiscFlags = ResourceOptionFlags.None,
                            }
                        );
                    }

                    session.Context!.CopyResource(session.LastFrame, frameTexture);
                    session.HasFrame = true;
                }
            }
            finally
            {
                desktopResource.Dispose();
                session.Duplication.ReleaseFrame();
            }
            return true;
        }

        if (result == WaitTimeout)
        {
            // Desktop has not changed since the last acquired frame; a cached copy stays valid.
            return false;
        }

        if (result == AccessLost)
        {
            // Output is being reconfigured (mode switch, HDR toggle); sessions must be rebuilt.
            Logger.LogDebug("Desktop duplication access lost; sessions will be rebuilt on the next capture.");
            ResetSessions();
        }

        return false;
    }
}

/// <summary>
/// Per-display advanced-color state via DISPLAYCONFIG (source name, HDR enablement, and the
/// user's SDR content brightness). Degrades to <see cref="DefaultSdrWhiteLevelNits"/> when
/// the API is unavailable so tone mapping still has a sane anchor.
/// </summary>
internal static class DisplayColorInfo
{
    private const float DefaultSdrWhiteLevelNits = 200f;
    private const float SdrWhiteLevelStep = 80f / 1000f;

    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int DisplayConfigDeviceInfoGetSourceName = 1;
    private const int DisplayConfigDeviceInfoGetAdvancedColorInfo = 9;
    private const int DisplayConfigDeviceInfoGetSdrWhiteLevel = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfig2DRegion
    {
        public uint Cx;
        public uint Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigVideoSignalInfo
    {
        public ulong PixelRate;
        public DisplayConfigRational HSyncFreq;
        public DisplayConfigRational VSyncFreq;
        public DisplayConfig2DRegion ActiveSize;
        public DisplayConfig2DRegion TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public DisplayConfigVideoSignalInfo TargetVideoSignalInfo;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public ulong TargetModePixelRate;
        public DisplayConfigRational TargetModeHSyncFreq;
        public DisplayConfigRational TargetModeVSyncFreq;
        public DisplayConfig2DRegion TargetModeActiveSize;
        public DisplayConfig2DRegion TargetModeTotalSize;
        public uint TargetModeVideoStandard;
        public uint TargetModeScanLineOrdering;
        public DisplayConfig2DRegion TargetModePosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSdrWhiteLevel
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint SdrWhiteLevel;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements
    );

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId
    );

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSdrWhiteLevel requestPacket);

    internal readonly struct AdvancedColorState
    {
        public AdvancedColorState(bool advancedColorEnabled, float sdrWhiteLevelNits)
        {
            AdvancedColorEnabled = advancedColorEnabled;
            SdrWhiteLevelNits = sdrWhiteLevelNits;
        }

        public bool AdvancedColorEnabled { get; }

        public float SdrWhiteLevelNits { get; }
    }

    /// <summary>
    /// Maps each GDI display device name to its advanced-color state. Returns an empty
    /// dictionary when the DISPLAYCONFIG API is unavailable.
    /// </summary>
    internal static Dictionary<string, AdvancedColorState> GetAdvancedColorStates()
    {
        Dictionary<string, AdvancedColorState> states = new(StringComparer.OrdinalIgnoreCase);

        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0)
            return states;

        DisplayConfigPathInfo[] paths = new DisplayConfigPathInfo[pathCount];
        DisplayConfigModeInfo[] modes = new DisplayConfigModeInfo[modeCount];

        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return states;

        for (int i = 0; i < pathCount; i++)
        {
            DisplayConfigPathInfo path = paths[i];

            DisplayConfigSourceDeviceName sourceName = new();
            sourceName.Header.Type = DisplayConfigDeviceInfoGetSourceName;
            sourceName.Header.Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>();
            sourceName.Header.AdapterId = path.SourceInfo.AdapterId;
            sourceName.Header.Id = path.SourceInfo.Id;

            if (DisplayConfigGetDeviceInfo(ref sourceName) != 0 || string.IsNullOrEmpty(sourceName.ViewGdiDeviceName))
                continue;

            bool advancedColorEnabled = false;

            DisplayConfigGetAdvancedColorInfo colorInfo = new();
            colorInfo.Header.Type = DisplayConfigDeviceInfoGetAdvancedColorInfo;
            colorInfo.Header.Size = (uint)Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>();
            colorInfo.Header.AdapterId = path.TargetInfo.AdapterId;
            colorInfo.Header.Id = path.TargetInfo.Id;

            if (DisplayConfigGetDeviceInfo(ref colorInfo) == 0)
                advancedColorEnabled = (colorInfo.Value & 0x2) != 0;

            float sdrWhiteLevelNits = DefaultSdrWhiteLevelNits;

            DisplayConfigSdrWhiteLevel whiteLevel = new();
            whiteLevel.Header.Type = DisplayConfigDeviceInfoGetSdrWhiteLevel;
            whiteLevel.Header.Size = (uint)Marshal.SizeOf<DisplayConfigSdrWhiteLevel>();
            whiteLevel.Header.AdapterId = path.TargetInfo.AdapterId;
            whiteLevel.Header.Id = path.TargetInfo.Id;

            if (DisplayConfigGetDeviceInfo(ref whiteLevel) == 0 && whiteLevel.SdrWhiteLevel > 0)
                sdrWhiteLevelNits = whiteLevel.SdrWhiteLevel * SdrWhiteLevelStep;

            states[sourceName.ViewGdiDeviceName] = new AdvancedColorState(advancedColorEnabled, sdrWhiteLevelNits);
        }

        return states;
    }
}
