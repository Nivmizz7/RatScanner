using System;
using System.Drawing;
using System.Threading.Tasks;

namespace RatScanner.Display;

/// <summary>
/// Converts FP16 linear scRGB desktop frames (the Windows HDR composition space, where
/// 1.0 == 80 nits) into perceptually accurate 8-bit sRGB output.
///
/// Design goals (mirroring the approach proven in the ShareX HDR ecosystem):
/// - SDR content (UI, text) composed at the display's SDR reference white maps 1:1 and
///   stays untouched, so the scan pipeline sees exactly the SDR look it was built for.
///   This holds even when HDR highlights share the frame: dimming reference white to buy
///   highlight headroom would shift every SDR pixel whenever any HDR pixel is present.
/// - Luminance above SDR reference white saturates at display white: 8-bit SDR output
///   has no range above reference white (white == 255), and the scan pipeline matches
///   against SDR reference templates, so the mapping above white degenerates to exactly
///   that saturation. PQ (ST.2084) helpers remain for the luminance basis conversions.
/// - Hue is preserved by tone mapping luminance and rescaling RGB; out-of-gamut colors
///   are softly desaturated toward the tone-mapped luminance instead of per-channel
///   clipping.
/// - Ordered dithering is applied before 8-bit quantization to avoid banding in smooth
///   gradients.
/// </summary>
internal static unsafe class HdrToneMapper
{
    private const int ToneMapLutSize = 4096;
    private const int EncodeLutSize = 16384;

    // Rec.709 / sRGB luminance weights (scRGB uses sRGB primaries).
    private const float LumR = 0.2126f;
    private const float LumG = 0.7152f;
    private const float LumB = 0.0722f;

    // ST.2084 (PQ) constants.
    private const float PqM1 = 2610f / 16384f;
    private const float PqM2 = 2523f / 4096f * 128f;
    private const float PqC1 = 3424f / 4096f;
    private const float PqC2 = 2413f / 4096f * 32f;
    private const float PqC3 = 2392f / 4096f * 32f;

    private static readonly float[] EncodeLut = BuildEncodeLut();

    // Half bit pattern -> float, with NaN sanitized to 0. Table lookup is faster than a
    // conversion in the per-pixel loop and pixels cluster in a small cache-friendly range.
    private static readonly float[] HalfLut = BuildHalfLut();

    // 8x8 Bayer matrix, normalized to [-0.5, 0.5) in 8-bit quantization steps.
    private static readonly float[] Bayer8 = BuildBayerMatrix();

    /// <summary>Builds the linear-to-8-bit sRGB lookup used by <see cref="EncodeChannel"/>.</summary>
    private static float[] BuildEncodeLut()
    {
        float[] lut = new float[EncodeLutSize];
        for (int i = 0; i < EncodeLutSize; i++)
            lut[i] = SrgbEncode(i / (float)(EncodeLutSize - 1)) * 255f;
        return lut;
    }

    /// <summary>Builds the half-bit-pattern to float table (NaN sanitized to zero).</summary>
    private static float[] BuildHalfLut()
    {
        float[] lut = new float[65536];
        for (int i = 0; i < 65536; i++)
        {
            float value = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            lut[i] = float.IsNaN(value) ? 0f : value;
        }
        return lut;
    }

    /// <summary>Builds the 8x8 ordered-dither matrix in 8-bit quantization steps.</summary>
    private static float[] BuildBayerMatrix()
    {
        int[] bayer =
        {
            0,
            32,
            8,
            40,
            2,
            34,
            10,
            42,
            48,
            16,
            56,
            24,
            50,
            18,
            58,
            26,
            12,
            44,
            4,
            36,
            14,
            46,
            6,
            38,
            60,
            28,
            52,
            20,
            62,
            30,
            54,
            22,
            3,
            35,
            11,
            43,
            1,
            33,
            9,
            41,
            51,
            19,
            59,
            27,
            49,
            17,
            57,
            25,
            15,
            47,
            7,
            39,
            13,
            45,
            5,
            37,
            63,
            31,
            55,
            23,
            61,
            29,
            53,
            21,
        };

        float[] matrix = new float[64];
        for (int i = 0; i < 64; i++)
            matrix[i] = (bayer[i] + 0.5f) / 64f - 0.5f;
        return matrix;
    }

