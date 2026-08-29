#nullable enable

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using RatScanner.Display;
using Xunit;

namespace RatScanner.Tests;

/// <summary>
/// Regression tests for the HDR capture tone mapping and detection gating.
/// The tone mapper is pure math (ST.2084 PQ, identity-anchored tone curve, sRGB encoding) and
/// must be deterministic and hermetic — no GPU or display access is needed.
/// </summary>
public class HdrToneMapperTests
{
    /// <summary>Verifies the sRGB transfer function against hand-computed reference values.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.0031308f, 12.92f * 0.0031308f)]
    [InlineData(1f, 1f)]
    [InlineData(0.5f, 0.7354f)] // sRGB(0.5) = 1.055 * 0.5^(1/2.4) - 0.055
    public void SrgbEncode_matches_standard(float linear, float expected)
    {
        Assert.Equal(expected, HdrToneMapper.SrgbEncode(linear), 4);
    }

    /// <summary>Verifies PQ encode/decode round-trips absolute luminance exactly.</summary>
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

    /// <summary>The tone curve must never invert or exceed the display range.</summary>
    [Fact]
    public void Tone_map_is_monotonic_and_bounded()
    {
        float previous = -1f;
        for (int i = 0; i <= 400; i++)
        {
            float norm = i / 100f;
            float mapped = HdrToneMapper.ToneMapLuminance(norm);
            Assert.InRange(mapped, 0f, 1.0001f);
            Assert.True(mapped >= previous, $"Tone map must be monotonic at norm={norm}");
            previous = mapped;
        }
    }

    /// <summary>
    /// The SDR range is an identity anchor: everything at or below reference white
    /// (normalized 1.0) maps 1:1 even when HDR highlights share the frame, so SDR
    /// pixels are never re-graded and the captured SDR look matches the pure-SDR fast
    /// path exactly.
    /// </summary>
    [Fact]
    public void Tone_map_preserves_sdr_range_exactly()
    {
        for (int i = 0; i <= 100; i++)
        {
            float norm = i / 100f;
            float mapped = HdrToneMapper.ToneMapLuminance(norm);
            Assert.True(
                Math.Abs(mapped - norm) < 0.0001f,
                $"Identity expected at or below reference white, norm={norm}, got {mapped}"
            );
        }
    }

    /// <summary>
    /// Luminance above SDR reference white saturates at display white (1.0). 8-bit SDR
    /// output has no range above reference white, so any HDR highlight must land at or
    /// below display white — never above it, and never dimmed below reference white.
    /// </summary>
    [Fact]
    public void Tone_map_saturates_hdr_highlights_at_display_white()
    {
        foreach (
            float norm in new[]
            {
                1.01f,
                1.5f,
                2.5f,
                4.93f, /* 1000/203 */
            }
        )
        {
            float mapped = HdrToneMapper.ToneMapLuminance(norm);
            Assert.True(
                MathF.Abs(mapped - 1f) < 0.0001f,
                $"Luminance above reference white must saturate at display white, norm={norm}, got {mapped}"
            );
        }

        // SDR reference white itself maps to itself (display white), the same destination
        // as the saturated peak — the mapping is continuous and monotone across 1.0.
        float sdrWhiteMapped = HdrToneMapper.ToneMapLuminance(1f);
        Assert.True(MathF.Abs(sdrWhiteMapped - 1f) < 0.0001f, $"SDR white must map to 1.0, got {sdrWhiteMapped}");
    }

    /// <summary>Pure-SDR FP16 content must take the identity fast path and land at the expected sRGB value.</summary>
    [Fact]
    public void ConvertScRgbToSdr_fast_path_maps_sdr_content_to_srgb()
    {
        // A 4x1 FP16 scRGB buffer holding pure SDR content (all pixels at 100 nits) must
        // take the fast path and produce an even sRGB value: 100 nits -> linear 100/203
        // = 0.4926 -> sRGB 0.729 (~186/255).
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

    /// <summary>Regression: the max-content percentile guard must not force tone mapping on tiny SDR regions.</summary>
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

    /// <summary>With HDR content present, output channels stay in [0,255] and highlights stay bright.</summary>
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
}
