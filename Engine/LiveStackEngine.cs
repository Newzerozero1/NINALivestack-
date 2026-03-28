using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;

namespace NinaLiveStack.Engine {

    public class LiveStackEngine {

        private readonly object _stackLock = new object();
        private float[] _accumulatorR, _accumulatorG, _accumulatorB;
        private int _width, _height;

        private readonly FrameAligner _aligner = new FrameAligner();
        private PlateSolveAligner _plateSolveAligner;
        public bool AlignmentEnabled { get; set; } = true;

        // Calibration frames
        private ushort[] _darkR, _darkG, _darkB;
        private float[] _flatR, _flatG, _flatB;
        public bool HasDark => _darkR != null;
        public bool HasFlat => _flatR != null;

        // Hot pixel rejection (fallback when no dark loaded)
        public bool EnableHotPixelFilter { get; set; } = true;
        public float HotPixelSigma { get; set; } = 3.0f;

        // Satellite trail masking
        public bool EnableTrailMask { get; set; } = true;

        // Noise / SNR tracking
        public NoiseTracker NoiseTracker { get; } = new NoiseTracker();

        // Frame quality tracking
        private readonly List<FrameInfo> _frames = new List<FrameInfo>();
        public IReadOnlyList<FrameInfo> Frames => _frames;
        public int BadFrameCount => _frames.Count(f => f.IsBad);

        // File paths for rebuild
        private readonly List<string> _filePaths = new List<string>();
        public bool CanRebuild => _filePaths.Count > 0;

        // Transform validation
        private AffineTransform _lastGoodNormal = null;
        private AffineTransform _lastGoodFlipped = null;
        private const float MAX_JUMP_PIXELS = 50f;

        public int FrameCount { get; private set; }
        public int AlignedCount { get; private set; }
        public int FailedAlignCount { get; private set; }
        public bool IsRunning { get; private set; }
        public string LastAlignStatus { get; private set; } = "";

        /// <summary>
        /// Status of plate solver availability — displayed in the UI so users know
        /// whether plate solving is active or falling back to triangle matching only.
        /// </summary>
        public string PlateSolverStatus {
            get {
                if (_plateSolveAligner == null)
                    return "Plate solver not configured — alignment uses triangle matching only";
                return "";
            }
        }

        public event EventHandler<StackUpdatedEventArgs> StackUpdated;
        private bool _suppressEvents = false;

        // Running quality baselines
        private readonly List<float> _elongationHistory = new List<float>();
        private readonly List<int> _starCountHistory = new List<int>();

        private void FireStackUpdatedIfNotSuppressed() {
            if (!_suppressEvents)
                StackUpdated?.Invoke(this, new StackUpdatedEventArgs(FrameCount));
        }

        public LiveStackEngine() { FrameCount = 0; IsRunning = false; }

        private CancellationTokenSource _solveCts;

        public void Start() { lock (_stackLock) { Reset(); IsRunning = true; _solveCts = new CancellationTokenSource(); } }
        public void Stop() {
            IsRunning = false;  // No lock — must be immediate
            _solveCts?.Cancel();
        }

        // ================================================================
        // PLATE SOLVER CONFIGURATION
        // ================================================================

        public void ConfigurePlateSolver(IPlateSolverFactory factory,
            IProfileService profileService, ITelescopeMediator telescopeMediator,
            IImageDataFactory imageDataFactory = null) {
            _plateSolveAligner = new PlateSolveAligner(factory, profileService,
                telescopeMediator, imageDataFactory);
            Logger.Info("LiveStack: Plate solver configured");
        }

        /// <summary>
        /// Set RA/DEC hint from FITS header for plate solving when telescope is disconnected.
        /// </summary>
        public void SetPlateSolveHint(double raHours, double decDeg) {
            _plateSolveAligner?.SetFitsHint(raHours, decDeg);
        }

        /// <summary>
        /// Set optics from FITS header for plate solving (overrides NINA profile).
        /// </summary>
        public void SetPlateSolveOptics(double focalLengthMm, double pixelSizeUm) {
            _plateSolveAligner?.SetFitsOptics(focalLengthMm, pixelSizeUm);
        }

