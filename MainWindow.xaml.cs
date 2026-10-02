using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using Microsoft.Win32;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;

namespace QuickEditor;

public sealed partial class MainWindow : Window
{
    private sealed class AudioWaveformBar
    {
        public required Microsoft.UI.Xaml.Shapes.Rectangle Shape { get; init; }
        public required double Time { get; init; }
        public required bool InSelection { get; init; }
        public bool HasPlayed { get; set; }
    }

    private enum TimelineDragMode
    {
        None,
        Start,
        End,
        Playhead
    }

    private enum CropDragMode
    {
        None,
        Drawing,
        Moving,
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v"
    };
    private static readonly string[] SupportedExtensions =
    [
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v",
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma", ".opus"
    ];

    private static readonly SolidColorBrush PlayheadRedBrush = new(Color.FromArgb(255, 255, 59, 48));

    private readonly MediaPlayer _player = new() { AutoPlay = false };
    private readonly MediaExportService _exportService = new();
    private readonly WaveformService _waveformService = new();
    private readonly List<string> _previewFiles = [];
    private readonly List<AudioWaveformBar> _audioWaveformBars = [];
    private IReadOnlyList<float> _waveformPeaks = Array.Empty<float>();
    private Microsoft.UI.Xaml.Shapes.Rectangle? _audioWaveformPlayhead;
    private string? _sourcePath;
    private bool _showingEditedPreview;
    private bool _startPreviewWhenReady;
    private bool _isDarkTheme;
    private bool _isThemeAnimating;
    private readonly Windows.UI.ViewManagement.UISettings? _uiSettings;
    private bool _waveformReady;
    private double _durationSeconds;
    private double _trimStartSeconds;
    private double _trimEndSeconds;
    private double _playheadSeconds;
    private double _previewPlaybackDuration;
    private double _videoAspectRatio = 16d / 9d;
    private double? _cropAspectRatio;
    private double _lastVolume = 1;
    private double _undoStart;
    private double _undoEnd;
    private bool _hasUndo;
    private bool _cropMode;
    private bool _hasCropSelection;
    private CropDragMode _cropDragMode;
    private Windows.Foundation.Point _cropDragStart;
    private Windows.Foundation.Rect _cropDragStartRectangle;
    private Windows.Foundation.Rect? _cropDragRectangle;
    private CropRegion _cropRegion = new(0, 0, 1, 1);
    private double? _snapGuideVerticalX;
    private double? _snapGuideHorizontalY;
    private TimelineDragMode _timelineDragMode;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _waveformCancellation;
    private Grid? _playheadMarker;
    private CanvasRenderTarget? _renderTarget;
    private bool _isReady;
    private AudioPitchEngine? _audioEngine;
    private bool _userMuted;
    private DateTime _lastDriftCorrection = DateTime.MinValue;

