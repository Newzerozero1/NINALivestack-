using System;
using NINA.Core.Utility;

namespace NinaLiveStack.Engine {

    public static class AdvancedStretch {

        // ====================================================================
        // ARCSINH STRETCH
        // ====================================================================

        public static void ApplyArcsinh(float[] r, float[] g, float[] b,
                                         float stretchFactor, float blackPoint) {
            if (stretchFactor < 1.0f) stretchFactor = 1.0f;

            double a = (double)stretchFactor;
            double normFactor = 1.0 / Math.Log(a + Math.Sqrt(a * a + 1.0));

            for (int i = 0; i < r.Length; i++) {
                r[i] = ArcsinhPixel(r[i], blackPoint, a, normFactor);
                g[i] = ArcsinhPixel(g[i], blackPoint, a, normFactor);
                b[i] = ArcsinhPixel(b[i], blackPoint, a, normFactor);
            }
        }

        private static float ArcsinhPixel(float val, float bp, double a, double norm) {
            float x = val - bp;
            if (x <= 0f) return 0f;
            x /= (1f - bp);
            if (x >= 1f) return 1f;
            double ax = a * x;
            double stretched = Math.Log(ax + Math.Sqrt(ax * ax + 1.0)) * norm;
            return (float)Math.Min(stretched, 1.0);
        }

        public static void AutoArcsinh(float[] r, float[] g, float[] b,
                                        int width, int height,
                                        float stretchFactor, float brightness = 0f,
                                        float contrast = 0f, float bpSigma = 2.8f) {
            int step = Math.Max(1, r.Length / 50000);
            int sc = 0;
            float[] lumSamples = new float[(r.Length + step - 1) / step];
            for (int i = 0; i < r.Length && sc < lumSamples.Length; i += step)
                lumSamples[sc++] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
            Array.Sort(lumSamples, 0, sc);
            float median = lumSamples[sc / 2];

            float[] absDevs = new float[sc];
            for (int i = 0; i < sc; i++)
                absDevs[i] = Math.Abs(lumSamples[i] - median);
            Array.Sort(absDevs, 0, sc);
            float mad = absDevs[sc / 2] * 1.4826f;

            float blackPoint = Math.Max(0f, median - bpSigma * mad);

            float contMul = 1.0f + contrast * 3.0f;
            if (contMul < 0.1f) contMul = 0.1f;
            blackPoint = Math.Max(0f, blackPoint * contMul);

            Logger.Info($"LiveStack AutoArcsinh: median={median:E3} MAD={mad:E3} BP={blackPoint:E3} factor={stretchFactor:F0}");

            ApplyArcsinh(r, g, b, stretchFactor, blackPoint);

            // Post-arcsinh: linear rescale to target brightness
            sc = 0;
            for (int i = 0; i < r.Length && sc < lumSamples.Length; i += step)
                lumSamples[sc++] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
            Array.Sort(lumSamples, 0, sc);
            float postMedian = lumSamples[sc / 2];

            float target = 0.21f * (float)Math.Pow(2.0, brightness * 3.0);
            target = Math.Max(0.05f, Math.Min(0.7f, target));

            if (postMedian > 0.00001f) {
                float scale = target / postMedian;
                Logger.Info($"LiveStack AutoArcsinh: postMedian={postMedian:F5} target={target:F3} → scale={scale:F3}");
                for (int i = 0; i < r.Length; i++) {
                    r[i] = Math.Min(1f, r[i] * scale);
                    g[i] = Math.Min(1f, g[i] * scale);
                    b[i] = Math.Min(1f, b[i] * scale);
                }
            }
        }

        // ====================================================================
        // STAR REDUCTION — MORPHOLOGICAL EROSION WITH LUMINANCE MASK
        // ====================================================================
        // This is how PixInsight, Photoshop, and RC Astro do it:
        // 1. Build a luminance star mask from stretched data (bright = star)
        // 2. Apply minimum filter (erosion) — shrinks all bright compact features
        // 3. Blend eroded result with original using the mask
        // No per-star detection, no Gaussian profiles, no radius math.
        // ====================================================================