        // ================================================================
        // CALIBRATION FRAME LOADING
        // ================================================================

        public void LoadMasterDark(string fitsPath) {
            try {
                var dark = FitsReader.ReadFits(fitsPath);
                _darkR = dark.R;
                _darkG = dark.G;
                _darkB = dark.B;
                Logger.Info($"LiveStack: Master dark loaded ({dark.Width}x{dark.Height})");
            } catch (Exception ex) {
                Logger.Error($"LiveStack: Failed to load master dark: {ex.Message}");
                _darkR = _darkG = _darkB = null;
            }
        }

        public void LoadMasterFlat(string fitsPath) {
            try {
                var flat = FitsReader.ReadFits(fitsPath);
                int len = flat.R.Length;
                _flatR = new float[len];
                _flatG = new float[len];
                _flatB = new float[len];

                double sumR = 0, sumG = 0, sumB = 0;
                for (int i = 0; i < len; i++) {
                    sumR += flat.R[i]; sumG += flat.G[i]; sumB += flat.B[i];
                }
                float meanR = (float)(sumR / len);
                float meanG = (float)(sumG / len);
                float meanB = (float)(sumB / len);

                for (int i = 0; i < len; i++) {
                    _flatR[i] = Math.Max(0.1f, flat.R[i] / meanR);
                    _flatG[i] = Math.Max(0.1f, flat.G[i] / meanG);
                    _flatB[i] = Math.Max(0.1f, flat.B[i] / meanB);
                }
                Logger.Info($"LiveStack: Master flat loaded ({flat.Width}x{flat.Height}), " +
                    $"means R={meanR:F0} G={meanG:F0} B={meanB:F0}");
            } catch (Exception ex) {
                Logger.Error($"LiveStack: Failed to load master flat: {ex.Message}");
                _flatR = _flatG = _flatB = null;
            }
        }

        public void ClearDark() { _darkR = _darkG = _darkB = null; }
        public void ClearFlat() { _flatR = _flatG = _flatB = null; }

        // ================================================================
        // CALIBRATION APPLICATION
        // ================================================================

        private void CalibrateFrame(ushort[] r, ushort[] g, ushort[] b, int width, int height) {
            int len = width * height;

            if (_darkR != null && _darkR.Length == len) {
                for (int i = 0; i < len; i++) {
                    r[i] = (ushort)Math.Max(0, r[i] - _darkR[i]);
                    g[i] = (ushort)Math.Max(0, g[i] - _darkG[i]);
                    b[i] = (ushort)Math.Max(0, b[i] - _darkB[i]);
                }
            }

            if (_flatR != null && _flatR.Length == len) {
                for (int i = 0; i < len; i++) {
                    r[i] = (ushort)Math.Min(65535, (int)(r[i] / _flatR[i]));
                    g[i] = (ushort)Math.Min(65535, (int)(g[i] / _flatG[i]));
                    b[i] = (ushort)Math.Min(65535, (int)(b[i] / _flatB[i]));
                }
            }
        }

        // ================================================================
        // RESET
        // ================================================================

        public void Reset() {
            IsRunning = false;  // Immediate — no lock
            _solveCts?.Cancel();
            lock (_stackLock) {
                _accumulatorR = _accumulatorG = _accumulatorB = null;
                _width = _height = 0;
                FrameCount = 0; AlignedCount = 0; FailedAlignCount = 0;
                LastAlignStatus = "";
                _lastGoodNormal = null; _lastGoodFlipped = null;
                _aligner.ClearReference();
                _plateSolveAligner?.ClearReference();
                NoiseTracker.Reset();
                _frames.Clear();
                _filePaths.Clear();
                _elongationHistory.Clear();
                _starCountHistory.Clear();
            }
        }

        // ================================================================
        // FRAME ADDITION
        // ================================================================

