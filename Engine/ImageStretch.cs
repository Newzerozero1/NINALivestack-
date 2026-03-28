using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Utility;

namespace NinaLiveStack.Engine {

    public static class ImageStretch {

        /// <summary>
        /// The Midtone Transfer Function (PixInsight STF).
        /// </summary>
        public static float MTF(float x, float midtone) {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            if (Math.Abs(midtone - 0.5f) < 0.0001f) return x;
            return (midtone - 1f) * x / ((2f * midtone - 1f) * x - midtone);
        }

        /// <summary>
        /// Compute per-channel background medians and return scale factors
        /// that equalize them (background neutralization / auto white balance).
        /// Scales each channel so its median matches the brightest channel's median.
        /// </summary>
        public static WhiteBalance ComputeWhiteBalance(float[] r, float[] g, float[] b) {
            int sampleSize = 50000;
            var rng = new Random(42);
            int len = r.Length;
            int sc = Math.Min(sampleSize, len);

            // Sample random pixels and compute luminance
            float[] rSamp = new float[sc], gSamp = new float[sc], bSamp = new float[sc];
            float[] lumSamp = new float[sc];
            for (int i = 0; i < sc; i++) {
                int idx = len <= sampleSize ? i : rng.Next(len);
                rSamp[i] = r[idx]; gSamp[i] = g[idx]; bSamp[i] = b[idx];
                lumSamp[i] = 0.2126f * r[idx] + 0.7152f * g[idx] + 0.0722f * b[idx];
            }

            // Find the 30th percentile of luminance — only use background pixels for WB
            float[] lumSorted = (float[])lumSamp.Clone();
            Array.Sort(lumSorted);
            float lumCutoff = lumSorted[(int)(sc * 0.30f)];

            // Collect only background pixel values
            float[] bgR = new float[sc], bgG = new float[sc], bgB = new float[sc];
            int bgCount = 0;
            for (int i = 0; i < sc; i++) {
                if (lumSamp[i] <= lumCutoff) {
                    bgR[bgCount] = rSamp[i]; bgG[bgCount] = gSamp[i]; bgB[bgCount] = bSamp[i];
                    bgCount++;
                }
            }
            if (bgCount < 100) bgCount = sc; // fallback if not enough background pixels

            Array.Sort(bgR, 0, bgCount); Array.Sort(bgG, 0, bgCount); Array.Sort(bgB, 0, bgCount);

            float rMed = bgR[bgCount / 2];
            float gMed = bgG[bgCount / 2];
            float bMed = bgB[bgCount / 2];

            // SUBTRACTIVE background equalization (not multiplicative).
            // For dual-band (Ha+OIII), background B >> G >> R due to filter transmission.
            // Multiplicative WB (geo mean or min) scales SIGNAL along with background,
            // either boosting red too much or crushing it.
            // Subtractive: subtract per-channel background offset so all channels have
            // the same background level. Signal ratios are preserved exactly.
            // This is applied as negative offsets stored in the WhiteBalance object —
            // the "factors" are actually subtraction amounts, applied differently.

            // Compute offsets: subtract each channel's excess above the minimum
            float minMed = Math.Min(rMed, Math.Min(gMed, bMed));
            float rSub = rMed - minMed;
            float gSub = gMed - minMed;
            float bSub = bMed - minMed;

            Logger.Info($"LiveStack WhiteBalance: bg medians R={rMed:E3} G={gMed:E3} B={bMed:E3} → " +
                $"subtract R={rSub:E3} G={gSub:E3} B={bSub:E3} (from {bgCount} bg pixels)");

            return new WhiteBalance(rSub, gSub, bSub);
        }

        /// <summary>
        /// Apply white balance as background subtraction (not multiplication).
        /// wb.Rf/Gf/Bf contain the per-channel subtraction amounts.
        /// </summary>
        public static void ApplyWhiteBalance(float[] r, float[] g, float[] b, WhiteBalance wb) {
            for (int i = 0; i < r.Length; i++) {
                r[i] = Math.Max(0f, r[i] - wb.Rf);
                g[i] = Math.Max(0f, g[i] - wb.Gf);
                b[i] = Math.Max(0f, b[i] - wb.Bf);
            }
        }

        /// <summary>
        /// Compute LINKED stretch parameters from luminance.
        /// Same black point and midtone for all channels — preserves color.
        /// Call AFTER white balance has been applied to the data.
        /// </summary>
        public static StretchParams AutoStretchLinked(float[] r, float[] g, float[] b) {
            // Compute luminance
            int pixelCount = r.Length;
            float[] lum = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
                lum[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];

            // Sample for speed
            int sampleSize = 50000;
            float[] sample;
            if (lum.Length <= sampleSize) {
                sample = (float[])lum.Clone();
            } else {
                sample = new float[sampleSize];
                var rng = new Random(42);
                for (int i = 0; i < sampleSize; i++)
                    sample[i] = lum[rng.Next(lum.Length)];
            }
            Array.Sort(sample);

            float median = sample[sample.Length / 2];

            // MAD
            float[] absDevs = new float[sample.Length];
            for (int i = 0; i < sample.Length; i++)
                absDevs[i] = Math.Abs(sample[i] - median);
            Array.Sort(absDevs);
            float mad = absDevs[absDevs.Length / 2] * 1.4826f;

            // Black point: clip at median - 2.8 * MAD
            float blackPoint = Math.Max(0f, median - 2.8f * mad);

            // Normalized median
            float normalizedMedian = (median - blackPoint) / (1f - blackPoint);
            if (normalizedMedian <= 0f) normalizedMedian = 0.0001f;
            if (normalizedMedian >= 1f) normalizedMedian = 0.9999f;

            // Solve for midtone that maps normalizedMedian → 0.20
            float m = normalizedMedian;
            float t = 0.21f;
            float midtone = m * (1f - t) / (m * (1f - 2f * t) + t);
            midtone = Math.Max(0.001f, Math.Min(0.99f, midtone));

            Logger.Info($"LiveStack AutoStretch: median={median:E3} MAD={mad:E3} BP={blackPoint:E3} midtone={midtone:F4}");

            return new StretchParams(blackPoint, midtone);
        }

