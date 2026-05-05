using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using FaceBlood_WinUI3.Models;
using FaceBlood_WinUI3.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI;

namespace FaceBlood_WinUI3
{
    public sealed partial class MainPage : Page
    {
        private const double RoiFraction = 0.55;
        private const int TargetFps = 30;
        private const int SampleStride = 4;
        private const int WavePoints = 200;
        private const int PreviewWidth = 320;
        private const int PreviewHeight = 240;

        private readonly RppgProcessor _processor = new RppgProcessor(12);
        private readonly Stopwatch _timebase = new Stopwatch();
        private readonly List<double> _waveformDisplay = new List<double>();

        private MediaCapture _mediaCapture;
        private DispatcherQueueTimer _captureTimer;
        private IReadOnlyList<DeviceInformation> _cameraDevices = new List<DeviceInformation>();
        private string _selectedDeviceId;
        private bool _isRunning;
        private bool _isCapturing;
        private bool _cameraSelectionReady;

        private double _smoothPulse;
        private double _lastPhase;
        private byte[] _framePixels;

        private Image _previewImage;
        private WriteableBitmap _previewBitmap;
        private Rectangle _colorWash;
        private Canvas _overlayCanvas;
        private Rectangle _roiRect;
        private TextBlock _statusText;
        private TextBlock _bpmText;
        private TextBlock _snrText;
        private TextBlock _confText;
        private TextBlock _fpsText;
        private TextBlock _samplesText;
        private TextBlock _freqText;
        private ComboBox _cameraSelector;
        private ComboBox _modeSelector;
        private Slider _ampSlider;
        private TextBlock _ampValueText;
        private Canvas _waveCanvas;
        private Line _waveMidLine;
        private Polyline _wavePolyline;

        public MainPage()
        {
            InitializeComponent();
            BuildUi();
            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;
            RootGrid.SizeChanged += RootGrid_SizeChanged;
        }

