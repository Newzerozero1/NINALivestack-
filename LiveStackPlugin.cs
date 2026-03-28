using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NinaLiveStack.Engine;

namespace NinaLiveStack {

    [Export(typeof(IPluginManifest))]
    public class LiveStackPlugin : PluginBase {

        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly IProfileService _profileService;

        public static LiveStackEngine StackEngine { get; } = new LiveStackEngine();
        public static string TargetName { get; set; } = "";
        public static event EventHandler<StackUpdatedEventArgs> StackUpdated;

        private static LiveStackPlugin _instance;

        /// <summary>
        /// Trigger plate solver configuration. Call from VM before file loading.
        /// </summary>
        public static void EnsurePlateSolver() {
            _instance?.EnsurePlateSolverConfigured();
        }

        /// <summary>
        /// True if a plate solver was successfully configured.
        /// False means alignment will fall back to triangle matching only.
        /// </summary>
        public static bool PlateSolverAvailable { get; private set; } = false;

        [Import(AllowDefault = true)]
        public IPlateSolverFactory PlateSolverFactory { get; set; }

        [Import(AllowDefault = true)]
        public ITelescopeMediator TelescopeMediator { get; set; }

        [Import(AllowDefault = true)]
        public IImageDataFactory ImageDataFactory { get; set; }

        private bool _plateSolverConfigured = false;

        [ImportingConstructor]
        public LiveStackPlugin(IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator, IProfileService profileService) {
            _imageSaveMediator = imageSaveMediator;
            _profileService = profileService;
            _instance = this;
            _imageSaveMediator.BeforeImageSaved += OnBeforeImageSaved;
            StackEngine.StackUpdated += (s, e) => StackUpdated?.Invoke(s, e);
            Logger.Info("LiveStack: Plugin v1.0.0 loaded");
        }

        private void EnsurePlateSolverConfigured() {
            if (_plateSolverConfigured) return;
            _plateSolverConfigured = true;
            try {
                if (PlateSolverFactory != null && _profileService != null) {
                    StackEngine.ConfigurePlateSolver(PlateSolverFactory, _profileService,
                        TelescopeMediator, ImageDataFactory);
                    PlateSolverAvailable = true;
                    Logger.Info("LiveStack: Plate solver configured");
                } else {
                    PlateSolverAvailable = false;
                    Logger.Warning("LiveStack: IPlateSolverFactory not available");
                }
            } catch (Exception ex) {
                PlateSolverAvailable = false;
                Logger.Warning($"LiveStack: Plate solver config failed: {ex.Message}");
            }
        }

        private Task OnBeforeImageSaved(object sender, BeforeImageSavedEventArgs e) {
            try {
                if (!StackEngine.IsRunning) return Task.CompletedTask;
                var imageData = e.Image;
                if (imageData == null) return Task.CompletedTask;

                EnsurePlateSolverConfigured();

                int width = imageData.Properties.Width;
                int height = imageData.Properties.Height;

                try { TargetName = e.Image.MetaData?.Target?.Name ?? ""; } catch { }

                if (imageData.Properties.IsBayered) {
                    DebayerDirect(imageData.Data.FlatArray, width, height);
                } else {
                    StackEngine.AddMonoFrame(imageData.Data.FlatArray, width, height);
                }
            } catch (Exception ex) {
                Logger.Error($"LiveStack: Error processing image: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        private void DebayerDirect(ushort[] rawBayer, int width, int height) {
            int pixelCount = width * height;
            ushort[] r = new ushort[pixelCount];
            ushort[] g = new ushort[pixelCount];
            ushort[] b = new ushort[pixelCount];

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int idx = y * width + x;
                    int ry = y & ~1;
                    int rx = x & ~1;
                    r[idx] = rawBayer[ry * width + rx];
                    int by = Math.Min(ry + 1, height - 1);
                    int bx = Math.Min(rx + 1, width - 1);
                    b[idx] = rawBayer[by * width + bx];
                    int g1x = Math.Min(rx + 1, width - 1);
                    int g2y = Math.Min(ry + 1, height - 1);
                    g[idx] = (ushort)((rawBayer[ry * width + g1x] + rawBayer[g2y * width + rx]) / 2);
                }
            }

            StackEngine.AddFrame(r, g, b, width, height);
        }

        public override Task Teardown() {
            _imageSaveMediator.BeforeImageSaved -= OnBeforeImageSaved;
            StackEngine.Stop();
            return Task.CompletedTask;
        }
    }
}
