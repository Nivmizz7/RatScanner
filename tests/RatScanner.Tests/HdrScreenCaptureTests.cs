#nullable enable

using System.Collections.Generic;
using System.Drawing;
using RatScanner.Display;
using Vortice.DXGI;
using Xunit;

namespace RatScanner.Tests;

/// <summary>
/// Regression tests for the HDR capture gating surface of <see cref="HdrScreenCapture"/>:
/// per-display HDR detection and all-or-nothing output coverage. These are pure decision
/// functions — no GPU or display access is needed. Tone-mapping math lives in
/// <see cref="HdrToneMapperTests"/>; live duplication interop requires real hardware and
/// is covered by manual smoke testing.
/// </summary>
public class HdrScreenCaptureTests
{
    /// <summary>Mixed HDR/SDR rigs must route each capture region by the display it overlaps.</summary>
    [Fact]
    public void Hdr_detection_is_evaluated_per_display()
    {
        // Mixed HDR/SDR rig: the HDR-required answer must depend on which display the
        // capture region overlaps, not on a single global flag.
        List<(Rectangle Bounds, bool IsHdr, float SdrWhiteNits)> outputs =
        [
            (new Rectangle(0, 0, 1920, 1080), true, 203f), // left: HDR
            (new Rectangle(1920, 0, 1920, 1080), false, 203f), // right: SDR
        ];

        Assert.True(HdrScreenCapture.AnyHdrDisplayIntersects(outputs, new Rectangle(100, 100, 400, 300)));
        Assert.False(HdrScreenCapture.AnyHdrDisplayIntersects(outputs, new Rectangle(2000, 100, 400, 300)));
        // Region spanning both displays still requires the HDR path.
        Assert.True(HdrScreenCapture.AnyHdrDisplayIntersects(outputs, new Rectangle(1800, 100, 400, 300)));
    }

    /// <summary>Every HDR10/HLG signal color space must be detected as HDR; SDR spaces must not.</summary>
    [Fact]
    public void Hdr_color_space_detection_covers_hdr10_and_hlg()
    {
        Assert.True(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.RgbFullG2084NoneP2020));
        Assert.True(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.RgbStudioG2084NoneP2020));
        Assert.True(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.YcbcrStudioG2084LeftP2020));
        Assert.True(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.YcbcrStudioG2084TopLeftP2020));
        Assert.True(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.YcbcrStudioGhlgTopLeftP2020));
        Assert.True(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.YcbcrFullGhlgTopLeftP2020));

        Assert.False(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.RgbFullG22NoneP709));
        Assert.False(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.RgbFullG10NoneP709));
        Assert.False(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.YcbcrStudioG22LeftP2020));
        Assert.False(HdrScreenCapture.IsHdrColorSpace(ColorSpaceType.Custom));
    }

    /// <summary>A single session fully containing the region is sufficient coverage.</summary>
    [Fact]
    public void Coverage_is_true_when_a_single_output_contains_the_region()
    {
        List<Rectangle> outputs = [new Rectangle(0, 0, 1920, 1080)];
        Assert.True(HdrScreenCapture.CoversRectangle(outputs, new Rectangle(10, 10, 400, 300)));
    }

    /// <summary>A region straddling two adjacent displays is covered when both have sessions.</summary>
    [Fact]
    public void Coverage_is_true_when_adjacent_outputs_tile_the_region()
    {
        // Region straddling two side-by-side displays: both halves are covered.
        List<Rectangle> outputs = [new Rectangle(0, 0, 1920, 1080), new Rectangle(1920, 0, 1920, 1080)];
        Assert.True(HdrScreenCapture.CoversRectangle(outputs, new Rectangle(1800, 100, 240, 200)));
    }

    /// <summary>A missing middle display must fail coverage so the GDI fallback runs.</summary>
    [Fact]
    public void Coverage_is_false_when_an_output_is_missing()
    {
        // The middle display is rotated (or its duplication setup failed) and has no
        // session: a region spanning it must not return a partially black bitmap.
        List<Rectangle> outputs = [new Rectangle(0, 0, 1920, 1080), new Rectangle(3840, 0, 1920, 1080)];
        Assert.False(HdrScreenCapture.CoversRectangle(outputs, new Rectangle(1800, 100, 2200, 200)));
    }

    [Fact]
    public void Coverage_is_false_when_no_outputs_intersect()
    {
        List<Rectangle> outputs = [new Rectangle(0, 0, 1920, 1080)];
        Assert.False(HdrScreenCapture.CoversRectangle(outputs, new Rectangle(5000, 100, 240, 200)));
        Assert.False(HdrScreenCapture.CoversRectangle([], new Rectangle(0, 0, 10, 10)));
    }

    /// <summary>
    /// QueryDisplayConfig writes native-sized records into the managed arrays. The
    /// original PR shipped 104/72-byte strides (native: 72/64), which shifted
    /// TargetInfo.AdapterId/Id to the wrong offsets and made every
    /// DisplayConfigGetDeviceInfo follow-up fail with ERROR_INVALID_PARAMETER (87) —
    /// silently disabling HDR detection and pinning SDR white at the hard-coded default.
    /// Lock the native x64 record sizes so any future field drift fails here first.
    /// </summary>
    [Fact]
    public void DisplayConfig_interop_structs_match_native_wingdi_h_layouts()
    {
        (int pathInfo, int modeInfo, int pathTargetInfo, int pathSourceInfo) =
            RatScanner.Display.DisplayColorInfo.GetManagedRecordSizes();

        Assert.Equal(RatScanner.Display.DisplayColorInfo.NativeDisplayConfigPathInfoSize, pathInfo);
        Assert.Equal(RatScanner.Display.DisplayColorInfo.NativeDisplayConfigModeInfoSize, modeInfo);
        Assert.Equal(48, pathTargetInfo);
        Assert.Equal(20, pathSourceInfo);
    }
}