        /// <summary>
        /// Reduce star sizes using morphological erosion (minimum filter)
        /// masked to bright areas. Works on STRETCHED data.
        /// amount: 0.0 = off, 1.0 = aggressive reduction.
        /// </summary>
        public static void MorphologicalStarReduce(float[] r, float[] g, float[] b,
                                                     int width, int height, float amount) {
            if (amount <= 0.01f) return;

            // Kernel radius: keep small to avoid blocky artifacts
            int kernelRadius = Math.Max(1, (int)(1 + amount * 1.5f));

            // Morphological erosion + dilation
            float[] rEroded = MinFilter(r, width, height, kernelRadius);
            float[] gEroded = MinFilter(g, width, height, kernelRadius);
            float[] bEroded = MinFilter(b, width, height, kernelRadius);

            float[] rDilated = MaxFilter(r, width, height, kernelRadius);
            float[] gDilated = MaxFilter(g, width, height, kernelRadius);
            float[] bDilated = MaxFilter(b, width, height, kernelRadius);

            float selection = 0.25f;

            // Self-masking: use the erosion DELTA as a natural star mask.
            // Star pixel:   original=0.8, eroded=0.1 → delta=0.7 → apply fully
            // Nebula pixel: original=0.3, eroded=0.28 → delta=0.02 → skip
            // No separate mask needed — the erosion itself tells us what's a star.

            // Find the max delta to normalize
            float maxDelta = 0.001f;
            for (int i = 0; i < r.Length; i++) {
                float lumOrig = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
                float lumErod = 0.2126f * rEroded[i] + 0.7152f * gEroded[i] + 0.0722f * bEroded[i];
                float delta = lumOrig - lumErod;
                if (delta > maxDelta) maxDelta = delta;
            }

            // Threshold: ignore deltas below 5% of max (noise/nebula)
            float deltaThresh = maxDelta * 0.05f;

            for (int i = 0; i < r.Length; i++) {
                float lumOrig = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
                float lumErod = 0.2126f * rEroded[i] + 0.7152f * gEroded[i] + 0.0722f * bEroded[i];
                float delta = lumOrig - lumErod;

                // Skip pixels where erosion barely changed anything (nebula, sky)
                if (delta <= deltaThresh) continue;

                // Ramp from 0 at threshold to 1 at significant delta
                float starness = Math.Min(1f, (delta - deltaThresh) / (maxDelta * 0.3f));

                // Morphological selection
                float rM = rEroded[i] * (1f - selection) + rDilated[i] * selection;
                float gM = gEroded[i] * (1f - selection) + gDilated[i] * selection;
                float bM = bEroded[i] * (1f - selection) + bDilated[i] * selection;

                // Blend: starness controls HOW MUCH erosion applies
                float blend = starness * amount;
                r[i] = r[i] + (rM - r[i]) * blend;
                g[i] = g[i] + (gM - g[i]) * blend;
                b[i] = b[i] + (bM - b[i]) * blend;
            }

            Logger.Info($"LiveStack: Star reduction (amount={amount:F2}, kernel={kernelRadius * 2 + 1})");
        }

        /// <summary>Erosion: replace each pixel with the minimum in a circular neighborhood.</summary>
        private static float[] MinFilter(float[] src, int width, int height, int radius) {
            // Two-pass separable approximation for speed
            float[] temp = new float[src.Length];
            float[] dst = new float[src.Length];

            // Horizontal min
            for (int y = 0; y < height; y++) {
                int row = y * width;
                for (int x = 0; x < width; x++) {
                    float min = float.MaxValue;
                    int x0 = Math.Max(0, x - radius);
                    int x1 = Math.Min(width - 1, x + radius);
                    for (int xx = x0; xx <= x1; xx++)
                        min = Math.Min(min, src[row + xx]);
                    temp[row + x] = min;
                }
            }

            // Vertical min
            for (int x = 0; x < width; x++) {
                for (int y = 0; y < height; y++) {
                    float min = float.MaxValue;
                    int y0 = Math.Max(0, y - radius);
                    int y1 = Math.Min(height - 1, y + radius);
                    for (int yy = y0; yy <= y1; yy++)
                        min = Math.Min(min, temp[yy * width + x]);
                    dst[y * width + x] = min;
                }
            }
            return dst;
        }

