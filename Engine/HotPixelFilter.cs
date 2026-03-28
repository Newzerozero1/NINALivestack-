using System;
using NINA.Core.Utility;

namespace NinaLiveStack.Engine {

    /// <summary>
    /// Detects and replaces hot/cold pixels in a single frame before stacking.
    /// Uses a local 5x5 median comparison: if a pixel deviates from its local
    /// median by more than (sigma × local_MAD), it's replaced with the median.
    /// 
    /// This runs per-frame BEFORE accumulation, so the stack never sees the bad pixels.
    /// </summary>
    public static class HotPixelFilter {

        /// <summary>
        /// Filter hot/cold pixels in a single channel (ushort).
        /// sigma: rejection threshold in MAD units (3.0 = aggressive, 5.0 = mild)
        /// Returns count of pixels replaced.
        /// </summary>
        public static int FilterChannel(ushort[] data, int width, int height, float sigma = 3.0f) {
            if (data == null || width < 7 || height < 7) return 0;

            int replaced = 0;
            int radius = 2; // 5x5 box
            ushort[] medianBuf = new ushort[25]; // max neighborhood size

            for (int y = radius; y < height - radius; y++) {
                for (int x = radius; x < width - radius; x++) {
                    int idx = y * width + x;
                    ushort center = data[idx];

                    // Gather 5x5 neighborhood (excluding center)
                    int count = 0;
                    for (int dy = -radius; dy <= radius; dy++) {
                        for (int dx = -radius; dx <= radius; dx++) {
                            if (dx == 0 && dy == 0) continue;
                            medianBuf[count++] = data[(y + dy) * width + (x + dx)];
                        }
                    }

                    // Sort for median and MAD
                    Array.Sort(medianBuf, 0, count);
                    ushort median = medianBuf[count / 2];

                    // Compute MAD (median absolute deviation)
                    float[] absDevs = new float[count];
                    for (int i = 0; i < count; i++) {
                        absDevs[i] = Math.Abs((float)medianBuf[i] - median);
                    }
                    Array.Sort(absDevs, 0, count);
                    float mad = absDevs[count / 2];

                    // Minimum MAD floor to avoid division issues in flat regions
                    if (mad < 3.0f) mad = 3.0f;

                    float deviation = Math.Abs((float)center - median);
                    if (deviation > sigma * mad) {
                        data[idx] = median;
                        replaced++;
                    }
                }
            }

            return replaced;
        }

        /// <summary>
        /// Filter hot/cold pixels in RGB channels.
        /// </summary>
        public static int FilterRGB(ushort[] r, ushort[] g, ushort[] b, int width, int height, float sigma = 3.0f) {
            int total = 0;
            total += FilterChannel(r, width, height, sigma);
            total += FilterChannel(g, width, height, sigma);
            total += FilterChannel(b, width, height, sigma);
            return total;
        }

        /// <summary>
        /// Fast version with DYNAMIC thresholds based on image statistics.
        /// Computes background median + MAD, then only fully checks pixels
        /// that deviate significantly from background.
        /// </summary>
        public static int FilterChannelFast(ushort[] data, int width, int height,
                                             float sigma = 3.0f) {
            if (data == null || width < 7 || height < 7) return 0;

            // Sample to find background level
            int sampleSize = 10000;
            var rng = new Random(42);
            ushort[] sample = new ushort[sampleSize];
            for (int i = 0; i < sampleSize; i++)
                sample[i] = data[rng.Next(data.Length)];
            Array.Sort(sample);
            ushort bgMedian = sample[sampleSize / 2];

            float[] absDevs = new float[sampleSize];
            for (int i = 0; i < sampleSize; i++)
                absDevs[i] = Math.Abs((float)sample[i] - bgMedian);
            Array.Sort(absDevs);
            float bgMad = absDevs[sampleSize / 2] * 1.4826f;
            if (bgMad < 10f) bgMad = 10f;

            // Dynamic thresholds: check any pixel far from background
            ushort hotThreshold = (ushort)Math.Min(65000, bgMedian + 8 * bgMad);
            ushort coldThreshold = (ushort)Math.Max(0, bgMedian - 8 * bgMad);

            int replaced = 0;
            int radius = 2;
            ushort[] neighborBuf = new ushort[24];

            for (int y = radius; y < height - radius; y++) {
                for (int x = radius; x < width - radius; x++) {
                    int idx = y * width + x;
                    ushort center = data[idx];

                    // Skip pixels close to background
                    if (center < hotThreshold && center > coldThreshold) continue;

                    // Gather neighbors
                    int count = 0;
                    for (int dy = -radius; dy <= radius; dy++) {
                        for (int dx = -radius; dx <= radius; dx++) {
                            if (dx == 0 && dy == 0) continue;
                            neighborBuf[count++] = data[(y + dy) * width + (x + dx)];
                        }
                    }

                    Array.Sort(neighborBuf, 0, count);
                    ushort median = neighborBuf[count / 2];

                    float mad = 0;
                    for (int i = 0; i < count; i++) {
                        mad += Math.Abs((float)neighborBuf[i] - median);
                    }
                    mad /= count;
                    if (mad < 3.0f) mad = 3.0f;

                    float deviation = Math.Abs((float)center - median);
                    if (deviation > sigma * mad) {
                        data[idx] = median;
                        replaced++;
                    }
                }
            }

            return replaced;
        }
    }
}
