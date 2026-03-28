using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;

namespace NinaLiveStack.Engine {

    /// <summary>
    /// Plate-solve alignment using NINA's own APIs:
    /// - IImageDataFactory.CreateFromFile() loads a FITS with full metadata
    /// - IPlateSolverFactory.GetPlateSolver().SolveAsync() solves via user's configured solver (PS3)
    /// 
    /// This is how NINA itself plate-solves. No CLI, no process shelling, no manual headers.
    /// CreateFromFile returns IImageData with fully populated metadata, so SaveToDisk works.
    /// </summary>
    public class PlateSolveAligner {

        private readonly IPlateSolverFactory _factory;
        private readonly IProfileService _profileService;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly IImageDataFactory _imageDataFactory;

        private WcsSolution _refWcs;

        // RA/DEC hint from FITS headers (used when telescope is disconnected)
        private double _fitsHintRaHours = double.NaN;
        private double _fitsHintDecDeg = double.NaN;
        private double _fitsFocalLength = double.NaN;

        public bool HasReference => _refWcs != null;

        public PlateSolveAligner(IPlateSolverFactory factory,
            IProfileService profileService, ITelescopeMediator telescopeMediator,
            IImageDataFactory imageDataFactory) {
            _factory = factory;
            _profileService = profileService;
            _telescopeMediator = telescopeMediator;
            _imageDataFactory = imageDataFactory;
        }

        public void SetFitsHint(double raHours, double decDeg) {
            _fitsHintRaHours = raHours;
            _fitsHintDecDeg = decDeg;
            Logger.Info($"PlateSolveAligner: FITS hint: RA={raHours:F4}h DEC={decDeg:F4}°");
        }

        public void SetFitsOptics(double focalLengthMm, double pixelSizeUm) {
            if (!double.IsNaN(focalLengthMm) && focalLengthMm > 0)
                _fitsFocalLength = focalLengthMm;
            Logger.Info($"PlateSolveAligner: FITS optics: FL={focalLengthMm:F0}mm pixel={pixelSizeUm:F2}µm");
        }

        public async Task<bool> SetReferenceFromFileAsync(string filePath, CancellationToken ct = default) {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) {
                Logger.Warning($"PlateSolveAligner: Reference file not found: {filePath}");
                return false;
            }
            try {
                _refWcs = await SolveFileAsync(filePath, "reference", ct);
                if (_refWcs != null) {
                    Logger.Info($"PlateSolveAligner: Reference solved: RA={_refWcs.CrVal1:F4} DEC={_refWcs.CrVal2:F4} " +
                        $"PA={_refWcs.PositionAngle:F1} scale={_refWcs.PixelScale:F2}\"/px");
                    return true;
                }
                Logger.Warning("PlateSolveAligner: Reference solve failed");
            } catch (Exception ex) {
                Logger.Warning($"PlateSolveAligner: Reference error: {ex.Message}");
            }
            return false;
        }

        public async Task<AffineTransform> SolveAndAlignFromFileAsync(string filePath, CancellationToken ct = default) {
            if (_refWcs == null) return null;
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return null;
            try {
                var frameWcs = await SolveFileAsync(filePath, "frame", ct);
                if (frameWcs == null) return null;
                return ComputeTransformFromWcs(_refWcs, frameWcs);
            } catch (Exception ex) {
                Logger.Warning($"PlateSolveAligner: Solve error: {ex.Message}");
                return null;
            }
        }

        public void ClearReference() {
            _refWcs = null;
            _fitsHintRaHours = double.NaN;
            _fitsHintDecDeg = double.NaN;
            _fitsFocalLength = double.NaN;
        }

