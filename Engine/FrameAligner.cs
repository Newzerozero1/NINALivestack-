using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace NinaLiveStack.Engine {

    public class FrameAligner {

        private List<StarPosition> _referenceStars;
        private List<Triangle> _referenceTriangles;
        private int _refWidth, _refHeight;

        public void SetReference(List<StarPosition> stars, int width, int height) {
            _referenceStars = stars;
            _refWidth = width;
            _refHeight = height;
            _referenceTriangles = BuildTriangles(stars);
            Logger.Info($"FrameAligner: Reference set with {stars.Count} stars, {_referenceTriangles.Count} triangles");
        }

        public bool HasReference => _referenceStars != null && _referenceStars.Count >= 3;

        public void ClearReference() {
            _referenceStars = null;
            _referenceTriangles = null;
        }

        public AlignResult ComputeAlignment(List<StarPosition> frameStars, int width, int height) {
            if (!HasReference || frameStars.Count < 3) return null;

            var frameTriangles = BuildTriangles(frameStars);
            var matches = MatchTriangles(_referenceTriangles, frameTriangles, _referenceStars, frameStars);

            if (matches.Count < 2) {
                Logger.Warning($"FrameAligner: Only {matches.Count} matches, need 2+");
                return null;
            }

            var transform = ComputeRigidTransform(matches);
            float rotDeg = Math.Abs(transform.RotationDeg);

            if (rotDeg > 150f) {
                Logger.Info($"FrameAligner: Meridian flip detected (rot={transform.RotationDeg:F1}°), will pre-rotate 180°");

                float cx = width / 2f;
                float cy = height / 2f;
                var rotatedStars = new List<StarPosition>();
                foreach (var s in frameStars) {
                    rotatedStars.Add(new StarPosition {
                        X = 2f * cx - s.X,
                        Y = 2f * cy - s.Y,
                        Brightness = s.Brightness
                    });
                }

                var rotTriangles = BuildTriangles(rotatedStars);
                var rotMatches = MatchTriangles(_referenceTriangles, rotTriangles, _referenceStars, rotatedStars);

                if (rotMatches.Count < 2) {
                    Logger.Warning("FrameAligner: Post-flip re-alignment failed");
                    return null;
                }

                var fineTransform = ComputeRigidTransform(rotMatches);
                Logger.Info($"FrameAligner: Post-flip fine align: dx={fineTransform.Tx:F1} dy={fineTransform.Ty:F1} rot={fineTransform.RotationDeg:F1}°");

                return new AlignResult {
                    IsFlipped = true,
                    FineTransform = fineTransform,
                    MatchCount = rotMatches.Count
                };
            }

            Logger.Debug($"FrameAligner: Normal align: dx={transform.Tx:F1} dy={transform.Ty:F1} rot={transform.RotationDeg:F1}° ({matches.Count} matches)");
            return new AlignResult {
                IsFlipped = false,
                FineTransform = transform,
                MatchCount = matches.Count
            };
        }

        public static ushort[] Rotate180(ushort[] data, int width, int height) {
            int pixelCount = width * height;
            ushort[] result = new ushort[pixelCount];
            for (int i = 0; i < pixelCount; i++) {
                result[i] = data[pixelCount - 1 - i];
            }
            return result;
        }

        /// <summary>
        /// Apply affine transform to warp an image. Uses Parallel.For on the outer loop
        /// for ~3x speedup on 4-core i5. Integer shift fast path for dithered frames.
        /// </summary>
        public static ushort[] WarpImageUShort(ushort[] source, int width, int height, AffineTransform t) {
            // Fast path: negligible rotation → integer pixel shift (zero softening)
            float absSin = Math.Abs(t.SinTheta);
            if (absSin < 0.0002f) {
                int shiftX = (int)Math.Round(t.Tx);
                int shiftY = (int)Math.Round(t.Ty);
                return ShiftImage(source, width, height, shiftX, shiftY);
            }

            // Bilinear interpolation with PARALLEL outer loop
            ushort[] result = new ushort[width * height];

            float cosA = t.CosTheta;
            float sinA = t.SinTheta;
            float invTx = -(cosA * t.Tx + sinA * t.Ty);
            float invTy = -(-sinA * t.Tx + cosA * t.Ty);

            Parallel.For(0, height, y => {
                for (int x = 0; x < width; x++) {
                    float sx = cosA * x + sinA * y + invTx;
                    float sy = -sinA * x + cosA * y + invTy;

                    int x0 = (int)Math.Floor(sx);
                    int y0 = (int)Math.Floor(sy);
                    float fx = sx - x0;
                    float fy = sy - y0;

                    if (x0 >= 0 && x0 < width - 1 && y0 >= 0 && y0 < height - 1) {
                        float v00 = source[y0 * width + x0];
                        float v10 = source[y0 * width + x0 + 1];
                        float v01 = source[(y0 + 1) * width + x0];
                        float v11 = source[(y0 + 1) * width + x0 + 1];
                        float val = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy) +
                                    v01 * (1 - fx) * fy + v11 * fx * fy;
                        result[y * width + x] = (ushort)Math.Max(0, Math.Min(65535, val + 0.5f));
                    }
                }
            });
            return result;
        }

        private static ushort[] ShiftImage(ushort[] source, int width, int height, int dx, int dy) {
            ushort[] result = new ushort[width * height];
            int srcXStart = Math.Max(0, -dx), srcXEnd = Math.Min(width, width - dx);
            int srcYStart = Math.Max(0, -dy), srcYEnd = Math.Min(height, height - dy);

            for (int sy = srcYStart; sy < srcYEnd; sy++) {
                int dstY = sy + dy;
                int srcRow = sy * width;
                int dstRow = dstY * width;
                for (int sx = srcXStart; sx < srcXEnd; sx++) {
                    result[dstRow + sx + dx] = source[srcRow + sx];
                }
            }
            return result;
        }

        // ---- Triangle internals ----

        private struct Triangle {
            public int Star1, Star2, Star3;
            public float R1, R2;
        }

        private static List<Triangle> BuildTriangles(List<StarPosition> stars) {
            var triangles = new List<Triangle>();
            int n = Math.Min(stars.Count, 25); // bumped from 20 to 25 for better post-flip matching

            for (int i = 0; i < n; i++) {
                for (int j = i + 1; j < n; j++) {
                    for (int k = j + 1; k < n; k++) {
                        float d12 = Dist(stars[i], stars[j]);
                        float d13 = Dist(stars[i], stars[k]);
                        float d23 = Dist(stars[j], stars[k]);

                        float[] sides = { d12, d13, d23 };
                        Array.Sort(sides);
                        if (sides[2] < 10f) continue;

                        triangles.Add(new Triangle {
                            Star1 = i, Star2 = j, Star3 = k,
                            R1 = sides[0] / sides[2],
                            R2 = sides[1] / sides[2]
                        });
                    }
                }
            }
            return triangles;
        }

        private static List<(StarPosition refStar, StarPosition frameStar)> MatchTriangles(
            List<Triangle> refTriangles, List<Triangle> frameTriangles,
            List<StarPosition> refStars, List<StarPosition> frameStars) {

            float tolerance = 0.01f;
            var votes = new Dictionary<(int refIdx, int frameIdx), int>();

            foreach (var rt in refTriangles) {
                foreach (var ft in frameTriangles) {
                    if (Math.Abs(rt.R1 - ft.R1) < tolerance && Math.Abs(rt.R2 - ft.R2) < tolerance) {
                        int[] ri = { rt.Star1, rt.Star2, rt.Star3 };
                        int[][] perms = {
                            new[] { ft.Star1, ft.Star2, ft.Star3 },
                            new[] { ft.Star1, ft.Star3, ft.Star2 },
                            new[] { ft.Star2, ft.Star1, ft.Star3 },
                            new[] { ft.Star2, ft.Star3, ft.Star1 },
                            new[] { ft.Star3, ft.Star1, ft.Star2 },
                            new[] { ft.Star3, ft.Star2, ft.Star1 }
                        };

                        foreach (var fi in perms) {
                            float d_r12 = Dist(refStars[ri[0]], refStars[ri[1]]);
                            float d_f12 = Dist(frameStars[fi[0]], frameStars[fi[1]]);
                            if (d_r12 < 1f || d_f12 < 1f) continue;
                            float scale = d_f12 / d_r12;
                            if (scale < 0.95f || scale > 1.05f) continue;

                            float d_r13 = Dist(refStars[ri[0]], refStars[ri[2]]);
                            float d_f13 = Dist(frameStars[fi[0]], frameStars[fi[2]]);
                            float scale2 = d_f13 / d_r13;
                            if (Math.Abs(scale - scale2) < 0.02f) {
                                for (int v = 0; v < 3; v++)
                                    votes[(ri[v], fi[v])] = votes.GetValueOrDefault((ri[v], fi[v])) + 1;
                                break;
                            }
                        }
                    }
                }
            }

            var pairs = new List<(StarPosition refStar, StarPosition frameStar)>();
            var usedRef = new HashSet<int>();
            var usedFrame = new HashSet<int>();

            foreach (var kv in votes.OrderByDescending(x => x.Value)) {
                if (kv.Value < 2) break;
                int ri = kv.Key.refIdx; int fi = kv.Key.frameIdx;
                if (usedRef.Contains(ri) || usedFrame.Contains(fi)) continue;
                pairs.Add((refStars[ri], frameStars[fi]));
                usedRef.Add(ri); usedFrame.Add(fi);
                if (pairs.Count >= 15) break;
            }
            return pairs;
        }

        private static AffineTransform ComputeRigidTransform(
            List<(StarPosition refStar, StarPosition frameStar)> matches) {

            int n = matches.Count;
            double sumFx = 0, sumFy = 0, sumRx = 0, sumRy = 0;

            for (int i = 0; i < n; i++) {
                sumFx += matches[i].frameStar.X; sumFy += matches[i].frameStar.Y;
                sumRx += matches[i].refStar.X; sumRy += matches[i].refStar.Y;
            }

            double cFx = sumFx / n, cFy = sumFy / n;
            double cRx = sumRx / n, cRy = sumRy / n;

            double num1 = 0, num2 = 0, den = 0;
            for (int i = 0; i < n; i++) {
                double fx = matches[i].frameStar.X - cFx;
                double fy = matches[i].frameStar.Y - cFy;
                double rx = matches[i].refStar.X - cRx;
                double ry = matches[i].refStar.Y - cRy;
                num1 += rx * fx + ry * fy;
                num2 += ry * fx - rx * fy;
                den += fx * fx + fy * fy;
            }

            if (den < 1e-10) return new AffineTransform(0, 0, 1, 0);

            float cos = (float)(num1 / den);
            float sin = (float)(num2 / den);
            float mag = (float)Math.Sqrt(cos * cos + sin * sin);
            if (mag > 0.01f) { cos /= mag; sin /= mag; }

            float tx = (float)(cRx - cos * cFx + sin * cFy);
            float ty = (float)(cRy - sin * cFx - cos * cFy);

            return new AffineTransform(tx, ty, cos, sin);
        }

        private static float Dist(StarPosition a, StarPosition b) {
            float dx = a.X - b.X; float dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }

    public class AlignResult {
        public bool IsFlipped { get; set; }
        public AffineTransform FineTransform { get; set; }
        public int MatchCount { get; set; }
    }

    public class AffineTransform {
        public float Tx { get; } public float Ty { get; }
        public float CosTheta { get; } public float SinTheta { get; }
        public float RotationDeg => (float)(Math.Atan2(SinTheta, CosTheta) * 180.0 / Math.PI);
        public AffineTransform(float tx, float ty, float cosTheta, float sinTheta) {
            Tx = tx; Ty = ty; CosTheta = cosTheta; SinTheta = sinTheta;
        }
        public static AffineTransform Identity => new AffineTransform(0, 0, 1, 0);
    }
}
