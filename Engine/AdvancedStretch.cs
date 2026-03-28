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
        // STAR REDUCTION — TWO-PHASE (detect LINEAR, apply STRETCHED)
        // ====================================================================
        // Phase 1: DetectStarsForReduction() — called on LINEAR data before stretch.
        //   Returns StarReductionMap with star positions and radii.
        // Phase 2: ApplyStarReduction() — called on STRETCHED data using the map.
        //   Only touches pixels at known star locations, so nebula is untouched.
        // ====================================================================

        /// <summary>
        /// Phase 1: Detect stars on LINEAR (pre-stretch) data and compute their
        /// positions, radii, and local backgrounds. Call BEFORE stretching.
        /// Returns null if no stars found or amount is zero.
        /// </summary>
        public static StarReductionMap DetectStarsForReduction(float[] r, float[] g, float[] b,
                                                                int width, int height) {
            int len = width * height;

            // Build luminance for star detection
            float[] lum = new float[len];
            for (int i = 0; i < len; i++)
                lum[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];

            // Detect stars — use more stars for thorough reduction
            var stars = StarDetector.DetectStars(lum, width, height, maxStars: 500);
            if (stars.Count == 0) return null;

            // Estimate global background level
            float background = EstimateBackground(lum);
            float sigma = EstimateNoise(lum, background);
            float starThreshold = background + 3f * sigma;

            var entries = new System.Collections.Generic.List<StarReductionEntry>();

            foreach (var star in stars) {
                int cx = (int)star.X, cy = (int)star.Y;
                if (cx < 2 || cx >= width - 2 || cy < 2 || cy >= height - 2) continue;

                // Measure star radius: walk outward until brightness drops near background
                float peak = lum[cy * width + cx];
                if (peak < starThreshold) continue;

                float cutoff = background + (peak - background) * 0.15f;
                int radius = 2;
                for (int rad = 2; rad <= 40; rad++) {
                    float avg = 0; int cnt = 0;
                    if (cx + rad < width) { avg += lum[cy * width + cx + rad]; cnt++; }
                    if (cx - rad >= 0) { avg += lum[cy * width + cx - rad]; cnt++; }
                    if (cy + rad < height) { avg += lum[(cy + rad) * width + cx]; cnt++; }
                    if (cy - rad >= 0) { avg += lum[(cy - rad) * width + cx]; cnt++; }
                    if (cnt > 0) avg /= cnt;
                    if (avg < cutoff) { radius = rad; break; }
                    radius = rad;
                }

                entries.Add(new StarReductionEntry {
                    X = star.X, Y = star.Y, Radius = radius
                });
            }

            if (entries.Count == 0) return null;
            Logger.Info($"LiveStack: StarReductionMap: {entries.Count} stars detected on linear data");
            return new StarReductionMap { Stars = entries, Width = width, Height = height };
        }

        /// <summary>
        /// Compatibility wrapper for v0.9.33 VM. Calls ReduceStarsLinear.
        /// threshold parameter is ignored (linear-space approach doesn't need it).
        /// </summary>
        public static void ReduceStars(float[] r, float[] g, float[] b,
                                        int width, int height,
                                        float amount, float threshold = 0.15f) {
            ReduceStarsLinear(r, g, b, width, height, amount);
        }

        /// <summary>
        /// Legacy fallback: detect and reduce stars in one pass on the input data.
        /// Prefer the two-phase approach (DetectStarsForReduction + ApplyStarReduction).
        /// </summary>
        public static void ReduceStarsLinear(float[] r, float[] g, float[] b,
                                              int width, int height, float amount) {
            if (amount <= 0.01f) return;
            var map = DetectStarsForReduction(r, g, b, width, height);
            if (map == null) return;
            ApplyStarReduction(r, g, b, width, height, amount, map);
        }

        /// <summary>
        /// Phase 2: Apply star reduction to (typically STRETCHED) data using
        /// star positions detected on LINEAR data. Only modifies pixels at
        /// known star locations — nebula detail is completely untouched.
        /// amount: 0.0 = no reduction, 1.0 = stars reduced to local background.
        /// </summary>
        public static void ApplyStarReduction(float[] r, float[] g, float[] b,
                                               int width, int height, float amount,
                                               StarReductionMap map) {
            if (amount <= 0.01f || map == null || map.Stars.Count == 0) return;
            int len = width * height;

            // Build luminance on the CURRENT (stretched) data for local background measurement
            float[] lum = new float[len];
            for (int i = 0; i < len; i++)
                lum[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];

            float background = EstimateBackground(lum);

            int reduced = 0;
            foreach (var star in map.Stars) {
                int cx = (int)star.X, cy = (int)star.Y;
                if (cx < 2 || cx >= width - 2 || cy < 2 || cy >= height - 2) continue;

                int radius = star.Radius;

                // Measure local background in an annulus on the STRETCHED data
                int innerR = radius + 2;
                int outerR = radius + 8;
                float bgSum = 0; int bgCount = 0;
                int y0 = Math.Max(0, cy - outerR);
                int y1 = Math.Min(height - 1, cy + outerR);
                int x0 = Math.Max(0, cx - outerR);
                int x1 = Math.Min(width - 1, cx + outerR);

                for (int y = y0; y <= y1; y++) {
                    float dy = y - star.Y;
                    for (int x = x0; x <= x1; x++) {
                        float dx = x - star.X;
                        float dist2 = dx * dx + dy * dy;
                        if (dist2 >= innerR * innerR && dist2 <= outerR * outerR) {
                            bgSum += lum[y * width + x];
                            bgCount++;
                        }
                    }
                }

                float localBg = bgCount > 10 ? bgSum / bgCount : background;

                // Reduce: for each pixel within the star footprint,
                // pull excess above local background toward zero.
                int footprint = (int)(radius * 1.5f) + 1;
                float sigma2 = radius * 0.7f;
                float invSigma2 = 1f / (2f * sigma2 * sigma2);

                y0 = Math.Max(0, cy - footprint);
                y1 = Math.Min(height - 1, cy + footprint);
                x0 = Math.Max(0, cx - footprint);
                x1 = Math.Min(width - 1, cx + footprint);

                for (int y = y0; y <= y1; y++) {
                    float dy = y - star.Y;
                    for (int x = x0; x <= x1; x++) {
                        float dx = x - star.X;
                        float dist2 = dx * dx + dy * dy;
                        float falloff = (float)Math.Exp(-dist2 * invSigma2);

                        if (falloff < 0.01f) continue;

                        int idx = y * width + x;
                        float reduce = amount * falloff;

                        // Per-channel: reduce excess above local background
                        // This preserves star color proportions
                        float localBgR = localBg * (r[idx] / Math.Max(0.0001f, lum[idx]));
                        float localBgG = localBg * (g[idx] / Math.Max(0.0001f, lum[idx]));
                        float localBgB = localBg * (b[idx] / Math.Max(0.0001f, lum[idx]));

                        float excessR = r[idx] - localBgR;
                        float excessG = g[idx] - localBgG;
                        float excessB = b[idx] - localBgB;

                        if (excessR > 0) r[idx] = localBgR + excessR * (1f - reduce);
                        if (excessG > 0) g[idx] = localBgG + excessG * (1f - reduce);
                        if (excessB > 0) b[idx] = localBgB + excessB * (1f - reduce);
                    }
                }
                reduced++;
            }

            if (reduced > 0)
                Logger.Info($"LiveStack: Reduced {reduced} stars on stretched data (amount={amount:F2})");
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

    public class StarReductionMap {
        public System.Collections.Generic.List<StarReductionEntry> Stars { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class StarReductionEntry {
        public float X { get; set; }
        public float Y { get; set; }
        public int Radius { get; set; }
    }
}