        public bool AddFrame(ushort[] r, ushort[] g, ushort[] b, int width, int height) {
            if (!IsRunning) return false;
            lock (_stackLock) {
                if (!IsRunning) return false;
                CalibrateFrame(r, g, b, width, height);
                if (AlignmentEnabled) AddFrameAligned(r, g, b, width, height);
                else { AccumulateRaw(r, g, b, width, height); FrameCount++; TrackNoise(); }
            }
            FireStackUpdatedIfNotSuppressed();
            return true;
        }

        public bool AddMonoFrame(ushort[] data, int width, int height) {
            return AddFrame(data, data, data, width, height);
        }

        public bool AddRGBFrameFromFile(ushort[] r, ushort[] g, ushort[] b,
            int width, int height, string filePath = null) {
            lock (_stackLock) {
                if (filePath != null && !_filePaths.Contains(filePath))
                    _filePaths.Add(filePath);
                CalibrateFrame(r, g, b, width, height);
                if (AlignmentEnabled) AddFrameAligned(r, g, b, width, height, filePath);
                else { AccumulateRaw(r, g, b, width, height); FrameCount++; TrackNoise(); }
                return true;
            }
        }

        public void FireStackUpdated() {
            StackUpdated?.Invoke(this, new StackUpdatedEventArgs(FrameCount));
        }

        // ================================================================
        // QUALITY GATING
        // ================================================================

        public int AutoDetectBadFrames() {
            lock (_stackLock) {
                if (_frames.Count < 3) return 0;

                // Don't flag align-failed frames — they're already excluded from the stack.
                // Only detect quality outliers among frames that DID make it into the stack.

                var starCounts = _frames.Where(f => !f.AlignFailed).Select(f => (float)f.StarCount).ToArray();
                if (starCounts.Length > 3) {
                    Array.Sort(starCounts);
                    float median = starCounts[starCounts.Length / 2];
                    float[] devs = starCounts.Select(s => Math.Abs(s - median)).ToArray();
                    Array.Sort(devs);
                    float mad = devs[devs.Length / 2] * 1.4826f;
                    if (mad < 1f) mad = 1f;
                    float lowThresh = median - 3f * mad;

                    foreach (var f in _frames) {
                        if (!f.AlignFailed && f.StarCount < lowThresh && f.StarCount < median * 0.5f) {
                            f.IsBad = true; f.BadReason = $"low stars ({f.StarCount})";
                        }
                    }
                }

                var elongations = _frames.Where(f => !f.IsBad && !f.AlignFailed)
                    .Select(f => f.Elongation).ToArray();
                if (elongations.Length > 3) {
                    Array.Sort(elongations);
                    float medElong = elongations[elongations.Length / 2];
                    float[] eDevs = elongations.Select(e => Math.Abs(e - medElong)).ToArray();
                    Array.Sort(eDevs);
                    float eMad = eDevs[eDevs.Length / 2] * 1.4826f;
                    if (eMad < 0.05f) eMad = 0.05f;
                    float highThresh = medElong + 3f * eMad;
                    if (highThresh > 2.0f) highThresh = 2.0f;

                    foreach (var f in _frames) {
                        if (!f.IsBad && !f.AlignFailed && f.Elongation > highThresh) {
                            f.IsBad = true;
                            f.BadReason = $"elongated stars (e={f.Elongation:F2}, thresh={highThresh:F2})";
                        }
                    }
                    Logger.Info($"LiveStack: Elongation stats: median={medElong:F2} MAD={eMad:F2} thresh={highThresh:F2}");
                }

                int bad = _frames.Count(f => f.IsBad);
                if (bad > 0)
                    Logger.Info($"LiveStack: Auto-detected {bad} bad frames out of {_frames.Count}");
                return bad;
            }
        }

        // ================================================================
        // REBUILD
        // ================================================================

