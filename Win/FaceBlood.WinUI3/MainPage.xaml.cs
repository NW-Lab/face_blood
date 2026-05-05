using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
using Windows.Media.Capture.Frames;
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
        private const int LightingHistorySize = 45;

        private readonly RppgProcessor _processor = new RppgProcessor(12);
        private readonly Stopwatch _timebase = new Stopwatch();
        private readonly List<double> _waveformDisplay = new List<double>();
        private readonly Queue<double> _roiLumaHistory = new Queue<double>();

        private MediaCapture _mediaCapture;
        private MediaFrameReader _frameReader;
        private DispatcherQueueTimer _captureTimer;
        private IReadOnlyList<DeviceInformation> _cameraDevices = new List<DeviceInformation>();
        private string _selectedDeviceId;
        private bool _isRunning;
        private bool _isCapturing;
        private bool _cameraSelectionReady;
        private bool _isPreviewMirrored;

        private double _smoothPulse;
        private double _lastPhase;
        private byte[] _framePixels;
        private SoftwareBitmap _latestFrame;
        private readonly object _frameLock = new object();

        private Image _previewImage;
        private WriteableBitmap _previewBitmap;
        private Rectangle _colorWash;
        private Canvas _overlayCanvas;
        private Rectangle _roiRect;
        private TextBlock _statusText;
        private TextBlock _lightingWarningText;
        private TextBlock _bpmText;
        private TextBlock _snrText;
        private TextBlock _confText;
        private TextBlock _fpsText;
        private TextBlock _samplesText;
        private TextBlock _freqText;
        private Button _mirrorButton;
        private ComboBox _cameraSelector;
        private ComboBox _modeSelector;
        private Slider _ampSlider;
        private TextBlock _ampValueText;
        private Canvas _waveCanvas;
        private Line _waveMidLine;
        private Line _waveNowLine;
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

            var statusStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
            };
            statusStack.Children.Add(_statusText);

            _lightingWarningText = new TextBlock
            {
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 196, 92)),
                MaxWidth = 520,
                Text = string.Empty,
            };
            statusStack.Children.Add(_lightingWarningText);
            topBar.Children.Add(statusStack);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            var buttonShell = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(168, 8, 12, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 141, 235, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                Child = buttons,
            };
            Grid.SetColumn(buttonShell, 1);

            var startButton = new Button
            {
                Content = "計測開始",
                Background = new SolidColorBrush(Color.FromArgb(230, 20, 30, 40)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(140, 141, 235, 255)),
                BorderThickness = new Thickness(1),
            };
            startButton.Click += StartButton_Click;
            var stopButton = new Button
            {
                Content = "停止",
                Background = new SolidColorBrush(Color.FromArgb(220, 48, 20, 24)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 140, 140)),
                BorderThickness = new Thickness(1),
            };
            stopButton.Click += StopButton_Click;

            _mirrorButton = new Button
            {
                Content = "鏡像: OFF",
                Background = new SolidColorBrush(Color.FromArgb(220, 20, 30, 40)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(140, 141, 235, 255)),
                BorderThickness = new Thickness(1),
            };
            _mirrorButton.Click += MirrorButton_Click;

            buttons.Children.Add(startButton);
            buttons.Children.Add(stopButton);
            buttons.Children.Add(_mirrorButton);
            topBar.Children.Add(buttonShell);
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
            stack.Children.Add(new TextBlock
            {
                Text = "信号対雑音比(dB)。高いほど脈波がノイズに埋もれていない。",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(204, 170, 221, 238)),
                MaxWidth = 210,
            });

            stack.Children.Add(new TextBlock { Text = "CONF", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) });
            _confText = new TextBlock { Text = "--", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 141, 235, 255)) };
            stack.Children.Add(_confText);
            stack.Children.Add(new TextBlock
            {
                Text = "推定信頼度(0-100%)。高いほどBPM推定が安定している。",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(204, 170, 221, 238)),
                MaxWidth = 210,
            });

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

            _waveNowLine = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(210, 240, 250, 255)),
                StrokeThickness = 1.5,
                X1 = 799,
                X2 = 799,
                Y1 = 0,
                Y2 = 72,
            };
            _waveCanvas.Children.Add(_waveNowLine);

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

            if (_cameraDevices.Count == 0)
            {
                _statusText.Text = "カメラが見つからない";
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedDeviceId))
            {
                _selectedDeviceId = _cameraDevices[0].Id;
            }

            try
            {
                _mediaCapture = new MediaCapture();
                var initSettings = new MediaCaptureInitializationSettings();
                initSettings.StreamingCaptureMode = StreamingCaptureMode.Video;
                initSettings.MemoryPreference = MediaCaptureMemoryPreference.Cpu;
                initSettings.VideoDeviceId = _selectedDeviceId;
                await _mediaCapture.InitializeAsync(initSettings);

                var colorSource = _mediaCapture.FrameSources.Values.FirstOrDefault(
                    source => source.Info.SourceKind == MediaFrameSourceKind.Color);
                if (colorSource == null)
                {
                    throw new InvalidOperationException("Color frame source not found");
                }

                _frameReader = await _mediaCapture.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
                _frameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                _frameReader.FrameArrived += FrameReader_FrameArrived;
                var readerStatus = await _frameReader.StartAsync();
                if (readerStatus != MediaFrameReaderStartStatus.Success)
                {
                    throw new InvalidOperationException("FrameReader start failed: " + readerStatus);
                }

                _processor.Reset();
                _smoothPulse = 0;
                _lastPhase = 0;
                _waveformDisplay.Clear();
                _roiLumaHistory.Clear();
                _lightingWarningText.Text = string.Empty;
                DrawWaveform();

                _timebase.Restart();
                EnsureCaptureTimer();
                _captureTimer.Start();
                _isRunning = true;
                _statusText.Text = "CALIBRATING";
            }
            catch (Exception ex)
            {
                await StopCameraAsync(false);

                var hr = ex.HResult;
                if (ex is UnauthorizedAccessException || hr == unchecked((int)0x80070005))
                {
                    _statusText.Text = "起動失敗: カメラ許可をWindows設定でONにして";
                }
                else
                {
                    _statusText.Text = "起動失敗: " + ex.Message + " (0x" + hr.ToString("X8") + ")";
                }
            }
        }

        private async Task StopCameraAsync(bool resetHud = true)
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
                    if (_frameReader != null)
                    {
                        _frameReader.FrameArrived -= FrameReader_FrameArrived;
                        await _frameReader.StopAsync();
                        _frameReader.Dispose();
                        _frameReader = null;
                    }
                }
                catch
                {
                }

                _mediaCapture.Dispose();
                _mediaCapture = null;
            }

            lock (_frameLock)
            {
                _latestFrame?.Dispose();
                _latestFrame = null;
            }

            _previewImage.Source = null;
            _previewBitmap = null;
            _processor.Reset();
            _smoothPulse = 0;
            _waveformDisplay.Clear();
            _roiLumaHistory.Clear();
            _lightingWarningText.Text = string.Empty;
            DrawWaveform();
            if (resetHud)
            {
                UpdateHudIdle();
            }
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
                SoftwareBitmap frameCopy;
                lock (_frameLock)
                {
                    frameCopy = _latestFrame == null ? null : SoftwareBitmap.Copy(_latestFrame);
                }

                if (frameCopy == null)
                {
                    return;
                }

                using (frameCopy)
                {
                    SampleAndAnalyze(frameCopy);
                }
            }
            catch (COMException ex)
            {
                _statusText.Text = "計測エラー: " + ex.Message + " (0x" + ex.HResult.ToString("X8") + ")";
                await StopCameraAsync(false);
            }
            catch (Exception ex)
            {
                _statusText.Text = "計測エラー: " + ex.Message + " (0x" + ex.HResult.ToString("X8") + ")";
                await StopCameraAsync(false);
            }
            finally
            {
                _isCapturing = false;
            }
        }

        private void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            using (var frameRef = sender.TryAcquireLatestFrame())
            {
                var bitmap = frameRef?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap == null)
                {
                    return;
                }

                SoftwareBitmap prepared;
                if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                {
                    prepared = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }
                else
                {
                    prepared = SoftwareBitmap.Copy(bitmap);
                }

                lock (_frameLock)
                {
                    _latestFrame?.Dispose();
                    _latestFrame = prepared;
                }
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
            var sumLuma = 0.0;
            var leftLuma = 0.0;
            var rightLuma = 0.0;
            var leftCount = 0;
            var rightCount = 0;
            var count = 0;
            var roiMidX = rx + (roiSize / 2);

            for (var y = ry; y < ry + roiSize; y += SampleStride)
            {
                var rowBase = y * width;
                for (var x = rx; x < rx + roiSize; x += SampleStride)
                {
                    var idx = (rowBase + x) * 4;
                    var b = _framePixels[idx];
                    var g = _framePixels[idx + 1];
                    var r = _framePixels[idx + 2];
                    sumB += b;
                    sumG += g;
                    sumR += r;

                    var luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                    sumLuma += luma;
                    if (x < roiMidX)
                    {
                        leftLuma += luma;
                        leftCount++;
                    }
                    else
                    {
                        rightLuma += luma;
                        rightCount++;
                    }
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

            var meanLuma = sumLuma / count;
            var meanLeft = leftCount > 0 ? leftLuma / leftCount : meanLuma;
            var meanRight = rightCount > 0 ? rightLuma / rightCount : meanLuma;
            UpdateLightingWarnings(meanLuma, meanLeft, meanRight);

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
            _lightingWarningText.Text = string.Empty;
            _bpmText.Text = "--";
            _snrText.Text = "--";
            _confText.Text = "--";
            _fpsText.Text = "--";
            _freqText.Text = "-- Hz / -- BPM";
            _samplesText.Text = "0";
        }

        private void UpdateLightingWarnings(double meanLuma, double meanLeft, double meanRight)
        {
            _roiLumaHistory.Enqueue(meanLuma);
            while (_roiLumaHistory.Count > LightingHistorySize)
            {
                _roiLumaHistory.Dequeue();
            }

            var lowLight = meanLuma < 58.0;
            var imbalance = Math.Abs(meanLeft - meanRight) / Math.Max(1.0, meanLuma);
            var unevenLighting = imbalance > 0.22;
            var flicker = DetectFlicker();

            var messages = new List<string>();
            if (unevenLighting)
            {
                messages.Add("照明ムラ注意: 顔の左右をなるべく均一に照らして");
            }

            if (lowLight)
            {
                messages.Add("環境光不足: 画面より部屋の明るさを上げて");
            }

            if (flicker)
            {
                messages.Add("ちらつき光源の疑い: 点滅LED/古い蛍光灯を避けて");
            }

            _lightingWarningText.Text = messages.Count == 0 ? string.Empty : string.Join(" / ", messages);
        }

        private bool DetectFlicker()
        {
            if (_roiLumaHistory.Count < 18)
            {
                return false;
            }

            var values = _roiLumaHistory.ToArray();
            var mean = values.Average();
            if (mean < 1)
            {
                return false;
            }

            var signChanges = 0;
            var absDiffSum = 0.0;
            var lastSign = 0;
            for (var i = 1; i < values.Length; i++)
            {
                var diff = values[i] - values[i - 1];
                absDiffSum += Math.Abs(diff);
                var sign = diff > 0 ? 1 : (diff < 0 ? -1 : 0);
                if (sign != 0 && lastSign != 0 && sign != lastSign)
                {
                    signChanges++;
                }

                if (sign != 0)
                {
                    lastSign = sign;
                }
            }

            var relJitter = absDiffSum / ((values.Length - 1) * mean);
            var frequentFlip = signChanges > ((values.Length - 1) * 0.42);
            return relJitter > 0.06 && frequentFlip;
        }

        private void ApplyOverlay(double pulse, double confidence)
        {
            if (_ampSlider == null || _colorWash == null || _roiRect == null)
            {
                return;
            }

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
            if (_modeSelector == null)
            {
                return PulseMode.Vivid;
            }

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

            var nowX = Math.Max(0, width - 1);
            _waveNowLine.X1 = nowX;
            _waveNowLine.X2 = nowX;
            _waveNowLine.Y1 = 0;
            _waveNowLine.Y2 = height;

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
            UpdatePreviewTransform();
        }

        private void MirrorButton_Click(object sender, RoutedEventArgs e)
        {
            _isPreviewMirrored = !_isPreviewMirrored;
            _mirrorButton.Content = _isPreviewMirrored ? "鏡像: ON" : "鏡像: OFF";
            UpdatePreviewTransform();
        }

        private void UpdatePreviewTransform()
        {
            if (_previewImage == null)
            {
                return;
            }

            _previewImage.RenderTransformOrigin = new Point(0.5, 0.5);
            _previewImage.RenderTransform = new ScaleTransform
            {
                ScaleX = _isPreviewMirrored ? -1 : 1,
                ScaleY = 1,
            };
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