        /// <summary>
        /// Render stretched RGB to bitmap. Same stretch applied to all channels (linked).
        /// </summary>
        public static BitmapSource StretchToBitmapLinked(float[] r, float[] g, float[] b,
            int width, int height, StretchParams sp,
            float brightnessOffset, float contrastOffset,
            float rBal, float gBal, float bBal, float blackClip = 0f) {

            int pixelCount = width * height;
            byte[] pixels = new byte[pixelCount * 3];

            float brightMul = (float)Math.Pow(2.0, -brightnessOffset * 3.0);
            float mt = Math.Max(0.001f, Math.Min(0.99f, sp.Midtone * brightMul));

            float contMul = 1.0f + contrastOffset * 1.5f;
            if (contMul < 0.1f) contMul = 0.1f;
            float bp = Math.Max(0f, Math.Min(0.5f, sp.BlackPoint * contMul));

            float invRange = 1f / (1f - bp);
            float clipInv = blackClip < 0.99f ? 1f / (1f - blackClip) : 1f;

            for (int i = 0; i < pixelCount; i++) {
                int idx = i * 3;
                float rv = Math.Max(0f, Math.Min(1f, (r[i] - bp) * invRange));
                float gv = Math.Max(0f, Math.Min(1f, (g[i] - bp) * invRange));
                float bv = Math.Max(0f, Math.Min(1f, (b[i] - bp) * invRange));

                float rOut = MTF(rv, mt) * rBal;
                float gOut = MTF(gv, mt) * gBal;
                float bOut = MTF(bv, mt) * bBal;

                // Post-stretch black clip
                if (blackClip > 0f) {
                    rOut = Math.Max(0f, (rOut - blackClip) * clipInv);
                    gOut = Math.Max(0f, (gOut - blackClip) * clipInv);
                    bOut = Math.Max(0f, (bOut - blackClip) * clipInv);
                }

                pixels[idx + 0] = (byte)Math.Min(255f, rOut * 255f + 0.5f);
                pixels[idx + 1] = (byte)Math.Min(255f, gOut * 255f + 0.5f);
                pixels[idx + 2] = (byte)Math.Min(255f, bOut * 255f + 0.5f);
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// Convert already-stretched [0,1] float data to bitmap with RGB balance.
        /// Used by the arcsinh path.
        /// </summary>
        public static BitmapSource FloatToBitmap(float[] r, float[] g, float[] b,
            int width, int height, float rBal, float gBal, float bBal, float blackClip = 0f) {

            int pixelCount = width * height;
            byte[] pixels = new byte[pixelCount * 3];
            float invRange = blackClip < 0.99f ? 1f / (1f - blackClip) : 1f;

            for (int i = 0; i < pixelCount; i++) {
                int idx = i * 3;
                float rv = blackClip > 0f ? Math.Max(0f, (r[i] - blackClip) * invRange) : r[i];
                float gv = blackClip > 0f ? Math.Max(0f, (g[i] - blackClip) * invRange) : g[i];
                float bv = blackClip > 0f ? Math.Max(0f, (b[i] - blackClip) * invRange) : b[i];
                pixels[idx + 0] = (byte)Math.Min(255f, Math.Max(0f, rv * rBal * 255f + 0.5f));
                pixels[idx + 1] = (byte)Math.Min(255f, Math.Max(0f, gv * gBal * 255f + 0.5f));
                pixels[idx + 2] = (byte)Math.Min(255f, Math.Max(0f, bv * bBal * 255f + 0.5f));
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// Apply linked MTF stretch to float arrays in-place (for star reduction path).
        /// </summary>
        public static void ApplyMTFInPlace(float[] r, float[] g, float[] b,
            StretchParams sp, float brightnessOffset, float contrastOffset) {

            float brightMul = (float)Math.Pow(2.0, -brightnessOffset * 3.0);
            float mt = Math.Max(0.001f, Math.Min(0.99f, sp.Midtone * brightMul));
            float contMul = 1.0f + contrastOffset * 1.5f;
            if (contMul < 0.1f) contMul = 0.1f;
            float bp = Math.Max(0f, Math.Min(0.5f, sp.BlackPoint * contMul));
            float invRange = 1f / (1f - bp);

            for (int i = 0; i < r.Length; i++) {
                r[i] = MTF(Math.Max(0f, Math.Min(1f, (r[i] - bp) * invRange)), mt);
                g[i] = MTF(Math.Max(0f, Math.Min(1f, (g[i] - bp) * invRange)), mt);
                b[i] = MTF(Math.Max(0f, Math.Min(1f, (b[i] - bp) * invRange)), mt);
            }
        }
    }

    public class StretchParams {
        public float BlackPoint { get; }
        public float Midtone { get; }
        public StretchParams(float bp, float mt) { BlackPoint = bp; Midtone = mt; }
    }

    public class WhiteBalance {
        public float Rf { get; }
        public float Gf { get; }
        public float Bf { get; }
        public WhiteBalance(float rf, float gf, float bf) { Rf = rf; Gf = gf; Bf = bf; }
    }
}
