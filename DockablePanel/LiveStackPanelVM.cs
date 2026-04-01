using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using NinaLiveStack.Engine;
using NinaLiveStack.FullScreen;

namespace NinaLiveStack.DockablePanel {

    [Export(typeof(IDockableVM))]
    public class LiveStackPanelVM : DockableVM {

        private readonly LiveStackEngine _engine;
        private FullScreenWindow _fullScreenWindow;
        private StretchParams _autoStretch;
        private WhiteBalance _whiteBalance;
        private StackResult _cachedDisplayResult;
        private int _cachedFrameCount = -1;
        private Timer _debounceTimer;
        private bool _suppressSlider = false;
        private CancellationTokenSource _loadCts;
        private PluginSettings _settings;

        public override bool IsTool => true;

        private BitmapSource _displayImage;
        public BitmapSource DisplayImage { get => _displayImage; set { _displayImage = value; RaisePropertyChanged(); } }

        private int _frameCount;
        public int FrameCount { get => _frameCount; set { _frameCount = value; RaisePropertyChanged(); } }

        private bool _isRunning;
        public bool IsRunning { get => _isRunning; set { _isRunning = value; RaisePropertyChanged(); } }

        private bool _alignmentEnabled = true;
        public bool AlignmentEnabled {
            get => _alignmentEnabled;
            set { _alignmentEnabled = value; _engine.AlignmentEnabled = value; RaisePropertyChanged(); }
        }

        private string _alignStatus = "";
        public string AlignStatus { get => _alignStatus; set { _alignStatus = value; RaisePropertyChanged(); } }

        private bool _useArcsinhStretch = false;
        public bool UseArcsinhStretch {
            get => _useArcsinhStretch;
            set { _useArcsinhStretch = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); }
        }