    /// <summary>Encodes linear light to sRGB (IEC 61966-2-1) in [0, 1].</summary>
    internal static float SrgbEncode(float linear)
    {
        if (linear <= 0.0031308f)
            return 12.92f * linear;
        return 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Encodes absolute luminance (nits) to PQ / ST.2084 in [0, 1].</summary>
    internal static float PqEncode(float nits)
    {
        float y = MathF.Max(nits, 0f) / 10000f;
        float ym = MathF.Pow(y, PqM1);
        return MathF.Pow((PqC1 + PqC2 * ym) / (1f + PqC3 * ym), PqM2);
    }

    /// <summary>Decodes a PQ / ST.2084 value in [0, 1] back to absolute nits.</summary>
    internal static float PqDecode(float pq)
    {
        float e = MathF.Pow(MathF.Max(pq, 0f), 1f / PqM2);
        float num = MathF.Max(e - PqC1, 0f);
        float den = PqC2 - PqC3 * e;
        return 10000f * MathF.Pow(num / den, 1f / PqM1);
    }

    /// <summary>
    /// Maps a normalized scene luminance (1.0 == SDR reference white) to the normalized
    /// display luminance in [0, 1] for the 8-bit SDR output.
    ///
    /// Normalized 1.0 is an identity anchor: SDR content (UI, text, icons) at or below
    /// reference white maps 1:1 even when HDR highlights share the frame. The scan
    /// pipeline matches captured pixels against SDR reference templates, so the SDR range
    /// must stay stable frame-to-frame and identical to the pure-SDR fast path — dimming
    /// reference white to buy highlight headroom (what a BT.2390 EETF with an SDR target
    /// peak does: <c>ToneMapLuminance(1, 203, 1000) ≈ 0.78</c>) would shift every SDR
    /// pixel whenever any HDR pixel is present.
    ///
    /// Luminance above reference white saturates at display white: 8-bit SDR output has
    /// no range above reference white (white == 255), so the roll-off segment above white
    /// degenerates to exactly that saturation. The display anchors (<c>sdrWhiteNits</c>,
    /// <c>maxContentNits</c>) still drive the caller: <see cref="BuildToneMapLut"/> uses
    /// them for the LUT input domain and <see cref="ConvertScRgbToSdr"/> for the
    /// fast-path decision; the curve itself is anchor-independent.
    /// Hue is preserved by the caller, which rescales RGB by the mapped luminance instead
    /// of clipping channels.
    /// </summary>
    internal static float ToneMapLuminance(float normalizedLuminance)
    {
        if (float.IsNaN(normalizedLuminance))
            return 0f;

        // Identity below reference white; saturate at display white above it.
        return Math.Clamp(normalizedLuminance, 0f, 1f);
    }

    /// <summary>Precomputes the luminance tone curve for one (white, peak) parameter pair.</summary>
    private static float[] BuildToneMapLut(float sdrWhiteNits, float maxContentNits)
    {
        float[] lut = new float[ToneMapLutSize];
        float maxInputNorm = MathF.Max(maxContentNits / sdrWhiteNits, 1f);
        for (int i = 0; i < ToneMapLutSize; i++)
        {
            float t = i / (float)(ToneMapLutSize - 1);
            lut[i] = ToneMapLuminance(t * t * maxInputNorm);
        }
        return lut;
    }

    /// <summary>
    /// Tone maps an FP16 linear scRGB frame region into 8-bit BGR output.
    /// </summary>
    /// <param name="source">Pointer to the top-left of the FP16 RGBA source frame.</param>
    /// <param name="sourceRowPitch">Source row pitch in bytes.</param>
    /// <param name="sourceRect">Region of the source frame to convert (source pixel coordinates).</param>
    /// <param name="destination">Pointer to the top-left of the destination BGR buffer region.</param>
    /// <param name="destinationRowPitch">Destination row pitch in bytes.</param>
    /// <param name="destinationBytesPerPixel">Destination bytes per pixel: 3 (BGR) or 4 (BGRA).</param>
    /// <param name="sdrWhiteLevelNits">SDR reference white of the source display in nits.</param>
    /// <param name="displayMaxNits">Reported peak luminance of the source display in nits.</param>
    internal static void ConvertScRgbToSdr(
        IntPtr source,
        int sourceRowPitch,
        Rectangle sourceRect,
        IntPtr destination,
        int destinationRowPitch,
        int destinationBytesPerPixel,
        float sdrWhiteLevelNits,
        float displayMaxNits
    )
    {
        if (sdrWhiteLevelNits < 80f)
            sdrWhiteLevelNits = 80f;

        float maxContentNits = EstimateMaxContentLuminance(source, sourceRowPitch, sourceRect, displayMaxNits);

        // Pure SDR content: nothing exceeds reference white, so tone mapping and gamut
        // mapping are identity operations and the analysis pipeline can be skipped.
        if (maxContentNits <= sdrWhiteLevelNits * 1.001f)
        {
            ConvertSdrFastPath(
                source,
                sourceRowPitch,
                sourceRect,
                destination,
                destinationRowPitch,
                destinationBytesPerPixel,
                sdrWhiteLevelNits
            );
            return;
        }

        float[] toneMapLut = BuildToneMapLut(sdrWhiteLevelNits, maxContentNits);
        float scRgbToRef = 80f / sdrWhiteLevelNits;
        float maxInputNorm = MathF.Max(maxContentNits / sdrWhiteLevelNits, 1f);
        float lutScale = (ToneMapLutSize - 1) / MathF.Sqrt(maxInputNorm);

        byte* srcBase = (byte*)source;
        byte* dstBase = (byte*)destination;

        Parallel.For(
            0,
            sourceRect.Height,
            y =>
            {
                ushort* srcRow = (ushort*)(srcBase + (long)(sourceRect.Y + y) * sourceRowPitch) + sourceRect.X * 4;
                byte* dstRow = dstBase + (long)y * destinationRowPitch;
                int bayerRow = (y & 7) << 3;

                for (int x = 0; x < sourceRect.Width; x++)
                {
                    float r = HalfLut[srcRow[0]] * scRgbToRef;
                    float g = HalfLut[srcRow[1]] * scRgbToRef;
                    float b = HalfLut[srcRow[2]] * scRgbToRef;

                    if (r < 0f)
                        r = 0f;
                    if (g < 0f)
                        g = 0f;
                    if (b < 0f)
                        b = 0f;

                    float lum = LumR * r + LumG * g + LumB * b;

                    if (lum > 1e-6f)
                    {
                        float lutPos = MathF.Sqrt(MathF.Min(lum, maxInputNorm)) * lutScale;
                        int lutIndex = (int)lutPos;
                        float frac = lutPos - lutIndex;
                        int lutNext = Math.Min(lutIndex + 1, ToneMapLutSize - 1);
                        float mappedLum = toneMapLut[lutIndex] * (1f - frac) + toneMapLut[lutNext] * frac;

                        float scale = mappedLum / lum;
                        r *= scale;
                        g *= scale;
                        b *= scale;

                        // Soft gamut mapping: desaturate toward the tone-mapped luminance
                        // until the color fits, instead of clipping channels independently.
                        float maxChannel = MathF.Max(r, MathF.Max(g, b));
                        if (maxChannel > 1f)
                        {
                            float t = (maxChannel - 1f) / MathF.Max(maxChannel - mappedLum, 1e-6f);
                            if (t > 1f)
                                t = 1f;
                            r += (mappedLum - r) * t;
                            g += (mappedLum - g) * t;
                            b += (mappedLum - b) * t;
                            if (r > 1f)
                                r = 1f;
                            if (g > 1f)
                                g = 1f;
                            if (b > 1f)
                                b = 1f;
                        }
                    }

                    float dither = Bayer8[bayerRow + (x & 7)];
                    WritePixel(dstRow, r, g, b, dither, destinationBytesPerPixel);
                    srcRow += 4;
                    dstRow += destinationBytesPerPixel;
                }
            }
        );
    }

    /// <summary>
    /// Identity conversion for frames whose content never exceeds SDR reference white:
    /// scale scRGB to the reference-white basis, clamp, and sRGB-encode with dithering.
    /// </summary>
    private static void ConvertSdrFastPath(
        IntPtr source,
        int sourceRowPitch,
        Rectangle sourceRect,
        IntPtr destination,
        int destinationRowPitch,
        int destinationBytesPerPixel,
        float sdrWhiteLevelNits
    )
    {
        float scRgbToRef = 80f / sdrWhiteLevelNits;
        byte* srcBase = (byte*)source;
        byte* dstBase = (byte*)destination;

        Parallel.For(
            0,
            sourceRect.Height,
            y =>
            {
                ushort* srcRow = (ushort*)(srcBase + (long)(sourceRect.Y + y) * sourceRowPitch) + sourceRect.X * 4;
                byte* dstRow = dstBase + (long)y * destinationRowPitch;
                int bayerRow = (y & 7) << 3;

                for (int x = 0; x < sourceRect.Width; x++)
                {
                    float r = HalfLut[srcRow[0]] * scRgbToRef;
                    float g = HalfLut[srcRow[1]] * scRgbToRef;
                    float b = HalfLut[srcRow[2]] * scRgbToRef;

                    if (r < 0f)
                        r = 0f;
                    else if (r > 1f)
                        r = 1f;
                    if (g < 0f)
                        g = 0f;
                    else if (g > 1f)
                        g = 1f;
                    if (b < 0f)
                        b = 0f;
                    else if (b > 1f)
                        b = 1f;

                    float dither = Bayer8[bayerRow + (x & 7)];
                    WritePixel(dstRow, r, g, b, dither, destinationBytesPerPixel);
                    srcRow += 4;
                    dstRow += destinationBytesPerPixel;
                }
            }
        );
    }

    /// <summary>Writes one pixel as BGR (or BGRA), sRGB-encoding and dithering each channel.</summary>
    private static void WritePixel(byte* dstRow, float r, float g, float b, float dither, int bytesPerPixel)
    {
        dstRow[0] = EncodeChannel(b, dither);
        dstRow[1] = EncodeChannel(g, dither);
        dstRow[2] = EncodeChannel(r, dither);
        if (bytesPerPixel >= 4)
            dstRow[3] = 255;
    }

    /// <summary>sRGB-encodes one channel via the LUT, applies dither, and quantizes to 8 bits.</summary>
    private static byte EncodeChannel(float linear, float dither)
    {
        // Interpolate the continuous sRGB value, dither, then quantize; dithering after
        // quantization would be a no-op.
        float pos = linear * (EncodeLutSize - 1);
        int index = (int)pos;
        float frac = pos - index;
        int next = Math.Min(index + 1, EncodeLutSize - 1);
        float encoded = EncodeLut[index] * (1f - frac) + EncodeLut[next] * frac + dither;

        if (encoded <= 0f)
            return 0;
        if (encoded >= 255f)
            return 255;
        return (byte)(encoded + 0.5f);
    }

    /// <summary>
    /// Estimates the peak content luminance of the frame region (99.99th percentile) so
    /// the tone curve only compresses as much as the content actually requires.
    /// </summary>
    private static float EstimateMaxContentLuminance(
        IntPtr source,
        int sourceRowPitch,
        Rectangle sourceRect,
        float displayMaxNits
    )
    {
        const int HistogramSize = 1024;
        const float MaxTrackedNits = 10000f;

        byte* srcBase = (byte*)source;
        int stepY = Math.Max(sourceRect.Height / 512, 1);
        int stepX = Math.Max(sourceRect.Width / 512, 1);

        int[] histogram = new int[HistogramSize];
        long samples = 0;

        // Bins are distributed on sqrt(luminance); any monotonic transform yields the
        // same percentile, and this avoids two pow() calls per sample.
        float binScale = (HistogramSize - 1) / MathF.Sqrt(MaxTrackedNits);

        for (int y = 0; y < sourceRect.Height; y += stepY)
        {
            ushort* srcRow = (ushort*)(srcBase + (long)(sourceRect.Y + y) * sourceRowPitch) + sourceRect.X * 4;
            for (int x = 0; x < sourceRect.Width; x += stepX)
            {
                ushort* px = srcRow + (long)x * 4;
                float r = HalfLut[px[0]];
                float g = HalfLut[px[1]];
                float b = HalfLut[px[2]];
                float nits = 80f * (LumR * MathF.Max(r, 0f) + LumG * MathF.Max(g, 0f) + LumB * MathF.Max(b, 0f));
                int bin = (int)(MathF.Sqrt(MathF.Min(nits, MaxTrackedNits)) * binScale);
                histogram[bin]++;
                samples++;
            }
        }

        if (samples == 0)
            return displayMaxNits > 0f ? displayMaxNits : 1000f;

        // At least one sample: with threshold == 0 even a fully SDR histogram trips the
        // (count >= threshold) break on the topmost (empty) bin, reporting 10,000 nits and
        // needlessly compressing small captures instead of taking the identity fast path.
        long threshold = Math.Max(1, (long)(samples * 0.0001f));
        long count = 0;
        int peakBin = 0;
        for (int i = HistogramSize - 1; i >= 0; i--)
        {
            count += histogram[i];
            if (count >= threshold)
            {
                peakBin = i;
                break;
            }
        }

        float sqrtPeak = peakBin / binScale;
        return MathF.Min(sqrtPeak * sqrtPeak, displayMaxNits > 0f ? displayMaxNits : 1000f);
    }
}
