using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinaLiveStack.Engine;

namespace NinaLiveStack.DockablePanel {

    /// <summary>
    /// Pure C# settings dialog (no XAML — matches FullScreenWindow pattern).
    /// Written for non-developers: step-by-step Cloudflare R2 setup with
    /// inline examples, common-mistake warnings, and input validation.
    /// </summary>
    public class SettingsWindow : Window {

        private readonly PluginSettings _settings;

        private readonly TextBox _tbEndpoint;
        private readonly TextBox _tbBucket;
        private readonly TextBox _tbAccessKey;
        private readonly TextBox _tbSecretKey;
        private readonly TextBox _tbPublicUrl;

        private readonly TextBox _tbOverlayText;
        private readonly TextBox _tbOverlayFont;
        private readonly CheckBox _cbOverlayEnabled;

        private readonly CheckBox _cbDefAlign;
        private readonly CheckBox _cbDefHotPx;
        private readonly CheckBox _cbDefTrail;
        private readonly CheckBox _cbDefArcsinh;
        private readonly TextBox _tbDefStretch;

        private readonly TextBlock _statusLabel;

        public SettingsWindow(PluginSettings settings) {
            _settings = settings;
            Title = "NinaLiveStack Settings";
            Width = 580;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 850;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));

            var mainStack = new StackPanel { Margin = new Thickness(16) };

            // ================================================================
            // WEB BROADCASTING
            // ================================================================
            mainStack.Children.Add(SectionHeader("Web Broadcasting Setup"));
            mainStack.Children.Add(HintText(
                "Upload your live stack so attendees can view on their phones. " +
                "Works with any S3-compatible storage: Cloudflare R2 (free tier), AWS S3, " +
                "Backblaze B2, MinIO, DigitalOcean Spaces, Wasabi, or your own server. " +
                "Leave all fields blank to disable broadcasting."));
            mainStack.Children.Add(Spacer(6));

            // ---- Step 1: Bucket ----
            mainStack.Children.Add(StepLabel("1. Bucket name"));
            mainStack.Children.Add(HintText(
                "The storage bucket you created for your images."));
            _tbBucket = AddField(mainStack, settings.R2Bucket);
            mainStack.Children.Add(ExampleText("Example:  my-livestack"));

            // ---- Step 2: S3 API endpoint ----
            mainStack.Children.Add(StepLabel("2. S3 API endpoint"));
            mainStack.Children.Add(HintText(
                "The S3-compatible API URL for your storage provider. " +
                "Find this in your provider's dashboard under API or S3 settings."));
            _tbEndpoint = AddField(mainStack, settings.R2Endpoint);
            mainStack.Children.Add(ExampleText("Cloudflare R2:  https://[account-id].r2.cloudflarestorage.com"));
            mainStack.Children.Add(ExampleText("AWS S3:  https://s3.us-east-1.amazonaws.com"));
            mainStack.Children.Add(ExampleText("Backblaze B2:  https://s3.us-west-002.backblazeb2.com"));
            mainStack.Children.Add(WarningText(
                "⚠ This is the storage API URL, not the public viewing link."));

            // ---- Step 3: API token ----
            mainStack.Children.Add(StepLabel("3. Access Key + Secret Key"));
            mainStack.Children.Add(HintText(
                "Your S3 access credentials. Create an API token/key pair in your storage provider's dashboard " +
                "with read+write permission for your bucket. The secret key is stored locally on this computer only."));
            mainStack.Children.Add(FieldLabel("Access Key ID:"));
            _tbAccessKey = AddField(mainStack, settings.R2AccessKey);
            mainStack.Children.Add(ExampleText("A short alphanumeric string from your provider"));
            mainStack.Children.Add(FieldLabel("Secret Access Key:"));
            _tbSecretKey = AddField(mainStack, settings.R2SecretKey);
            mainStack.Children.Add(ExampleText("A longer key — stored locally, never shared"));