        private float _arcsinhFactor = 100f;
        public float ArcsinhFactor {
            get => _arcsinhFactor;
            set { _arcsinhFactor = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); }
        }

        private float _brightness;
        public float Brightness { get => _brightness; set { _brightness = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }

        private float _contrast;
        public float Contrast { get => _contrast; set { _contrast = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }

        private float _blackClip = 0f;
        public float BlackClip { get => _blackClip; set { _blackClip = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }

        private float _rBalance = 1.0f;
        public float RBalance { get => _rBalance; set { _rBalance = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }
        private float _gBalance = 1.0f;
        public float GBalance { get => _gBalance; set { _gBalance = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }
        private float _bBalance = 1.0f;
        public float BBalance { get => _bBalance; set { _bBalance = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }

        private float _starReduction = 0f;
        public float StarReduction {
            get => _starReduction;
            set {
                _starReduction = value; RaisePropertyChanged();
                if (!_suppressSlider) DebouncedRender();
            }
        }

        private float _sharpen = 0f;
        public float Sharpen {
            get => _sharpen;
            set { _sharpen = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); }
        }

        // Crop percentages — 0 to 0.30 (30% max from each edge)
        private float _cropLeft = 0f, _cropRight = 0f, _cropTop = 0f, _cropBottom = 0f;
        public float CropLeft { get => _cropLeft; set { _cropLeft = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }
        public float CropRight { get => _cropRight; set { _cropRight = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }
        public float CropTop { get => _cropTop; set { _cropTop = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }
        public float CropBottom { get => _cropBottom; set { _cropBottom = value; RaisePropertyChanged(); if (!_suppressSlider) DebouncedRender(); } }

        private bool HasCrop => _cropLeft > 0.001f || _cropRight > 0.001f || _cropTop > 0.001f || _cropBottom > 0.001f;

        // Crop aspect ratio lock — when locked, single slider drives all four edges equally
        private bool _cropLocked = true;
        public bool CropLocked {
            get => _cropLocked;
            set { _cropLocked = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(CropUnlocked)); }
        }
        public bool CropUnlocked => !_cropLocked;

        private float _cropAll = 0f;
        public float CropAll {
            get => _cropAll;
            set {
                _cropAll = value; RaisePropertyChanged();
                if (_cropLocked && !_suppressSlider) {
                    _suppressSlider = true;
                    CropLeft = value; CropRight = value; CropTop = value; CropBottom = value;
                    _suppressSlider = false;
                    DebouncedRender();
                }
            }
        }

        private bool _enableHotPixelFilter = true;
        public bool EnableHotPixelFilter {
            get => _enableHotPixelFilter;
            set { _enableHotPixelFilter = value; _engine.EnableHotPixelFilter = value; RaisePropertyChanged(); }
        }

        private bool _enableTrailMask = true;
        public bool EnableTrailMask {
            get => _enableTrailMask;
            set { _enableTrailMask = value; _engine.EnableTrailMask = value; RaisePropertyChanged(); }
        }

        private bool _enableBackgroundSub = false;
        public bool EnableBackgroundSub {
            get => _enableBackgroundSub;
            set { _enableBackgroundSub = value; RaisePropertyChanged(); if (!_suppressSlider) { InvalidateDisplayCache(); DebouncedRender(); } }
        }

        private bool _uploadEnabled = false;
        public bool UploadEnabled {
            get => _uploadEnabled;
            set {
                if (value && (_settings == null || !_settings.HasR2Credentials)) {
                    StatusText = "Broadcasting requires R2 credentials — click ⚙ to configure";
                    return;
                }
                _uploadEnabled = value;
                CloudUploader.Enabled = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(UploadStatus));
                // Turning ON immediately uploads the current image
                if (value && DisplayImage != null)
                    CloudUploader.UploadIfEnabled(DisplayImage, LiveStackPlugin.TargetName);
            }
        }

        public bool BroadcastAvailable => _settings != null && _settings.HasR2Credentials;

        public string UploadStatus {
            get {
                if (!_uploadEnabled) return "";
                if (_settings == null || !_settings.HasR2Credentials)
                    return "⚙ R2 not configured — click gear to set up";
                string err = CloudUploader.LastError;
                if (!string.IsNullOrEmpty(err))
                    return "❌ " + err;
                string url = _settings.ViewerUrl;
                if (!string.IsNullOrEmpty(url))
                    return "📡 Broadcasting to " + url;
                return "📡 Broadcasting enabled";
            }
        }

        private BitmapSource _histogramImage;
        public BitmapSource HistogramImage { get => _histogramImage; set { _histogramImage = value; RaisePropertyChanged(); } }

        private string _snrStatus = "";
        public string SNRStatus { get => _snrStatus; set { _snrStatus = value; RaisePropertyChanged(); } }
        public string PlateSolverStatus => _engine.PlateSolverStatus;
        private string _badFrameStatus = "";
        public string BadFrameStatus { get => _badFrameStatus; set { _badFrameStatus = value; RaisePropertyChanged(); } }
        private string _calibrationStatus = "No calibration";
        public string CalibrationStatus { get => _calibrationStatus; set { _calibrationStatus = value; RaisePropertyChanged(); } }

        private double _panelZoom = 1.0;
        public double PanelZoom { get => _panelZoom; set { _panelZoom = value; RaisePropertyChanged(); } }
        private double _memoryUsageMB;
        public double MemoryUsageMB { get => _memoryUsageMB; set { _memoryUsageMB = value; RaisePropertyChanged(); } }
        private string _statusText = "Stopped";
        public string StatusText { get => _statusText; set { _statusText = value; RaisePropertyChanged(); } }

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand FullScreenCommand { get; }
        public ICommand SaveStackCommand { get; }
        public ICommand LoadFilesCommand { get; }
        public ICommand ResetStretchCommand { get; }
        public ICommand RecomputeStretchCommand { get; }
        public ICommand RebuildCommand { get; }
        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ZoomFitCommand { get; }
        public ICommand CompareCommand { get; }

        private bool _compareMode = false;
        public bool CompareMode {
            get => _compareMode;
            set { _compareMode = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(CompareLabel)); DebouncedRender(); }
        }
        public string CompareLabel => _compareMode ? "Edited" : "Raw";
        public ICommand LoadDarkCommand { get; }
        public ICommand LoadFlatCommand { get; }
        public ICommand ClearCalibrationCommand { get; }
        public ICommand SettingsCommand { get; }

        public string BroadcastTooltip =>
            (_settings != null && _settings.HasR2Credentials)
                ? "Upload each stacked frame to Cloudflare R2 for live web viewing"
                : "Configure R2 credentials in Settings (⚙) to enable broadcasting";

        [ImportingConstructor]
        public LiveStackPanelVM(IProfileService profileService) : base(profileService) {
            Title = "Live Stack";
            _engine = LiveStackPlugin.StackEngine;

            // Load persisted settings and push to CloudUploader
            _settings = PluginSettings.Load();
            CloudUploader.Settings = _settings;

            // Apply saved defaults
            _alignmentEnabled = _settings.DefaultAlignEnabled;
            _enableHotPixelFilter = _settings.DefaultHotPixelFilter;
            _enableTrailMask = _settings.DefaultTrailMask;
            _useArcsinhStretch = _settings.DefaultArcsinhStretch;
            _arcsinhFactor = _settings.DefaultStretchFactor;

            _engine.AlignmentEnabled = _alignmentEnabled;
            _engine.EnableHotPixelFilter = _enableHotPixelFilter;
            _engine.EnableTrailMask = _enableTrailMask;

            StartCommand = new RelayCommand(_ => {
                _engine.Start(); IsRunning = true; FrameCount = 0;
                AlignStatus = ""; SNRStatus = ""; BadFrameStatus = "";
                StatusText = "Running - waiting for images...";
            });
            StopCommand = new RelayCommand(_ => {
                _engine.Stop(); IsRunning = false;
                _loadCts?.Cancel();
                StatusText = $"Stopped ({FrameCount} frames, {_engine.AlignedCount} aligned)";
            });
            ResetCommand = new RelayCommand(_ => {
                _engine.Reset(); FrameCount = 0; DisplayImage = null;
                MemoryUsageMB = 0; StatusText = "Stopped";
                AlignStatus = ""; SNRStatus = ""; BadFrameStatus = "";
                InvalidateAll();
                PanelZoom = 1.0;
                _loadCts?.Cancel();
                _suppressSlider = true;
                Brightness = 0; Contrast = 0; BlackClip = 0; RBalance = 1; GBalance = 1; BBalance = 1;
                StarReduction = 0; Sharpen = 0; ArcsinhFactor = 100;
                CropLeft = 0; CropRight = 0; CropTop = 0; CropBottom = 0; CropAll = 0;
                _suppressSlider = false;
                _fullScreenWindow?.UpdateImage(null, 0);
            });
            FullScreenCommand = new RelayCommand(_ => ToggleFullScreen());
            SaveStackCommand = new RelayCommand(_ => SaveCurrentStack());
            LoadFilesCommand = new RelayCommand(_ => LoadFiles());
            ResetStretchCommand = new RelayCommand(_ => {
                _suppressSlider = true;
                Brightness = 0; Contrast = 0; BlackClip = 0; RBalance = 1; GBalance = 1; BBalance = 1;
                StarReduction = 0; Sharpen = 0; ArcsinhFactor = 100;
                CropLeft = 0; CropRight = 0; CropTop = 0; CropBottom = 0; CropAll = 0;
                _suppressSlider = false;
                InvalidateAll();
                RenderNow();
            });
            RecomputeStretchCommand = new RelayCommand(_ => {
                InvalidateAll();
                RenderNow();
            });
            RebuildCommand = new RelayCommand(_ => RebuildWithoutBadFrames());
            ZoomInCommand = new RelayCommand(_ => PanelZoom = Math.Min(8.0, PanelZoom + 0.25));
            ZoomOutCommand = new RelayCommand(_ => PanelZoom = Math.Max(0.25, PanelZoom - 0.25));
            ZoomFitCommand = new RelayCommand(_ => PanelZoom = 1.0);
            CompareCommand = new RelayCommand(_ => CompareMode = !CompareMode);

            LoadDarkCommand = new RelayCommand(_ => LoadCalibrationFrame("dark"));
            LoadFlatCommand = new RelayCommand(_ => LoadCalibrationFrame("flat"));
            ClearCalibrationCommand = new RelayCommand(_ => {
                _engine.ClearDark(); _engine.ClearFlat();
                UpdateCalibrationStatus();
            });
            SettingsCommand = new RelayCommand(_ => OpenSettings());

            LiveStackPlugin.StackUpdated += OnStackUpdated;
            UpdateCalibrationStatus();
        }

        private void OpenSettings() {
            var win = new SettingsWindow(_settings);
            win.Owner = Application.Current.MainWindow;
            if (win.ShowDialog() == true) {
                CloudUploader.Settings = _settings;
                RaisePropertyChanged(nameof(BroadcastTooltip));
                RaisePropertyChanged(nameof(BroadcastAvailable));
                // If broadcast was on but credentials were removed, turn it off
                if (_uploadEnabled && !_settings.HasR2Credentials) {
                    _uploadEnabled = false;
                    CloudUploader.Enabled = false;
                    RaisePropertyChanged(nameof(UploadEnabled));
                    StatusText = "Broadcasting disabled — R2 credentials removed";
                }
                // If broadcast is on and we have credentials, re-upload immediately
                // (picks up new overlay text, font, and ensures viewer HTML uses correct PublicUrl)
                else if (_uploadEnabled && _settings.HasR2Credentials && DisplayImage != null) {
                    CloudUploader.UploadIfEnabled(DisplayImage, LiveStackPlugin.TargetName);
                    StatusText = "Settings saved — re-uploading with new settings...";
                }
            }
        }

        private void InvalidateAll() {
            _autoStretch = null; _whiteBalance = null;
            _cachedDisplayResult = null; _cachedFrameCount = -1;
        }

        private void InvalidateDisplayCache() {
            _cachedDisplayResult = null; _cachedFrameCount = -1;
        }

        // ================================================================
        // CALIBRATION
        // ================================================================

        private void LoadCalibrationFrame(string type) {
            var dialog = new Microsoft.Win32.OpenFileDialog {
                Title = $"Select master {type} FITS",
                Filter = "FITS files (*.fits;*.fit;*.fts)|*.fits;*.fit;*.fts",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;
            if (type == "dark") _engine.LoadMasterDark(dialog.FileName);
            else _engine.LoadMasterFlat(dialog.FileName);
            UpdateCalibrationStatus();
        }

        private void UpdateCalibrationStatus() {
            string dark = _engine.HasDark ? "Dark ✓" : "No dark";
            string flat = _engine.HasFlat ? "Flat ✓" : "No flat";
            CalibrationStatus = $"{dark} | {flat}";
        }

        // ================================================================
        // DISPLAY-RESOLUTION RENDER PIPELINE
        // ================================================================

        private void DebouncedRender() {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => RenderNow(), null, 150, Timeout.Infinite);
        }

        /// <summary>
        /// Get display-resolution WB'd data with star reduction and BG sub applied in linear space.
        /// Uses 2x downsample (~6.5MP from 26MP). Caches between frames.
        /// </summary>
        private StackResult GetDisplayWBResult() {
            int fc = _engine.FrameCount;
            if (fc == 0) return null;

            if (_cachedDisplayResult == null || _cachedFrameCount != fc) {
                var raw = _engine.GetDisplayResult(2);
                if (raw == null) return null;

                // WB
                _whiteBalance = ImageStretch.ComputeWhiteBalance(raw.R, raw.G, raw.B);
                ImageStretch.ApplyWhiteBalance(raw.R, raw.G, raw.B, _whiteBalance);

                // Background subtraction in linear space
                if (EnableBackgroundSub)
                    AdvancedStretch.SubtractBackground(raw.R, raw.G, raw.B);

                _autoStretch = null;
                _cachedDisplayResult = raw;
                _cachedFrameCount = fc;
            }

            // Clone for in-place stretching
            return new StackResult(
                (float[])_cachedDisplayResult.R.Clone(),
                (float[])_cachedDisplayResult.G.Clone(),
                (float[])_cachedDisplayResult.B.Clone(),
                _cachedDisplayResult.Width, _cachedDisplayResult.Height);
        }

        private BitmapSource RenderStack(StackResult result) {
            if (result == null) return null;

            if (UseArcsinhStretch) {
                AdvancedStretch.AutoArcsinh(result.R, result.G, result.B,
                    result.Width, result.Height, ArcsinhFactor,
                    brightness: Brightness, contrast: Contrast, bpSigma: 2.8f);
            } else {
                if (_autoStretch == null)
                    _autoStretch = ImageStretch.AutoStretchLinked(result.R, result.G, result.B);
                ImageStretch.ApplyMTFInPlace(result.R, result.G, result.B,
                    _autoStretch, Brightness, Contrast);
            }

            // Skip processing in compare mode — shows stretch only
            if (!CompareMode) {
                // Morphological star reduction — works directly on stretched data
                if (StarReduction > 0.01f)
                    AdvancedStretch.MorphologicalStarReduce(result.R, result.G, result.B,
                        result.Width, result.Height, StarReduction);

                // Wavelet sharpening AFTER stretch and star reduction
                if (Sharpen > 0.01f)
                    AdvancedStretch.WaveletSharpen(result.R, result.G, result.B,
                        result.Width, result.Height, Sharpen);
            }

            var bmp = ImageStretch.FloatToBitmap(result.R, result.G, result.B,
                result.Width, result.Height, RBalance, GBalance, BBalance, BlackClip);

            // Apply crop if any edges are trimmed
            if (!CompareMode && HasCrop && bmp != null)
                bmp = CropBitmap(bmp, _cropLeft, _cropTop, _cropRight, _cropBottom);

            return bmp;
        }

        private static BitmapSource CropBitmap(BitmapSource src, float left, float top, float right, float bottom) {
            int w = src.PixelWidth, h = src.PixelHeight;
            int x = (int)(w * left);
            int y = (int)(h * top);
            int cw = w - x - (int)(w * right);
            int ch = h - y - (int)(h * bottom);
            if (cw < 100 || ch < 100) return src; // safety
            var cropped = new CroppedBitmap(src, new Int32Rect(x, y, cw, ch));
            cropped.Freeze();
            return cropped;
        }

        private void RenderNow() {
            if (_engine.FrameCount == 0) return;
            Task.Run(() => {
                var result = GetDisplayWBResult();
                var bmp = RenderStack(result);
                if (bmp == null) return;
                var hist = BuildHistogram(result);
                Application.Current?.Dispatcher?.Invoke(() => {
                    DisplayImage = bmp;
                    if (hist != null) HistogramImage = hist;
                    _fullScreenWindow?.UpdateImage(bmp, FrameCount, LiveStackPlugin.TargetName, SNRStatus);
                    if (_uploadEnabled) {
                        CloudUploader.UploadIfEnabled(bmp, LiveStackPlugin.TargetName);
                        RaisePropertyChanged(nameof(UploadStatus));
                    }
                });
            });
        }

        private void OnStackUpdated(object sender, StackUpdatedEventArgs e) {
            InvalidateDisplayCache();
            Task.Run(() => {
                var result = GetDisplayWBResult();
                var bmp = RenderStack(result);
                if (bmp == null) return;
                string snr = _engine.NoiseTracker?.GetStatusText() ?? "";
                var hist = BuildHistogram(result);
                Application.Current?.Dispatcher?.Invoke(() => {
                    DisplayImage = bmp;
                    if (hist != null) HistogramImage = hist;
                    FrameCount = _engine.FrameCount;
                    MemoryUsageMB = _engine.GetMemoryUsageMB();
                    AlignStatus = _engine.LastAlignStatus;
                    RaisePropertyChanged(nameof(PlateSolverStatus));
                    SNRStatus = snr;
                    StatusText = AlignmentEnabled
                        ? $"Stacking... ({FrameCount} frames, {_engine.AlignedCount} aligned, {MemoryUsageMB:F0} MB)"
                        : $"Stacking... ({FrameCount} frames, {MemoryUsageMB:F0} MB)";
                    _fullScreenWindow?.UpdateImage(bmp, FrameCount, LiveStackPlugin.TargetName, SNRStatus);
                    if (_uploadEnabled) {
                        CloudUploader.UploadIfEnabled(bmp, LiveStackPlugin.TargetName);
                        RaisePropertyChanged(nameof(UploadStatus));
                    }
                });
            });
        }

        // ================================================================
        // SAVE — full resolution
        // ================================================================

        private void SaveCurrentStack() {
            var result = _engine.GetStackedImageResult();
            if (result == null) { StatusText = "Nothing to save"; return; }
            var dialog = new Microsoft.Win32.SaveFileDialog {
                Filter = "32-bit FITS|*.fits|TIFF|*.tiff|PNG|*.png", DefaultExt = ".fits",
                FileName = $"LiveStack_{DateTime.Now:yyyyMMdd_HHmmss}_{FrameCount}frames"
            };
            if (dialog.ShowDialog() == true) {
                try {
                    string ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                    if (ext == ".fits" || ext == ".fit" || ext == ".fts") {
                        FitsWriter.Write32BitFits(dialog.FileName, result.R, result.G, result.B,
                            result.Width, result.Height);
                        StatusText = $"Saved 32-bit FITS ({FrameCount} frames, {result.Width}x{result.Height})";
                    } else {
                        // Full-res stretched save
                        var wb = ImageStretch.ComputeWhiteBalance(result.R, result.G, result.B);
                        ImageStretch.ApplyWhiteBalance(result.R, result.G, result.B, wb);
                        if (EnableBackgroundSub)
                            AdvancedStretch.SubtractBackground(result.R, result.G, result.B);

                        if (UseArcsinhStretch) {
                            AdvancedStretch.AutoArcsinh(result.R, result.G, result.B,
                                result.Width, result.Height, ArcsinhFactor,
                                brightness: Brightness, contrast: Contrast);
                        } else {
                            var sp = ImageStretch.AutoStretchLinked(result.R, result.G, result.B);
                            ImageStretch.ApplyMTFInPlace(result.R, result.G, result.B, sp, Brightness, Contrast);
                        }

                        // Star reduction after stretch — detect on linear, apply on stretched
                        // Morphological star reduction on full-res stretched data
                        if (StarReduction > 0.01f)
                            AdvancedStretch.MorphologicalStarReduce(result.R, result.G, result.B,
                                result.Width, result.Height, StarReduction);

                        // Wavelet sharpening on full-res
                        if (Sharpen > 0.01f)
                            AdvancedStretch.WaveletSharpen(result.R, result.G, result.B,
                                result.Width, result.Height, Sharpen);

                        var bmp = ImageStretch.FloatToBitmap(result.R, result.G, result.B,
                            result.Width, result.Height, RBalance, GBalance, BBalance, BlackClip);
                        if (bmp == null) { StatusText = "Render failed"; return; }
                        using var stream = new FileStream(dialog.FileName, FileMode.Create);
                        BitmapEncoder enc = ext == ".png"
                            ? new PngBitmapEncoder() : new TiffBitmapEncoder { Compression = TiffCompressOption.None };
                        enc.Frames.Add(BitmapFrame.Create(bmp)); enc.Save(stream);
                        StatusText = $"Saved {ext.ToUpper()} ({FrameCount} frames, {result.Width}x{result.Height})";
                    }
                } catch (Exception ex) { Logger.Error($"LiveStack: Save error: {ex.Message}"); StatusText = "Save failed"; }
            }
        }

        // ================================================================
        // LOAD FILES — with target name from FITS OBJECT header
        // ================================================================

        private void LoadFiles() {
            var dialog = new Microsoft.Win32.OpenFileDialog {
                Title = "Select FITS files to stack",
                Filter = "FITS files (*.fits;*.fit;*.fts)|*.fits;*.fit;*.fts|All files (*.*)|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;

            string[] files = dialog.FileNames;
            _engine.Start(); IsRunning = true;

            // Ensure plate solver is configured for post-flip frame recovery
            LiveStackPlugin.EnsurePlateSolver();
            RaisePropertyChanged(nameof(PlateSolverStatus));
            if (!LiveStackPlugin.PlateSolverAvailable)
                AlignStatus = "Plate solver not configured — using triangle matching only";
            else
                AlignStatus = "";
            InvalidateAll();
            BadFrameStatus = "";
            StatusText = $"Loading {files.Length} files...";

            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            Task.Run(() => {
                int loaded = 0, failed = 0, skippedBad = 0;
                foreach (var file in files) {
                    if (token.IsCancellationRequested) break;

                    string fileName = Path.GetFileName(file);
                    if (fileName.IndexOf("BAD", StringComparison.OrdinalIgnoreCase) >= 0) {
                        skippedBad++;
                        Logger.Info($"LiveStack: Skipping {fileName} (marked BAD)");
                        continue;
                    }

                    try {
                        var fits = FitsReader.ReadFits(file);

                        // Extract target name from FITS OBJECT header
                        if (!string.IsNullOrEmpty(fits.ObjectName) && string.IsNullOrEmpty(LiveStackPlugin.TargetName))
                            LiveStackPlugin.TargetName = fits.ObjectName;

                        // Pass RA/DEC from first file's FITS header to plate solver
                        // (critical when telescope is disconnected during file loading)
                        if (fits.HasCoordinates && loaded == 0)
                            _engine.SetPlateSolveHint(fits.RaHours, fits.DecDeg);

                        // Pass focal length from FITS header — NINA profile may be wrong
                        if (fits.HasFocalLength && loaded == 0)
                            _engine.SetPlateSolveOptics(fits.FocalLength, fits.PixelSizeUm);

                        _engine.AddRGBFrameFromFile(fits.R, fits.G, fits.B, fits.Width, fits.Height, file);
                        loaded++;
                        Application.Current?.Dispatcher?.Invoke(() => {
                            StatusText = $"Loading... {loaded}/{files.Length - skippedBad}";
                            FrameCount = _engine.FrameCount;
                            AlignStatus = _engine.LastAlignStatus;
                            RaisePropertyChanged(nameof(PlateSolverStatus));
                            SNRStatus = _engine.NoiseTracker?.GetStatusText() ?? "";
                        });
                    } catch (Exception ex) {
                        failed++;
                        string reason = ex.Message;
                        Logger.Warning($"LiveStack: Failed {Path.GetFileName(file)}: {reason}");
                        // Show first failure reason in status so user knows what went wrong
                        if (failed == 1) {
                            Application.Current?.Dispatcher?.Invoke(() => {
                                BadFrameStatus = $"⚠ {fileName}: {reason}";
                            });
                        }
                    }
                }

                if (!token.IsCancellationRequested) {
                    if (!AlignmentEnabled) _engine.FireStackUpdated();
                    int badCount = _engine.AutoDetectBadFrames();
                    int alignFailed = _engine.FailedAlignCount;
                    Application.Current?.Dispatcher?.Invoke(() => {
                        string failStr = failed > 0 ? $", {failed} failed" : "";
                        string badStr = skippedBad > 0 ? $", {skippedBad} BAD skipped" : "";
                        string alignStr = alignFailed > 0 ? $", {alignFailed} align failed" : "";
                        StatusText = $"Loaded {loaded} files{failStr}{badStr} — {_engine.FrameCount} frames, {_engine.AlignedCount} aligned{alignStr}";
                        if (badCount > 0)
                            BadFrameStatus = $"{badCount} quality outliers detected — click Rebuild";
                        else if (alignFailed > 0)
                            BadFrameStatus = $"{alignFailed} frames failed alignment (need plate solving)";
                        else
                            BadFrameStatus = $"All {_engine.Frames.Count} frames OK";
                    });
                }

                Application.Current?.Dispatcher?.Invoke(() => {
                    FrameCount = _engine.FrameCount;
                    MemoryUsageMB = _engine.GetMemoryUsageMB();
                    SNRStatus = _engine.NoiseTracker?.GetStatusText() ?? "";
                });
            });
        }

        private void RebuildWithoutBadFrames() {
            if (!_engine.CanRebuild) { StatusText = "No files to rebuild from"; return; }
            _engine.AutoDetectBadFrames();
            int badCount = _engine.BadFrameCount;

            StatusText = badCount > 0
                ? $"Rebuilding, excluding {badCount} bad frames..."
                : "Rebuilding stack from scratch...";
            InvalidateAll();

            Task.Run(() => {
                var (loaded, skipped) = _engine.RebuildFromFiles((current, total) => {
                    Application.Current?.Dispatcher?.BeginInvoke((Action)(() => {
                        StatusText = $"Rebuilding... {current}/{total} frames";
                        FrameCount = _engine.FrameCount;
                    }));
                });
                _engine.FireStackUpdated();
                Application.Current?.Dispatcher?.Invoke(() => {
                    StatusText = $"Rebuilt: {loaded} frames stacked, {skipped} excluded";
                    FrameCount = _engine.FrameCount;
                    MemoryUsageMB = _engine.GetMemoryUsageMB();
                    SNRStatus = _engine.NoiseTracker?.GetStatusText() ?? "";
                    BadFrameStatus = "";
                });
            });
        }

        // ================================================================
        // FULLSCREEN
        // ================================================================

        private void ToggleFullScreen() {
            if (_fullScreenWindow != null && _fullScreenWindow.IsVisible) {
                _fullScreenWindow.Close(); _fullScreenWindow = null;
            } else {
                _fullScreenWindow = new FullScreenWindow();
                _fullScreenWindow.Closed += (s, e) => _fullScreenWindow = null;
                if (DisplayImage != null) _fullScreenWindow.UpdateImage(DisplayImage, FrameCount, LiveStackPlugin.TargetName, SNRStatus);
                _fullScreenWindow.Show();
            }
        }

        // ================================================================
        // HISTOGRAM
        // ================================================================

        private BitmapSource BuildHistogram(StackResult result) {
            if (result == null) return null;
            try {
                int len = result.R.Length;
                int step = Math.Max(1, len / 100000);
                int[] rH = new int[256], gH = new int[256], bH = new int[256];

                for (int i = 0; i < len; i += step) {
                    int ri = Math.Min(255, (int)(result.R[i] * 255f));
                    int gi = Math.Min(255, (int)(result.G[i] * 255f));
                    int bi = Math.Min(255, (int)(result.B[i] * 255f));
                    if (ri >= 0) rH[ri]++;
                    if (gi >= 0) gH[gi]++;
                    if (bi >= 0) bH[bi]++;
                }

                int maxVal = 1;
                for (int i = 1; i < 256; i++)
                    maxVal = Math.Max(maxVal, Math.Max(rH[i], Math.Max(gH[i], bH[i])));

                int w = 256, h = 60;
                byte[] pixels = new byte[w * h * 4];
                for (int x = 0; x < w; x++) {
                    int rBar = (int)((double)rH[x] / maxVal * (h - 1));
                    int gBar = (int)((double)gH[x] / maxVal * (h - 1));
                    int bBar = (int)((double)bH[x] / maxVal * (h - 1));
                    for (int y = 0; y < h; y++) {
                        int iy = h - 1 - y;
                        int idx = (iy * w + x) * 4;
                        byte pr = (byte)(y < rBar ? 180 : 0);
                        byte pg = (byte)(y < gBar ? 180 : 0);
                        byte pb = (byte)(y < bBar ? 180 : 0);
                        pixels[idx] = pb; pixels[idx + 1] = pg; pixels[idx + 2] = pr;
                        pixels[idx + 3] = (byte)((pr > 0 || pg > 0 || pb > 0) ? 255 : 40);
                    }
                }

                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                bmp.Freeze();
                return bmp;
            } catch { return null; }
        }
    }
}
