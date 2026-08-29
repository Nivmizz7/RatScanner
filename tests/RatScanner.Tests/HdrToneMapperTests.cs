#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using RatScanner.Display;
using Vortice.DXGI;
using Xunit;

namespace RatScanner.Tests;

/// <summary>
/// Regression tests for the HDR capture tone mapping and detection gating.
/// The tone mapper is pure math (ST.2084 PQ, BT.2390-4 EETF, sRGB encoding) and must
/// be deterministic and hermetic — no GPU or display access is needed.
/// </summary>
public class HdrToneMapperTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.0031308f, 12.92f * 0.0031308f)]
    [InlineData(1f, 1f)]
    [InlineData(0.5f, 0.7354f)] // sRGB(0.5) = 1.055 * 0.5^(1/2.4) - 0.055
    public void SrgbEncode_matches_standard(float linear, float expected)
    {
        Assert.Equal(expected, HdrToneMapper.SrgbEncode(linear), 4);
    }

    [Theory]
    [InlineData(80f)]
    [InlineData(100f)]
    [InlineData(203f)]
    [InlineData(500f)]
    [InlineData(1000f)]
    [InlineData(10000f)]
    public void Pq_round_trips_nits(float nits)
    {
        Assert.Equal(nits, HdrToneMapper.PqDecode(HdrToneMapper.PqEncode(nits)), 1);
    }

    [Fact]
    public void Tone_map_is_monotonic_and_bounded()
    {
        const float sdrWhite = 203f;
        const float maxContent = 1000f;
        float previous = -1f;
        for (int i = 0; i <= 200; i++)
        {
            float norm = i / 100f;
            float mapped = HdrToneMapper.ToneMapLuminance(norm, sdrWhite, maxContent);
            Assert.InRange(mapped, 0f, 1.0001f);
            Assert.True(mapped >= previous, $"Tone map must be monotonic at norm={norm}");
            previous = mapped;
        }
    }

    [Fact]
    public void Tone_map_preserves_low_luminance_exactly()
    {
        // The knee starts at ks = 1.5 * maxLumNorm - 0.5 (PQ domain); for 203/1000 nits
        // the identity region covers roughly the bottom 40% of the SDR range.
        const float sdrWhite = 203f;
        const float maxContent = 1000f;
        for (int i = 0; i <= 40; i++)
        {
            float norm = i / 100f;
            float mapped = HdrToneMapper.ToneMapLuminance(norm, sdrWhite, maxContent);
            Assert.True(
                Math.Abs(mapped - norm) < 0.01f,
                $"Identity expected below the knee at norm={norm}, got {mapped}"
            );
        }
    }

    [Fact]
    public void Tone_map_compresses_highlights_toward_white()
    {
        const float sdrWhite = 203f;
        const float maxContent = 1000f;
        float peak = HdrToneMapper.ToneMapLuminance(maxContent / sdrWhite, sdrWhite, maxContent);
        Assert.InRange(peak, 0.95f, 1.0001f);

        // SDR white compresses softly (the BT.2390 roll-off leaves headroom for highlights)
        // but must stay perceptually bright — dimming more than ~30% would break the SDR look.
        float sdrWhiteMapped = HdrToneMapper.ToneMapLuminance(1f, sdrWhite, maxContent);
        Assert.InRange(sdrWhiteMapped, 0.7f, 0.95f);

        // More input luminance must never map to less output.
        Assert.True(sdrWhiteMapped < peak);
    }

    [Fact]
    public void Tone_map_is_identity_when_content_fits_sdr()
    {
        const float sdrWhite = 203f;
        for (int i = 0; i <= 100; i++)
        {
            float norm = i / 100f;
            Assert.Equal(norm, HdrToneMapper.ToneMapLuminance(norm, sdrWhite, 150f), 4);
        }
    }

    [Fact]
    public void ConvertScRgbToSdr_fast_path_maps_sdr_content_to_srgb()
    {
        // A 4x1 FP16 scRGB buffer holding pure SDR content (all pixels at 100 nits) must
        // take the fast path and produce an even sRGB value: 100 nits -> norm 100/203
        // -> sRGB ~ 0.46 (117/255).
        const float sdrWhite = 203f;
        ushort[] pixels = new ushort[4 * 4];
        for (int i = 0; i < 4; i++)
        {
            ushort half = BitConverter.HalfToUInt16Bits((Half)(100f / 80f));
            pixels[i * 4 + 0] = half;
            pixels[i * 4 + 1] = half;
            pixels[i * 4 + 2] = half;
            pixels[i * 4 + 3] = BitConverter.HalfToUInt16Bits((Half)1f);
        }

        byte[] output = new byte[4 * 3];
        GCHandle src = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        GCHandle dst = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            HdrToneMapper.ConvertScRgbToSdr(
                src.AddrOfPinnedObject(),
                4 * 8,
                new Rectangle(0, 0, 4, 1),
                dst.AddrOfPinnedObject(),
                4 * 3,
                3,
                sdrWhite,
                1000f
            );
        }
        finally
        {
            src.Free();
            dst.Free();
        }

        // Uniform content across the row: ordered dithering varies pixels by at most one
        // quantization step, so channels must agree to within 2 and sit at the expected
        // sRGB value for 100 nits (~185-190).
        for (int i = 0; i < 4; i++)
        {
            byte b = output[i * 3 + 0];
            byte g = output[i * 3 + 1];
            byte r = output[i * 3 + 2];
            Assert.InRange(b, 180, 195);
            Assert.Equal(b, g, tolerance: (byte)1);
            Assert.Equal(g, r, tolerance: (byte)1);
        }
        Assert.InRange(Math.Abs(output[0] - output[3]), 0, 2);
    }

    [Fact]
    public void ConvertScRgbToSdr_small_sdr_region_uses_identity_fast_path()
    {
        // Regression: the max-content percentile guard must require at least one sample.
        // With a threshold of 0 every sub-10k-sample region estimated 10,000 nits, forcing
        // the tone-map path and compressing plain SDR content (185/255 here) instead of the
        // identity fast path (186/255).
        const float sdrWhite = 203f;
        ushort[] pixels = new ushort[4 * 4];
        for (int i = 0; i < 4; i++)
        {
            ushort half = BitConverter.HalfToUInt16Bits((Half)(100f / 80f));
            pixels[i * 4 + 0] = half;
            pixels[i * 4 + 1] = half;
            pixels[i * 4 + 2] = half;
            pixels[i * 4 + 3] = BitConverter.HalfToUInt16Bits((Half)1f);
        }

        byte[] output = new byte[4 * 3];
        GCHandle src = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        GCHandle dst = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            HdrToneMapper.ConvertScRgbToSdr(
                src.AddrOfPinnedObject(),
                4 * 8,
                new Rectangle(0, 0, 4, 1),
                dst.AddrOfPinnedObject(),
                4 * 3,
                3,
                sdrWhite,
                1000f
            );
        }
        finally
        {
            src.Free();
            dst.Free();
        }

        // Identity: 100 nits -> 100/203 linear -> sRGB 0.7306 -> 186.3 (+- 1 dither step).
        // The tone-map path would produce 185 or lower at the first pixel.
        Assert.True(
            output[0] is >= 186,
            $"Identity fast path expected (186), got B={output[0]} — region was tone-mapped."
        );
        Assert.Equal(output[0], output[1], tolerance: (byte)1);
        Assert.Equal(output[1], output[2], tolerance: (byte)1);
    }

    [Fact]
    public void ConvertScRgbToSdr_tone_map_path_bounds_output()
    {
        // Buffer with a bright HDR pixel (500 nits) forcing the tone-map path; every
        // output channel must be in [0, 255].
        const float sdrWhite = 203f;
        ushort[] pixels = new ushort[4 * 4];
        for (int i = 0; i < 4; i++)
        {
            float nits = i == 0 ? 500f : 100f;
            ushort half = BitConverter.HalfToUInt16Bits((Half)(nits / 80f));
            pixels[i * 4 + 0] = half;
            pixels[i * 4 + 1] = half;
            pixels[i * 4 + 2] = half;
            pixels[i * 4 + 3] = BitConverter.HalfToUInt16Bits((Half)1f);
        }

        byte[] output = new byte[4 * 3];
        GCHandle src = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        GCHandle dst = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            HdrToneMapper.ConvertScRgbToSdr(
                src.AddrOfPinnedObject(),
                4 * 8,
                new Rectangle(0, 0, 4, 1),
                dst.AddrOfPinnedObject(),
                4 * 3,
                3,
                sdrWhite,
                1000f
            );
        }
        finally
        {
            src.Free();
            dst.Free();
        }

        // Bright pixel must stay bright (not dimmed below the SDR pixel).
        Assert.True(output[0] >= output[3], "HDR highlight must remain brighter than SDR content.");
        foreach (byte channel in output)
            Assert.InRange(channel, 0, 255);
    }

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
}
