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
/// - HDR highlights above reference white are rolled off with the BT.2390-4 EETF
///   evaluated in the PQ (ST.2084) domain, so highlight detail compresses smoothly
///   instead of clipping to white.
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

    private static float[] BuildEncodeLut()
    {
        float[] lut = new float[EncodeLutSize];
        for (int i = 0; i < EncodeLutSize; i++)
            lut[i] = SrgbEncode(i / (float)(EncodeLutSize - 1)) * 255f;
        return lut;
    }

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

    internal static float SrgbEncode(float linear)
    {
        if (linear <= 0.0031308f)
            return 12.92f * linear;
        return 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    internal static float PqEncode(float nits)
    {
        float y = MathF.Max(nits, 0f) / 10000f;
        float ym = MathF.Pow(y, PqM1);
        return MathF.Pow((PqC1 + PqC2 * ym) / (1f + PqC3 * ym), PqM2);
    }

    internal static float PqDecode(float pq)
    {
        float e = MathF.Pow(MathF.Max(pq, 0f), 1f / PqM2);
        float num = MathF.Max(e - PqC1, 0f);
        float den = PqC2 - PqC3 * e;
        return 10000f * MathF.Pow(num / den, 1f / PqM1);
    }

    /// <summary>
    /// Maps a normalized scene luminance (1.0 == SDR reference white) to a tone-mapped
    /// normalized display luminance in [0, 1], using the BT.2390-4 EETF hermite spline
    /// roll-off evaluated in the PQ domain.
    /// </summary>
    internal static float ToneMapLuminance(float normalizedLuminance, float sdrWhiteNits, float maxContentNits)
    {
        float maxInputNorm = MathF.Max(maxContentNits / sdrWhiteNits, 1f);
        if (normalizedLuminance >= maxInputNorm)
            normalizedLuminance = maxInputNorm;

        float pqSourceMax = PqEncode(maxContentNits);
        float pqTargetMax = PqEncode(sdrWhiteNits);

        if (pqSourceMax <= pqTargetMax + 1e-6f)
            return MathF.Min(normalizedLuminance, 1f);

        float maxLumNorm = pqTargetMax / pqSourceMax;
        float ks = 1.5f * maxLumNorm - 0.5f;

        float nits = normalizedLuminance * sdrWhiteNits;
        float e1 = PqEncode(nits) / pqSourceMax;

        float e2;
        if (e1 < ks)
        {
            e2 = e1;
        }
        else
        {
            float t = (e1 - ks) / (1f - ks);
            float t2 = t * t;
            float t3 = t2 * t;
            e2 = (2f * t3 - 3f * t2 + 1f) * ks + (t3 - 2f * t2 + t) * (1f - ks) + (-2f * t3 + 3f * t2) * maxLumNorm;
        }

        float mappedNits = PqDecode(e2 * pqSourceMax);
        return MathF.Min(mappedNits / sdrWhiteNits, 1f);
    }

    private static float[] BuildToneMapLut(float sdrWhiteNits, float maxContentNits)
    {
        float[] lut = new float[ToneMapLutSize];
        float maxInputNorm = MathF.Max(maxContentNits / sdrWhiteNits, 1f);
        for (int i = 0; i < ToneMapLutSize; i++)
        {
            float t = i / (float)(ToneMapLutSize - 1);
            lut[i] = ToneMapLuminance(t * t * maxInputNorm, sdrWhiteNits, maxContentNits);
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

    private static void WritePixel(byte* dstRow, float r, float g, float b, float dither, int bytesPerPixel)
    {
        dstRow[0] = EncodeChannel(b, dither);
        dstRow[1] = EncodeChannel(g, dither);
        dstRow[2] = EncodeChannel(r, dither);
        if (bytesPerPixel >= 4)
            dstRow[3] = 255;
    }

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