            // ---- Step 4: Public URL ----
            mainStack.Children.Add(StepLabel("4. Public viewing URL"));
            mainStack.Children.Add(HintText(
                "The public URL where people can view the uploaded images. " +
                "Enable public access on your bucket and paste the base URL here. " +
                "The plugin adds /index.html and /latest.jpg automatically — don't include filenames."));
            _tbPublicUrl = AddField(mainStack, settings.R2PublicUrl);
            mainStack.Children.Add(ExampleText("Cloudflare R2:  https://pub-[id].r2.dev  or your custom domain"));
            mainStack.Children.Add(ExampleText("AWS S3:  https://my-livestack.s3.amazonaws.com"));
            mainStack.Children.Add(ExampleText("Custom domain:  https://live.yourdomain.com"));
            mainStack.Children.Add(WarningText(
                "⚠ Don't add /index.html or /latest.jpg — just the base URL.\n" +
                "⚠ Apple may flag shared domains (like r2.dev). A custom domain fixes this."));

            mainStack.Children.Add(Spacer(14));

            // ================================================================
            // IMAGE OVERLAY
            // ================================================================
            mainStack.Children.Add(SectionHeader("Image Overlay"));
            mainStack.Children.Add(HintText(
                "Text burned into the broadcast image. Target name appears bottom-left automatically " +
                "(from the FITS OBJECT header). Your name/handle appears bottom-right."));

            mainStack.Children.Add(FieldLabel("Your Name or Handle (bottom-right):"));
            _tbOverlayText = AddField(mainStack, settings.OverlayText);
            mainStack.Children.Add(ExampleText("Example:  @YourAstroHandle   or   Your Name"));

            mainStack.Children.Add(FieldLabel("Font:"));
            _tbOverlayFont = AddField(mainStack, settings.OverlayFont);
            mainStack.Children.Add(ExampleText("Example:  Monotype Corsiva   or   Segoe Script   or   Arial"));

            _cbOverlayEnabled = AddCheckbox(mainStack, "Show text overlay on broadcast images", settings.OverlayEnabled);

            mainStack.Children.Add(Spacer(14));

            // ================================================================
            // STARTUP DEFAULTS
            // ================================================================
            mainStack.Children.Add(SectionHeader("Startup Defaults"));
            mainStack.Children.Add(HintText(
                "Which checkboxes should be ON when the plugin first loads. " +
                "Changing these won't affect your current session — only the next time NINA starts."));
            _cbDefAlign = AddCheckbox(mainStack, "Star alignment", settings.DefaultAlignEnabled);
            _cbDefHotPx = AddCheckbox(mainStack, "Hot pixel filter", settings.DefaultHotPixelFilter);
            _cbDefTrail = AddCheckbox(mainStack, "Satellite trail mask", settings.DefaultTrailMask);
            _cbDefArcsinh = AddCheckbox(mainStack, "Arcsinh stretch", settings.DefaultArcsinhStretch);
            mainStack.Children.Add(FieldLabel("Default stretch factor (1–500):"));
            _tbDefStretch = AddField(mainStack, settings.DefaultStretchFactor.ToString("F0"));
            mainStack.Children.Add(ExampleText("100 is a good starting point — higher reveals fainter detail"));

            mainStack.Children.Add(Spacer(14));

            // ---- Status + Buttons ----
            _statusLabel = new TextBlock {
                Text = "", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(255, 130, 130)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            };
            mainStack.Children.Add(_statusLabel);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnSave = new Button {
                Content = "Save", Padding = new Thickness(24, 6, 24, 6), Margin = new Thickness(0, 0, 8, 0),
                FontSize = 12, IsDefault = true
            };
            btnSave.Click += OnSave;
            var btnCancel = new Button {
                Content = "Cancel", Padding = new Thickness(24, 6, 24, 6),
                FontSize = 12, IsCancel = true
            };
            btnCancel.Click += (s, e) => Close();
            btnPanel.Children.Add(btnSave);
            btnPanel.Children.Add(btnCancel);
            mainStack.Children.Add(btnPanel);