        public (int loaded, int skipped) RebuildFromFiles(Action<int, int> onProgress = null) {
            List<string> savedPaths;
            List<FrameInfo> savedFrames;
            HashSet<int> badIndices;

            lock (_stackLock) {
                if (_filePaths.Count == 0) return (0, 0);
                _suppressEvents = true;

                badIndices = new HashSet<int>();
                for (int i = 0; i < _frames.Count && i < _filePaths.Count; i++) {
                    if (_frames[i].IsBad) badIndices.Add(i);
                }

                savedPaths = new List<string>(_filePaths);
                savedFrames = new List<FrameInfo>(_frames);

                _accumulatorR = _accumulatorG = _accumulatorB = null;
                _width = _height = 0;
                FrameCount = 0; AlignedCount = 0; FailedAlignCount = 0;
                _lastGoodNormal = null; _lastGoodFlipped = null;
                _aligner.ClearReference();
                _plateSolveAligner?.ClearReference();
                NoiseTracker.Reset();
                _elongationHistory.Clear();
                _starCountHistory.Clear();
                _frames.Clear();
                _filePaths.Clear();
                _filePaths.AddRange(savedPaths);
            }

            int loaded = 0, skipped = 0;
            int total = savedPaths.Count - badIndices.Count;
            try {
                for (int i = 0; i < savedPaths.Count; i++) {
                    if (badIndices.Contains(i)) {
                        skipped++;
                        lock (_stackLock) {
                            if (i < savedFrames.Count) _frames.Add(savedFrames[i]);
                        }
                        continue;
                    }

                    try {
                        var fits = FitsReader.ReadFits(savedPaths[i]);
                        lock (_stackLock) {
                            CalibrateFrame(fits.R, fits.G, fits.B, fits.Width, fits.Height);
                            if (AlignmentEnabled) AddFrameAligned(fits.R, fits.G, fits.B, fits.Width, fits.Height, savedPaths[i]);
                            else { AccumulateRaw(fits.R, fits.G, fits.B, fits.Width, fits.Height); FrameCount++; TrackNoise(); }
                        }
                        loaded++;
                        onProgress?.Invoke(loaded, total);
                    } catch (Exception ex) {
                        Logger.Warning($"LiveStack: Rebuild failed for {savedPaths[i]}: {ex.Message}");
                        skipped++;
                    }
                }
            } finally {
                _suppressEvents = false;
            }
            return (loaded, skipped);
        }

        // ================================================================
        // CORE ALIGNMENT PIPELINE
        // ================================================================

        private void AddFrameAligned(ushort[] r, ushort[] g, ushort[] b, int width, int height, string filePath = null) {
            int pixelCount = width * height;

            float[] lum = new float[pixelCount];
            float invMax = 1f / 65535f;
            for (int i = 0; i < pixelCount; i++)
                lum[i] = (0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i]) * invMax;

            var stars = StarDetector.DetectStars(lum, width, height, 40);
            float elongation = StarDetector.MedianElongation(stars);

            // First frame: set as reference
            if (!_aligner.HasReference) {
                if (stars.Count >= 3) _aligner.SetReference(stars, width, height);
                _plateSolveAligner?.SetReferenceFromFileAsync(filePath, _solveCts?.Token ?? CancellationToken.None).Wait();
                AccumulateRaw(r, g, b, width, height);
                FrameCount++; AlignedCount++; TrackNoise();
                _elongationHistory.Add(elongation);
                _starCountHistory.Add(stars.Count);
                RecordFrameInfo(stars.Count, 0, 0, 0, false, false, elongation);
                LastAlignStatus = $"Reference frame ({stars.Count} stars, e={elongation:F2})";
                FireStackUpdatedIfNotSuppressed();
                return;
            }

            // Per-frame quality gate
            string rejectReason = CheckFrameQuality(stars.Count, elongation);
            if (rejectReason != null) {
                Logger.Warning($"LiveStack: Frame {FrameCount + 1} REJECTED: {rejectReason}");
                FailedAlignCount++;
                RecordFrameInfo(stars.Count, 0, 0, 0, false, true, elongation);
                LastAlignStatus = $"Frame {FrameCount + 1}: REJECTED — {rejectReason}";
                FireStackUpdatedIfNotSuppressed();
                return;
            }

            _elongationHistory.Add(elongation);
            _starCountHistory.Add(stars.Count);