        /// <summary>Dilation: replace each pixel with the maximum in a circular neighborhood.</summary>
        private static float[] MaxFilter(float[] src, int width, int height, int radius) {
            float[] temp = new float[src.Length];
            float[] dst = new float[src.Length];

            // Horizontal max
            for (int y = 0; y < height; y++) {
                int row = y * width;
                for (int x = 0; x < width; x++) {
                    float max = float.MinValue;
                    int x0 = Math.Max(0, x - radius);
                    int x1 = Math.Min(width - 1, x + radius);
                    for (int xx = x0; xx <= x1; xx++)
                        max = Math.Max(max, src[row + xx]);
                    temp[row + x] = max;
                }
            }

            // Vertical max
            for (int x = 0; x < width; x++) {
                for (int y = 0; y < height; y++) {
                    float max = float.MinValue;
                    int y0 = Math.Max(0, y - radius);
                    int y1 = Math.Min(height - 1, y + radius);
                    for (int yy = y0; yy <= y1; yy++)
                        max = Math.Max(max, temp[yy * width + x]);
                    dst[y * width + x] = max;
                }
            }
            return dst;
        }

        // ====================================================================
        // PER-CHANNEL BACKGROUND SUBTRACTION
        // ====================================================================

        /// <summary>
        /// Remove per-channel sky background pedestal.
        /// Subtracts the difference between each channel's median and the
        /// minimum median. This neutralizes light pollution color cast
        /// without affecting relative channel brightness.
        /// Apply to [0,1] normalized data, after WB, before stretch.
        /// </summary>
        public static void SubtractBackground(float[] r, float[] g, float[] b) {
            float rMed = SampleMedian(r);
            float gMed = SampleMedian(g);
            float bMed = SampleMedian(b);
            float minMed = Math.Min(rMed, Math.Min(gMed, bMed));

            float rSub = rMed - minMed;
            float gSub = gMed - minMed;
            float bSub = bMed - minMed;

            if (rSub < 0.00001f && gSub < 0.00001f && bSub < 0.00001f) return;

            for (int i = 0; i < r.Length; i++) {
                r[i] = Math.Max(0f, r[i] - rSub);
                g[i] = Math.Max(0f, g[i] - gSub);
                b[i] = Math.Max(0f, b[i] - bSub);
            }

            Logger.Info($"LiveStack: Background subtracted: R-={rSub:E3} G-={gSub:E3} B-={bSub:E3}");
        }

        // ====================================================================
        // À TROUS WAVELET SHARPENING
        // ====================================================================
        // Decomposes into 3 detail layers at different scales:
        //   Layer 1 = finest detail (noise, star edges, tiny features)
        //   Layer 2 = medium detail (star profiles, small nebula texture)
        //   Layer 3 = large-scale structure (nebula arms, gradients)
        // Each layer = (previous smooth) - (next smooth).
        // Boost a layer to sharpen at that scale.
        // Uses separable B3 spline kernel with increasing gaps (à trous).
        // ====================================================================

        /// <summary>
        /// Apply wavelet sharpening to stretched RGB data.
        /// sharpenAmount: 0 = off, 0.3 = subtle, 0.7 = strong, 1.0 = aggressive.
        /// Boosts layers 1 (fine) and 2 (medium) — layer 3 left alone.
        /// Runs on display-resolution data (~6.5MP), well under 500ms on i5.
        /// </summary>
        public static void WaveletSharpen(float[] r, float[] g, float[] b,
                                           int width, int height, float sharpenAmount) {
            if (sharpenAmount <= 0.01f) return;

            // Sharpen luminance only — preserves color, avoids amplifying chromatic noise
            float[] lum = new float[r.Length];
            for (int i = 0; i < r.Length; i++)
                lum[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];

            // Decompose: 3 layers via à trous B3 spline
            float[] smooth0 = lum;
            float[] smooth1 = ATrousBlur(smooth0, width, height, 1);  // gap=1
            float[] smooth2 = ATrousBlur(smooth1, width, height, 2);  // gap=2
            float[] smooth3 = ATrousBlur(smooth2, width, height, 4);  // gap=4

            // Detail layers = difference between successive smoothed versions
            float[] detail1 = new float[lum.Length]; // finest
            float[] detail2 = new float[lum.Length]; // medium
            for (int i = 0; i < lum.Length; i++) {
                detail1[i] = smooth0[i] - smooth1[i];
                detail2[i] = smooth1[i] - smooth2[i];
            }

            // Boost factors: layer 1 gets full boost, layer 2 gets 60%
            float boost1 = sharpenAmount * 3.0f;
            float boost2 = sharpenAmount * 1.8f;

            // Apply: add boosted detail back to each channel proportionally
            for (int i = 0; i < r.Length; i++) {
                float lumVal = lum[i];
                if (lumVal < 0.001f) continue; // skip pure black

                float delta = detail1[i] * boost1 + detail2[i] * boost2;

                // Protect shadows: reduce sharpening in dark areas to avoid amplifying noise
                float shadowMask = Math.Min(1f, lumVal * 8f);
                delta *= shadowMask;

                // Apply proportionally to each channel (preserves color ratios)
                float ratio = (lumVal + delta) / lumVal;
                if (ratio < 0.3f) ratio = 0.3f; // prevent inversion
                if (ratio > 3.0f) ratio = 3.0f;  // prevent blowout

                r[i] = Math.Min(1f, Math.Max(0f, r[i] * ratio));
                g[i] = Math.Min(1f, Math.Max(0f, g[i] * ratio));
                b[i] = Math.Min(1f, Math.Max(0f, b[i] * ratio));
            }

            Logger.Info($"LiveStack: Wavelet sharpen applied (amount={sharpenAmount:F2})");
        }