    public MainWindow(string? initialFile = null)
    {
        InitializeComponent();
        _isReady = true;
        SetupTitleBar();
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        SpeedSlider.Maximum = 2;
        SpeedSlider.Value = 1;
        SpeedSlider.Minimum = 0.5;
        _isDarkTheme = DetectWindowsDarkTheme();
        ApplyThemeState();

        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (_, _) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var systemIsDark = DetectWindowsDarkTheme();
                    if (systemIsDark != _isDarkTheme)
                    {
                        AnimateThemeToggle();
                    }
                });
            };
        }
        catch
        {
            // System color change listener is optional fallback
        }

        Title = "Quick Editor";
        SetAppIcon();
        Preview.SetMediaPlayer(_player);
        _player.Volume = VolumeSlider.Value;
        _player.IsMuted = false;
        _userMuted = false;
        UpdateVolumeControl();
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaFailed += Player_MediaFailed;
        _player.PlaybackSession.NaturalDurationChanged += Player_NaturalDurationChanged;
        _player.VideoFrameAvailable += Player_VideoFrameAvailable;
        Closed += MainWindow_Closed;
        PreviewSurface.DragOver += PreviewSurface_DragOver;
        PreviewSurface.Drop += PreviewSurface_Drop;
        TimelineCanvas.IsHitTestVisible = false;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => UpdateCurrentPosition();
        timer.Start();

        if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
        {
            _ = LoadFileAsync(initialFile);
        }
    }

    private void SetAppIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // Ignore if setting the icon fails on some platforms
        }
    }

    private static bool DetectWindowsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int appsUseLightTheme)
            {
                return appsUseLightTheme == 0;
            }
        }
        catch
        {
            // Fall through to UISettings
        }

        try
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            var bg = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return (299 * bg.R + 587 * bg.G + 114 * bg.B) < 128000;
        }
        catch
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Dark;
        }
    }

    private void ApplyThemeState()
    {
        RootLayout.RequestedTheme = _isDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        ThemeToggleGlyph.Glyph = _isDarkTheme ? "\uE706" : "\uE708";
        var nextTheme = _isDarkTheme ? "light" : "dark";
        ToolTipService.SetToolTip(ThemeToggleButton, $"Switch to {nextTheme} theme");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThemeToggleButton, $"Switch to {nextTheme} theme");
        DrawTimeline();
        DrawAudioWaveformViewport();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint WM_NCHITTEST = 0x0084;
    private const nint HTCLIENT = 1;
    private const nint HTCAPTION = 2;

    private SubclassProc? _titleBarSubclassProc;
    private readonly List<RECT> _titleBarButtonRects = new();

    private void SetupTitleBar()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, false);
            }

            var hWnd = WindowNative.GetWindowHandle(this);
            _titleBarSubclassProc = TitleBarSubclassProc;
            SetWindowSubclass(hWnd, _titleBarSubclassProc, 101, 0);

            AppTitleBar.SizeChanged += (s, e) => UpdateTitleBarRegions();
            AppTitleBar.Loaded += (s, e) => UpdateTitleBarRegions();

            AppWindow.Changed += (s, e) =>
            {
                if (e.DidPresenterChange || e.DidSizeChange)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateMaximizeGlyph();
                        UpdateTitleBarRegions();
                    });
                }
            };
            UpdateMaximizeGlyph();
        }
        catch
        {
            // Fallback to default title bar if customization is not supported
        }
    }

    private void UpdateTitleBarRegions()
    {
        if (AppTitleBar is null || RootLayout?.XamlRoot is null) return;
        double scale = RootLayout.XamlRoot.RasterizationScale;
        var buttons = new FrameworkElement?[]
        {
            ThemeToggleButton,
            OpenButton,
            ExportButton,
            MinimizeButton,
            MaximizeButton,
            CloseButton
        };

        var list = new List<RECT>();
        foreach (var btn in buttons)
        {
            if (btn is not null && btn.ActualWidth > 0 && btn.ActualHeight > 0)
            {
                try
                {
                    var transform = btn.TransformToVisual(AppTitleBar);
                    var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    list.Add(new RECT
                    {
                        Left = (int)Math.Round(origin.X * scale),
                        Top = (int)Math.Round(origin.Y * scale),
                        Right = (int)Math.Round((origin.X + btn.ActualWidth) * scale),
                        Bottom = (int)Math.Round((origin.Y + btn.ActualHeight) * scale)
                    });
                }
                catch { }
            }
        }

        lock (_titleBarButtonRects)
        {
            _titleBarButtonRects.Clear();
            _titleBarButtonRects.AddRange(list);
        }

        try
        {
            int titleBarHeight = (int)Math.Round(AppTitleBar.ActualHeight * scale);
            if (titleBarHeight <= 0) titleBarHeight = (int)Math.Round(48.0 * scale);
            int dragWidth = list.Count > 0 ? list[0].Left : (int)Math.Round(AppTitleBar.ActualWidth * scale);
            if (dragWidth > 0)
            {
                AppWindow.TitleBar.SetDragRectangles([new Windows.Graphics.RectInt32(0, 0, dragWidth, titleBarHeight)]);
            }
        }
        catch { }
    }

    private nint TitleBarSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (uMsg == WM_NCHITTEST)
        {
            var def = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            if (def != HTCLIENT)
            {
                return def;
            }

            int screenX = unchecked((short)(long)lParam);
            int screenY = unchecked((short)((long)lParam >> 16));

            var pt = new POINT { X = screenX, Y = screenY };
            ScreenToClient(hWnd, ref pt);

            double scale = RootLayout?.XamlRoot?.RasterizationScale ?? 1.0;
            int titleBarHeight = (int)Math.Round((AppTitleBar?.ActualHeight ?? 48.0) * scale);
            if (titleBarHeight <= 0) titleBarHeight = (int)Math.Round(48.0 * scale);

            if (pt.Y >= 0 && pt.Y < titleBarHeight)
            {
                bool isOverButton = false;
                lock (_titleBarButtonRects)
                {
                    foreach (var r in _titleBarButtonRects)
                    {
                        if (pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom)
                        {
                            isOverButton = true;
                            break;
                        }
                    }
                }

                if (!isOverButton)
                {
                    return HTCAPTION;
                }
            }

            return HTCLIENT;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ToggleMaximize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                presenter.Restore();
            }
            else
            {
                presenter.Maximize();
            }
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeGlyph()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && MaximizeGlyph is not null)
        {
            bool isMaximized = presenter.State == OverlappedPresenterState.Maximized;
            MaximizeGlyph.Glyph = isMaximized ? "\uE923" : "\uE922";
            ToolTipService.SetToolTip(MaximizeButton, isMaximized ? "Restore" : "Maximize");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MaximizeButton, isMaximized ? "Restore" : "Maximize");
        }
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        AnimateThemeToggle();
    }

    private void AnimateThemeToggle()
    {
        if (_isThemeAnimating) return;
        _isThemeAnimating = true;

        var sw = Stopwatch.StartNew();
        const double phase1Duration = 180.0;
        const double phase2Duration = 220.0;
        const double totalDuration = phase1Duration + phase2Duration;
        bool swapped = false;

        _isDarkTheme = !_isDarkTheme;

        void OnRendering(object? sender, object e)
        {
            double elapsed = sw.Elapsed.TotalMilliseconds;

            if (elapsed < phase1Duration)
            {
                // Phase 1: Spin 0 -> 180 deg, shrink scale 1.0 -> 0.0 with ease-in
                double p = Math.Clamp(elapsed / phase1Duration, 0.0, 1.0);
                double ease = p * p;
                ThemeGlyphRotate.Angle = ease * 180.0;
                double s = Math.Max(0.0, 1.0 - ease);
                ThemeGlyphScale.ScaleX = s;
                ThemeGlyphScale.ScaleY = s;
            }
            else if (elapsed < totalDuration)
            {
                if (!swapped)
                {
                    swapped = true;
                    ApplyThemeState();
                }

                // Phase 2: Spin 180 -> 360 deg, bounce scale 0.0 -> 1.0 with BackEase out
                double p = Math.Clamp((elapsed - phase1Duration) / phase2Duration, 0.0, 1.0);
                double t = p - 1.0;
                const double overshoot = 1.70158;
                double easeOutBack = 1.0 + (t * t * ((overshoot + 1.0) * t + overshoot));

                ThemeGlyphRotate.Angle = 180.0 + (p * 180.0);
                ThemeGlyphScale.ScaleX = Math.Max(0.0, easeOutBack);
                ThemeGlyphScale.ScaleY = Math.Max(0.0, easeOutBack);
            }
            else
            {
                CompositionTarget.Rendering -= OnRendering;
                if (!swapped)
                {
                    ApplyThemeState();
                }
                ThemeGlyphRotate.Angle = 0;
                ThemeGlyphScale.ScaleX = 1.0;
                ThemeGlyphScale.ScaleY = 1.0;
                _isThemeAnimating = false;
            }
        }

        CompositionTarget.Rendering += OnRendering;
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in SupportedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await LoadFileAsync(file.Path);
        }
    }

    private async Task LoadFileAsync(string path)
    {
        try
        {
            _player.Pause();
            Preview.Source = null;
            _player.Source = null;
            _showingEditedPreview = false;
            _startPreviewWhenReady = false;
            _waveformCancellation?.Cancel();
            _waveformCancellation = null;
            _waveformPeaks = Array.Empty<float>();
            _waveformReady = false;
            _audioWaveformPlayhead = null;
            CleanupPreviewFiles();
            _sourcePath = path;
            _durationSeconds = 0;
            _trimStartSeconds = 0;
            _trimEndSeconds = 0;
            _playheadSeconds = 0;
            _videoAspectRatio = 16d / 9d;
            _hasUndo = false;
            UndoButton.IsEnabled = false;
            DrawTimeline();
            var isVideo = VideoExtensions.Contains(Path.GetExtension(path));
            _cropRegion = new CropRegion(0, 0, 1, 1);
            _hasCropSelection = false;
            _cropAspectRatio = null;
            _cropMode = false;
            _cropDragMode = CropDragMode.None;
            UpdateCropOverlayInteractionState();
            CropModeButton.IsEnabled = false;
            CropModeButton.Content = "Crop video";
            CropPresetComboBox.SelectedIndex = 0;
            CropPresetComboBox.IsEnabled = false;
            CropHintText.Text = "Choose a ratio, then drag over the video.";
            _player.IsVideoFrameServerEnabled = isVideo;
            VideoCanvas.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            Preview.Visibility = Visibility.Collapsed;
            AudioWaveformViewport.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
            DrawAudioWaveformViewport();
            AdjustmentTabsGrid.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            SelectAdjustmentTab(!isVideo);
            _audioEngine?.Dispose();
            _audioEngine = new AudioPitchEngine();
            _audioEngine.PlaybackStopped += AudioEngine_PlaybackStopped;
            _audioEngine.Load(path);
            _audioEngine.SetVolume(VolumeSlider?.Value ?? 1.0, _userMuted);
            AudioPitchEngine.LogAudio($"MainWindow.LoadFileAsync: HasAudioTrack={_audioEngine.HasAudioTrack}, Device={_audioEngine.OutputDeviceName}, _userMuted={_userMuted}");
            if (_audioEngine.HasAudioTrack)
            {
                _player.IsMuted = true;
                _player.Volume = 0;
            }
            else
            {
                _player.IsMuted = _userMuted;
                _player.Volume = _userMuted ? 0 : (VolumeSlider?.Value ?? 1.0);
            }
            ResetVideoAdjustments();
            ResetAudioAdjustments();
            EmptyStateSurface.Visibility = Visibility.Collapsed;
            FileNameText.Text = Path.GetFileName(path);
            MetadataText.Text = $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} file\n{new FileInfo(path).Length / 1024d / 1024d:0.0} MB";
            FooterText.Text = path;
            StatusText.Text = "Loading preview...";
            ExportButton.IsEnabled = false;
            TimelineCanvas.IsHitTestVisible = false;
            PlaybackButton.IsEnabled = false;
            PreviewTrimButton.IsEnabled = false;

            MediaSource mediaSource;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                mediaSource = MediaSource.CreateFromStorageFile(file);
            }
            catch
            {
                mediaSource = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path)));
            }

            _player.Source = mediaSource;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open this file: {ex.Message}";
            ShowEmptyState("Couldn't open this file", ex.Message, "\uE783");
            AudioWaveformViewport.Visibility = Visibility.Collapsed;
            Preview.Visibility = Visibility.Visible;
            ExportButton.IsEnabled = false;
        }
    }

    private void AudioEngine_PlaybackStopped(object? sender, NAudio.Wave.StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            AudioPitchEngine.LogAudio($"MainWindow fallback: AudioEngine stopped with exception: {e.Exception.Message}. Unmuting _player.");
            DispatcherQueue.TryEnqueue(() =>
            {
                _player.IsMuted = _userMuted;
                _player.Volume = _userMuted ? 0 : (VolumeSlider?.Value ?? 1.0);
            });
        }
    }

    private void Player_MediaOpened(MediaPlayer sender, object args) => HandleMediaReady(sender);

    private void Player_NaturalDurationChanged(MediaPlaybackSession sender, object args) => HandleMediaReady(_player);

    private void HandleMediaReady(MediaPlayer sender)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_showingEditedPreview)
            {
                if (_startPreviewWhenReady)
                {
                    _startPreviewWhenReady = false;
                    sender.Play();
                }

                return;
            }

            var duration = sender.PlaybackSession.NaturalDuration.TotalSeconds;
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0)
            {
                return;
            }

            if (Math.Abs(_durationSeconds - duration) < 0.001 && ExportButton.IsEnabled)
            {
                return;
            }

            _durationSeconds = duration;

            var naturalWidth = sender.PlaybackSession.NaturalVideoWidth;
            var naturalHeight = sender.PlaybackSession.NaturalVideoHeight;
            if (naturalWidth > 0 && naturalHeight > 0)
            {
                _videoAspectRatio = (double)naturalWidth / naturalHeight;
            }

            _trimStartSeconds = 0;
            _trimEndSeconds = _durationSeconds;
            _playheadSeconds = 0;
            TimelineCanvas.IsHitTestVisible = true;
            ExportButton.IsEnabled = true;
            PlaybackButton.IsEnabled = true;
            PreviewTrimButton.IsEnabled = true;
            var isVideo = VideoExtensions.Contains(Path.GetExtension(_sourcePath ?? string.Empty));
            CropModeButton.IsEnabled = isVideo;
            AdjustmentTabsGrid.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            SelectAdjustmentTab(!isVideo ? true : _isAudioTabSelected);
            MetadataText.Text = $"{(isVideo ? "Video" : "Audio")}\n{FormatTime(_durationSeconds)} long";
            StatusText.Text = "Preview ready";
            EmptyStateSurface.Visibility = Visibility.Collapsed;
            UpdateTrimLabels();
            DrawTimeline();
            DrawAudioWaveformViewport();
            DrawCropOverlay();
            ApplyVideoPreviewEffects();

            if (isVideo)
            {
                try
                {
                    EnsureRenderTarget();
                    _player.IsVideoFrameServerEnabled = true;
                    VideoCanvas.Visibility = Visibility.Visible;
                    Preview.Visibility = Visibility.Collapsed;
                    sender.PlaybackSession.Position = TimeSpan.FromMilliseconds(1);
                    sender.PlaybackSession.Position = TimeSpan.Zero;
                    RequestFrameRender();
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(_sourcePath))
            {
                _ = LoadWaveformAsync(_sourcePath, _durationSeconds);
            }
        });
    }

    private async Task LoadWaveformAsync(string inputPath, double durationSeconds)
    {
        _waveformCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _waveformCancellation = cancellation;

        try
        {
            var peaks = await _waveformService.CreatePeaksAsync(inputPath, durationSeconds, cancellation.Token);
            if (!cancellation.IsCancellationRequested &&
                string.Equals(_sourcePath, inputPath, StringComparison.OrdinalIgnoreCase))
            {
                _waveformPeaks = peaks;
                _waveformReady = true;
                DrawTimeline();
                DrawAudioWaveformViewport();
                ApplyAudioPitchAndSpeed();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _waveformPeaks = Array.Empty<float>();
            _waveformReady = true;
            DispatcherQueue.TryEnqueue(DrawAudioWaveformViewport);
        }
        finally
        {
            if (ReferenceEquals(_waveformCancellation, cancellation))
            {
                _waveformCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var msg = args.ErrorMessage;
            if (string.IsNullOrWhiteSpace(msg))
            {
                msg = $"Error 0x{args.ExtendedErrorCode?.HResult:X8}";
            }

            StatusText.Text = $"Preview couldn't be loaded: {msg}";
            ShowEmptyState(
                "Couldn't preview this media file",
                $"Format or codec might not be supported natively by Windows for direct preview ({msg}). You can still export or convert it with Quick Editor.",
                "\uE783");
            ExportButton.IsEnabled = !string.IsNullOrWhiteSpace(_sourcePath) && File.Exists(_sourcePath);
            PlaybackButton.IsEnabled = false;
            PreviewTrimButton.IsEnabled = false;
        });
    }

    private void ShowEmptyState(string title = "Drop a clip here to get started", string? subtitle = null, string glyph = "\uE714")
    {
        EmptyStateIcon.Glyph = glyph;
        EmptyStateTitle.Text = title;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            EmptyStateSubtitle.Text = subtitle;
            EmptyStateSubtitle.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyStateSubtitle.Visibility = Visibility.Collapsed;
        }

        EmptyStateSurface.Visibility = Visibility.Visible;
        AdjustmentTabsGrid.Visibility = Visibility.Collapsed;
        VideoAdjustments.Visibility = Visibility.Collapsed;
        AudioAdjustments.Visibility = Visibility.Collapsed;
        UpdateAudioActiveIndicator(false);
        _audioEngine?.Dispose();
        _audioEngine = null;
    }

    private void CropModeButton_Click(object sender, RoutedEventArgs e)
    {
        _cropMode = !_cropMode;
        UpdateCropOverlayInteractionState();
        CropPresetComboBox.IsEnabled = _cropMode;
        CropModeButton.Content = _cropMode ? "Done" : (_hasCropSelection ? "Edit crop" : "Crop video");
        CropHintText.Text = _cropMode
            ? "Drag inside to move or use handles to resize, then click Done."
            : (_hasCropSelection
                ? "Crop applied (zoomed in viewport). Click 'Edit crop' to adjust."
                : "Choose a ratio, then drag over the video.");
        VideoCanvas?.Invalidate();
        DrawCropOverlay();
    }

    private void ResetCropButton_Click(object sender, RoutedEventArgs e)
    {
        _cropRegion = new CropRegion(0, 0, 1, 1);
        _hasCropSelection = false;
        _cropDragRectangle = null;
        _cropMode = false;
        CropModeButton.Content = "Crop video";
        CropPresetComboBox.IsEnabled = false;
        UpdateCropOverlayInteractionState();
        CropHintText.Text = "Choose a ratio, then drag over the video.";
        VideoCanvas?.Invalidate();
        DrawCropOverlay();
    }

    private void UpdateCropOverlayInteractionState()
    {
        CropOverlay.Visibility = _cropMode ? Visibility.Visible : Visibility.Collapsed;
        CropOverlay.IsHitTestVisible = _cropMode;
        if (ResetCropButton is not null)
        {
            ResetCropButton.Visibility = _hasCropSelection ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void CropPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (CropPresetComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _cropAspectRatio = double.TryParse(tag, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var aspectRatio)
            ? aspectRatio
            : null;

        if (CropHintText is not null && _cropMode)
        {
            CropHintText.Text = _cropAspectRatio.HasValue
                ? $"Drag to crop at {((ComboBoxItem)CropPresetComboBox.SelectedItem).Content}."
                : "Drag to draw a freeform crop.";
        }
    }

    private void CropOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropMode)
        {
            return;
        }

        var frame = GetVideoFrameBounds(ignoreCrop: true);
        var point = e.GetCurrentPoint(CropOverlay).Position;
        if (!frame.Contains(point))
        {
            return;
        }

        _cropDragStart = point;
        _cropDragStartRectangle = GetCropSelectionBounds(frame);
        _snapGuideVerticalX = null;
        _snapGuideHorizontalY = null;
        var handleMode = _hasCropSelection ? GetCropHandleMode(point, _cropDragStartRectangle) : CropDragMode.None;
        if (handleMode != CropDragMode.None)
        {
            _cropDragMode = handleMode;
            _cropDragRectangle = _cropDragStartRectangle;
        }
        else if (_hasCropSelection && _cropDragStartRectangle.Contains(point))
        {
            _cropDragMode = CropDragMode.Moving;
            _cropDragRectangle = _cropDragStartRectangle;
        }
        else
        {
            _cropDragMode = CropDragMode.Drawing;
            _cropDragRectangle = new Windows.Foundation.Rect(point.X, point.Y, 0, 0);
        }

        CropOverlay.CapturePointer(e.Pointer);
        DrawCropOverlay();
        e.Handled = true;
    }

    private void CropOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_cropDragMode == CropDragMode.None)
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlay).Position;
        var nextRectangle = _cropDragMode switch
        {
            CropDragMode.Drawing => CreateCropDragRectangle(point),
            CropDragMode.Moving => MoveCropRectangle(point),
            _ => ResizeCropRectangle(point)
        };

        if (nextRectangle.Width > 0 && nextRectangle.Height > 0)
        {
            _cropDragRectangle = nextRectangle;
            DrawCropOverlay();
        }
    }

    private void CropOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_cropDragMode == CropDragMode.None)
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlay).Position;
        var nextRectangle = _cropDragMode switch
        {
            CropDragMode.Drawing => CreateCropDragRectangle(point),
            CropDragMode.Moving => MoveCropRectangle(point),
            _ => ResizeCropRectangle(point)
        };

        _cropDragRectangle = nextRectangle.Width > 0 && nextRectangle.Height > 0 ? nextRectangle : null;
        _cropDragMode = CropDragMode.None;
        _snapGuideVerticalX = null;
        _snapGuideHorizontalY = null;
        CropOverlay.ReleasePointerCapture(e.Pointer);

        if (_cropDragRectangle is { Width: >= 20, Height: >= 20 } selection)
        {
            var frame = GetVideoFrameBounds(ignoreCrop: true);
            _cropRegion = new CropRegion(
                Math.Clamp((selection.X - frame.X) / frame.Width, 0, 1),
                Math.Clamp((selection.Y - frame.Y) / frame.Height, 0, 1),
                Math.Clamp(selection.Width / frame.Width, 0.001, 1),
                Math.Clamp(selection.Height / frame.Height, 0.001, 1));
            _hasCropSelection = true;
            _cropDragRectangle = null;
            CropHintText.Text = "Drag inside to move or use handles to resize, then click Done.";
            UpdateCropOverlayInteractionState();
        }

        DrawCropOverlay();
        e.Handled = true;
    }

    private void CropOverlay_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _cropDragMode = CropDragMode.None;
        _cropDragRectangle = null;
        _snapGuideVerticalX = null;
        _snapGuideHorizontalY = null;
        DrawCropOverlay();
    }

    private void CropOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => DrawCropOverlay();

    private Windows.Foundation.Rect GetCropSelectionBounds(Windows.Foundation.Rect frame) => new(
        frame.X + _cropRegion.X * frame.Width,
        frame.Y + _cropRegion.Y * frame.Height,
        _cropRegion.Width * frame.Width,
        _cropRegion.Height * frame.Height);

    private static CropDragMode GetCropHandleMode(Windows.Foundation.Point point, Windows.Foundation.Rect selection)
    {
        const double handleRadius = 12;
        bool Near(double x, double y) => Math.Abs(point.X - x) <= handleRadius && Math.Abs(point.Y - y) <= handleRadius;

        if (Near(selection.Left, selection.Top)) return CropDragMode.TopLeft;
        if (Near(selection.Right, selection.Top)) return CropDragMode.TopRight;
        if (Near(selection.Left, selection.Bottom)) return CropDragMode.BottomLeft;
        if (Near(selection.Right, selection.Bottom)) return CropDragMode.BottomRight;
        if (Math.Abs(point.Y - selection.Top) <= handleRadius && point.X > selection.Left + handleRadius && point.X < selection.Right - handleRadius) return CropDragMode.Top;
        if (Math.Abs(point.Y - selection.Bottom) <= handleRadius && point.X > selection.Left + handleRadius && point.X < selection.Right - handleRadius) return CropDragMode.Bottom;
        if (Math.Abs(point.X - selection.Left) <= handleRadius && point.Y > selection.Top + handleRadius && point.Y < selection.Bottom - handleRadius) return CropDragMode.Left;
        if (Math.Abs(point.X - selection.Right) <= handleRadius && point.Y > selection.Top + handleRadius && point.Y < selection.Bottom - handleRadius) return CropDragMode.Right;
        return CropDragMode.None;
    }

    private Windows.Foundation.Rect MoveCropRectangle(Windows.Foundation.Point point)
    {
        var frame = GetVideoFrameBounds(ignoreCrop: true);
        var deltaX = point.X - _cropDragStart.X;
        var deltaY = point.Y - _cropDragStart.Y;
        var width = _cropDragStartRectangle.Width;
        var height = _cropDragStartRectangle.Height;

        var left = Math.Clamp(_cropDragStartRectangle.Left + deltaX, frame.Left, frame.Right - width);
        var top = Math.Clamp(_cropDragStartRectangle.Top + deltaY, frame.Top, frame.Bottom - height);

        const double snapThreshold = 9.0;
        _snapGuideVerticalX = null;
        _snapGuideHorizontalY = null;

        var centerX = left + width / 2.0;
        var frameCenterX = frame.X + frame.Width / 2.0;

        // Vertical axis snap: center vertical axis, left edge, right edge
        if (Math.Abs(centerX - frameCenterX) <= snapThreshold)
        {
            left = frameCenterX - width / 2.0;
            _snapGuideVerticalX = frameCenterX;
        }
        else if (Math.Abs(left - frame.Left) <= snapThreshold)
        {
            left = frame.Left;
            _snapGuideVerticalX = frame.Left;
        }
        else if (Math.Abs(left + width - frame.Right) <= snapThreshold)
        {
            left = frame.Right - width;
            _snapGuideVerticalX = frame.Right;
        }

        var centerY = top + height / 2.0;
        var frameCenterY = frame.Y + frame.Height / 2.0;

        // Horizontal axis snap: center horizontal axis, top edge, bottom edge
        if (Math.Abs(centerY - frameCenterY) <= snapThreshold)
        {
            top = frameCenterY - height / 2.0;
            _snapGuideHorizontalY = frameCenterY;
        }
        else if (Math.Abs(top - frame.Top) <= snapThreshold)
        {
            top = frame.Top;
            _snapGuideHorizontalY = frame.Top;
        }
        else if (Math.Abs(top + height - frame.Bottom) <= snapThreshold)
        {
            top = frame.Bottom - height;
            _snapGuideHorizontalY = frame.Bottom;
        }

        return new Windows.Foundation.Rect(left, top, width, height);
    }

    private Windows.Foundation.Rect ResizeCropRectangle(Windows.Foundation.Point point)
    {
        var frame = GetVideoFrameBounds(ignoreCrop: true);
        var start = _cropDragStartRectangle;
        var left = start.Left;
        var top = start.Top;
        var right = start.Right;
        var bottom = start.Bottom;

        const double snapThreshold = 9.0;
        _snapGuideVerticalX = null;
        _snapGuideHorizontalY = null;

        var frameCenterX = frame.X + frame.Width / 2.0;
        var frameCenterY = frame.Y + frame.Height / 2.0;

        switch (_cropDragMode)
        {
            case CropDragMode.TopLeft: left = point.X; top = point.Y; break;
            case CropDragMode.Top: top = point.Y; break;
            case CropDragMode.TopRight: right = point.X; top = point.Y; break;
            case CropDragMode.Right: right = point.X; break;
            case CropDragMode.BottomRight: right = point.X; bottom = point.Y; break;
            case CropDragMode.Bottom: bottom = point.Y; break;
            case CropDragMode.BottomLeft: left = point.X; bottom = point.Y; break;
            case CropDragMode.Left: left = point.X; break;
        }

        // Snap active moving edges to frame boundary or center axis
        if (_cropDragMode is CropDragMode.Left or CropDragMode.TopLeft or CropDragMode.BottomLeft)
        {
            if (Math.Abs(left - frame.Left) <= snapThreshold)
            {
                left = frame.Left;
                _snapGuideVerticalX = frame.Left;
            }
            else if (Math.Abs(left - frameCenterX) <= snapThreshold)
            {
                left = frameCenterX;
                _snapGuideVerticalX = frameCenterX;
            }
        }
        else if (_cropDragMode is CropDragMode.Right or CropDragMode.TopRight or CropDragMode.BottomRight)
        {
            if (Math.Abs(right - frame.Right) <= snapThreshold)
            {
                right = frame.Right;
                _snapGuideVerticalX = frame.Right;
            }
            else if (Math.Abs(right - frameCenterX) <= snapThreshold)
            {
                right = frameCenterX;
                _snapGuideVerticalX = frameCenterX;
            }
        }

        if (_cropDragMode is CropDragMode.Top or CropDragMode.TopLeft or CropDragMode.TopRight)
        {
            if (Math.Abs(top - frame.Top) <= snapThreshold)
            {
                top = frame.Top;
                _snapGuideHorizontalY = frame.Top;
            }
            else if (Math.Abs(top - frameCenterY) <= snapThreshold)
            {
                top = frameCenterY;
                _snapGuideHorizontalY = frameCenterY;
            }
        }
        else if (_cropDragMode is CropDragMode.Bottom or CropDragMode.BottomLeft or CropDragMode.BottomRight)
        {
            if (Math.Abs(bottom - frame.Bottom) <= snapThreshold)
            {
                bottom = frame.Bottom;
                _snapGuideHorizontalY = frame.Bottom;
            }
            else if (Math.Abs(bottom - frameCenterY) <= snapThreshold)
            {
                bottom = frameCenterY;
                _snapGuideHorizontalY = frameCenterY;
            }
        }

        left = Math.Clamp(left, frame.Left, right - 20);
        right = Math.Clamp(right, left + 20, frame.Right);
        top = Math.Clamp(top, frame.Top, bottom - 20);
        bottom = Math.Clamp(bottom, top + 20, frame.Bottom);

        if (_cropAspectRatio is double ratio && ratio > 0)
        {
            if (_cropDragMode is CropDragMode.Left or CropDragMode.Right)
            {
                var anchorX = _cropDragMode == CropDragMode.Left ? start.Right : start.Left;
                var width = Math.Abs((_cropDragMode == CropDragMode.Left ? left : right) - anchorX);
                var height = width / ratio;
                var centerY = (start.Top + start.Bottom) / 2.0;

                // Snap center Y to horizontal center axis
                if (Math.Abs(centerY - frameCenterY) <= snapThreshold)
                {
                    centerY = frameCenterY;
                    _snapGuideHorizontalY = frameCenterY;
                }

                var maxHeight = 2 * Math.Min(centerY - frame.Top, frame.Bottom - centerY);
                if (height > maxHeight)
                {
                    height = maxHeight;
                    width = height * ratio;
                }

                left = _cropDragMode == CropDragMode.Left ? anchorX - width : anchorX;
                right = left + width;
                top = centerY - height / 2.0;
                bottom = centerY + height / 2.0;

                if (Math.Abs((left + right) / 2.0 - frameCenterX) <= snapThreshold)
                {
                    _snapGuideVerticalX = frameCenterX;
                }
            }
            else if (_cropDragMode is CropDragMode.Top or CropDragMode.Bottom)
            {
                var anchorY = _cropDragMode == CropDragMode.Top ? start.Bottom : start.Top;
                var height = Math.Abs((_cropDragMode == CropDragMode.Top ? top : bottom) - anchorY);
                var width = height * ratio;
                var centerX = (start.Left + start.Right) / 2.0;

                // Snap center X to vertical center axis
                if (Math.Abs(centerX - frameCenterX) <= snapThreshold)
                {
                    centerX = frameCenterX;
                    _snapGuideVerticalX = frameCenterX;
                }

                var maxWidth = 2 * Math.Min(centerX - frame.Left, frame.Right - centerX);
                if (width > maxWidth)
                {
                    width = maxWidth;
                    height = width / ratio;
                }

                left = centerX - width / 2.0;
                right = centerX + width / 2.0;
                top = _cropDragMode == CropDragMode.Top ? anchorY - height : anchorY;
                bottom = top + height;

                if (Math.Abs((top + bottom) / 2.0 - frameCenterY) <= snapThreshold)
                {
                    _snapGuideHorizontalY = frameCenterY;
                }
            }
            else
            {
                var growsLeft = _cropDragMode is CropDragMode.TopLeft or CropDragMode.BottomLeft;
                var growsUp = _cropDragMode is CropDragMode.TopLeft or CropDragMode.TopRight;
                var anchorX = growsLeft ? start.Right : start.Left;
                var anchorY = growsUp ? start.Bottom : start.Top;
                var width = Math.Abs((growsLeft ? left : right) - anchorX);
                var height = Math.Abs((growsUp ? top : bottom) - anchorY);
                if (height <= 0 || width / height > ratio)
                {
                    height = width / ratio;
                }
                else
                {
                    width = height * ratio;
                }

                var maxWidth = growsLeft ? anchorX - frame.Left : frame.Right - anchorX;
                var maxHeight = growsUp ? anchorY - frame.Top : frame.Bottom - anchorY;
                var scale = Math.Min(1, Math.Min(maxWidth / Math.Max(width, 1), maxHeight / Math.Max(height, 1)));
                width *= scale;
                height *= scale;
                left = growsLeft ? anchorX - width : anchorX;
                right = left + width;
                top = growsUp ? anchorY - height : anchorY;
                bottom = top + height;

                if (Math.Abs((left + right) / 2.0 - frameCenterX) <= snapThreshold)
                {
                    _snapGuideVerticalX = frameCenterX;
                }
                if (Math.Abs((top + bottom) / 2.0 - frameCenterY) <= snapThreshold)
                {
                    _snapGuideHorizontalY = frameCenterY;
                }
            }
        }
        else
        {
            if (Math.Abs((left + right) / 2.0 - frameCenterX) <= snapThreshold)
            {
                _snapGuideVerticalX = frameCenterX;
            }
            if (Math.Abs((top + bottom) / 2.0 - frameCenterY) <= snapThreshold)
            {
                _snapGuideHorizontalY = frameCenterY;
            }
        }

        return new Windows.Foundation.Rect(left, top, Math.Max(20, right - left), Math.Max(20, bottom - top));
    }

    private Windows.Foundation.Rect CreateCropDragRectangle(Windows.Foundation.Point point)
    {
        var frame = GetVideoFrameBounds(ignoreCrop: true);
        var currentX = Math.Clamp(point.X, frame.Left, frame.Right);
        var currentY = Math.Clamp(point.Y, frame.Top, frame.Bottom);

        const double snapThreshold = 9.0;
        _snapGuideVerticalX = null;
        _snapGuideHorizontalY = null;

        var frameCenterX = frame.X + frame.Width / 2.0;
        var frameCenterY = frame.Y + frame.Height / 2.0;

        if (Math.Abs(currentX - frameCenterX) <= snapThreshold)
        {
            currentX = frameCenterX;
            _snapGuideVerticalX = frameCenterX;
        }
        else if (Math.Abs(currentX - frame.Left) <= snapThreshold)
        {
            currentX = frame.Left;
            _snapGuideVerticalX = frame.Left;
        }
        else if (Math.Abs(currentX - frame.Right) <= snapThreshold)
        {
            currentX = frame.Right;
            _snapGuideVerticalX = frame.Right;
        }

        if (Math.Abs(currentY - frameCenterY) <= snapThreshold)
        {
            currentY = frameCenterY;
            _snapGuideHorizontalY = frameCenterY;
        }
        else if (Math.Abs(currentY - frame.Top) <= snapThreshold)
        {
            currentY = frame.Top;
            _snapGuideHorizontalY = frame.Top;
        }
        else if (Math.Abs(currentY - frame.Bottom) <= snapThreshold)
        {
            currentY = frame.Bottom;
            _snapGuideHorizontalY = frame.Bottom;
        }

        var signX = Math.Sign(currentX - _cropDragStart.X);
        var signY = Math.Sign(currentY - _cropDragStart.Y);
        var width = Math.Abs(currentX - _cropDragStart.X);
        var height = Math.Abs(currentY - _cropDragStart.Y);

        if (_cropAspectRatio is double ratio && ratio > 0)
        {
            if (height <= 0)
            {
                height = width / ratio;
            }
            else if (width / height > ratio)
            {
                width = height * ratio;
            }
            else
            {
                height = width / ratio;
            }
        }

        width = Math.Min(width, signX < 0 ? _cropDragStart.X - frame.Left : frame.Right - _cropDragStart.X);
        height = Math.Min(height, signY < 0 ? _cropDragStart.Y - frame.Top : frame.Bottom - _cropDragStart.Y);
        var left = signX < 0 ? _cropDragStart.X - width : _cropDragStart.X;
        var top = signY < 0 ? _cropDragStart.Y - height : _cropDragStart.Y;

        if (Math.Abs((left + width / 2.0) - frameCenterX) <= snapThreshold)
        {
            _snapGuideVerticalX = frameCenterX;
        }
        if (Math.Abs((top + height / 2.0) - frameCenterY) <= snapThreshold)
        {
            _snapGuideHorizontalY = frameCenterY;
        }

        return new Windows.Foundation.Rect(left, top, Math.Max(0, width), Math.Max(0, height));
    }

    private Windows.Foundation.Rect GetVideoFrameBounds(bool ignoreCrop = false)
    {
        var width = (PreviewSurface is not null && PreviewSurface.ActualWidth > 0)
            ? PreviewSurface.ActualWidth
            : (CropOverlay?.ActualWidth ?? 0);
        var height = (PreviewSurface is not null && PreviewSurface.ActualHeight > 0)
            ? PreviewSurface.ActualHeight
            : (CropOverlay?.ActualHeight ?? 0);
        if (width <= 0 || height <= 0)
        {
            return new Windows.Foundation.Rect(0, 0, 0, 0);
        }

        var effectiveAspect = _videoAspectRatio;
        if (!ignoreCrop && !_cropMode && _hasCropSelection && _cropRegion.Width > 0.001 && _cropRegion.Height > 0.001)
        {
            effectiveAspect = (_cropRegion.Width / _cropRegion.Height) * _videoAspectRatio;
        }

        if (effectiveAspect <= 0)
        {
            effectiveAspect = 16.0 / 9.0;
        }

        var viewAspect = width / height;
        if (effectiveAspect > viewAspect)
        {
            var frameHeight = width / effectiveAspect;
            return new Windows.Foundation.Rect(0, (height - frameHeight) / 2, width, frameHeight);
        }

        var frameWidth = height * effectiveAspect;
        return new Windows.Foundation.Rect((width - frameWidth) / 2, 0, frameWidth, height);
    }

    private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVideoAdjustmentBounds();
        DrawCropOverlay();
    }

    private void UpdateVideoAdjustmentBounds()
    {
        VideoCanvas?.Invalidate();
    }

    private void DrawCropOverlay()
    {
        if (CropOverlay is null || CropOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        CropOverlay.Children.Clear();
        var frame = GetVideoFrameBounds(ignoreCrop: true);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        var selection = _cropDragRectangle ?? new Windows.Foundation.Rect(
            frame.X + _cropRegion.X * frame.Width,
            frame.Y + _cropRegion.Y * frame.Height,
            _cropRegion.Width * frame.Width,
            _cropRegion.Height * frame.Height);
        var dimBrush = new SolidColorBrush(Color.FromArgb(145, 0, 0, 0));
        AddCropOverlayRectangle(0, 0, CropOverlay.ActualWidth, Math.Max(0, selection.Top), dimBrush);
        AddCropOverlayRectangle(0, selection.Bottom, CropOverlay.ActualWidth, Math.Max(0, CropOverlay.ActualHeight - selection.Bottom), dimBrush);
        AddCropOverlayRectangle(0, selection.Top, Math.Max(0, selection.Left), selection.Height, dimBrush);
        AddCropOverlayRectangle(selection.Right, selection.Top, Math.Max(0, CropOverlay.ActualWidth - selection.Right), selection.Height, dimBrush);

        var outline = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = Math.Max(1, selection.Width),
            Height = Math.Max(1, selection.Height),
            Stroke = (Brush)Application.Current.Resources["AccentBrush"],
            StrokeThickness = 2
        };
        CropOverlay.Children.Add(outline);
        Canvas.SetLeft(outline, selection.Left);
        Canvas.SetTop(outline, selection.Top);

        if (_hasCropSelection || _cropDragMode != CropDragMode.None)
        {
            if (_cropDragMode != CropDragMode.None)
            {
                if (_snapGuideVerticalX is double snapX)
                {
                    var vLine = new Microsoft.UI.Xaml.Shapes.Line
                    {
                        X1 = snapX,
                        Y1 = frame.Top,
                        X2 = snapX,
                        Y2 = frame.Bottom,
                        Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0)),
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 4, 3 }
                    };
                    CropOverlay.Children.Add(vLine);

                    bool isCenter = Math.Abs(snapX - (frame.X + frame.Width / 2.0)) < 1.0;
                    var badge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(230, 24, 24, 24)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Child = new TextBlock
                        {
                            Text = isCenter ? "Center X" : (snapX <= frame.Left + 1 ? "Left" : "Right"),
                            FontSize = 10,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0))
                        }
                    };
                    CropOverlay.Children.Add(badge);
                    Canvas.SetLeft(badge, Math.Clamp(snapX + 4, 0, Math.Max(0, CropOverlay.ActualWidth - 65)));
                    Canvas.SetTop(badge, Math.Max(frame.Top + 6, 6));
                }

                if (_snapGuideHorizontalY is double snapY)
                {
                    var hLine = new Microsoft.UI.Xaml.Shapes.Line
                    {
                        X1 = frame.Left,
                        Y1 = snapY,
                        X2 = frame.Right,
                        Y2 = snapY,
                        Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0)),
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 4, 3 }
                    };
                    CropOverlay.Children.Add(hLine);

                    bool isCenter = Math.Abs(snapY - (frame.Y + frame.Height / 2.0)) < 1.0;
                    var badge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(230, 24, 24, 24)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Child = new TextBlock
                        {
                            Text = isCenter ? "Center Y" : (snapY <= frame.Top + 1 ? "Top" : "Bottom"),
                            FontSize = 10,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0))
                        }
                    };
                    CropOverlay.Children.Add(badge);
                    Canvas.SetLeft(badge, Math.Max(frame.Left + 6, 6));
                    Canvas.SetTop(badge, Math.Clamp(snapY + 4, 0, Math.Max(0, CropOverlay.ActualHeight - 25)));
                }
            }

            AddCropHandle(selection.Left, selection.Top);
            AddCropHandle((selection.Left + selection.Right) / 2, selection.Top);
            AddCropHandle(selection.Right, selection.Top);
            AddCropHandle(selection.Right, (selection.Top + selection.Bottom) / 2);
            AddCropHandle(selection.Right, selection.Bottom);
            AddCropHandle((selection.Left + selection.Right) / 2, selection.Bottom);
            AddCropHandle(selection.Left, selection.Bottom);
            AddCropHandle(selection.Left, (selection.Top + selection.Bottom) / 2);
        }
    }

    private void AddCropHandle(double centerX, double centerY)
    {
        const double handleSize = 12;
        var handle = new Border
        {
            Width = handleSize,
            Height = handleSize,
            Background = (Brush)Application.Current.Resources["AccentBrush"],
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2)
        };
        CropOverlay.Children.Add(handle);
        Canvas.SetLeft(handle, centerX - handleSize / 2);
        Canvas.SetTop(handle, centerY - handleSize / 2);
    }

    private void AddCropOverlayRectangle(double left, double top, double width, double height, Brush fill)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var rectangle = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = width, Height = height, Fill = fill };
        CropOverlay.Children.Add(rectangle);
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
    }

    private void PlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_durationSeconds <= 0)
        {
            return;
        }

        var session = _player.PlaybackSession;
        AudioPitchEngine.LogAudio($"MainWindow.PlaybackButton_Click: session.State={session.PlaybackState}, Pos={session.Position.TotalSeconds:F2}, HasAudioTrack={_audioEngine?.HasAudioTrack}, EngineIsPlaying={_audioEngine?.IsPlaying}");
        if (session.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            _audioEngine?.Pause();
        }
        else
        {
            var playbackEnd = _showingEditedPreview ? _previewPlaybackDuration : _trimEndSeconds;
            if (session.Position.TotalSeconds >= playbackEnd ||
                (!_showingEditedPreview && session.Position.TotalSeconds < _trimStartSeconds))
            {
                session.Position = TimeSpan.FromSeconds(_showingEditedPreview ? 0 : _trimStartSeconds);
                _audioEngine?.Seek(session.Position);
            }

            if (_showingEditedPreview)
            {
                _audioEngine?.Stop();
                _player.IsMuted = _userMuted;
                _player.Volume = _userMuted ? 0 : (VolumeSlider?.Value ?? 1.0);
                _player.Play();
            }
            else if (_audioEngine is not null && _audioEngine.HasAudioTrack)
            {
                _player.IsMuted = true;
                _player.Volume = 0;
                _audioEngine.Seek(_player.PlaybackSession.Position);
                _player.Play();
                _audioEngine.Play();
                if (!_audioEngine.IsPlaying)
                {
                    _player.IsMuted = _userMuted;
                    _player.Volume = _userMuted ? 0 : (VolumeSlider?.Value ?? 1.0);
                }
            }
            else
            {
                _audioEngine?.Stop();
                _player.IsMuted = _userMuted;
                _player.Volume = _userMuted ? 0 : (VolumeSlider?.Value ?? 1.0);
                _player.Play();
            }
        }

        UpdatePlaybackButton();
    }

    private void TimelineCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_durationSeconds <= 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(TimelineCanvas).Position;
        var startX = TimeToTimelineX(_trimStartSeconds);
        var endX = TimeToTimelineX(_trimEndSeconds);
        _timelineDragMode = Math.Abs(point.X - startX) <= 14
            ? TimelineDragMode.Start
            : Math.Abs(point.X - endX) <= 14
                ? TimelineDragMode.End
                : TimelineDragMode.Playhead;

        if (_timelineDragMode is TimelineDragMode.Start or TimelineDragMode.End)
        {
            CaptureUndo();
        }
        else
        {
            SetPlayheadFromTimelineX(point.X);
        }

        TimelineCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TimelineCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_timelineDragMode == TimelineDragMode.None || _durationSeconds <= 0)
        {
            return;
        }

        var x = e.GetCurrentPoint(TimelineCanvas).Position.X;
        var time = TimelineXToTime(x);
        switch (_timelineDragMode)
        {
            case TimelineDragMode.Start:
                _trimStartSeconds = Math.Clamp(time, 0, Math.Max(0, _trimEndSeconds - 0.05));
                break;
            case TimelineDragMode.End:
                _trimEndSeconds = Math.Clamp(time, Math.Min(_durationSeconds, _trimStartSeconds + 0.05), _durationSeconds);
                break;
            case TimelineDragMode.Playhead:
                SetPlayheadFromTimelineX(x);
                break;
        }

        UpdateTrimLabels();
        if (_timelineDragMode == TimelineDragMode.Playhead)
        {
            UpdatePlayheadMarker();
            UpdateAudioWaveformPlayhead();
        }
        else
        {
            DrawTimeline();
            DrawAudioWaveformViewport();
        }
    }

    private void TimelineCanvas_PointerReleased(object sender, PointerRoutedEventArgs e) => EndTimelineDrag();

    private void TimelineCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndTimelineDrag();

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTimeline();

    private void AudioWaveformViewport_SizeChanged(object sender, SizeChangedEventArgs e) => DrawAudioWaveformViewport();

    private void EndTimelineDrag()
    {
        _timelineDragMode = TimelineDragMode.None;
    }

    private void SetPlayheadFromTimelineX(double x)
    {
        _playheadSeconds = TimelineXToTime(x);
        if (_showingEditedPreview)
        {
            ExitEditedPreview();
        }

        if (_sourcePath is not null)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(_playheadSeconds);
            _audioEngine?.Seek(TimeSpan.FromSeconds(_playheadSeconds));
        }

        UpdateTrimLabels();
        UpdatePlayheadMarker();
        UpdateAudioWaveformPlayhead();
    }

    private double TimelineXToTime(double x)
    {
        var width = Math.Max(1, TimelineCanvas.ActualWidth - 16);
        return Math.Clamp((x - 8) / width, 0, 1) * _durationSeconds;
    }

    private double TimeToTimelineX(double time)
    {
        var width = Math.Max(1, TimelineCanvas.ActualWidth - 16);
        return 8 + Math.Clamp(time / Math.Max(0.001, _durationSeconds), 0, 1) * width;
    }

    private void DrawTimeline()
    {
        if (TimelineCanvas is null)
        {
            return;
        }

        TimelineCanvas.Children.Clear();
        _playheadMarker = null;
        var width = TimelineCanvas.ActualWidth;
        if (width < 40)
        {
            return;
        }

        var trackLeft = 8d;
        var trackWidth = Math.Max(1, width - 16);
        const double rulerBottom = 21;
        const double trackTop = 29;
        const double trackHeight = 24;
        const double trackRadius = 6;

        AddTrackSegment(TimelineCanvas, trackLeft, trackTop, trackWidth, trackHeight,
            ThemeBrush(Color.FromArgb(255, 222, 227, 232), Color.FromArgb(255, 56, 56, 56)),
            new CornerRadius(trackRadius));

        if (_durationSeconds > 0)
        {
            var startX = TimeToTimelineX(_trimStartSeconds);
            var endX = TimeToTimelineX(_trimEndSeconds);
            var leftDimWidth = Math.Max(0, startX - trackLeft);
            var rightDimWidth = Math.Max(0, trackLeft + trackWidth - endX);
            var selWidth = Math.Max(0, endX - startX);

            if (leftDimWidth > 0)
            {
                AddTrackSegment(TimelineCanvas, trackLeft, trackTop, leftDimWidth, trackHeight,
                    ThemeBrush(Color.FromArgb(180, 184, 192, 200), Color.FromArgb(180, 16, 16, 16)),
                    new CornerRadius(trackRadius, 0, 0, trackRadius));
            }

            if (rightDimWidth > 0)
            {
                AddTrackSegment(TimelineCanvas, endX, trackTop, rightDimWidth, trackHeight,
                    ThemeBrush(Color.FromArgb(180, 184, 192, 200), Color.FromArgb(180, 16, 16, 16)),
                    new CornerRadius(0, trackRadius, trackRadius, 0));
            }

            if (selWidth > 0)
            {
                var selLeftRadius = Math.Abs(startX - trackLeft) < 1 ? trackRadius : 0;
                var selRightRadius = Math.Abs(endX - (trackLeft + trackWidth)) < 1 ? trackRadius : 0;
                var selCornerRadius = new CornerRadius(selLeftRadius, selRightRadius, selRightRadius, selLeftRadius);

                AddTrackSegment(TimelineCanvas, startX, trackTop, selWidth, trackHeight,
                    ThemeBrush(Color.FromArgb(255, 185, 216, 244), Color.FromArgb(255, 26, 91, 150)),
                    selCornerRadius);

                AddTrackSegment(TimelineCanvas, startX, trackTop, selWidth, trackHeight,
                    ThemeBrush(Color.FromArgb(30, 255, 255, 255), Color.FromArgb(30, 255, 255, 255)),
                    selCornerRadius);
            }

            DrawWaveform(TimelineCanvas, trackLeft, trackTop, trackWidth, trackHeight, startX, endX);
            AddTimelineHandle(startX, trackTop, trackHeight);
            AddTimelineHandle(endX, trackTop, trackHeight);

            var tickCount = Math.Clamp((int)(trackWidth / 70), 2, 12);
            for (var index = 0; index <= tickCount; index++)
            {
                var fraction = (double)index / tickCount;
                var x = trackLeft + trackWidth * fraction;
                var major = index % Math.Max(1, tickCount / 4) == 0;
                AddTimelineLine(x, major ? 5 : 11, x, rulerBottom, ThemeBrush(Color.FromArgb(major ? (byte)175 : (byte)95, 101, 113, 126), Color.FromArgb(major ? (byte)190 : (byte)105, 184, 194, 205)), major ? 1.2 : 0.8);
                if (major)
                {
                    var label = new TextBlock
                    {
                        Text = FormatTime(_durationSeconds * fraction),
                        FontSize = 10,
                        Foreground = ThemeBrush(Color.FromArgb(230, 76, 86, 96), Color.FromArgb(220, 220, 226, 232))
                    };
                    TimelineCanvas.Children.Add(label);
                    Canvas.SetLeft(label, Math.Clamp(x - 18, 0, width - 40));
                    Canvas.SetTop(label, 1);
                }
            }

            var playheadX = TimeToTimelineX(_playheadSeconds);
            _playheadMarker = new Grid
            {
                Width = 9,
                Height = trackTop + trackHeight
            };
            var stem = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 2,
                Fill = PlayheadRedBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var head = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = PlayheadRedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 18, 0, 0)
            };
            _playheadMarker.Children.Add(stem);
            _playheadMarker.Children.Add(head);
            TimelineCanvas.Children.Add(_playheadMarker);
            Canvas.SetLeft(_playheadMarker, playheadX - 4.5);
            Canvas.SetTop(_playheadMarker, 4);
        }
        else
        {
            AddTimelineLine(trackLeft, 12, trackLeft + trackWidth, 12, ThemeBrush(Color.FromArgb(90, 101, 113, 126), Color.FromArgb(90, 184, 194, 205)), 1);
        }
    }

    private void DrawWaveform(Canvas container, double left, double top, double width, double height, double selectionStart, double selectionEnd)
    {
        if (_waveformPeaks.Count == 0)
        {
            return;
        }

        var barCount = Math.Min(_waveformPeaks.Count, Math.Max(1, (int)(width / 2)));
        var barSpacing = width / barCount;
        var centerY = top + height / 2;
        var maximumBarHeight = height * 0.82;
        var normalizationPeak = Math.Max(0.001f, _waveformPeaks.Max());
        var barWidth = Math.Max(1, barSpacing * 0.56);

        for (var index = 0; index < barCount; index++)
        {
            var peakIndex = Math.Min(_waveformPeaks.Count - 1, (int)((index + 0.5) / barCount * _waveformPeaks.Count));
            var normalizedPeak = Math.Clamp(_waveformPeaks[peakIndex] / normalizationPeak, 0, 1);
            var peak = Math.Pow(normalizedPeak, 0.7);
            var barHeight = Math.Max(2, peak * maximumBarHeight);
            var x = left + (index + 0.5) * barSpacing;
            var insideSelection = x >= selectionStart && x <= selectionEnd;
            var brush = ThemeBrush(
                insideSelection ? Color.FromArgb(255, 0, 78, 138) : Color.FromArgb(175, 101, 116, 132),
                insideSelection ? Color.FromArgb(255, 214, 235, 255) : Color.FromArgb(165, 153, 163, 174));
            AddTrackRectangle(container, x - barWidth / 2, centerY - barHeight / 2, barWidth, barHeight, brush);
        }
    }

    private void DrawAudioWaveformViewport()
    {
        if (AudioWaveformViewport is null || AudioWaveformViewport.Visibility != Visibility.Visible)
        {
            return;
        }

        AudioWaveformViewport.Children.Clear();
        _audioWaveformBars.Clear();
        _audioWaveformPlayhead = null;
        var width = AudioWaveformViewport.ActualWidth;
        var height = AudioWaveformViewport.ActualHeight;
        if (width < 80 || height < 80)
        {
            return;
        }

        var secondaryTextBrush = ThemeBrush(Color.FromArgb(255, 93, 93, 93), Color.FromArgb(255, 181, 181, 181));
        var title = new TextBlock
        {
            Text = "AUDIO WAVEFORM",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = secondaryTextBrush
        };
        AudioWaveformViewport.Children.Add(title);
        Canvas.SetLeft(title, 28);
        Canvas.SetTop(title, 24);

        var fileName = new TextBlock
        {
            Text = Path.GetFileName(_sourcePath ?? string.Empty),
            FontSize = 15,
            Foreground = ThemeBrush(Color.FromArgb(255, 26, 26, 26), Color.FromArgb(255, 245, 245, 245)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(120, width - 56)
        };
        AudioWaveformViewport.Children.Add(fileName);
        Canvas.SetLeft(fileName, 28);
        Canvas.SetTop(fileName, 44);

        var duration = new TextBlock
        {
            Text = FormatTime(_durationSeconds),
            FontSize = 11,
            Foreground = secondaryTextBrush
        };
        AudioWaveformViewport.Children.Add(duration);
        Canvas.SetLeft(duration, 28);
        Canvas.SetTop(duration, 66);

        if (_waveformPeaks.Count == 0)
        {
            var status = new TextBlock
            {
                Text = _waveformReady ? "Waveform unavailable" : "Preparing waveform...",
                FontSize = 15,
                Foreground = secondaryTextBrush
            };
            AudioWaveformViewport.Children.Add(status);
            Canvas.SetLeft(status, Math.Max(28, (width - 190) / 2));
            Canvas.SetTop(status, height / 2 - 10);
            return;
        }

        const double left = 28;
        var plotWidth = Math.Max(1, width - left * 2);
        var centerY = height * 0.58;
        var maximumBarHeight = Math.Min(height * 0.34, 150);
        var barCount = Math.Min(_waveformPeaks.Count, Math.Max(1, (int)(plotWidth / 3.5)));
        var barSpacing = plotWidth / barCount;
        var barWidth = Math.Max(1.5, barSpacing * 0.58);
        var normalizationPeak = Math.Max(0.001f, _waveformPeaks.Max());

        AddAudioViewportRectangle(left, centerY - 0.5, plotWidth, 1,
            ThemeBrush(Color.FromArgb(100, 101, 113, 126), Color.FromArgb(100, 184, 194, 205)));

        for (var index = 0; index < barCount; index++)
        {
            var peakIndex = Math.Min(_waveformPeaks.Count - 1, (int)((index + 0.5) / barCount * _waveformPeaks.Count));
            var normalizedPeak = Math.Clamp(_waveformPeaks[peakIndex] / normalizationPeak, 0, 1);
            var peak = Math.Pow(normalizedPeak, 0.7);
            var barHeight = Math.Max(2, peak * maximumBarHeight);
            var fraction = (index + 0.5) / barCount;
            var barTime = fraction * _durationSeconds;
            var inSelection = barTime >= _trimStartSeconds && barTime <= _trimEndSeconds;
            var hasPlayed = barTime <= _playheadSeconds;
            var x = left + fraction * plotWidth;
            var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = CreateAudioWaveformBrush(inSelection, hasPlayed)
            };
            AudioWaveformViewport.Children.Add(bar);
            Canvas.SetLeft(bar, x - barWidth / 2);
            Canvas.SetTop(bar, centerY - barHeight / 2);
            _audioWaveformBars.Add(new AudioWaveformBar
            {
                Shape = bar,
                Time = barTime,
                InSelection = inSelection,
                HasPlayed = hasPlayed
            });
        }

        _audioWaveformPlayhead = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 2,
            Height = maximumBarHeight * 2 + 14,
            Fill = PlayheadRedBrush
        };
        AudioWaveformViewport.Children.Add(_audioWaveformPlayhead);
        Canvas.SetTop(_audioWaveformPlayhead, centerY - _audioWaveformPlayhead.Height / 2);
        UpdateAudioWaveformPlayhead();
    }

    private void AddAudioViewportRectangle(double left, double top, double width, double height, Brush fill)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var rectangle = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = width, Height = height, Fill = fill };
        AudioWaveformViewport.Children.Add(rectangle);
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
    }

    private void UpdateAudioWaveformPlayhead()
    {
        foreach (var bar in _audioWaveformBars)
        {
            var hasPlayed = bar.Time <= _playheadSeconds;
            if (hasPlayed != bar.HasPlayed)
            {
                bar.HasPlayed = hasPlayed;
                bar.Shape.Fill = CreateAudioWaveformBrush(bar.InSelection, hasPlayed);
            }
        }

        if (_audioWaveformPlayhead is null || _durationSeconds <= 0)
        {
            return;
        }

        var plotWidth = Math.Max(1, AudioWaveformViewport.ActualWidth - 56);
        var x = 28 + Math.Clamp(_playheadSeconds / _durationSeconds, 0, 1) * plotWidth;
        Canvas.SetLeft(_audioWaveformPlayhead, x - _audioWaveformPlayhead.Width / 2);
    }

    private SolidColorBrush CreateAudioWaveformBrush(bool inSelection, bool hasPlayed)
    {
        var color = _isDarkTheme
            ? (hasPlayed && inSelection ? Color.FromArgb(255, 96, 205, 255) : Color.FromArgb(165, 153, 163, 174))
            : (hasPlayed && inSelection ? Color.FromArgb(255, 0, 103, 192) : Color.FromArgb(190, 101, 113, 126));
        return new SolidColorBrush(color);
    }

    private void UpdatePlayheadMarker()
    {
        if (_playheadMarker is not null)
        {
            Canvas.SetLeft(_playheadMarker, TimeToTimelineX(_playheadSeconds) - _playheadMarker.Width / 2);
        }
    }

    private void AddTimelineHandle(double x, double top, double height)
    {
        var handle = new Border
        {
            Width = 10,
            Height = height + 8,
            CornerRadius = new CornerRadius(3),
            Background = ThemeBrush(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 245, 249, 247)),
            BorderBrush = ThemeBrush(Color.FromArgb(255, 0, 103, 192), Color.FromArgb(255, 96, 205, 255)),
            BorderThickness = new Thickness(2)
        };
        TimelineCanvas.Children.Add(handle);
        Canvas.SetLeft(handle, x - 5);
        Canvas.SetTop(handle, top - 4);
    }

    private void AddTrackSegment(Canvas container, double left, double top, double width, double height, Brush background, CornerRadius cornerRadius)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var segment = new Border
        {
            Width = width,
            Height = height,
            Background = background,
            CornerRadius = cornerRadius
        };
        container.Children.Add(segment);
        Canvas.SetLeft(segment, left);
        Canvas.SetTop(segment, top);
    }

    private void AddTimelineRectangle(double left, double top, double width, double height, Brush fill, double radius = 0)
        => AddTrackRectangle(TimelineCanvas, left, top, width, height, fill, radius);

    private void AddTrackRectangle(Canvas container, double left, double top, double width, double height, Brush fill, double radius = 0)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var rectangle = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = fill,
            RadiusX = radius,
            RadiusY = radius
        };
        container.Children.Add(rectangle);
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
    }

    private void AddTimelineLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness)
    {
        var line = new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        TimelineCanvas.Children.Add(line);
    }

    private SolidColorBrush ThemeBrush(Color light, Color dark) => new(_isDarkTheme ? dark : light);

    private void CaptureUndo()
    {
        if (!_hasUndo)
        {
            _undoStart = _trimStartSeconds;
            _undoEnd = _trimEndSeconds;
            _hasUndo = true;
            UndoButton.IsEnabled = true;
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasUndo)
        {
            return;
        }

        _trimStartSeconds = _undoStart;
        _trimEndSeconds = _undoEnd;
        _hasUndo = false;
        UndoButton.IsEnabled = false;
        UpdateTrimLabels();
        DrawTimeline();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_durationSeconds <= 0)
        {
            return;
        }

        CaptureUndo();
        _trimStartSeconds = 0;
        _trimEndSeconds = _durationSeconds;
        UpdateTrimLabels();
        DrawTimeline();
    }

    private void ExitEditedPreview()
    {
        if (!_showingEditedPreview) return;
        _ = ExitEditedPreviewAsync();
    }

    private async Task ExitEditedPreviewAsync()
    {
        if (!_showingEditedPreview || string.IsNullOrWhiteSpace(_sourcePath))
        {
            return;
        }

        _showingEditedPreview = false;
        _startPreviewWhenReady = false;
        if (PreviewTrimButton is not null)
        {
            PreviewTrimButton.Content = "Preview edits";
        }

        var isVideo = VideoExtensions.Contains(Path.GetExtension(_sourcePath));
        _player.Pause();
        _audioEngine?.Pause();

        _player.IsVideoFrameServerEnabled = isVideo;
        if (VideoCanvas is not null) VideoCanvas.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        if (Preview is not null) Preview.Visibility = Visibility.Collapsed;

        MediaSource mediaSource;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_sourcePath);
            mediaSource = MediaSource.CreateFromStorageFile(file);
        }
        catch
        {
            mediaSource = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(_sourcePath)));
        }

        _player.Source = mediaSource;

        _player.PlaybackSession.Position = TimeSpan.FromSeconds(_playheadSeconds);
        _audioEngine?.Seek(TimeSpan.FromSeconds(_playheadSeconds));

        StatusText.Text = "Live editing mode";
        UpdateTrimLabels();
        DrawTimeline();
        ApplyVideoPreviewEffects();
        ApplyAudioPitchAndSpeed();
    }

    private async void PreviewTrimButton_Click(object sender, RoutedEventArgs e)
    {
        if (_showingEditedPreview)
        {
            await ExitEditedPreviewAsync();
            return;
        }

        if (_sourcePath is null || _durationSeconds <= 0)
        {
            return;
        }

        var previewDirectory = Path.Combine(Path.GetTempPath(), "QuickEditor");
        Directory.CreateDirectory(previewDirectory);
        var previewPath = Path.Combine(previewDirectory, $"preview-{Guid.NewGuid():N}{Path.GetExtension(_sourcePath)}");
        _exportCancellation = new CancellationTokenSource();
        SetExporting(true);
        ExportProgress.Value = 0;
        StatusText.Text = "Rendering edited preview...";
        var progress = new Progress<double>(value => ExportProgress.Value = value);

        try
        {
            var isVideo = VideoExtensions.Contains(Path.GetExtension(_sourcePath));
            await _exportService.ExportTrimAsync(
                _sourcePath,
                previewPath,
                _trimStartSeconds,
                _trimEndSeconds - _trimStartSeconds,
                isVideo,
                GetAdjustments(),
                progress,
                _exportCancellation.Token);

            _player.Pause();
            _audioEngine?.Stop();
            _player.Source = null;
            _showingEditedPreview = true;
            if (PreviewTrimButton is not null) PreviewTrimButton.Content = "Exit preview";
            _player.IsVideoFrameServerEnabled = false;
            VideoCanvas.Visibility = Visibility.Collapsed;
            Preview.Visibility = Visibility.Visible;
            _player.IsMuted = _userMuted;
            _player.Volume = _userMuted ? 0 : (VolumeSlider?.Value ?? 1.0);
            _startPreviewWhenReady = true;
            _previewPlaybackDuration = (_trimEndSeconds - _trimStartSeconds) / Math.Clamp(SpeedSlider.Value, 0.5, 2.0);
            _previewFiles.Add(previewPath);
            MediaSource previewSource;
            try
            {
                var previewFile = await StorageFile.GetFileFromPathAsync(previewPath);
                previewSource = MediaSource.CreateFromStorageFile(previewFile);
            }
            catch
            {
                previewSource = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(previewPath)));
            }

            _player.Source = previewSource;
            StatusText.Text = "Playing edited preview (Click 'Exit preview' or adjust sliders to return to live editing)";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Preview canceled.";
            if (PreviewTrimButton is not null) PreviewTrimButton.Content = "Preview edits";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            if (PreviewTrimButton is not null) PreviewTrimButton.Content = "Preview edits";
            if (File.Exists(previewPath))
            {
                File.Delete(previewPath);
            }
        }
        finally
        {
            _exportCancellation.Dispose();
            _exportCancellation = null;
            SetExporting(false);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _durationSeconds <= 0)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_sourcePath)}_edited"
        };
        picker.FileTypeChoices.Add("Keep original format", new List<string> { Path.GetExtension(_sourcePath) });
        picker.DefaultFileExtension = Path.GetExtension(_sourcePath);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var output = await picker.PickSaveFileAsync();
        if (output is null)
        {
            return;
        }

        _exportCancellation = new CancellationTokenSource();
        SetExporting(true);
        ExportProgress.Value = 0;
        StatusText.Text = "Preparing export...";
        var progress = new Progress<double>(value => ExportProgress.Value = value);

        try
        {
            await _exportService.ExportTrimAsync(
                _sourcePath,
                output.Path,
                _trimStartSeconds,
                _trimEndSeconds - _trimStartSeconds,
                VideoExtensions.Contains(Path.GetExtension(_sourcePath)),
                GetAdjustments(),
                progress,
                _exportCancellation.Token);

            ExportProgress.Value = 100;
            StatusText.Text = "Export complete";
            FooterText.Text = $"Saved to {output.Path}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Export canceled. The original file is unchanged.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _exportCancellation.Dispose();
            _exportCancellation = null;
            SetExporting(false);
        }
    }

    private void CancelExportButton_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private void SetExporting(bool exporting)
    {
        ExportProgress.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        CancelExportButton.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = !exporting;
        ExportButton.IsEnabled = !exporting && _durationSeconds > 0;
        TimelineCanvas.IsHitTestVisible = !exporting && _durationSeconds > 0;
        PreviewTrimButton.IsEnabled = !exporting && _durationSeconds > 0;
        PlaybackButton.IsEnabled = !exporting && _durationSeconds > 0;
        CropModeButton.IsEnabled = !exporting && _durationSeconds > 0 && VideoExtensions.Contains(Path.GetExtension(_sourcePath ?? string.Empty));
        CropPresetComboBox.IsEnabled = !exporting && _cropMode;
        CropOverlay.IsHitTestVisible = !exporting && _cropMode;
        UndoButton.IsEnabled = !exporting && _hasUndo;
        ResetButton.IsEnabled = !exporting && _durationSeconds > 0;
        AdjustmentTabsGrid.IsHitTestVisible = !exporting;
        AdjustmentTabsGrid.Opacity = exporting ? 0.6 : 1.0;
        VideoAdjustments.IsHitTestVisible = !exporting;
        VideoAdjustments.Opacity = exporting ? 0.6 : 1.0;
        AudioAdjustments.IsHitTestVisible = !exporting;
        AudioAdjustments.Opacity = exporting ? 0.6 : 1.0;
    }

    private MediaAdjustments GetAdjustments() => new(
        _cropRegion,
        TemperatureSlider?.Value ?? 0,
        TintSlider?.Value ?? 0,
        BrightnessSlider?.Value ?? 0,
        ContrastSlider?.Value ?? 0,
        HighlightsSlider?.Value ?? 0,
        ShadowsSlider?.Value ?? 0,
        VibranceSlider?.Value ?? 0,
        SaturationSlider?.Value ?? 1,
        HueSlider?.Value ?? 0,
        PitchSlider?.Value ?? 0,
        SpeedSlider?.Value ?? 1,
        (VolumeBoostSlider?.Value ?? 100) / 100.0,
        NormalizeCheckBox?.IsChecked ?? false,
        [
            EqBand1Slider?.Value ?? 0,
            EqBand2Slider?.Value ?? 0,
            EqBand3Slider?.Value ?? 0,
            EqBand4Slider?.Value ?? 0,
            EqBand5Slider?.Value ?? 0,
            EqBand6Slider?.Value ?? 0
        ],
        (ReverbMixSlider?.Value ?? 0) / 100.0,
        (ReverbRoomSlider?.Value ?? 50) / 100.0,
        (_audioEngine?.HasAudioTrack ?? true) || !VideoExtensions.Contains(Path.GetExtension(_sourcePath ?? string.Empty)));

    private void VideoAdjustment_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isReady)
        {
            return;
        }

        if (_showingEditedPreview)
        {
            ExitEditedPreview();
        }

        if (TemperatureValueText is not null && TemperatureSlider is not null)
        {
            var val = (int)Math.Round(TemperatureSlider.Value);
            TemperatureValueText.Text = val switch
            {
                > 0 => $"+{val} Warm",
                < 0 => $"{val} Cool",
                _ => "0"
            };
        }
        if (TintValueText is not null && TintSlider is not null)
        {
            var val = (int)Math.Round(TintSlider.Value);
            TintValueText.Text = val switch
            {
                > 0 => $"+{val} Magenta",
                < 0 => $"{val} Green",
                _ => "0"
            };
        }
        if (BrightnessValueText is not null && BrightnessSlider is not null) BrightnessValueText.Text = $"{BrightnessSlider.Value:+0.00;-0.00;0.00}";
        if (ContrastValueText is not null && ContrastSlider is not null) ContrastValueText.Text = $"{ContrastSlider.Value:+0.00;-0.00;0.00}";
        if (HighlightsValueText is not null && HighlightsSlider is not null) HighlightsValueText.Text = $"{HighlightsSlider.Value:+0.00;-0.00;0.00}";
        if (ShadowsValueText is not null && ShadowsSlider is not null) ShadowsValueText.Text = $"{ShadowsSlider.Value:+0.00;-0.00;0.00}";
        if (VibranceValueText is not null && VibranceSlider is not null) VibranceValueText.Text = $"{VibranceSlider.Value:+0.00;-0.00;0.00}";
        if (SaturationValueText is not null && SaturationSlider is not null) SaturationValueText.Text = $"{SaturationSlider.Value:0.00}";
        if (HueValueText is not null && HueSlider is not null) HueValueText.Text = $"{HueSlider.Value:+0;-0;0}°";
        ApplyVideoPreviewEffects();
    }

    private void ApplyVideoPreviewEffects()
    {
        if (!_isReady)
        {
            return;
        }

        if (_showingEditedPreview)
        {
            ExitEditedPreview();
            return;
        }

        VideoCanvas?.Invalidate();
    }

    private void VideoCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(Microsoft.UI.Colors.Black);

        if (_renderTarget is null)
        {
            EnsureRenderTarget();
            if (_renderTarget is null)
            {
                return;
            }
        }

        var frameBounds = GetVideoFrameBounds();
        if (frameBounds.Width <= 0 || frameBounds.Height <= 0)
        {
            return;
        }

        ICanvasImage currentImage = _renderTarget;

        // 1. White Balance: Temperature & Tint (-100 to +100)
        var temp = (float)(TemperatureSlider?.Value ?? 0) / 100f;
        var tint = (float)(TintSlider?.Value ?? 0) / 100f;
        if (Math.Abs(temp) > 0.005f || Math.Abs(tint) > 0.005f)
        {
            currentImage = new TemperatureAndTintEffect
            {
                Source = currentImage,
                Temperature = Math.Clamp(temp, -1f, 1f),
                Tint = Math.Clamp(tint, -1f, 1f)
            };
        }

        // 2 & 3. Brightness & Contrast
        var brightness = (float)(BrightnessSlider?.Value ?? 0);
        var contrast = (float)(ContrastSlider?.Value ?? 0);
        if (Math.Abs(brightness) > 0.005f || Math.Abs(contrast) > 0.005f)
        {
            float c = contrast > 0 ? (1f + contrast * 1.5f) : (1f + contrast);
            float b = brightness * 1.5f;
            float t = (1f - c) / 2f + b;
            currentImage = new ColorMatrixEffect
            {
                Source = currentImage,
                ColorMatrix = new Microsoft.Graphics.Canvas.Effects.Matrix5x4
                {
                    M11 = c, M12 = 0, M13 = 0, M14 = 0,
                    M21 = 0, M22 = c, M23 = 0, M24 = 0,
                    M31 = 0, M32 = 0, M33 = c, M34 = 0,
                    M41 = 0, M42 = 0, M43 = 0, M44 = 1,
                    M51 = t, M52 = t, M53 = t, M54 = 0
                }
            };
        }

        // 4. Highlights and Shadows (-1.0 to +1.0)
        var highlights = (float)(HighlightsSlider?.Value ?? 0);
        var shadows = (float)(ShadowsSlider?.Value ?? 0);
        if (Math.Abs(highlights) > 0.005f || Math.Abs(shadows) > 0.005f)
        {
            currentImage = new HighlightsAndShadowsEffect
            {
                Source = currentImage,
                Highlights = Math.Clamp(highlights, -1f, 1f),
                Shadows = Math.Clamp(shadows, -1f, 1f)
            };
        }

        // 5. Saturation & Vibrance (0.0 to 2.0)
        var saturation = (float)(SaturationSlider?.Value ?? 1.0);
        var vibrance = (float)(VibranceSlider?.Value ?? 0);
        var totalSat = Math.Clamp(saturation + vibrance * 0.4f, 0f, 2f);
        if (Math.Abs(totalSat - 1f) > 0.005f)
        {
            currentImage = new SaturationEffect
            {
                Source = currentImage,
                Saturation = totalSat
            };
        }

        // 6. Hue (-180 to +180 deg)
        var hue = (float)(HueSlider?.Value ?? 0);
        if (Math.Abs(hue) > 0.5f)
        {
            currentImage = new HueRotationEffect
            {
                Source = currentImage,
                Angle = (float)(hue * Math.PI / 180.0)
            };
        }

        bool isCroppedView = !_cropMode && _hasCropSelection &&
            (_cropRegion.Width < 0.999 || _cropRegion.Height < 0.999 || _cropRegion.X > 0.001 || _cropRegion.Y > 0.001) &&
            _cropRegion.Width > 0.001 && _cropRegion.Height > 0.001;

        var destRect = new Windows.Foundation.Rect(frameBounds.Left, frameBounds.Top, frameBounds.Width, frameBounds.Height);
        Windows.Foundation.Rect sourceRect;
        if (isCroppedView)
        {
            var cropX = Math.Clamp(_cropRegion.X, 0.0, 0.999);
            var cropY = Math.Clamp(_cropRegion.Y, 0.0, 0.999);
            var cropW = Math.Clamp(_cropRegion.Width, 0.001, 1.0 - cropX);
            var cropH = Math.Clamp(_cropRegion.Height, 0.001, 1.0 - cropY);
            var srcX = cropX * _renderTarget.SizeInPixels.Width;
            var srcY = cropY * _renderTarget.SizeInPixels.Height;
            var srcW = Math.Min(cropW * _renderTarget.SizeInPixels.Width, _renderTarget.SizeInPixels.Width - srcX);
            var srcH = Math.Min(cropH * _renderTarget.SizeInPixels.Height, _renderTarget.SizeInPixels.Height - srcY);
            sourceRect = new Windows.Foundation.Rect(srcX, srcY, Math.Max(1, srcW), Math.Max(1, srcH));
        }
        else
        {
            sourceRect = _renderTarget.Bounds;
        }

        ds.DrawImage(currentImage, destRect, sourceRect);
    }

    private void Player_VideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (_showingEditedPreview) return;
        try
        {
            EnsureRenderTarget();
            if (_renderTarget is not null)
            {
                sender.CopyFrameToVideoSurface(_renderTarget);
                DispatcherQueue.TryEnqueue(() => VideoCanvas?.Invalidate());
            }
        }
        catch
        {
        }
    }

    private void EnsureRenderTarget()
    {
        var session = _player.PlaybackSession;
        var w = (int)session.NaturalVideoWidth;
        var h = (int)session.NaturalVideoHeight;
        if (w <= 0 || h <= 0) return;

        var device = CanvasDevice.GetSharedDevice();
        if (_renderTarget is null || _renderTarget.SizeInPixels.Width != (uint)w || _renderTarget.SizeInPixels.Height != (uint)h)
        {
            _renderTarget?.Dispose();
            _renderTarget = new CanvasRenderTarget(device, w, h, 96);
        }
    }

    private void RequestFrameRender()
    {
        if (_renderTarget is not null && !_showingEditedPreview)
        {
            try
            {
                _player.CopyFrameToVideoSurface(_renderTarget);
                VideoCanvas?.Invalidate();
            }
            catch { }
        }
    }

    private void AudioAdjustment_CheckChanged(object sender, RoutedEventArgs e)
    {
        ApplyAudioPitchAndSpeed();
    }

    private void AudioAdjustment_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (PitchValueText is not null && PitchSlider is not null)
            PitchValueText.Text = $"{PitchSlider.Value:+0;-0;0} semitones";
        if (SpeedValueText is not null && SpeedSlider is not null)
            SpeedValueText.Text = $"{SpeedSlider.Value:0.00}x";
        if (VolumeBoostValueText is not null && VolumeBoostSlider is not null)
            VolumeBoostValueText.Text = $"{VolumeBoostSlider.Value:0}%";

        if (EqBand1Text is not null && EqBand1Slider is not null) EqBand1Text.Text = $"{EqBand1Slider.Value:+0;-0;0} dB";
        if (EqBand2Text is not null && EqBand2Slider is not null) EqBand2Text.Text = $"{EqBand2Slider.Value:+0;-0;0} dB";
        if (EqBand3Text is not null && EqBand3Slider is not null) EqBand3Text.Text = $"{EqBand3Slider.Value:+0;-0;0} dB";
        if (EqBand4Text is not null && EqBand4Slider is not null) EqBand4Text.Text = $"{EqBand4Slider.Value:+0;-0;0} dB";
        if (EqBand5Text is not null && EqBand5Slider is not null) EqBand5Text.Text = $"{EqBand5Slider.Value:+0;-0;0} dB";
        if (EqBand6Text is not null && EqBand6Slider is not null) EqBand6Text.Text = $"{EqBand6Slider.Value:+0;-0;0} dB";

        if (ReverbMixValueText is not null && ReverbMixSlider is not null)
        {
            var mix = (int)Math.Round(ReverbMixSlider.Value);
            ReverbMixValueText.Text = mix == 0 ? "0% (Dry)" : $"{mix}%";
        }
        if (ReverbRoomValueText is not null && ReverbRoomSlider is not null)
            ReverbRoomValueText.Text = $"{ReverbRoomSlider.Value:0}%";

        ApplyAudioPitchAndSpeed();
    }

    private void EqPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EqPresetComboBox is null || EqBand1Slider is null) return;

        double[] preset = EqPresetComboBox.SelectedIndex switch
        {
            1 => [6, 4, 1, 0, 0, 0],         // Bass Boost
            2 => [-2, 0, 3, 4, 2, 1],        // Vocal Enhance
            3 => [0, 0, 0, 2, 5, 6],         // Treble Boost
            4 => [5, 2, -1, 0, 3, 5],        // Loudness
            5 => [3, 1, 0, 2, 3, 4],         // Acoustic
            _ => [0, 0, 0, 0, 0, 0]          // Flat
        };

        EqBand1Slider.Value = preset[0];
        EqBand2Slider.Value = preset[1];
        EqBand3Slider.Value = preset[2];
        EqBand4Slider.Value = preset[3];
        EqBand5Slider.Value = preset[4];
        EqBand6Slider.Value = preset[5];
        ApplyAudioPitchAndSpeed();
    }

    private void ResetEq_Click(object sender, RoutedEventArgs e)
    {
        if (EqPresetComboBox is not null) EqPresetComboBox.SelectedIndex = 0;
        if (EqBand1Slider is not null) EqBand1Slider.Value = 0;
        if (EqBand2Slider is not null) EqBand2Slider.Value = 0;
        if (EqBand3Slider is not null) EqBand3Slider.Value = 0;
        if (EqBand4Slider is not null) EqBand4Slider.Value = 0;
        if (EqBand5Slider is not null) EqBand5Slider.Value = 0;
        if (EqBand6Slider is not null) EqBand6Slider.Value = 0;
        ApplyAudioPitchAndSpeed();
    }

    private void AutoNormalizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeBoostSlider is null || NormalizeCheckBox is null) return;
        NormalizeCheckBox.IsChecked = true;

        var peak = _waveformPeaks.Count > 0 ? _waveformPeaks.Max() : 0f;
        if (peak > 0.01f)
        {
            var optimalBoost = Math.Clamp(Math.Round(0.95 / peak * 100), 100, 300);
            VolumeBoostSlider.Value = optimalBoost;
            StatusText.Text = $"Audio auto-calibrated to peak (Boost: {optimalBoost}%)";
        }
        else
        {
            VolumeBoostSlider.Value = 120;
            StatusText.Text = "Audio normalization enabled";
        }
    }

    private void ApplyAudioPitchAndSpeed()
    {
        var speed = Math.Clamp(SpeedSlider?.Value ?? 1.0, 0.5, 2.0);
        var pitch = PitchSlider?.Value ?? 0;
        var boost = Math.Clamp((VolumeBoostSlider?.Value ?? 100) / 100.0, 1.0, 3.0);
        var normalize = NormalizeCheckBox?.IsChecked ?? false;
        var eq = new double[]
        {
            EqBand1Slider?.Value ?? 0,
            EqBand2Slider?.Value ?? 0,
            EqBand3Slider?.Value ?? 0,
            EqBand4Slider?.Value ?? 0,
            EqBand5Slider?.Value ?? 0,
            EqBand6Slider?.Value ?? 0
        };
        var revMix = Math.Clamp((ReverbMixSlider?.Value ?? 0) / 100.0, 0.0, 1.0);
        var revRoom = Math.Clamp((ReverbRoomSlider?.Value ?? 50) / 100.0, 0.1, 1.0);
        var peak = _waveformPeaks.Count > 0 ? (double)_waveformPeaks.Max() : 1.0;

        try
        {
            if (_player.PlaybackSession is not null)
            {
                _player.PlaybackSession.PlaybackRate = speed;
            }
            _player.PlaybackRate = speed;
        }
        catch { }

        if (_showingEditedPreview)
        {
            ExitEditedPreview();
            return;
        }

        if (_audioEngine is not null && _audioEngine.HasAudioTrack)
        {
            _audioEngine.SetPitchAndSpeed(pitch, speed);
            _audioEngine.SetEqualizer(eq);
            _audioEngine.SetReverb(revMix, revRoom);
            _audioEngine.SetVolumeBoostAndNormalize(boost, normalize, peak);
            _audioEngine.SetVolume(VolumeSlider?.Value ?? 1.0, _userMuted);

            var hasActiveEffects = _audioEngine.HasActiveAudioEffects;
            UpdateAudioActiveIndicator(hasActiveEffects);

            _player.IsMuted = true;
            _player.Volume = 0;

            if (_player.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
            {
                if (!_audioEngine.IsPlaying)
                {
                    _audioEngine.Seek(_player.PlaybackSession.Position);
                    _audioEngine.Play();
                    if (!_audioEngine.IsPlaying)
                    {
                        _player.IsMuted = _userMuted;
                        _player.Volume = _userMuted ? 0 : (VolumeSlider?.Value ?? 1.0);
                    }
                }
            }
        }
        else
        {
            UpdateAudioActiveIndicator(false);
            _player.IsMuted = _userMuted;
            _player.Volume = _userMuted ? 0 : (VolumeSlider?.Value ?? 1.0);
        }
    }

    private void SyncAudioVolume()
    {
        var vol = VolumeSlider?.Value ?? 1.0;

        if (_showingEditedPreview)
        {
            _player.Volume = _userMuted ? 0 : vol;
            _player.IsMuted = _userMuted;
            _audioEngine?.Stop();
        }
        else if (_audioEngine is not null && _audioEngine.HasAudioTrack)
        {
            _audioEngine.SetVolume(vol, _userMuted);
            _player.IsMuted = true;
            _player.Volume = 0;
        }
        else
        {
            _player.Volume = _userMuted ? 0 : vol;
            _player.IsMuted = _userMuted;
        }

        UpdateVolumeControl();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var volume = Math.Clamp(e.NewValue, 0, 1);
        _userMuted = volume <= 0;
        if (volume > 0)
        {
            _lastVolume = volume;
        }

        SyncAudioVolume();
    }

    private void VolumeMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_userMuted || VolumeSlider.Value <= 0)
        {
            _userMuted = false;
            VolumeSlider.Value = Math.Clamp(_lastVolume, 0.05, 1);
        }
        else
        {
            _lastVolume = VolumeSlider.Value;
            _userMuted = true;
        }

        SyncAudioVolume();
    }

    private void UpdateVolumeControl()
    {
        var muted = _userMuted || (VolumeSlider?.Value ?? 1) <= 0;
        VolumeGlyph.Glyph = muted ? "\uE74F" : "\uE767";
        var vol = VolumeSlider?.Value ?? 1;
        VolumePercentText.Text = muted ? "0%" : $"{Math.Round(vol * 100):0}%";
        var action = muted ? "Unmute" : "Mute";
        ToolTipService.SetToolTip(VolumeMuteButton, action);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(VolumeMuteButton, action);
    }

    private bool _isAudioTabSelected;

    private void VideoTabButton_Click(object sender, RoutedEventArgs e) => SelectAdjustmentTab(false);

    private void AudioTabButton_Click(object sender, RoutedEventArgs e) => SelectAdjustmentTab(true);

    private void SelectAdjustmentTab(bool isAudioTab)
    {
        _isAudioTabSelected = isAudioTab;
        var isVideo = VideoExtensions.Contains(Path.GetExtension(_sourcePath ?? string.Empty));

        if (VideoTabButton is not null && AudioTabButton is not null)
        {
            var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
            var defaultStyle = (Style)Application.Current.Resources["DefaultButtonStyle"];

            VideoTabButton.Style = isAudioTab ? defaultStyle : accentStyle;
            AudioTabButton.Style = isAudioTab ? accentStyle : defaultStyle;
        }

        if (VideoAdjustments is not null)
        {
            VideoAdjustments.Visibility = (isVideo && !isAudioTab) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (AudioAdjustments is not null)
        {
            AudioAdjustments.Visibility = (!isVideo || isAudioTab) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateAudioActiveIndicator(bool hasActive)
    {
        if (AudioActiveBadge is not null)
        {
            AudioActiveBadge.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        }

        if (AudioStatusBadge is not null)
        {
            AudioStatusBadge.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ResetVideoAdjustments_Click(object sender, RoutedEventArgs e) => ResetVideoAdjustments();

    private void ResetAudioAdjustments_Click(object sender, RoutedEventArgs e) => ResetAudioAdjustments();

    private void ResetVideoAdjustments()
    {
        _cropRegion = new CropRegion(0, 0, 1, 1);
        _hasCropSelection = false;
        _cropDragRectangle = null;
        _cropMode = false;
        if (CropModeButton is not null) CropModeButton.Content = "Crop video";
        UpdateCropOverlayInteractionState();
        if (CropHintText is not null) CropHintText.Text = "Choose a ratio, then drag over the video.";
        DrawCropOverlay();
        if (TemperatureSlider is not null) TemperatureSlider.Value = 0;
        if (TintSlider is not null) TintSlider.Value = 0;
        if (BrightnessSlider is not null) BrightnessSlider.Value = 0;
        if (ContrastSlider is not null) ContrastSlider.Value = 0;
        if (HighlightsSlider is not null) HighlightsSlider.Value = 0;
        if (ShadowsSlider is not null) ShadowsSlider.Value = 0;
        if (VibranceSlider is not null) VibranceSlider.Value = 0;
        if (SaturationSlider is not null) SaturationSlider.Value = 1;
        if (HueSlider is not null) HueSlider.Value = 0;
        if (TemperatureValueText is not null) TemperatureValueText.Text = "0";
        if (TintValueText is not null) TintValueText.Text = "0";
        if (BrightnessValueText is not null) BrightnessValueText.Text = "0.00";
        if (ContrastValueText is not null) ContrastValueText.Text = "0.00";
        if (HighlightsValueText is not null) HighlightsValueText.Text = "0.00";
        if (ShadowsValueText is not null) ShadowsValueText.Text = "0.00";
        if (VibranceValueText is not null) VibranceValueText.Text = "0.00";
        if (SaturationValueText is not null) SaturationValueText.Text = "1.00";
        if (HueValueText is not null) HueValueText.Text = "0°";
        VideoCanvas?.Invalidate();
    }

    private void ResetAudioAdjustments()
    {
        if (PitchSlider is not null) PitchSlider.Value = 0;
        if (SpeedSlider is not null) SpeedSlider.Value = 1;
        if (VolumeBoostSlider is not null) VolumeBoostSlider.Value = 100;
        if (NormalizeCheckBox is not null) NormalizeCheckBox.IsChecked = false;
        ResetEq_Click(this, new RoutedEventArgs());
        if (ReverbMixSlider is not null) ReverbMixSlider.Value = 0;
        if (ReverbRoomSlider is not null) ReverbRoomSlider.Value = 50;
        UpdateAudioActiveIndicator(false);
        ApplyAudioPitchAndSpeed();
    }

    private void UpdateTrimLabels()
    {
        var start = _trimStartSeconds;
        var end = _trimEndSeconds;
        StartTimeText.Text = $"Start  {FormatTime(start)}";
        EndTimeText.Text = $"End  {FormatTime(end)}";
        PlayheadTimeText.Text = $"Playhead  {FormatTime(_playheadSeconds)}";
        DurationText.Text = $"{FormatTime(end - start)} selected / {FormatTime(_durationSeconds)}";
    }

    private void UpdateCurrentPosition()
    {
        if (_sourcePath is not null && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            var playbackEnd = _showingEditedPreview
                ? _previewPlaybackDuration
                : _trimEndSeconds;
            if (_player.PlaybackSession.Position.TotalSeconds >= playbackEnd)
            {
                _player.Pause();
                _audioEngine?.Pause();
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(playbackEnd);
                _audioEngine?.Seek(TimeSpan.FromSeconds(playbackEnd));
            }

            if (!_showingEditedPreview && _audioEngine is not null && _audioEngine.IsPlaying)
            {
                var drift = Math.Abs((_player.PlaybackSession.Position - _audioEngine.CurrentPosition).TotalSeconds);
                if (drift > 1.5 && (DateTime.UtcNow - _lastDriftCorrection).TotalSeconds > 2.0)
                {
                    _lastDriftCorrection = DateTime.UtcNow;
                    _audioEngine.Seek(_player.PlaybackSession.Position);
                }
            }

            _playheadSeconds = _showingEditedPreview
                ? Math.Min(_trimEndSeconds, _trimStartSeconds + _player.PlaybackSession.Position.TotalSeconds * Math.Clamp(SpeedSlider.Value, 0.5, 2.0))
                : _player.PlaybackSession.Position.TotalSeconds;
            UpdateTrimLabels();
            UpdatePlayheadMarker();
            UpdateAudioWaveformPlayhead();
        }

        UpdatePlaybackButton();
    }

    private void UpdatePlaybackButton()
    {
        if (PlaybackButton is null || PlaybackGlyph is null)
        {
            return;
        }

        var isPlaying = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        PlaybackGlyph.Glyph = isPlaying ? "\uE769" : "\uE768";
        var action = isPlaying ? "Pause" : "Play";
        ToolTipService.SetToolTip(PlaybackButton, action);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlaybackButton, action);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _exportCancellation?.Cancel();
        _waveformCancellation?.Cancel();
        _player.Pause();
        _audioEngine?.Dispose();
        _audioEngine = null;
        _renderTarget?.Dispose();
        _renderTarget = null;
        Preview.Source = null;
        _player.Source = null;
        CleanupPreviewFiles();
    }

    private void CleanupPreviewFiles()
    {
        foreach (var path in _previewFiles.ToArray())
        {
            try
            {
                File.Delete(path);
                _previewFiles.Remove(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss\.f")
            : value.ToString(@"m\:ss\.f");
    }

    private void PreviewSurface_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
    }

    private async void PreviewSurface_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.FirstOrDefault() is StorageFile file)
            {
                await LoadFileAsync(file.Path);
            }
        }
    }
}