        private async Task<WcsSolution> SolveFileAsync(string filePath, string tag, CancellationToken ct = default) {
            if (_factory == null || _imageDataFactory == null) {
                Logger.Warning("PlateSolveAligner: Factory not available");
                return null;
            }

            ct.ThrowIfCancellationRequested();

            // Load the FITS file through NINA's own factory — fully populated metadata
            IImageData imageData;
            try {
                imageData = await _imageDataFactory.CreateFromFile(
                    filePath, 16, false, NINA.Core.Enum.RawConverterEnum.FREEIMAGE, ct);
                Logger.Info($"PlateSolveAligner: Loaded {tag}: {imageData.Properties.Width}x{imageData.Properties.Height}");
            } catch (OperationCanceledException) { throw; }
            catch (Exception ex) {
                Logger.Warning($"PlateSolveAligner: Failed to load FITS: {ex.Message}");
                return null;
            }

            ct.ThrowIfCancellationRequested();

            // Build coordinate hint
            Coordinates hint = null;
            try {
                if (_telescopeMediator != null) {
                    var info = _telescopeMediator.GetInfo();
                    if (info?.Connected == true && info.Coordinates != null)
                        hint = info.Coordinates;
                }
            } catch { }
            if (hint == null && !double.IsNaN(_fitsHintRaHours) && !double.IsNaN(_fitsHintDecDeg)) {
                hint = new Coordinates(_fitsHintRaHours, _fitsHintDecDeg, Epoch.J2000, Coordinates.RAType.Hours);
            }

            // Get solver from NINA's factory (uses user's PS3 configuration)
            var plateSolveSettings = _profileService.ActiveProfile.PlateSolveSettings;
            var solver = _factory.GetPlateSolver(plateSolveSettings);

            double focalLength = !double.IsNaN(_fitsFocalLength) ? _fitsFocalLength
                : _profileService.ActiveProfile.TelescopeSettings.FocalLength;

            var parameter = new PlateSolveParameter {
                FocalLength = focalLength,
                Binning = 1,
                Coordinates = hint,
                SearchRadius = 30,
                BlindFailoverEnabled = true,
                DisableNotifications = true
            };

            Logger.Info($"PlateSolveAligner: Solving {tag} via {solver.GetType().Name} FL={focalLength}mm " +
                $"hint={hint?.RA:F4}h/{hint?.Dec:F4}°");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Link external cancellation (from Stop button) with 60s timeout
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var progress = new Progress<ApplicationStatus>();

            PlateSolveResult result;
            try {
                result = await solver.SolveAsync(imageData, parameter, progress, cts.Token);
            } catch (OperationCanceledException) {
                Logger.Info($"PlateSolveAligner: {tag} cancelled/timed out");
                return null;
            } catch (Exception ex) {
                Logger.Warning($"PlateSolveAligner: {tag} solve error: {ex.Message}");
                return null;
            }
            sw.Stop();

            if (result == null || !result.Success) {
                Logger.Warning($"PlateSolveAligner: {tag} failed ({sw.ElapsedMilliseconds}ms)");
                return null;
            }

            Logger.Info($"PlateSolveAligner: {tag} solved in {sw.ElapsedMilliseconds}ms: " +
                $"RA={result.Coordinates.RA:F4}h DEC={result.Coordinates.Dec:F4}° " +
                $"PA={result.PositionAngle:F1}° scale={result.Pixscale:F2}\"/px");

            // Convert to WcsSolution
            double raDeg = result.Coordinates.RA * 15.0;
            double decDeg = result.Coordinates.Dec;
            double pa = result.PositionAngle;
            double scale = result.Pixscale / 3600.0; // arcsec/px → deg/px
            double paRad = pa * Math.PI / 180.0;

            return new WcsSolution {
                CrVal1 = raDeg,
                CrVal2 = decDeg,
                CrPix1 = imageData.Properties.Width / 2.0,
                CrPix2 = imageData.Properties.Height / 2.0,
                Cd11 = -scale * Math.Cos(paRad),
                Cd12 = scale * Math.Sin(paRad),
                Cd21 = scale * Math.Sin(paRad),
                Cd22 = scale * Math.Cos(paRad),
                PixelScale = result.Pixscale,
                PositionAngle = pa
            };
        }

        private AffineTransform ComputeTransformFromWcs(WcsSolution refWcs, WcsSolution frameWcs) {
            double dRa = frameWcs.CrVal1 - refWcs.CrVal1;
            double dDec = frameWcs.CrVal2 - refWcs.CrVal2;
            double det = refWcs.Cd11 * refWcs.Cd22 - refWcs.Cd12 * refWcs.Cd21;
            if (Math.Abs(det) < 1e-20) return null;

            double cosDec = Math.Cos(refWcs.CrVal2 * Math.PI / 180.0);
            double dRaDeg = dRa * cosDec;
            double invCd11 = refWcs.Cd22 / det;
            double invCd12 = -refWcs.Cd12 / det;
            double invCd21 = -refWcs.Cd21 / det;
            double invCd22 = refWcs.Cd11 / det;

            double dpx = invCd11 * dRaDeg + invCd12 * dDec;
            double dpy = invCd21 * dRaDeg + invCd22 * dDec;
            float tx = (float)(dpx + (refWcs.CrPix1 - frameWcs.CrPix1));
            float ty = (float)(dpy + (refWcs.CrPix2 - frameWcs.CrPix2));

            double rotDeg = frameWcs.PositionAngle - refWcs.PositionAngle;
            double rotRad = rotDeg * Math.PI / 180.0;

            Logger.Info($"PlateSolveAligner: dx={tx:F1} dy={ty:F1} rot={rotDeg:F1}");
            return new AffineTransform(tx, ty, (float)Math.Cos(rotRad), (float)Math.Sin(rotRad));
        }
    }

    public class WcsSolution {
        public double CrVal1, CrVal2, CrPix1, CrPix2;
        public double Cd11, Cd12, Cd21, Cd22;
        public double PixelScale, PositionAngle;
    }
}