            // Try triangle matching
            AlignResult result = null;
            bool triangleCompletelyFailed = false;
            if (stars.Count >= 3)
                result = _aligner.ComputeAlignment(stars, width, height);
            if (result == null)
                triangleCompletelyFailed = true;

            ushort[] rFinal, gFinal, bFinal;
            AffineTransform transformToUse = null;
            bool isFlipped = false;
            bool usedStale = false;

            if (result != null) {
                if (result.IsFlipped) {
                    isFlipped = true;
                    if (IsTransformConsistent(result.FineTransform, _lastGoodFlipped, true)) {
                        _lastGoodFlipped = result.FineTransform;
                        transformToUse = result.FineTransform;
                        Logger.Info($"LiveStack: Frame {FrameCount + 1} flip OK ({result.MatchCount} stars, " +
                            $"dx={transformToUse.Tx:F1} dy={transformToUse.Ty:F1} e={elongation:F2})");
                    }
                } else {
                    if (IsTransformConsistent(result.FineTransform, _lastGoodNormal, false)) {
                        _lastGoodNormal = result.FineTransform;
                        transformToUse = result.FineTransform;
                    } else if (_lastGoodNormal == null) {
                        _lastGoodNormal = result.FineTransform;
                        transformToUse = result.FineTransform;
                    }
                }
            }

            // PLATE SOLVE FALLBACK: triangle matching failed, try plate solving
            if (transformToUse == null && _plateSolveAligner != null && _plateSolveAligner.HasReference) {
                Logger.Info($"LiveStack: Frame {FrameCount + 1} triangle match failed, trying plate solve...");
                LastAlignStatus = $"Frame {FrameCount + 1}: plate solving...";
                try {
                    var psTransform = _plateSolveAligner.SolveAndAlignFromFileAsync(filePath, _solveCts?.Token ?? CancellationToken.None).Result;
                    if (psTransform != null) {
                        transformToUse = psTransform;
                        float absRot = Math.Abs(psTransform.RotationDeg);
                        if (absRot > 150f) isFlipped = true;
                        Logger.Info($"LiveStack: Frame {FrameCount + 1} plate solved: " +
                            $"dx={transformToUse.Tx:F1} dy={transformToUse.Ty:F1} rot={transformToUse.RotationDeg:F1}°");
                    }
                } catch (Exception ex) {
                    Logger.Warning($"LiveStack: Plate solve failed: {ex.Message}");
                }
            }

            // Last resort: use last good ONLY if triangle matched but was rejected
            if (transformToUse == null) {
                if (triangleCompletelyFailed) {
                    Logger.Warning($"LiveStack: Frame {FrameCount + 1} no triangle matches — SKIPPING");
                    FailedAlignCount++;
                    RecordFrameInfo(stars.Count, 0, 0, 0, false, true, elongation);
                    LastAlignStatus = $"Frame {FrameCount + 1}: no matches — SKIPPED";
                    FireStackUpdatedIfNotSuppressed();
                    return;
                }

                bool frameIsFlipped = (result != null && result.IsFlipped);
                if (frameIsFlipped && _lastGoodFlipped != null) {
                    isFlipped = true;
                    transformToUse = _lastGoodFlipped;
                    usedStale = true;
                } else if (frameIsFlipped) {
                    FailedAlignCount++;
                    RecordFrameInfo(stars.Count, 0, 0, 0, true, true, elongation);
                    LastAlignStatus = $"Frame {FrameCount + 1}: flip, no ref — SKIPPED";
                    FireStackUpdatedIfNotSuppressed();
                    return;
                } else if (_lastGoodNormal != null) {
                    transformToUse = _lastGoodNormal;
                    usedStale = true;
                } else {
                    FailedAlignCount++;
                    RecordFrameInfo(stars.Count, 0, 0, 0, false, true, elongation);
                    LastAlignStatus = $"Frame {FrameCount + 1}: all align failed — SKIPPED";
                    FireStackUpdatedIfNotSuppressed();
                    return;
                }
            }

