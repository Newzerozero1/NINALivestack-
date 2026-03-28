using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.Core.Utility;

namespace NinaLiveStack.Engine {

    public class PluginSettings {

        // Broadcasting
        [JsonPropertyName("broadcastEnabled")]
        public bool BroadcastEnabled { get; set; } = false;

        [JsonPropertyName("r2Endpoint")]
        public string R2Endpoint { get; set; } = "";

        [JsonPropertyName("r2Bucket")]
        public string R2Bucket { get; set; } = "";

        [JsonPropertyName("r2AccessKey")]
        public string R2AccessKey { get; set; } = "";

        [JsonPropertyName("r2SecretKey")]
        public string R2SecretKey { get; set; } = "";

        [JsonPropertyName("r2PublicUrl")]
        public string R2PublicUrl { get; set; } = "";

        // Overlay
        [JsonPropertyName("overlayText")]
        public string OverlayText { get; set; } = "";

        [JsonPropertyName("overlayFont")]
        public string OverlayFont { get; set; } = "Monotype Corsiva";

        [JsonPropertyName("overlayEnabled")]
        public bool OverlayEnabled { get; set; } = true;

        // Defaults
        [JsonPropertyName("defaultAlignEnabled")]
        public bool DefaultAlignEnabled { get; set; } = true;

        [JsonPropertyName("defaultHotPixelFilter")]
        public bool DefaultHotPixelFilter { get; set; } = true;

        [JsonPropertyName("defaultTrailMask")]
        public bool DefaultTrailMask { get; set; } = true;

        [JsonPropertyName("defaultArcsinhStretch")]
        public bool DefaultArcsinhStretch { get; set; } = true;

        [JsonPropertyName("defaultStretchFactor")]
        public float DefaultStretchFactor { get; set; } = 100f;

        // ================================================================
        // PERSISTENCE
        // ================================================================

        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins", "3.0.0", "NinaLiveStack");

        private static readonly string SettingsFile =
            Path.Combine(SettingsDir, "settings.json");

        /// <summary>
        /// True if R2 endpoint, bucket, access key, and secret key are all non-empty.
        /// </summary>
        [JsonIgnore]
        public bool HasR2Credentials =>
            !string.IsNullOrWhiteSpace(R2Endpoint) &&
            !string.IsNullOrWhiteSpace(R2Bucket) &&
            !string.IsNullOrWhiteSpace(R2AccessKey) &&
            !string.IsNullOrWhiteSpace(R2SecretKey);

        /// <summary>
        /// The public URL for the viewer page. Falls back to empty string if not configured.
        /// </summary>
        [JsonIgnore]
        public string ViewerUrl {
            get {
                if (string.IsNullOrWhiteSpace(R2PublicUrl)) return "";
                string baseUrl = R2PublicUrl.TrimEnd('/');
                return $"{baseUrl}/index.html";
            }
        }

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static PluginSettings Load() {
            try {
                if (File.Exists(SettingsFile)) {
                    string json = File.ReadAllText(SettingsFile);
                    var settings = JsonSerializer.Deserialize<PluginSettings>(json, _jsonOpts);
                    if (settings != null) {
                        Logger.Info($"LiveStack: Settings loaded from {SettingsFile}");
                        return settings;
                    }
                }
            } catch (Exception ex) {
                Logger.Warning($"LiveStack: Failed to load settings: {ex.Message}");
            }
            Logger.Info("LiveStack: Using default settings");
            return new PluginSettings();
        }

        public void Save() {
            try {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(this, _jsonOpts);
                File.WriteAllText(SettingsFile, json);
                Logger.Info($"LiveStack: Settings saved to {SettingsFile}");
            } catch (Exception ex) {
                Logger.Warning($"LiveStack: Failed to save settings: {ex.Message}");
            }
        }
    }
}
