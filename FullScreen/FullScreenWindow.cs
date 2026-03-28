using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NinaLiveStack.FullScreen {

    public class FullScreenWindow : Window {

        private readonly Image _image;
        private readonly TextBlock _frameCountText;
        private readonly TextBlock _targetText;
        private readonly TextBlock _placeholderText;
        private readonly TextBlock _zoomText;
        private readonly ScaleTransform _scaleTransform;
        private readonly TranslateTransform _translateTransform;
        private double _zoom = 1.0;
        private Point _panStart;
        private bool _isPanning = false;

        public FullScreenWindow() {
            Title = "Live Stack - Fullscreen"; WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized; Background = Brushes.Black;
            Topmost = true; KeyDown += OnKeyDown;

            var grid = new Grid();

            _scaleTransform = new ScaleTransform(1, 1);
            _translateTransform = new TranslateTransform(0, 0);
            var tg = new TransformGroup(); tg.Children.Add(_scaleTransform); tg.Children.Add(_translateTransform);

            _image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, RenderTransform = tg, RenderTransformOrigin = new Point(0.5, 0.5) };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _image.MouseLeftButtonDown += (s, e) => { if (_zoom > 1.0) { _isPanning = true; _panStart = e.GetPosition(this); _image.CaptureMouse(); } };
            _image.MouseLeftButtonUp += (s, e) => { _isPanning = false; _image.ReleaseMouseCapture(); };
            _image.MouseMove += (s, e) => { if (_isPanning && _zoom > 1.0) { var p = e.GetPosition(this); _translateTransform.X += p.X - _panStart.X; _translateTransform.Y += p.Y - _panStart.Y; _panStart = p; } };
            grid.Children.Add(_image);

            // Target name top-left
            var tb = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(0, 0, 8, 0) };
            _targetText = new TextBlock { Text = "", Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)), FontSize = 18, FontStyle = FontStyles.Italic };
            tb.Child = _targetText; grid.Children.Add(tb);

            // Top-right controls
            var cp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var ib = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), Padding = new Thickness(10, 6, 10, 6) };
            _frameCountText = new TextBlock { Text = "0 frames", Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), FontSize = 14, FontWeight = FontWeights.SemiBold };
            ib.Child = _frameCountText; cp.Children.Add(ib);

            var zo = MakeBtn("−", 44); zo.Click += (s, e) => ZoomBy(-0.25); cp.Children.Add(zo);
            var zb = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), Padding = new Thickness(6), MinWidth = 50 };
            _zoomText = new TextBlock { Text = "1.0x", Foreground = Brushes.White, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
            zb.Child = _zoomText; cp.Children.Add(zb);
            var zi = MakeBtn("+", 44); zi.Click += (s, e) => ZoomBy(0.25); cp.Children.Add(zi);
            var fb = MakeBtn("Fit", 44); fb.Click += (s, e) => ResetZoom(); cp.Children.Add(fb);
            var cb = MakeBtn("✕", 50); cb.Background = new SolidColorBrush(Color.FromArgb(0x88, 0xCC, 0, 0)); cb.Click += (s, e) => Close(); cp.Children.Add(cb);
            grid.Children.Add(cp);

            _placeholderText = new TextBlock { Text = "Waiting for images...\n\nPress ESC to exit.",
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)), FontSize = 24, LineHeight = 36 };
            grid.Children.Add(_placeholderText);
            Content = grid;
        }

        private Button MakeBtn(string t, int s) => new Button { Content = t, FontSize = 18, FontWeight = FontWeights.Bold,
            Width = s, Height = s, Cursor = Cursors.Hand, Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };

        private void ZoomBy(double d) { _zoom = System.Math.Max(0.25, System.Math.Min(8.0, _zoom + d));
            _scaleTransform.ScaleX = _zoom; _scaleTransform.ScaleY = _zoom; _zoomText.Text = $"{_zoom:F1}x";
            if (_zoom <= 1.0) { _translateTransform.X = 0; _translateTransform.Y = 0; } }
        private void ResetZoom() { _zoom = 1.0; _scaleTransform.ScaleX = 1; _scaleTransform.ScaleY = 1;
            _translateTransform.X = 0; _translateTransform.Y = 0; _zoomText.Text = "1.0x"; }

        public void UpdateImage(BitmapSource image, int frameCount, string targetName = "", string snrText = "") {
            _image.Source = image;
            string info = $"{frameCount} frame{(frameCount != 1 ? "s" : "")}";
            if (!string.IsNullOrEmpty(snrText)) info += $"  |  {snrText}";
            _frameCountText.Text = info;
            _targetText.Text = targetName ?? "";
            _placeholderText.Visibility = image != null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnKeyDown(object s, KeyEventArgs e) {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.OemPlus || e.Key == Key.Add) ZoomBy(0.25);
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract) ZoomBy(-0.25);
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0) ResetZoom();
        }
    }
}