            // Apply transform
            if (isFlipped) {
                ushort[] rRot = FrameAligner.Rotate180(r, width, height);
                ushort[] gRot = FrameAligner.Rotate180(g, width, height);
                ushort[] bRot = FrameAligner.Rotate180(b, width, height);
                rFinal = FrameAligner.WarpImageUShort(rRot, width, height, transformToUse);
                gFinal = FrameAligner.WarpImageUShort(gRot, width, height, transformToUse);
                bFinal = FrameAligner.WarpImageUShort(bRot, width, height, transformToUse);
            } else {
                rFinal = FrameAligner.WarpImageUShort(r, width, height, transformToUse);
                gFinal = FrameAligner.WarpImageUShort(g, width, height, transformToUse);
                bFinal = FrameAligner.WarpImageUShort(b, width, height, transformToUse);
            }

            // Satellite trail masking
            if (EnableTrailMask && FrameCount >= 5) {
                MaskSatelliteTrails(rFinal, gFinal, bFinal, stars, width, height);
            }

            string method = usedStale ? "STALE" : (isFlipped ? "FLIP" : "tri");
            if (_plateSolveAligner != null && !usedStale && triangleCompletelyFailed) method = "PS3";
            RecordFrameInfo(stars.Count, transformToUse.Tx, transformToUse.Ty,
                transformToUse.RotationDeg, isFlipped, false, elongation, usedStale);
            LastAlignStatus = $"Frame {FrameCount + 1}: [{method}] dx={transformToUse.Tx:F1} dy={transformToUse.Ty:F1} e={elongation:F2}";
            Logger.Info($"LiveStack: Frame {FrameCount + 1} [{method}] dx={transformToUse.Tx:F1} dy={transformToUse.Ty:F1} " +
                $"rot={transformToUse.RotationDeg:F1}° e={elongation:F2}");

            AccumulateRaw(rFinal, gFinal, bFinal, width, height);
            FrameCount++; AlignedCount++; TrackNoise();
            FireStackUpdatedIfNotSuppressed();
        }

        // ================================================================
        // SATELLITE TRAIL MASKING
        // ================================================================

        private void MaskSatelliteTrails(ushort[] r, ushort[] g, ushort[] b,
            List<StarPosition> stars, int width, int height) {
            if (_accumulatorR == null || FrameCount < 5) return;

            int len = width * height;
            float invCount = 1f / FrameCount;
            float invMax = 1f / 65535f;

            int step = Math.Max(1, len / 20000);
            int sc = 0;
            float[] lumSamples = new float[(len + step - 1) / step];
            for (int i = 0; i < len && sc < lumSamples.Length; i += step) {
                float meanLum = (_accumulatorR[i] * 0.2126f + _accumulatorG[i] * 0.7152f + _accumulatorB[i] * 0.0722f) * invCount * invMax;
                lumSamples[sc++] = meanLum;
            }
            Array.Sort(lumSamples, 0, sc);
            float stackMedian = lumSamples[sc / 2];
            float[] absDevs = new float[sc];
            for (int i = 0; i < sc; i++)
                absDevs[i] = Math.Abs(lumSamples[i] - stackMedian);
            Array.Sort(absDevs, 0, sc);
            float sigma = absDevs[sc / 2] * 1.4826f;
            if (sigma < 0.001f) sigma = 0.001f;

            float threshold = 15f * sigma * 65535f; // high threshold — only catch actual satellite trails
            int masked = 0;

            for (int i = 0; i < len; i++) {
                float meanR = _accumulatorR[i] * invCount;
                float meanG = _accumulatorG[i] * invCount;
                float meanB = _accumulatorB[i] * invCount;

                float deltaR = r[i] - meanR;
                float deltaG = g[i] - meanG;
                float deltaB = b[i] - meanB;
                float deltaMax = Math.Max(deltaR, Math.Max(deltaG, deltaB));

                if (deltaMax > threshold) {
                    int px = i % width;
                    int py = i / width;
                    bool nearStar = false;
                    foreach (var s in stars) {
                        float dx = px - s.X;
                        float dy = py - s.Y;
                        if (dx * dx + dy * dy < 225f) {
                            nearStar = true;
                            break;
                        }
                    }

                    if (!nearStar) {
                        r[i] = (ushort)Math.Max(0, Math.Min(65535, meanR));
                        g[i] = (ushort)Math.Max(0, Math.Min(65535, meanG));
                        b[i] = (ushort)Math.Max(0, Math.Min(65535, meanB));
                        masked++;
                    }
                }
            }

            if (masked > 0)
                Logger.Info($"LiveStack: Masked {masked} satellite trail pixels in frame {FrameCount + 1}");
        }