        private void BuildUi()
        {
            _previewImage = new Image
            {
                Stretch = Stretch.UniformToFill,
            };
            RootGrid.Children.Add(_previewImage);

            _colorWash = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0, 255, 51, 85)),
                IsHitTestVisible = false,
                Opacity = 0,
            };
            RootGrid.Children.Add(_colorWash);

            _overlayCanvas = new Canvas
            {
                IsHitTestVisible = false,
            };
            _roiRect = new Rectangle
            {
                Fill = new SolidColorBrush(Colors.Transparent),
                Stroke = new SolidColorBrush(Color.FromArgb(136, 61, 250, 255)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 8, 6 },
            };
            _overlayCanvas.Children.Add(_roiRect);
            RootGrid.Children.Add(_overlayCanvas);

            var topBar = new Grid
            {
                Margin = new Thickness(12),
                VerticalAlignment = VerticalAlignment.Top,
            };
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusText = new TextBlock
            {
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)),
                Text = "IDLE",
            };
            topBar.Children.Add(_statusText);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            Grid.SetColumn(buttons, 1);

            var startButton = new Button { Content = "計測開始" };
            startButton.Click += StartButton_Click;
            var stopButton = new Button { Content = "停止" };
            stopButton.Click += StopButton_Click;
            buttons.Children.Add(startButton);
            buttons.Children.Add(stopButton);
            topBar.Children.Add(buttons);
            RootGrid.Children.Add(topBar);

            RootGrid.Children.Add(BuildLeftPanel());
            RootGrid.Children.Add(BuildRightPanel());
            RootGrid.Children.Add(BuildBottomPanel());
        }

        private UIElement BuildLeftPanel()
        {
            var panel = new Border
            {
                Margin = new Thickness(12, 56, 0, 0),
                Padding = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(102, 16, 16, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(85, 255, 51, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "HEART RATE", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) });
            _bpmText = new TextBlock { Text = "--", FontSize = 56, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 77, 112)) };
            stack.Children.Add(_bpmText);
            stack.Children.Add(new TextBlock { Text = "BPM", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) });
            panel.Child = stack;
            return panel;
        }

        private UIElement BuildRightPanel()
        {
            var panel = new Border
            {
                Margin = new Thickness(0, 56, 12, 0),
                Padding = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(102, 16, 16, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(85, 61, 250, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };

            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = "SNR", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) });
            _snrText = new TextBlock { Text = "--", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) };
            stack.Children.Add(_snrText);

            stack.Children.Add(new TextBlock { Text = "CONF", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) });
            _confText = new TextBlock { Text = "--", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) };
            stack.Children.Add(_confText);

            stack.Children.Add(new TextBlock { Text = "FPS", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) });
            _fpsText = new TextBlock { Text = "--", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) };
            stack.Children.Add(_fpsText);

            stack.Children.Add(new TextBlock { Text = "SAMPLES", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) });
            _samplesText = new TextBlock { Text = "0", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(255, 208, 208, 208)) };
            stack.Children.Add(_samplesText);

            panel.Child = stack;
            return panel;
        }

        private UIElement BuildBottomPanel()
        {
            var panel = new Border
            {
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(170, 10, 10, 10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)),
                BorderThickness = new Thickness(1, 1, 1, 0),
            };

            var root = new StackPanel { Spacing = 8 };

            var controls = new Grid();
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

            _cameraSelector = new ComboBox
            {
                DisplayMemberPath = "Name",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _cameraSelector.SelectionChanged += CameraSelector_SelectionChanged;
            controls.Children.Add(_cameraSelector);

            _modeSelector = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            _modeSelector.SelectionChanged += ModeSelector_SelectionChanged;
            _modeSelector.Items.Add(new ComboBoxItem { Content = "SUBTLE", Tag = "Subtle" });
            _modeSelector.Items.Add(new ComboBoxItem { Content = "VIVID", Tag = "Vivid" });
            _modeSelector.Items.Add(new ComboBoxItem { Content = "EXTREME", Tag = "Extreme" });
            _modeSelector.SelectedIndex = 1;
            Grid.SetColumn(_modeSelector, 1);
            controls.Children.Add(_modeSelector);

            var ampRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            ampRow.Children.Add(new TextBlock { Text = "AMP", VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) });

            _ampSlider = new Slider
            {
                Width = 140,
                Minimum = 1,
                Maximum = 10,
                StepFrequency = 0.5,
                Value = 5,
            };
            _ampSlider.ValueChanged += AmpSlider_ValueChanged;
            ampRow.Children.Add(_ampSlider);

            _ampValueText = new TextBlock
            {
                Text = "5.0",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)),
            };
            ampRow.Children.Add(_ampValueText);

            Grid.SetColumn(ampRow, 2);
            controls.Children.Add(ampRow);
            root.Children.Add(controls);

            _waveCanvas = new Canvas
            {
                Height = 72,
                Background = new SolidColorBrush(Color.FromArgb(34, 0, 0, 0)),
            };
            _waveMidLine = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(34, 80, 212, 255)),
                StrokeThickness = 1,
                X1 = 0,
                X2 = 800,
                Y1 = 36,
                Y2 = 36,
            };
            _waveCanvas.Children.Add(_waveMidLine);

            _wavePolyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(255, 72, 200, 255)),
                StrokeThickness = 2,
            };
            _waveCanvas.Children.Add(_wavePolyline);
            root.Children.Add(_waveCanvas);

            _freqText = new TextBlock
            {
                Text = "-- Hz / -- BPM",
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 170, 221, 238)),
            };
            root.Children.Add(_freqText);

            panel.Child = root;
            return panel;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadCamerasAsync();
            UpdateHudIdle();
            UpdateRoiRect();
        }

        private async void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            await StopCameraAsync();
        }

        private async Task LoadCamerasAsync()
        {
            try
            {
                _cameraSelectionReady = false;
                _cameraDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                _cameraSelector.ItemsSource = _cameraDevices;

                if (_cameraDevices.Count > 0)
                {
                    _cameraSelector.SelectedIndex = 0;
                    _selectedDeviceId = _cameraDevices[0].Id;
                }

                _cameraSelectionReady = true;
            }
            catch (Exception ex)
            {
                _statusText.Text = "カメラ列挙エラー: " + ex.Message;
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            await StartCameraAsync();
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await StopCameraAsync();
        }

        private async Task StartCameraAsync()
        {
            if (_isRunning)
            {
                return;
            }

            try
            {
                _mediaCapture = new MediaCapture();
                var initSettings = new MediaCaptureInitializationSettings();
                initSettings.StreamingCaptureMode = StreamingCaptureMode.Video;
                initSettings.MemoryPreference = MediaCaptureMemoryPreference.Cpu;
                initSettings.VideoDeviceId = _selectedDeviceId;
                await _mediaCapture.InitializeAsync(initSettings);

                await _mediaCapture.StartPreviewAsync();

                _processor.Reset();
                _smoothPulse = 0;
                _lastPhase = 0;
                _waveformDisplay.Clear();
                DrawWaveform();

                _timebase.Restart();
                EnsureCaptureTimer();
                _captureTimer.Start();
                _isRunning = true;
                _statusText.Text = "CALIBRATING";
            }
            catch (Exception ex)
            {
                _statusText.Text = "起動失敗: " + ex.Message;
                await StopCameraAsync();
            }
        }

        private async Task StopCameraAsync()
        {
            if (_captureTimer != null)
            {
                _captureTimer.Stop();
            }

            _isRunning = false;
            _isCapturing = false;

            if (_mediaCapture != null)
            {
                try
                {
                    await _mediaCapture.StopPreviewAsync();
                }
                catch
                {
                }

                _mediaCapture.Dispose();
                _mediaCapture = null;
            }

            _previewImage.Source = null;
            _previewBitmap = null;
            _processor.Reset();
            _smoothPulse = 0;
            _waveformDisplay.Clear();
            DrawWaveform();
            UpdateHudIdle();
            ApplyOverlay(0, 0);
        }

        private void EnsureCaptureTimer()
        {
            if (_captureTimer != null)
            {
                return;
            }

            _captureTimer = DispatcherQueue.CreateTimer();
            _captureTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
            _captureTimer.Tick += async delegate { await CaptureTickAsync(); };
        }

        private async Task CaptureTickAsync()
        {
            if (!_isRunning || _mediaCapture == null || _isCapturing)
            {
                return;
            }

            _isCapturing = true;
            try
            {
                using (var frame = new VideoFrame(BitmapPixelFormat.Bgra8, PreviewWidth, PreviewHeight))
                {
                    await _mediaCapture.GetPreviewFrameAsync(frame);
                    var bitmap = frame.SoftwareBitmap;
                    if (bitmap == null)
                    {
                        return;
                    }

                    if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    {
                        bitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
                    }

                    SampleAndAnalyze(bitmap);
                }
            }
            catch (Exception ex)
            {
                _statusText.Text = "計測エラー: " + ex.Message;
            }
            finally
            {
                _isCapturing = false;
            }
        }

        private void SampleAndAnalyze(SoftwareBitmap bitmap)
        {
            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var bytes = width * height * 4;

            if (_framePixels == null || _framePixels.Length != bytes)
            {
                _framePixels = new byte[bytes];
            }

            var buffer = new Windows.Storage.Streams.Buffer((uint)bytes);
            bitmap.CopyToBuffer(buffer);
            var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(_framePixels);
            UpdatePreviewImage(width, height);

            var roiSize = (int)(Math.Min(width, height) * RoiFraction);
            var rx = (width - roiSize) / 2;
            var ry = (height - roiSize) / 2;

            var sumR = 0.0;
            var sumG = 0.0;
            var sumB = 0.0;
            var count = 0;

            for (var y = ry; y < ry + roiSize; y += SampleStride)
            {
                var rowBase = y * width;
                for (var x = rx; x < rx + roiSize; x += SampleStride)
                {
                    var idx = (rowBase + x) * 4;
                    sumB += _framePixels[idx];
                    sumG += _framePixels[idx + 1];
                    sumR += _framePixels[idx + 2];
                    count++;
                }
            }

            if (count <= 0)
            {
                return;
            }

            var sample = new RgbSample();
            sample.T = _timebase.Elapsed.TotalMilliseconds;
            sample.R = sumR / count;
            sample.G = sumG / count;
            sample.B = sumB / count;
            _processor.Push(sample);

            var sampleCount = _processor.Count;
            _samplesText.Text = sampleCount.ToString();

            var result = _processor.Analyze();
            var pulseRaw = 0.0;
            var confidence = 0.0;

            if (result != null)
            {
                if (result.Waveform.Count > 0)
                {
                    pulseRaw = result.Waveform[result.Waveform.Count - 1];
                }

                confidence = result.Confidence;
                _waveformDisplay.Clear();
                var stride = Math.Max(1, result.Waveform.Count / WavePoints);
                for (var i = 0; i < result.Waveform.Count; i += stride)
                {
                    _waveformDisplay.Add(result.Waveform[i]);
                }

                DrawWaveform();
                UpdateVitals(result);

                if (_lastPhase > 5.0 && result.Phase < 1.0)
                {
                    _smoothPulse += 0.08;
                }

                _lastPhase = result.Phase;
            }
            else
            {
                _statusText.Text = sampleCount >= 64 ? "ANALYZING" : "CALIBRATING";
                _bpmText.Text = "--";
                _snrText.Text = "--";
                _confText.Text = "--";
                _fpsText.Text = "--";
                _freqText.Text = "-- Hz / -- BPM";
            }

            _smoothPulse += (pulseRaw - _smoothPulse) * 0.35;
            ApplyOverlay(_smoothPulse, confidence);
        }

        private void UpdateVitals(RppgResult result)
        {
            _statusText.Text = "MEASURING";
            _bpmText.Text = Math.Round(result.Bpm).ToString();
            _snrText.Text = result.Snr.ToString("0.0");
            _confText.Text = Math.Round(result.Confidence * 100).ToString() + "%";
            _fpsText.Text = result.Fps.ToString("0");
            _freqText.Text = result.FreqHz.ToString("0.00") + " Hz / " + Math.Round(result.Bpm) + " BPM";

            if (result.Confidence > 0.6)
            {
                _confText.Foreground = new SolidColorBrush(Colors.LightGreen);
            }
            else if (result.Confidence > 0.3)
            {
                _confText.Foreground = new SolidColorBrush(Colors.Gold);
            }
            else
            {
                _confText.Foreground = new SolidColorBrush(Colors.OrangeRed);
            }
        }

        private void UpdateHudIdle()
        {
            _statusText.Text = "IDLE";
            _bpmText.Text = "--";
            _snrText.Text = "--";
            _confText.Text = "--";
            _fpsText.Text = "--";
            _freqText.Text = "-- Hz / -- BPM";
            _samplesText.Text = "0";
        }

        private void ApplyOverlay(double pulse, double confidence)
        {
            double ampScale;
            Color systole;
            Color diastole;
            double baseOpacity;
            GetModeSpec(out ampScale, out systole, out diastole, out baseOpacity);

            var intensity = Math.Clamp(pulse * (_ampSlider.Value / 3.0) * ampScale, -1.5, 1.5);
            var absI = Math.Abs(intensity);
            var tint = intensity >= 0 ? systole : diastole;
            var opacity = confidence > 0.1 ? Math.Min(0.95, (baseOpacity * absI) + 0.05) : 0;

            _colorWash.Fill = new SolidColorBrush(tint);
            _colorWash.Opacity = opacity;
            _roiRect.Stroke = new SolidColorBrush(tint);
            _roiRect.Opacity = confidence > 0.1 ? 0.45 + Math.Min(0.5, absI * 0.4) : 0.4;
        }

        private void GetModeSpec(out double ampScale, out Color systole, out Color diastole, out double baseOpacity)
        {
            var mode = GetSelectedMode();
            if (mode == PulseMode.Subtle)
            {
                ampScale = 1.0;
                systole = Color.FromArgb(255, 255, 60, 90);
                diastole = Color.FromArgb(255, 80, 200, 255);
                baseOpacity = 0.25;
                return;
            }

            if (mode == PulseMode.Extreme)
            {
                ampScale = 5.0;
                systole = Color.FromArgb(255, 255, 0, 30);
                diastole = Color.FromArgb(255, 0, 80, 255);
                baseOpacity = 0.70;
                return;
            }

            ampScale = 2.5;
            systole = Color.FromArgb(255, 255, 30, 60);
            diastole = Color.FromArgb(255, 40, 120, 255);
            baseOpacity = 0.45;
        }

        private PulseMode GetSelectedMode()
        {
            var item = _modeSelector.SelectedItem as ComboBoxItem;
            if (item != null && item.Tag is string)
            {
                PulseMode mode;
                if (Enum.TryParse((string)item.Tag, out mode))
                {
                    return mode;
                }
            }

            return PulseMode.Vivid;
        }

        private void DrawWaveform()
        {
            var width = _waveCanvas.ActualWidth;
            var height = _waveCanvas.ActualHeight;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            _waveMidLine.X1 = 0;
            _waveMidLine.X2 = width;
            _waveMidLine.Y1 = height / 2.0;
            _waveMidLine.Y2 = height / 2.0;

            var points = new PointCollection();
            if (_waveformDisplay.Count >= 2)
            {
                for (var i = 0; i < _waveformDisplay.Count; i++)
                {
                    var x = (i / (double)(_waveformDisplay.Count - 1)) * width;
                    var y = (height / 2.0) - (_waveformDisplay[i] * height * 0.42);
                    points.Add(new Point(x, y));
                }
            }

            _wavePolyline.Points = points;
        }

        private void UpdatePreviewImage(int width, int height)
        {
            if (_previewBitmap == null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
            {
                _previewBitmap = new WriteableBitmap(width, height);
                _previewImage.Source = _previewBitmap;
            }

            using (var stream = _previewBitmap.PixelBuffer.AsStream())
            {
                stream.Seek(0, SeekOrigin.Begin);
                stream.Write(_framePixels, 0, _framePixels.Length);
            }

            _previewBitmap.Invalidate();
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateRoiRect();
            DrawWaveform();
        }

        private void UpdateRoiRect()
        {
            var width = _overlayCanvas.ActualWidth;
            var height = _overlayCanvas.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var size = Math.Min(width, height) * RoiFraction;
            _roiRect.Width = size;
            _roiRect.Height = size;
            Canvas.SetLeft(_roiRect, (width - size) / 2.0);
            Canvas.SetTop(_roiRect, (height - size) / 2.0);
        }

        private async void CameraSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_cameraSelectionReady)
            {
                return;
            }

            var device = _cameraSelector.SelectedItem as DeviceInformation;
            if (device == null)
            {
                return;
            }

            _selectedDeviceId = device.Id;
            if (_isRunning)
            {
                await StopCameraAsync();
                await StartCameraAsync();
            }
        }

        private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyOverlay(_smoothPulse, 0.3);
        }

        private void AmpSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _ampValueText.Text = e.NewValue.ToString("0.0");
        }
    }
}