            Content = new ScrollViewer {
                Content = mainStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private void OnSave(object sender, RoutedEventArgs e) {
            string endpoint = _tbEndpoint.Text.Trim();
            string bucket = _tbBucket.Text.Trim();
            string accessKey = _tbAccessKey.Text.Trim();
            string secretKey = _tbSecretKey.Text.Trim();
            string publicUrl = _tbPublicUrl.Text.Trim().TrimEnd('/');

            // Validate R2 fields — either all empty (broadcasting disabled) or all filled
            bool anyFilled = !string.IsNullOrEmpty(endpoint) || !string.IsNullOrEmpty(bucket) ||
                             !string.IsNullOrEmpty(accessKey) || !string.IsNullOrEmpty(secretKey);
            bool allFilled = !string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(bucket) &&
                             !string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey);

            if (anyFilled && !allFilled) {
                _statusLabel.Text = "Broadcasting requires all four fields: endpoint, bucket, access key, and secret key. " +
                    "Fill them all in, or clear them all to disable broadcasting.";
                return;
            }

            // Catch common mistakes
            if (!string.IsNullOrEmpty(endpoint)) {
                if (!endpoint.StartsWith("https://") && !endpoint.StartsWith("http://")) {
                    _statusLabel.Text = "The S3 endpoint should start with https:// — " +
                        "it's the API URL from your storage provider's settings.";
                    return;
                }
                if (endpoint.Contains("/index.html") || endpoint.Contains("/latest.jpg")) {
                    _statusLabel.Text = "That looks like a file link, not the S3 API endpoint. " +
                        "The endpoint is the storage API URL (not the public viewing URL).";
                    return;
                }
            }

            if (!string.IsNullOrEmpty(publicUrl)) {
                if (!publicUrl.StartsWith("https://") && !publicUrl.StartsWith("http://")) {
                    _statusLabel.Text = "The public URL should start with https:// — " +
                        "it's where people view the images (your public bucket URL or custom domain).";
                    return;
                }
                if (publicUrl.EndsWith(".html") || publicUrl.EndsWith(".jpg") || publicUrl.EndsWith(".txt")) {
                    _statusLabel.Text = "Paste just the base URL without any filename at the end. " +
                        "The plugin adds /index.html and /latest.jpg automatically.";
                    return;
                }
            }

            _settings.R2Endpoint = endpoint;
            _settings.R2Bucket = bucket;
            _settings.R2AccessKey = accessKey;
            _settings.R2SecretKey = secretKey;
            _settings.R2PublicUrl = publicUrl;

            _settings.OverlayText = _tbOverlayText.Text.Trim();
            _settings.OverlayFont = _tbOverlayFont.Text.Trim();
            _settings.OverlayEnabled = _cbOverlayEnabled.IsChecked == true;

            _settings.DefaultAlignEnabled = _cbDefAlign.IsChecked == true;
            _settings.DefaultHotPixelFilter = _cbDefHotPx.IsChecked == true;
            _settings.DefaultTrailMask = _cbDefTrail.IsChecked == true;
            _settings.DefaultArcsinhStretch = _cbDefArcsinh.IsChecked == true;

            if (float.TryParse(_tbDefStretch.Text, out float sf) && sf >= 1 && sf <= 500)
                _settings.DefaultStretchFactor = sf;

            _settings.Save();
            DialogResult = true;
            Close();
        }

        // ---- UI Helpers ----

        private static TextBlock SectionHeader(string text) {
            return new TextBlock {
                Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 6)
            };
        }

        private static TextBlock StepLabel(string text) {
            return new TextBlock {
                Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 175, 255)),
                Margin = new Thickness(0, 8, 0, 2)
            };
        }

        private static TextBlock FieldLabel(string text) {
            return new TextBlock {
                Text = text, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                Margin = new Thickness(0, 4, 0, 2)
            };
        }

        private static TextBlock HintText(string text) {
            return new TextBlock {
                Text = text, FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4)
            };
        }

        private static TextBlock ExampleText(string text) {
            return new TextBlock {
                Text = text, FontSize = 9, FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(Color.FromRgb(110, 160, 110)),
                Margin = new Thickness(4, 0, 0, 2)
            };
        }

        private static TextBlock WarningText(string text) {
            return new TextBlock {
                Text = text, FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 180, 90)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 0, 4)
            };
        }

        private static TextBox AddField(StackPanel parent, string value) {
            var tb = new TextBox {
                Text = value ?? "",
                FontSize = 11, FontFamily = new FontFamily("Consolas, Courier New"),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 1),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1)
            };
            parent.Children.Add(tb);
            return tb;
        }

        private static CheckBox AddCheckbox(StackPanel parent, string label, bool isChecked) {
            var cb = new CheckBox {
                Content = new TextBlock {
                    Text = label, FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210))
                },
                IsChecked = isChecked,
                Margin = new Thickness(0, 3, 0, 3)
            };
            parent.Children.Add(cb);
            return cb;
        }

        private static FrameworkElement Spacer(double height = 12) {
            return new Border { Height = height };
        }
    }
}