        // ================================================================
        // ACCUMULATION
        // ================================================================

        private void AccumulateRaw(ushort[] r, ushort[] g, ushort[] b, int width, int height) {
            int pixelCount = width * height;
            if (_accumulatorR == null) {
                _width = width; _height = height;
                _accumulatorR = new float[pixelCount];
                _accumulatorG = new float[pixelCount];
                _accumulatorB = new float[pixelCount];
            }
            if (width != _width || height != _height) return;

            if (EnableHotPixelFilter && !HasDark) {
                int cleaned = HotPixelFilter.FilterChannelFast(r, width, height)
                            + HotPixelFilter.FilterChannelFast(g, width, height)
                            + HotPixelFilter.FilterChannelFast(b, width, height);
                if (cleaned > 0)
                    Logger.Info($"LiveStack: Cleaned {cleaned} hot/cold pixels from frame {FrameCount + 1}");
            }

            for (int i = 0; i < pixelCount; i++) {
                _accumulatorR[i] += r[i];
                _accumulatorG[i] += g[i];
                _accumulatorB[i] += b[i];
            }
        }

        // ================================================================
        // STACK RESULT
        // ================================================================

        public StackResult GetStackedImageResult() {
            lock (_stackLock) { return GetStackedImageResult_Internal(); }
        }

