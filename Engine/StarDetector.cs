using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Core.Utility;

namespace NinaLiveStack.Engine {

    /// <summary>
    /// Simple star detector for frame alignment.
    /// Finds bright peaks in the image and returns centroid positions.
    /// Designed for stretched or unstretched astro images with clear stars.
    /// </summary>
    public static class StarDetector {

        /// <summary>
        /// Detect stars in a luminance image (0-1 range).
        /// Returns list of (x, y) centroid positions sorted by brightness.
        /// </summary>
        public static List<StarPosition> DetectStars(float[] luminance, int width, int height, int maxStars = 50) {
            // Step 1: Estimate background using median of a sample
            float background = EstimateBackground(luminance);

            // Step 2: Compute threshold - stars must be well above background
            // Use 5 sigma above background
            float sigma = EstimateNoise(luminance, background);
            float threshold = background + 5f * sigma;

            // Minimum threshold to avoid detecting noise in very clean images
            if (threshold < 0.005f) threshold = 0.005f;

            // Step 3: Find local maxima above threshold
            var candidates = new List<StarCandidate>();
            int margin = 5; // stay away from edges

            for (int y = margin; y < height - margin; y++) {
                for (int x = margin; x < width - margin; x++) {
                    int idx = y * width + x;
                    float val = luminance[idx];

                    if (val < threshold) continue;

                    // Check if this pixel is a local maximum in 3x3 neighborhood
                    bool isMax = true;
                    for (int dy = -1; dy <= 1 && isMax; dy++) {
                        for (int dx = -1; dx <= 1 && isMax; dx++) {
                            if (dx == 0 && dy == 0) continue;
                            if (luminance[(y + dy) * width + (x + dx)] > val)
                                isMax = false;
                        }
                    }

                    if (isMax) {
                        candidates.Add(new StarCandidate { X = x, Y = y, Peak = val });
                    }
                }
            }

            // Step 4: Sort by brightness and take top candidates
            candidates.Sort((a, b) => b.Peak.CompareTo(a.Peak));
            int maxCandidates = Math.Min(candidates.Count, maxStars * 3);

            // Step 5: Compute centroids for top candidates with duplicate suppression
            var stars = new List<StarPosition>();
            int suppressRadius = 10; // min distance between stars (pixels)

            for (int c = 0; c < maxCandidates && stars.Count < maxStars; c++) {
                var cand = candidates[c];

                // Check not too close to existing detected star
                bool tooClose = false;
                foreach (var s in stars) {
                    float dx = cand.X - s.X;
                    float dy = cand.Y - s.Y;
                    if (dx * dx + dy * dy < suppressRadius * suppressRadius) {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                // Compute weighted centroid in 5x5 box
                float sumW = 0, sumWX = 0, sumWY = 0;
                for (int dy = -2; dy <= 2; dy++) {
                    for (int dx = -2; dx <= 2; dx++) {
                        int px = cand.X + dx;
                        int py = cand.Y + dy;
                        if (px < 0 || px >= width || py < 0 || py >= height) continue;

                        float w = luminance[py * width + px] - background;
                        if (w < 0) w = 0;
                        sumW += w;
                        sumWX += w * px;
                        sumWY += w * py;
                    }
                }

                if (sumW > 0) {
                    float cx = sumWX / sumW;
                    float cy = sumWY / sumW;

                    // Compute second moments for elongation (eccentricity)
                    float mxx = 0, myy = 0, mxy = 0;
                    for (int dy = -2; dy <= 2; dy++) {
                        for (int dx = -2; dx <= 2; dx++) {
                            int px = cand.X + dx;
                            int py = cand.Y + dy;
                            if (px < 0 || px >= width || py < 0 || py >= height) continue;
                            float w = luminance[py * width + px] - background;
                            if (w < 0) w = 0;
                            float ddx = px - cx, ddy = py - cy;
                            mxx += w * ddx * ddx;
                            myy += w * ddy * ddy;
                            mxy += w * ddx * ddy;
                        }
                    }
                    if (sumW > 0) { mxx /= sumW; myy /= sumW; mxy /= sumW; }

                    // Elongation = ratio of semi-major to semi-minor axis
                    float trace = mxx + myy;
                    float det = mxx * myy - mxy * mxy;
                    float disc = trace * trace - 4f * det;
                    float elongation = 1f;
                    if (disc > 0 && trace > 0) {
                        float sqrtDisc = (float)Math.Sqrt(disc);
                        float a = (trace + sqrtDisc) / 2f;
                        float b = (trace - sqrtDisc) / 2f;
                        if (b > 0.001f) elongation = (float)Math.Sqrt(a / b);
                    }

                    stars.Add(new StarPosition {
                        X = cx, Y = cy,
                        Brightness = cand.Peak,
                        Elongation = elongation
                    });
                }
            }

            Logger.Debug($"StarDetector: Found {stars.Count} stars (threshold={threshold:F4}, bg={background:F4})");
            return stars;
        }

        /// <summary>
        /// Compute median elongation from detected stars.
        /// Round stars ≈ 1.0. Trailed stars >> 1.0. 
        /// </summary>
        public static float MedianElongation(List<StarPosition> stars) {
            if (stars.Count < 3) return 1f;
            // Use the brightest half for more reliable measurement
            int n = Math.Max(3, stars.Count / 2);
            float[] vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = stars[i].Elongation;
            Array.Sort(vals);
            return vals[n / 2];
        }

        private static float EstimateBackground(float[] data) {
            // Sample and find median
            int sampleSize = Math.Min(data.Length, 10000);
            float[] sample = new float[sampleSize];
            var rng = new Random(42);
            for (int i = 0; i < sampleSize; i++)
                sample[i] = data[rng.Next(data.Length)];
            Array.Sort(sample);
            return sample[sampleSize / 2];
        }

        private static float EstimateNoise(float[] data, float background) {
            // MAD-based noise estimate
            int sampleSize = Math.Min(data.Length, 10000);
            float[] absDevs = new float[sampleSize];
            var rng = new Random(42);
            for (int i = 0; i < sampleSize; i++)
                absDevs[i] = Math.Abs(data[rng.Next(data.Length)] - background);
            Array.Sort(absDevs);
            return absDevs[sampleSize / 2] * 1.4826f;
        }

        private struct StarCandidate {
            public int X, Y;
            public float Peak;
        }
    }

    public struct StarPosition {
        public float X, Y;
        public float Brightness;
        public float Elongation;
    }
}