        /// <summary>
        /// À trous blur: separable B3 spline kernel [1,4,6,4,1]/16 with gaps.
        /// Gap doubles each layer: 1, 2, 4 — captures progressively larger scales.
        /// O(n) per pixel regardless of gap size (fixed 5-tap kernel).
        /// </summary>
        private static float[] ATrousBlur(float[] src, int width, int height, int gap) {
            float[] temp = new float[src.Length];
            float[] dst = new float[src.Length];

            // Horizontal pass: kernel [1,4,6,4,1]/16 with spacing = gap
            for (int y = 0; y < height; y++) {
                int row = y * width;
                for (int x = 0; x < width; x++) {
                    int x0 = Math.Max(0, x - 2 * gap);
                    int x1 = Math.Max(0, x - gap);
                    int x2 = x;
                    int x3 = Math.Min(width - 1, x + gap);
                    int x4 = Math.Min(width - 1, x + 2 * gap);
                    temp[row + x] = (src[row + x0] + 4f * src[row + x1] + 6f * src[row + x2]
                                   + 4f * src[row + x3] + src[row + x4]) / 16f;
                }
            }

            // Vertical pass
            for (int y = 0; y < height; y++) {
                int y0 = Math.Max(0, y - 2 * gap) * width;
                int y1 = Math.Max(0, y - gap) * width;
                int y2 = y * width;
                int y3 = Math.Min(height - 1, y + gap) * width;
                int y4 = Math.Min(height - 1, y + 2 * gap) * width;
                for (int x = 0; x < width; x++) {
                    dst[y2 + x] = (temp[y0 + x] + 4f * temp[y1 + x] + 6f * temp[y2 + x]
                                 + 4f * temp[y3 + x] + temp[y4 + x]) / 16f;
                }
            }

            return dst;
        }

        // ====================================================================
        // NOISE ESTIMATION
        // ====================================================================

        public static float EstimateNoise(float[] channel, int width, int height) {
            float bg = EstimateBackground(channel);
            return EstimateNoise(channel, bg);
        }

        private static float EstimateBackground(float[] data) {
            int sampleSize = Math.Min(data.Length, 10000);
            float[] sample = new float[sampleSize];
            var rng = new Random(42);
            for (int i = 0; i < sampleSize; i++)
                sample[i] = data[rng.Next(data.Length)];
            Array.Sort(sample);
            return sample[sampleSize / 2];
        }

        private static float EstimateNoise(float[] data, float background) {
            int sampleSize = Math.Min(data.Length, 10000);
            float[] absDevs = new float[sampleSize];
            var rng = new Random(42);
            for (int i = 0; i < sampleSize; i++)
                absDevs[i] = Math.Abs(data[rng.Next(data.Length)] - background);
            Array.Sort(absDevs);
            return absDevs[sampleSize / 2] * 1.4826f;
        }

        private static float SampleMedian(float[] data) {
            int step = Math.Max(1, data.Length / 50000);
            int sc = (data.Length + step - 1) / step;
            float[] samples = new float[sc];
            int idx = 0;
            for (int i = 0; i < data.Length && idx < sc; i += step)
                samples[idx++] = data[i];
            Array.Sort(samples, 0, idx);
            return samples[idx / 2];
        }
    }

}