        private StackResult GetStackedImageResult_Internal() {
            if (FrameCount == 0 || _accumulatorR == null) return null;
            int pixelCount = _width * _height;
            float invCount = 1.0f / FrameCount;
            float invMax = 1.0f / 65535.0f;
            float[] r = new float[pixelCount], g = new float[pixelCount], b = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++) {
                r[i] = _accumulatorR[i] * invCount * invMax;
                g[i] = _accumulatorG[i] * invCount * invMax;
                b[i] = _accumulatorB[i] * invCount * invMax;
            }
            return new StackResult(r, g, b, _width, _height);
        }

        public double GetMemoryUsageMB() {
            if (_accumulatorR == null) return 0;
            double accum = 3.0 * _accumulatorR.Length * sizeof(float);
            double dark = HasDark ? 3.0 * _darkR.Length * sizeof(ushort) : 0;
            double flat = HasFlat ? 3.0 * _flatR.Length * sizeof(float) : 0;
            return (accum + dark + flat) / (1024.0 * 1024.0);
        }

        /// <summary>
        /// Get a downsampled stack for display rendering.
        /// Factor 2 = half width/height (~6.5MP from 26MP).
        /// </summary>
        public StackResult GetDisplayResult(int factor = 2) {
            lock (_stackLock) {
                if (FrameCount == 0 || _accumulatorR == null) return null;
                int dw = _width / factor;
                int dh = _height / factor;
                int dpx = dw * dh;
                float invCount = 1.0f / FrameCount;
                float invMax = 1.0f / 65535.0f;

                float[] r = new float[dpx], g = new float[dpx], b = new float[dpx];
                for (int dy = 0; dy < dh; dy++) {
                    int sy = dy * factor;
                    for (int dx = 0; dx < dw; dx++) {
                        int sx = dx * factor;
                        float sumR = 0, sumG = 0, sumB = 0;
                        int count = 0;
                        for (int yy = 0; yy < factor && sy + yy < _height; yy++) {
                            for (int xx = 0; xx < factor && sx + xx < _width; xx++) {
                                int si = (sy + yy) * _width + (sx + xx);
                                sumR += _accumulatorR[si];
                                sumG += _accumulatorG[si];
                                sumB += _accumulatorB[si];
                                count++;
                            }
                        }
                        float inv = invCount * invMax / count;
                        int di = dy * dw + dx;
                        r[di] = sumR * inv;
                        g[di] = sumG * inv;
                        b[di] = sumB * inv;
                    }
                }
                return new StackResult(r, g, b, dw, dh);
            }
        }

        // ================================================================
        // HELPERS
        // ================================================================

        private void TrackNoise() {
            try {
                var result = GetStackedImageResult_Internal();
                if (result != null)
                    NoiseTracker.RecordFrame(result.G, _width, _height, FrameCount);
            } catch (Exception ex) {
                Logger.Warning($"LiveStack: Noise tracking failed: {ex.Message}");
            }
        }

        private void RecordFrameInfo(int starCount, float dx, float dy, float rot,
            bool isFlipped, bool alignFailed, float elongation = 1f, bool usedStale = false) {
            _frames.Add(new FrameInfo {
                Index = _frames.Count,
                StarCount = starCount,
                Dx = dx, Dy = dy, Rotation = rot,
                Elongation = elongation,
                IsFlipped = isFlipped,
                AlignFailed = alignFailed,
                UsedStaleTransform = usedStale,
                IsBad = false
            });
        }

        private bool IsTransformConsistent(AffineTransform newT, AffineTransform lastGood, bool isFlipped) {
            if (isFlipped) {
                float absRot = Math.Abs(newT.RotationDeg);
                if (absRot > 2f) return false;
                if (Math.Abs(newT.Tx) > 200f || Math.Abs(newT.Ty) > 200f) return false;
            }
            if (lastGood != null) {
                float dxJump = Math.Abs(newT.Tx - lastGood.Tx);
                float dyJump = Math.Abs(newT.Ty - lastGood.Ty);
                if (dxJump > MAX_JUMP_PIXELS || dyJump > MAX_JUMP_PIXELS) return false;
            }
            return true;
        }

        private string CheckFrameQuality(int starCount, float elongation) {
            if (_elongationHistory.Count < 5) return null;

            var scSorted = _starCountHistory.ToArray();
            Array.Sort(scSorted);
            int medianStars = scSorted[scSorted.Length / 2];
            if (starCount < medianStars * 0.5f && starCount < medianStars - 3)
                return $"low stars ({starCount} vs median {medianStars})";

            var eSorted = _elongationHistory.ToArray();
            Array.Sort(eSorted);
            float medE = eSorted[eSorted.Length / 2];
            float[] eDevs = new float[eSorted.Length];
            for (int i = 0; i < eSorted.Length; i++)
                eDevs[i] = Math.Abs(eSorted[i] - medE);
            Array.Sort(eDevs);
            float eMad = eDevs[eDevs.Length / 2] * 1.4826f;
            if (eMad < 0.05f) eMad = 0.05f;
            float eThresh = medE + 3f * eMad;
            if (eThresh > 2.0f) eThresh = 2.0f;
            if (elongation > eThresh)
                return $"elongated (e={elongation:F2} thresh={eThresh:F2})";

            return null;
        }
    }

    public class FrameInfo {
        public int Index { get; set; }
        public int StarCount { get; set; }
        public float Dx { get; set; }
        public float Dy { get; set; }
        public float Rotation { get; set; }
        public float Elongation { get; set; }
        public bool IsFlipped { get; set; }
        public bool AlignFailed { get; set; }
        public bool UsedStaleTransform { get; set; }
        public bool IsBad { get; set; }
        public string BadReason { get; set; } = "";
    }

    public class StackUpdatedEventArgs : EventArgs {
        public int FrameCount { get; }
        public StackUpdatedEventArgs(int frameCount) { FrameCount = frameCount; }
    }

    public class StackResult {
        public float[] R { get; } public float[] G { get; } public float[] B { get; }
        public int Width { get; } public int Height { get; }
        public StackResult(float[] r, float[] g, float[] b, int w, int h) {
            R = r; G = g; B = b; Width = w; Height = h;
        }
    }
}
