using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
using System.Globalization;
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

    public sealed record CutRegion(Guid Id, double Start, double End);

    private enum TimelineDragMode
    {
        None,
        Start,
        End,
        CutStart,
        CutEnd,
        CutMove,
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
    private readonly List<CutRegion> _cutRegions = [];
    private Guid? _selectedCutId;
    private CutRegion? _draggingCut;
    private double _cutDragOffset;
    private double _undoStart;
    private double _undoEnd;
    private List<CutRegion> _undoCuts = [];
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
    private double _timelineZoom = 1.0;
    private bool _isUpdatingZoomUi;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _waveformCancellation;
    private Grid? _playheadMarker;
    private CanvasRenderTarget? _renderTarget;
    private bool _isReady;
    private AudioPitchEngine? _audioEngine;
    private bool _userMuted;
    private readonly List<MediaOverlay> _overlays = [];
    private string? _selectedOverlayId;
    private MediaOverlay? _draggingOverlay;
    private Windows.Foundation.Point _overlayDragStartPoint;
    private double _overlayStartNormX;
    private double _overlayStartNormY;
    private double _draggingElementWidth;
    private double _draggingElementHeight;
    private int _videoNaturalWidth = 1920;
    private int _videoNaturalHeight = 1080;
    private List<MediaOverlay> _undoOverlays = [];
    private readonly Dictionary<string, (int Width, int Height)> _imageDimensions = [];
    private readonly Dictionary<string, BitmapImage> _overlayBitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private enum OverlayInteractionMode
    {
        None,
        Moving,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomLeft,
        ResizeBottomRight
    }
    private OverlayInteractionMode _overlayInteractionMode = OverlayInteractionMode.None;
    private double _overlayStartWidth;
    private double _overlayStartHeight;
    private double _overlayStartPosX;
    private double _overlayStartPosY;
    private bool _updatingOverlayInspector;
    private double? _overlaySnapVerticalX;
    private double? _overlaySnapHorizontalY;
    private enum AdjustmentTab { Video, Audio, Overlays }
    private AdjustmentTab _selectedAdjustmentTab = AdjustmentTab.Video;
    private static readonly string[] PresetColors = ["#FFFFFF", "#000000", "#FFEB3B", "#FF3B30", "#00E5FF", "#4CAF50", "#FF9800"];
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
        SetTimelineZoomEnabled(false);
        TimelineZoomOutButton.Click += TimelineZoomOutButton_Click;
        TimelineZoomInButton.Click += TimelineZoomInButton_Click;
        TimelineZoomResetButton.Click += TimelineZoomResetButton_Click;
        TimelineZoomSlider.ValueChanged += TimelineZoomSlider_ValueChanged;
        TimelineScrollViewer.SizeChanged += TimelineScrollViewer_SizeChanged;
        TimelineScrollViewer.PointerWheelChanged += TimelineScrollViewer_PointerWheelChanged;
        TimelineCanvas.PointerWheelChanged += TimelineScrollViewer_PointerWheelChanged;
        InitializeColorPalette();

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
        UpdateCropRatioButtonsUI();
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
            _cutRegions.Clear();
            _selectedCutId = null;
            _draggingCut = null;
            UpdateCutButtonStates();
            _playheadSeconds = 0;
            _videoAspectRatio = 16d / 9d;
            _undoCuts = [];
            _undoOverlays = [];
            _hasUndo = false;
            UndoButton.IsEnabled = false;
            DrawTimeline();
            SetTimelineZoom(1.0);
            var isVideo = VideoExtensions.Contains(Path.GetExtension(path));
            _overlays.Clear();
            _selectedOverlayId = null;
            _draggingOverlay = null;
            UpdateOverlaysCount();
            UpdateOverlaysList();
            UpdateOverlayInspector();
            DrawOverlays();
            _cropRegion = new CropRegion(0, 0, 1, 1);
            _hasCropSelection = false;
            _cropAspectRatio = null;
            _cropMode = false;
            _cropDragMode = CropDragMode.None;
            UpdateCropOverlayInteractionState();
            CropModeButton.IsEnabled = false;
            CropModeButton.Content = "Crop video";
            SelectCropRatio("free", applyToViewport: false);
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
            SetTimelineZoomEnabled(false);

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
                _videoNaturalWidth = (int)naturalWidth;
                _videoNaturalHeight = (int)naturalHeight;
            }

            _trimStartSeconds = 0;
            _trimEndSeconds = _durationSeconds;
            _playheadSeconds = 0;
            TimelineCanvas.IsHitTestVisible = true;
            ExportButton.IsEnabled = true;
            PlaybackButton.IsEnabled = true;
            PreviewTrimButton.IsEnabled = true;
            SetTimelineZoom(1.0);
            SetTimelineZoomEnabled(true);
            UpdateCutButtonStates();
            var isVideo = VideoExtensions.Contains(Path.GetExtension(_sourcePath ?? string.Empty));
            CropModeButton.IsEnabled = isVideo;
            AdjustmentTabsGrid.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            SelectAdjustmentTab(!isVideo ? AdjustmentTab.Audio : _selectedAdjustmentTab);
            MetadataText.Text = $"{(isVideo ? "Video" : "Audio")}\n{FormatTime(_durationSeconds)} long";
            StatusText.Text = "Preview ready";
            EmptyStateSurface.Visibility = Visibility.Collapsed;
            UpdateTrimLabels();
            DrawTimeline();
            DrawAudioWaveformViewport();
            DrawCropOverlay();
            DrawOverlays();
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
            SetTimelineZoomEnabled(false);
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
        if (_cropMode)
        {
            ApplyCropAspectRatio(_cropAspectRatio);
        }
        UpdateCropOverlayInteractionState();
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
        SelectCropRatio("free", applyToViewport: false);
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

    private void CropRatioButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            if (!_cropMode)
            {
                _cropMode = true;
                UpdateCropOverlayInteractionState();
                CropModeButton.Content = "Done";
                VideoCanvas?.Invalidate();
            }
            SelectCropRatio(tag, applyToViewport: true);
        }
    }

    private void CropRatiosScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (CropRatiosScrollViewer is null) return;
        var delta = e.GetCurrentPoint(CropRatiosScrollViewer).Properties.MouseWheelDelta;
        CropRatiosScrollViewer.ChangeView(CropRatiosScrollViewer.HorizontalOffset - delta, null, null, true);
        e.Handled = true;
    }

    private string _selectedCropRatioTag = "free";

    private void SelectCropRatio(string ratioTag, bool applyToViewport = true)
    {
        _selectedCropRatioTag = ratioTag;

        if (ratioTag == "free")
        {
            _cropAspectRatio = null;
        }
        else if (double.TryParse(ratioTag, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedRatio))
        {
            _cropAspectRatio = parsedRatio;
        }

        UpdateCropRatioButtonsUI();

        if (applyToViewport && _cropMode)
        {
            ApplyCropAspectRatio(_cropAspectRatio);
        }

        if (CropHintText is not null && _cropMode)
        {
            var label = GetCropRatioLabel(ratioTag);
            CropHintText.Text = _cropAspectRatio.HasValue
                ? $"Crop ratio {label} active. Drag inside to move or use handles to resize."
                : "Freeform crop active. Drag inside to move or use handles to resize.";
        }
    }

    private static string GetCropRatioLabel(string tag) => tag switch
    {
        "1.7777778" => "16:9",
        "0.5625" => "9:16",
        "1" => "1:1",
        "0.8" => "4:5",
        "1.25" => "5:4",
        "1.3333333" => "4:3",
        "0.75" => "3:4",
        "2.3333333" => "21:9",
        "2" => "2:1",
        "1.5" => "3:2",
        "0.6666667" => "2:3",
        _ => "Free"
    };

    private void UpdateCropRatioButtonsUI()
    {
        if (CropRatiosPanel is null) return;

        var accentBrush = (Brush)Application.Current.Resources["AccentBrush"];
        var inkBrush = (Brush)Application.Current.Resources["InkBrush"];
        var lineBrush = (Brush)Application.Current.Resources["LineBrush"];
        var selectedBg = ThemeBrush(
            Windows.UI.Color.FromArgb(40, 0, 103, 192),
            Windows.UI.Color.FromArgb(60, 96, 205, 255));

        foreach (var child in CropRatiosPanel.Children)
        {
            if (child is Button btn && btn.Tag is string tag)
            {
                bool isSelected = string.Equals(tag, _selectedCropRatioTag, StringComparison.OrdinalIgnoreCase);

                btn.Background = isSelected ? selectedBg : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                btn.BorderBrush = isSelected ? accentBrush : lineBrush;
                btn.BorderThickness = new Thickness(isSelected ? 1.5 : 1);

                if (btn.Content is StackPanel sp)
                {
                    foreach (var spChild in sp.Children)
                    {
                        if (spChild is Grid grid)
                        {
                            foreach (var gChild in grid.Children)
                            {
                                if (gChild is Microsoft.UI.Xaml.Shapes.Rectangle rect)
                                {
                                    rect.Stroke = isSelected ? accentBrush : inkBrush;
                                    rect.StrokeThickness = isSelected ? 2 : 1.5;
                                }
                            }
                        }
                        else if (spChild is TextBlock tb)
                        {
                            tb.Foreground = isSelected ? accentBrush : inkBrush;
                            tb.FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                        }
                    }
                }
            }
        }
    }

    private static double SafeClamp(double value, double min, double max)
    {
        if (double.IsNaN(value)) return min;
        if (max < min) (min, max) = (max, min);
        return Math.Clamp(value, min, max);
    }

    private void ApplyCropAspectRatio(double? targetAspect)
    {
        try
        {
            var frame = GetVideoFrameBounds(ignoreCrop: true);
            if (frame.Width <= 0 || frame.Height <= 0)
            {
                return;
            }

            if (targetAspect is double ratio && ratio > 0)
            {
                // Determine center point (preserve current crop center if already active, or default to frame center)
                double currentNormCenterX = (_hasCropSelection && _cropRegion.Width > 0)
                    ? _cropRegion.X + _cropRegion.Width / 2.0
                    : 0.5;
                double currentNormCenterY = (_hasCropSelection && _cropRegion.Height > 0)
                    ? _cropRegion.Y + _cropRegion.Height / 2.0
                    : 0.5;

                // Fit target aspect ratio box into video frame bounds
                double newWidth, newHeight;
                if (frame.Width / frame.Height > ratio)
                {
                    // Video frame is wider than target ratio
                    newHeight = frame.Height;
                    newWidth = newHeight * ratio;
                }
                else
                {
                    // Video frame is taller than target ratio
                    newWidth = frame.Width;
                    newHeight = newWidth / ratio;
                }

                double normW = SafeClamp(newWidth / frame.Width, 0.001, 1.0);
                double normH = SafeClamp(newHeight / frame.Height, 0.001, 1.0);

                double normX = currentNormCenterX - normW / 2.0;
                double normY = currentNormCenterY - normH / 2.0;

                normX = SafeClamp(normX, 0.0, Math.Max(0.0, 1.0 - normW));
                normY = SafeClamp(normY, 0.0, Math.Max(0.0, 1.0 - normH));

                _cropRegion = new CropRegion(normX, normY, normW, normH);
                _hasCropSelection = true;
                _cropDragRectangle = null;
            }
            else
            {
                // Freeform crop: if no selection exists yet, initialize to full frame
                if (!_hasCropSelection)
                {
                    _cropRegion = new CropRegion(0, 0, 1, 1);
                    _hasCropSelection = true;
                    _cropDragRectangle = null;
                }
            }

            UpdateCropOverlayInteractionState();
            DrawCropOverlay();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApplyCropAspectRatio exception: {ex}");
        }
    }

    private void CropOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (!_cropMode)
            {
                return;
            }

            var frame = GetVideoFrameBounds(ignoreCrop: true);
            if (frame.Width <= 0 || frame.Height <= 0) return;
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CropOverlay_PointerPressed error: {ex}");
        }
    }

    private void CropOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        try
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CropOverlay_PointerMoved error: {ex}");
        }
    }

    private void CropOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        try
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
            try { CropOverlay.ReleasePointerCapture(e.Pointer); } catch { }

            if (_cropDragRectangle is { Width: >= 20, Height: >= 20 } selection)
            {
                var frame = GetVideoFrameBounds(ignoreCrop: true);
                if (frame.Width > 0 && frame.Height > 0)
                {
                    _cropRegion = new CropRegion(
                        SafeClamp((selection.X - frame.X) / frame.Width, 0, 1),
                        SafeClamp((selection.Y - frame.Y) / frame.Height, 0, 1),
                        SafeClamp(selection.Width / frame.Width, 0.001, 1),
                        SafeClamp(selection.Height / frame.Height, 0.001, 1));
                    _hasCropSelection = true;
                    _cropDragRectangle = null;
                    CropHintText.Text = "Drag inside to move or use handles to resize, then click Done.";
                    UpdateCropOverlayInteractionState();
                }
            }

            DrawCropOverlay();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CropOverlay_PointerReleased error: {ex}");
        }
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
        if (frame.Width <= 0 || frame.Height <= 0) return _cropDragStartRectangle;

        var deltaX = point.X - _cropDragStart.X;
        var deltaY = point.Y - _cropDragStart.Y;
        var width = _cropDragStartRectangle.Width;
        var height = _cropDragStartRectangle.Height;

        var maxLeft = Math.Max(frame.Left, frame.Right - width);
        var maxTop = Math.Max(frame.Top, frame.Bottom - height);

        var left = SafeClamp(_cropDragStartRectangle.Left + deltaX, frame.Left, maxLeft);
        var top = SafeClamp(_cropDragStartRectangle.Top + deltaY, frame.Top, maxTop);

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

        left = SafeClamp(left, frame.Left, Math.Max(frame.Left, right - 20));
        right = SafeClamp(right, Math.Min(frame.Right, left + 20), frame.Right);
        top = SafeClamp(top, frame.Top, Math.Max(frame.Top, bottom - 20));
        bottom = SafeClamp(bottom, Math.Min(frame.Bottom, top + 20), frame.Bottom);

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
        DrawOverlays();
    }

    private void OverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawOverlays();
    }

    private void UpdateVideoAdjustmentBounds()
    {
        VideoCanvas?.Invalidate();
    }

    private void DrawCropOverlay()
    {
        try
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

            if (_cropMode || _hasCropSelection || _cropDragMode != CropDragMode.None)
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DrawCropOverlay error: {ex}");
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
            else if (!_showingEditedPreview && _cutRegions.Count > 0)
            {
                var insideCut = _cutRegions.FirstOrDefault(c => session.Position.TotalSeconds >= c.Start && session.Position.TotalSeconds < c.End - 0.04);
                if (insideCut is not null)
                {
                    session.Position = TimeSpan.FromSeconds(insideCut.End);
                    _audioEngine?.Seek(session.Position);
                }
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
        var playheadX = TimeToTimelineX(_playheadSeconds);

        const double trackTop = 29;
        const double trackHeight = 24;
        var isInTrackY = point.Y >= trackTop - 5 && point.Y <= trackTop + trackHeight + 6;

        // 1. Playhead Knob hit test: red knob sits at Y ~ 21..32 centered on playheadX.
        // If clicking on or near the knob, prioritize moving the playhead even if overlapping a trim handle!
        var isPlayheadKnobHit = point.Y <= 33 && Math.Abs(point.X - playheadX) <= 10;

        // 2. Ruler area hit test: clicking anywhere in the ruler (Y < 24) should scrub playhead (unless clicking a cut badge)
        var isRulerHit = point.Y < trackTop - 5;

        CutRegion? hitCutBadge = null;
        if (isRulerHit && _cutRegions.Count > 0)
        {
            var trackLeft = 8d;
            var trackWidth = Math.Max(1, TimelineCanvas.ActualWidth - 16);
            foreach (var cut in _cutRegions)
            {
                var csX = Math.Max(startX, TimeToTimelineX(cut.Start));
                var ceX = Math.Min(endX, TimeToTimelineX(cut.End));
                var badgeCenter = (csX + ceX) / 2;
                var badgeLeft = Math.Clamp(badgeCenter - 34, trackLeft, trackLeft + trackWidth - 68);
                if (point.Y >= 5 && point.Y <= 26 && point.X >= badgeLeft && point.X <= badgeLeft + 68)
                {
                    hitCutBadge = cut;
                    break;
                }
            }
        }

        CutRegion? hitCutStart = null;
        CutRegion? hitCutEnd = null;
        CutRegion? hitCutBody = null;

        if (isInTrackY)
        {
            foreach (var cut in _cutRegions)
            {
                var csX = TimeToTimelineX(cut.Start);
                var ceX = TimeToTimelineX(cut.End);
                if (Math.Abs(point.X - csX) <= 8)
                {
                    hitCutStart = cut;
                    break;
                }
                if (Math.Abs(point.X - ceX) <= 8)
                {
                    hitCutEnd = cut;
                    break;
                }
                if (point.X >= csX && point.X <= ceX)
                {
                    hitCutBody = cut;
                }
            }
        }

        if (isPlayheadKnobHit)
        {
            // Clicking directly on the playhead knob / top portion
            _timelineDragMode = TimelineDragMode.Playhead;
            _selectedCutId = null;
            _draggingCut = null;
            SetPlayheadFromTimelineX(point.X);
        }
        else if (hitCutBadge is not null)
        {
            // Clicking cut duration badge selects that cut
            _selectedCutId = hitCutBadge.Id;
            _draggingCut = null;
            _timelineDragMode = TimelineDragMode.None;
        }
        else if (isRulerHit)
        {
            // Clicking anywhere in the top ruler area scrubs the playhead
            _timelineDragMode = TimelineDragMode.Playhead;
            _selectedCutId = null;
            _draggingCut = null;
            SetPlayheadFromTimelineX(point.X);
        }
        else if (hitCutStart is not null)
        {
            _selectedCutId = hitCutStart.Id;
            _draggingCut = hitCutStart;
            _timelineDragMode = TimelineDragMode.CutStart;
            CaptureUndo();
        }
        else if (hitCutEnd is not null)
        {
            _selectedCutId = hitCutEnd.Id;
            _draggingCut = hitCutEnd;
            _timelineDragMode = TimelineDragMode.CutEnd;
            CaptureUndo();
        }
        else if (isInTrackY && Math.Abs(point.X - startX) <= 10)
        {
            _timelineDragMode = TimelineDragMode.Start;
            _selectedCutId = null;
            _draggingCut = null;
            CaptureUndo();
        }
        else if (isInTrackY && Math.Abs(point.X - endX) <= 10)
        {
            _timelineDragMode = TimelineDragMode.End;
            _selectedCutId = null;
            _draggingCut = null;
            CaptureUndo();
        }
        else if (Math.Abs(point.X - playheadX) <= 6)
        {
            // Direct hit on the red playhead stem inside the track
            _timelineDragMode = TimelineDragMode.Playhead;
            _selectedCutId = null;
            _draggingCut = null;
            SetPlayheadFromTimelineX(point.X);
        }
        else if (hitCutBody is not null)
        {
            _selectedCutId = hitCutBody.Id;
            _draggingCut = hitCutBody;
            _timelineDragMode = TimelineDragMode.CutMove;
            _cutDragOffset = TimelineXToTime(point.X) - hitCutBody.Start;
            CaptureUndo();
        }
        else
        {
            // Clicking anywhere else on the track moves the playhead
            _timelineDragMode = TimelineDragMode.Playhead;
            _selectedCutId = null;
            _draggingCut = null;
            SetPlayheadFromTimelineX(point.X);
        }

        UpdateCutButtonStates();
        DrawTimeline();
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

        if (_timelineZoom > 1.001 && TimelineScrollViewer is not null)
        {
            var viewPointX = e.GetCurrentPoint(TimelineScrollViewer).Position.X;
            var viewWidth = TimelineScrollViewer.ActualWidth;
            if (viewPointX < 24)
            {
                var targetOffset = Math.Max(0, TimelineScrollViewer.HorizontalOffset - 25);
                TimelineScrollViewer.ChangeView(targetOffset, null, null, true);
            }
            else if (viewPointX > viewWidth - 24)
            {
                var maxOffset = Math.Max(0, TimelineCanvas.ActualWidth - viewWidth);
                var targetOffset = Math.Min(maxOffset, TimelineScrollViewer.HorizontalOffset + 25);
                TimelineScrollViewer.ChangeView(targetOffset, null, null, true);
            }
        }
        switch (_timelineDragMode)
        {
            case TimelineDragMode.Start:
                _trimStartSeconds = Math.Clamp(time, 0, Math.Max(0, _trimEndSeconds - 0.05));
                for (var i = 0; i < _cutRegions.Count; i++)
                {
                    if (_cutRegions[i].Start < _trimStartSeconds)
                    {
                        var end = Math.Max(_trimStartSeconds + 0.1, _cutRegions[i].End);
                        _cutRegions[i] = _cutRegions[i] with { Start = _trimStartSeconds, End = end };
                    }
                }
                break;

            case TimelineDragMode.End:
                _trimEndSeconds = Math.Clamp(time, Math.Min(_durationSeconds, _trimStartSeconds + 0.05), _durationSeconds);
                for (var i = 0; i < _cutRegions.Count; i++)
                {
                    if (_cutRegions[i].End > _trimEndSeconds)
                    {
                        var start = Math.Min(_trimEndSeconds - 0.1, _cutRegions[i].Start);
                        _cutRegions[i] = _cutRegions[i] with { Start = start, End = _trimEndSeconds };
                    }
                }
                break;

            case TimelineDragMode.CutStart:
                if (_draggingCut is not null)
                {
                    var idx = _cutRegions.FindIndex(c => c.Id == _draggingCut.Id);
                    if (idx >= 0)
                    {
                        var minTime = _trimStartSeconds;
                        var prev = _cutRegions.Where(c => c.Id != _draggingCut.Id && c.End <= _cutRegions[idx].End)
                            .OrderByDescending(c => c.End).FirstOrDefault();
                        if (prev is not null) minTime = Math.Max(minTime, prev.End + 0.05);

                        var maxTime = _cutRegions[idx].End - 0.1;
                        var newStart = Math.Clamp(time, minTime, maxTime);
                        _cutRegions[idx] = _cutRegions[idx] with { Start = newStart };
                        _draggingCut = _cutRegions[idx];
                    }
                }
                break;

            case TimelineDragMode.CutEnd:
                if (_draggingCut is not null)
                {
                    var idx = _cutRegions.FindIndex(c => c.Id == _draggingCut.Id);
                    if (idx >= 0)
                    {
                        var minTime = _cutRegions[idx].Start + 0.1;
                        var maxTime = _trimEndSeconds;
                        var next = _cutRegions.Where(c => c.Id != _draggingCut.Id && c.Start >= _cutRegions[idx].Start)
                            .OrderBy(c => c.Start).FirstOrDefault();
                        if (next is not null) maxTime = Math.Min(maxTime, next.Start - 0.05);

                        var newEnd = Math.Clamp(time, minTime, maxTime);
                        _cutRegions[idx] = _cutRegions[idx] with { End = newEnd };
                        _draggingCut = _cutRegions[idx];
                    }
                }
                break;

            case TimelineDragMode.CutMove:
                if (_draggingCut is not null)
                {
                    var idx = _cutRegions.FindIndex(c => c.Id == _draggingCut.Id);
                    if (idx >= 0)
                    {
                        var cutDuration = _cutRegions[idx].End - _cutRegions[idx].Start;
                        var desiredStart = time - _cutDragOffset;
                        var minTime = _trimStartSeconds;
                        var maxTime = _trimEndSeconds - cutDuration;
                        desiredStart = Math.Clamp(desiredStart, minTime, maxTime);
                        _cutRegions[idx] = _cutRegions[idx] with { Start = desiredStart, End = desiredStart + cutDuration };
                        _draggingCut = _cutRegions[idx];
                    }
                }
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

    private void TimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
        {
            UpdateTimelineZoomLayout();
        }
    }

    private void TimelineScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (TimelineScrollViewer is null || _durationSeconds <= 0) return;

        var props = e.GetCurrentPoint(TimelineScrollViewer).Properties;
        var ctrlPressed = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                           & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (ctrlPressed)
        {
            var delta = props.MouseWheelDelta;
            if (delta == 0) return;

            var mouseCanvasX = e.GetCurrentPoint(TimelineCanvas).Position.X;
            var pivotTime = TimelineXToTime(mouseCanvasX);
            var mouseViewX = e.GetCurrentPoint(TimelineScrollViewer).Position.X;

            var factor = delta > 0 ? 1.25 : 0.8;
            var newZoom = Math.Clamp(_timelineZoom * factor, 1.0, 10.0);
            SetTimelineZoom(newZoom, pivotTime, mouseViewX);
            e.Handled = true;
        }
        else if (_timelineZoom > 1.001)
        {
            var delta = props.MouseWheelDelta;
            if (delta != 0)
            {
                var step = -delta * 0.8;
                var maxScroll = Math.Max(0, TimelineCanvas.ActualWidth - TimelineScrollViewer.ActualWidth);
                var targetOffset = Math.Clamp(TimelineScrollViewer.HorizontalOffset + step, 0, maxScroll);
                TimelineScrollViewer.ChangeView(targetOffset, null, null, true);
                e.Handled = true;
            }
        }
    }

    private void TimelineZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingZoomUi) return;
        SetTimelineZoom(e.NewValue);
    }

    private void TimelineZoomControl_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (TimelineZoomSlider is null || !TimelineZoomSlider.IsEnabled) return;
        var props = e.GetCurrentPoint(TimelineZoomSlider).Properties;
        var delta = props.MouseWheelDelta;
        if (delta != 0)
        {
            var step = delta > 0 ? 0.5 : -0.5;
            SetTimelineZoom(_timelineZoom + step);
            e.Handled = true;
        }
    }

    private void TimelineZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetTimelineZoom(_timelineZoom - 0.5);
    }

    private void TimelineZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        SetTimelineZoom(_timelineZoom + 0.5);
    }

    private void TimelineZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        SetTimelineZoom(1.0);
    }

    private void SetTimelineZoomEnabled(bool enabled)
    {
        if (TimelineZoomOutButton is not null) TimelineZoomOutButton.IsEnabled = enabled;
        if (TimelineZoomSlider is not null) TimelineZoomSlider.IsEnabled = enabled;
        if (TimelineZoomInButton is not null) TimelineZoomInButton.IsEnabled = enabled;
        if (TimelineZoomResetButton is not null) TimelineZoomResetButton.IsEnabled = enabled;
        if (!enabled && TimelineScrollViewer is not null)
        {
            TimelineScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
    }

    private void SetTimelineZoom(double zoom, double? pivotTime = null, double? viewportPivotX = null)
    {
        zoom = Math.Clamp(Math.Round(zoom, 2), 1.0, 10.0);
        if (Math.Abs(_timelineZoom - zoom) < 0.01)
        {
            return;
        }

        _timelineZoom = zoom;
        UpdateTimelineZoomUi();
        UpdateTimelineZoomLayout(pivotTime, viewportPivotX);
    }

    private void UpdateTimelineZoomUi()
    {
        _isUpdatingZoomUi = true;
        try
        {
            if (TimelineZoomSlider is not null)
            {
                TimelineZoomSlider.Value = _timelineZoom;
            }
            if (TimelineZoomPercentText is not null)
            {
                TimelineZoomPercentText.Text = $"{Math.Round(_timelineZoom * 100)}%";
            }
        }
        finally
        {
            _isUpdatingZoomUi = false;
        }
    }

    private void UpdateTimelineZoomLayout(double? pivotTime = null, double? viewportPivotX = null)
    {
        if (TimelineCanvas is null || TimelineScrollViewer is null)
        {
            return;
        }

        var viewWidth = TimelineScrollViewer.ActualWidth;
        if (viewWidth <= 0)
        {
            return;
        }

        if (_timelineZoom <= 1.001)
        {
            TimelineCanvas.Width = viewWidth;
            TimelineScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            TimelineScrollViewer.ChangeView(0, null, null, true);
            DrawTimeline();
        }
        else
        {
            var targetWidth = Math.Max(viewWidth, (viewWidth - 16) * _timelineZoom + 16);
            TimelineCanvas.Width = targetWidth;
            TimelineScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

            var pivot = pivotTime ?? _playheadSeconds;
            var newTrackWidth = targetWidth - 16;
            var pivotFrac = _durationSeconds > 0 ? Math.Clamp(pivot / _durationSeconds, 0, 1) : 0;
            var newPivotX = 8 + pivotFrac * newTrackWidth;

            var pivotScreenX = viewportPivotX ?? (viewWidth / 2.0);
            var maxScroll = Math.Max(0, targetWidth - viewWidth);
            var targetOffset = Math.Clamp(newPivotX - pivotScreenX, 0, maxScroll);
            TimelineScrollViewer.ChangeView(targetOffset, null, null, true);
            DrawTimeline();
        }
    }

    private void EnsurePlayheadVisible(bool smooth = true)
    {
        if (TimelineScrollViewer is null || _timelineZoom <= 1.05 || _durationSeconds <= 0)
        {
            return;
        }

        var playheadX = TimeToTimelineX(_playheadSeconds);
        var viewLeft = TimelineScrollViewer.HorizontalOffset;
        var viewWidth = TimelineScrollViewer.ActualWidth;
        if (viewWidth <= 40) return;

        // Auto-scroll forward if playhead gets close to the right edge or is off screen
        if (playheadX > viewLeft + viewWidth - 30 || playheadX < viewLeft)
        {
            var maxScroll = Math.Max(0, TimelineCanvas.ActualWidth - viewWidth);
            var targetOffset = Math.Clamp(playheadX - viewWidth * 0.25, 0, maxScroll);
            TimelineScrollViewer.ChangeView(targetOffset, null, null, !smooth);
        }
    }

    private void AudioWaveformViewport_SizeChanged(object sender, SizeChangedEventArgs e) => DrawAudioWaveformViewport();

    private void EndTimelineDrag()
    {
        _timelineDragMode = TimelineDragMode.None;
        _draggingCut = null;
        try
        {
            TimelineCanvas?.ReleasePointerCaptures();
        }
        catch { }
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

            // Draw intermediate cut regions (YouTube Studio style)
            foreach (var cut in _cutRegions.OrderBy(c => c.Start))
            {
                var cutStartX = Math.Max(startX, TimeToTimelineX(cut.Start));
                var cutEndX = Math.Min(endX, TimeToTimelineX(cut.End));
                var cutWidth = Math.Max(4, cutEndX - cutStartX);
                if (cutEndX <= cutStartX)
                {
                    continue;
                }

                var isSelected = cut.Id == _selectedCutId;

                // Red-tinted overlay on track
                var cutOverlayBrush = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)175 : (byte)115, 255, 59, 48));
                AddTrackRectangle(TimelineCanvas, cutStartX, trackTop, cutWidth, trackHeight, cutOverlayBrush, 0);

                // Distinct red top and bottom borders
                AddTimelineRectangle(cutStartX, trackTop, cutWidth, 2.5, PlayheadRedBrush, 0);
                AddTimelineRectangle(cutStartX, trackTop + trackHeight - 2.5, cutWidth, 2.5, PlayheadRedBrush, 0);

                // Left cut handle
                var leftCutHandle = new Border
                {
                    Width = 6,
                    Height = trackHeight + 6,
                    CornerRadius = new CornerRadius(2),
                    Background = PlayheadRedBrush,
                    BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White),
                    BorderThickness = new Thickness(1)
                };
                TimelineCanvas.Children.Add(leftCutHandle);
                Canvas.SetLeft(leftCutHandle, cutStartX - 3);
                Canvas.SetTop(leftCutHandle, trackTop - 3);

                // Right cut handle
                var rightCutHandle = new Border
                {
                    Width = 6,
                    Height = trackHeight + 6,
                    CornerRadius = new CornerRadius(2),
                    Background = PlayheadRedBrush,
                    BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White),
                    BorderThickness = new Thickness(1)
                };
                TimelineCanvas.Children.Add(rightCutHandle);
                Canvas.SetLeft(rightCutHandle, cutEndX - 3);
                Canvas.SetTop(rightCutHandle, trackTop - 3);

                // Interactive badge pill above the cut
                var badge = new Border
                {
                    Height = 18,
                    CornerRadius = new CornerRadius(9),
                    Background = isSelected
                        ? PlayheadRedBrush
                        : ThemeBrush(Color.FromArgb(240, 255, 255, 255), Color.FromArgb(240, 36, 36, 36)),
                    BorderBrush = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : PlayheadRedBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 1, 4, 1)
                };
                var badgeStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                var badgeIcon = new FontIcon
                {
                    Glyph = "\uE8C0",
                    FontSize = 9,
                    Foreground = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : PlayheadRedBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var badgeText = new TextBlock
                {
                    Text = $"-{FormatTime(cut.End - cut.Start)}",
                    FontSize = 9.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : PlayheadRedBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var delBtn = new Button
                {
                    Width = 14,
                    Height = 14,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0),
                    CornerRadius = new CornerRadius(7),
                    Background = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : PlayheadRedBrush,
                    Foreground = isSelected ? PlayheadRedBrush : new SolidColorBrush(Microsoft.UI.Colors.White),
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 7 }
                };
                ToolTipService.SetToolTip(delBtn, "Delete this cut");
                var targetCutId = cut.Id;
                delBtn.Click += (s, args) => DeleteCut(targetCutId);

                badgeStack.Children.Add(badgeIcon);
                badgeStack.Children.Add(badgeText);
                badgeStack.Children.Add(delBtn);
                badge.Child = badgeStack;

                TimelineCanvas.Children.Add(badge);
                var badgeCenter = (cutStartX + cutEndX) / 2;
                var badgeLeft = Math.Clamp(badgeCenter - 34, trackLeft, trackLeft + trackWidth - 68);
                Canvas.SetLeft(badge, badgeLeft);
                Canvas.SetTop(badge, 6);
            }

            AddTimelineHandle(startX, trackTop, trackHeight);
            AddTimelineHandle(endX, trackTop, trackHeight);

            var tickCount = Math.Max(2, (int)(trackWidth / 70));
            var majorStep = 4;
            for (var index = 0; index <= tickCount; index++)
            {
                var fraction = (double)index / tickCount;
                var x = trackLeft + trackWidth * fraction;
                var major = index % majorStep == 0 || index == tickCount;
                AddTimelineLine(x, major ? 5 : 11, x, rulerBottom, ThemeBrush(Color.FromArgb(major ? (byte)175 : (byte)95, 101, 113, 126), Color.FromArgb(major ? (byte)190 : (byte)105, 184, 194, 205)), major ? 1.2 : 0.8);
                if (major)
                {
                    if (index == tickCount && (tickCount % majorStep) != 0 && (tickCount % majorStep) < 2)
                    {
                        continue;
                    }

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
                Width = 11,
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
                Width = 11,
                Height = 11,
                Fill = PlayheadRedBrush,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
                StrokeThickness = 1.2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 17, 0, 0)
            };
            _playheadMarker.Children.Add(stem);
            _playheadMarker.Children.Add(head);
            TimelineCanvas.Children.Add(_playheadMarker);
            Canvas.SetLeft(_playheadMarker, playheadX - 5.5);
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

        var barCount = Math.Min(1200, Math.Min(_waveformPeaks.Count, Math.Max(1, (int)(width / 3))));
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
            var barTime = TimelineXToTime(x);
            var insideSelection = x >= selectionStart && x <= selectionEnd;
            var insideCut = insideSelection && _cutRegions.Any(c => barTime >= c.Start && barTime <= c.End);

            Brush brush;
            if (insideCut)
            {
                brush = ThemeBrush(Color.FromArgb(160, 255, 120, 110), Color.FromArgb(160, 255, 90, 80));
            }
            else if (insideSelection)
            {
                brush = ThemeBrush(Color.FromArgb(255, 0, 78, 138), Color.FromArgb(255, 214, 235, 255));
            }
            else
            {
                brush = ThemeBrush(Color.FromArgb(175, 101, 116, 132), Color.FromArgb(165, 153, 163, 174));
            }
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
            var inCut = inSelection && _cutRegions.Any(c => barTime >= c.Start && barTime <= c.End);
            var x = left + fraction * plotWidth;
            var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = CreateAudioWaveformBrush(inSelection, hasPlayed, inCut)
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

        // Render cut regions over audio waveform
        foreach (var cut in _cutRegions)
        {
            var csX = left + (cut.Start / _durationSeconds) * plotWidth;
            var ceX = left + (cut.End / _durationSeconds) * plotWidth;
            var cw = Math.Max(2, ceX - csX);
            var cutOverlay = new Border
            {
                Width = cw,
                Height = maximumBarHeight * 2 + 10,
                Background = new SolidColorBrush(Color.FromArgb(45, 255, 59, 48)),
                BorderBrush = PlayheadRedBrush,
                BorderThickness = new Thickness(1, 0, 1, 0),
                CornerRadius = new CornerRadius(3)
            };
            AudioWaveformViewport.Children.Add(cutOverlay);
            Canvas.SetLeft(cutOverlay, csX);
            Canvas.SetTop(cutOverlay, centerY - cutOverlay.Height / 2);
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
                var inCut = bar.InSelection && _cutRegions.Any(c => bar.Time >= c.Start && bar.Time <= c.End);
                bar.Shape.Fill = CreateAudioWaveformBrush(bar.InSelection, hasPlayed, inCut);
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

    private SolidColorBrush CreateAudioWaveformBrush(bool inSelection, bool hasPlayed, bool inCut = false)
    {
        if (inCut)
        {
            return new SolidColorBrush(Color.FromArgb(160, 255, 90, 80));
        }

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

    private List<(double Start, double End)> GetKeptSegments()
    {
        if (_durationSeconds <= 0)
        {
            return [];
        }

        var trimStart = Math.Clamp(_trimStartSeconds, 0, _durationSeconds);
        var trimEnd = Math.Clamp(_trimEndSeconds, trimStart, _durationSeconds);
        if (trimEnd <= trimStart)
        {
            return [];
        }

        if (_cutRegions.Count == 0)
        {
            return [(trimStart, trimEnd)];
        }

        // Sanitize and sort cuts within active trim bounds
        var validCuts = _cutRegions
            .Select(c => (Start: Math.Clamp(c.Start, trimStart, trimEnd), End: Math.Clamp(c.End, trimStart, trimEnd)))
            .Where(c => c.End > c.Start + 0.04)
            .OrderBy(c => c.Start)
            .ToList();

        if (validCuts.Count == 0)
        {
            return [(trimStart, trimEnd)];
        }

        // Merge overlapping or adjacent cuts
        var mergedCuts = new List<(double Start, double End)>();
        var currentCut = validCuts[0];
        for (var i = 1; i < validCuts.Count; i++)
        {
            var next = validCuts[i];
            if (next.Start <= currentCut.End + 0.05)
            {
                currentCut = (currentCut.Start, Math.Max(currentCut.End, next.End));
            }
            else
            {
                mergedCuts.Add(currentCut);
                currentCut = next;
            }
        }
        mergedCuts.Add(currentCut);

        // Extract kept segments between merged cuts
        var segments = new List<(double Start, double End)>();
        var cursor = trimStart;
        foreach (var cut in mergedCuts)
        {
            if (cut.Start > cursor + 0.04)
            {
                segments.Add((cursor, cut.Start));
            }
            cursor = Math.Max(cursor, cut.End);
        }
        if (cursor < trimEnd - 0.04)
        {
            segments.Add((cursor, trimEnd));
        }

        return segments.Count > 0 ? segments : [(trimStart, trimEnd)];
    }

    private void NewCutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_durationSeconds <= 0)
        {
            return;
        }

        var trimStart = _trimStartSeconds;
        var trimEnd = _trimEndSeconds;
        if (trimEnd <= trimStart + 0.2)
        {
            return;
        }

        CaptureUndo();

        // Target cut duration: default 3s or 10% of trim, clamped between 0.5s and 5s
        var desiredDuration = Math.Clamp((trimEnd - trimStart) * 0.1, 0.5, 3.0);
        var playhead = Math.Clamp(_playheadSeconds, trimStart, trimEnd);
        var inExistingCut = _cutRegions.Any(c => playhead >= c.Start && playhead <= c.End);
        double cutStart;
        double cutEnd;

        if (!inExistingCut)
        {
            cutStart = playhead;
            cutEnd = Math.Min(trimEnd, cutStart + desiredDuration);
            var nextCut = _cutRegions.Where(c => c.Start > cutStart).OrderBy(c => c.Start).FirstOrDefault();
            if (nextCut is not null && cutEnd > nextCut.Start)
            {
                cutEnd = nextCut.Start;
            }

            if (cutEnd - cutStart < 0.1)
            {
                cutEnd = playhead;
                cutStart = Math.Max(trimStart, cutEnd - desiredDuration);
                var prevCut = _cutRegions.Where(c => c.End < cutEnd).OrderByDescending(c => c.End).FirstOrDefault();
                if (prevCut is not null && cutStart < prevCut.End)
                {
                    cutStart = prevCut.End;
                }
            }
        }
        else
        {
            var kept = GetKeptSegments();
            var largest = kept.OrderByDescending(s => s.End - s.Start).FirstOrDefault();
            if (largest == default || largest.End - largest.Start < 0.2)
            {
                return;
            }

            var mid = (largest.Start + largest.End) / 2;
            var half = Math.Min(desiredDuration, (largest.End - largest.Start) * 0.6) / 2;
            cutStart = mid - half;
            cutEnd = mid + half;
        }

        if (cutEnd - cutStart >= 0.1)
        {
            var newCut = new CutRegion(Guid.NewGuid(), cutStart, cutEnd);
            _cutRegions.Add(newCut);
            _selectedCutId = newCut.Id;
            UpdateCutButtonStates();
            UpdateTrimLabels();
            DrawTimeline();
            DrawAudioWaveformViewport();
        }
    }

    private void DeleteCutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCutId.HasValue)
        {
            DeleteCut(_selectedCutId.Value);
        }
    }

    private void DeleteCut(Guid cutId)
    {
        CaptureUndo();
        _cutRegions.RemoveAll(c => c.Id == cutId);
        if (_selectedCutId == cutId)
        {
            _selectedCutId = null;
        }
        UpdateCutButtonStates();
        UpdateTrimLabels();
        DrawTimeline();
        DrawAudioWaveformViewport();
    }

    private void UpdateCutButtonStates()
    {
        if (NewCutButton is not null)
        {
            NewCutButton.IsEnabled = _durationSeconds > 0;
        }

        if (DeleteCutButton is not null)
        {
            var hasCutSelected = _selectedCutId.HasValue && _cutRegions.Any(c => c.Id == _selectedCutId.Value);
            DeleteCutButton.Visibility = hasCutSelected ? Visibility.Visible : Visibility.Collapsed;
            DeleteCutButton.IsEnabled = hasCutSelected;
        }
    }

    private void CaptureUndo()
    {
        if (!_hasUndo)
        {
            _undoStart = _trimStartSeconds;
            _undoEnd = _trimEndSeconds;
            _undoCuts = _cutRegions.Select(c => c with {}).ToList();
            _undoOverlays = _overlays.Select(o => o.Clone()).ToList();
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
        _cutRegions.Clear();
        _cutRegions.AddRange(_undoCuts.Select(c => c with {}));
        _selectedCutId = null;
        _overlays.Clear();
        _overlays.AddRange(_undoOverlays.Select(o => o.Clone()));
        _selectedOverlayId = null;
        _hasUndo = false;
        UndoButton.IsEnabled = false;
        UpdateCutButtonStates();
        UpdateTrimLabels();
        DrawTimeline();
        DrawAudioWaveformViewport();
        UpdateOverlaysCount();
        UpdateOverlaysList();
        UpdateOverlayInspector();
        DrawOverlays();
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
        _cutRegions.Clear();
        _selectedCutId = null;
        UpdateCutButtonStates();
        UpdateTrimLabels();
        DrawTimeline();
        DrawAudioWaveformViewport();
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
            var keptSegments = GetKeptSegments();
            var keptDuration = keptSegments.Sum(s => Math.Max(0, s.End - s.Start));
            await _exportService.ExportTrimAsync(
                _sourcePath,
                previewPath,
                keptSegments,
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
            _previewPlaybackDuration = keptDuration / Math.Clamp(SpeedSlider.Value, 0.5, 2.0);
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
            var keptSegments = GetKeptSegments();
            await _exportService.ExportTrimAsync(
                _sourcePath,
                output.Path,
                keptSegments,
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
        SetTimelineZoomEnabled(!exporting && _durationSeconds > 0);
        NewCutButton.IsEnabled = !exporting && _durationSeconds > 0;
        DeleteCutButton.IsEnabled = !exporting && _selectedCutId.HasValue && _cutRegions.Any(c => c.Id == _selectedCutId.Value);
        CropModeButton.IsEnabled = !exporting && _durationSeconds > 0 && VideoExtensions.Contains(Path.GetExtension(_sourcePath ?? string.Empty));
        if (CropRatiosPanel is not null) CropRatiosPanel.IsHitTestVisible = !exporting;
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
        (_audioEngine?.HasAudioTrack ?? true) || !VideoExtensions.Contains(Path.GetExtension(_sourcePath ?? string.Empty)),
        _overlays.Select(o => o.Clone()).ToList(),
        _videoNaturalWidth > 0 ? _videoNaturalWidth : 1920,
        _videoNaturalHeight > 0 ? _videoNaturalHeight : 1080);

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
            var cropX = SafeClamp(_cropRegion.X, 0.0, 0.999);
            var cropY = SafeClamp(_cropRegion.Y, 0.0, 0.999);
            var cropW = SafeClamp(_cropRegion.Width, 0.001, Math.Max(0.001, 1.0 - cropX));
            var cropH = SafeClamp(_cropRegion.Height, 0.001, Math.Max(0.001, 1.0 - cropY));
            var srcX = cropX * _renderTarget.SizeInPixels.Width;
            var srcY = cropY * _renderTarget.SizeInPixels.Height;
            var srcW = SafeClamp(cropW * _renderTarget.SizeInPixels.Width, 1.0, Math.Max(1.0, _renderTarget.SizeInPixels.Width - srcX));
            var srcH = SafeClamp(cropH * _renderTarget.SizeInPixels.Height, 1.0, Math.Max(1.0, _renderTarget.SizeInPixels.Height - srcY));
            sourceRect = new Windows.Foundation.Rect(srcX, srcY, Math.Max(1, srcW), Math.Max(1, srcH));
        }
        else
        {
            sourceRect = _renderTarget.Bounds;
        }

        try
        {
            ds.DrawImage(currentImage, destRect, sourceRect);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VideoCanvas_Draw DrawImage error: {ex}");
        }
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

    private void VolumeControl_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (VolumeSlider is null) return;
        var props = e.GetCurrentPoint(VolumeSlider).Properties;
        var delta = props.MouseWheelDelta;
        if (delta != 0)
        {
            var step = delta > 0 ? 0.05 : -0.05;
            VolumeSlider.Value = Math.Clamp(Math.Round(VolumeSlider.Value + step, 2), 0, 1);
            e.Handled = true;
        }
    }

    private void VideoTabButton_Click(object sender, RoutedEventArgs e) => SelectAdjustmentTab(AdjustmentTab.Video);

    private void AudioTabButton_Click(object sender, RoutedEventArgs e) => SelectAdjustmentTab(AdjustmentTab.Audio);

    private void OverlaysTabButton_Click(object sender, RoutedEventArgs e) => SelectAdjustmentTab(AdjustmentTab.Overlays);

    private void SelectAdjustmentTab(bool isAudioTab) => SelectAdjustmentTab(isAudioTab ? AdjustmentTab.Audio : AdjustmentTab.Video);

    private void SelectAdjustmentTab(AdjustmentTab tab)
    {
        _selectedAdjustmentTab = tab;
        var isVideo = VideoExtensions.Contains(Path.GetExtension(_sourcePath ?? string.Empty));

        if (VideoTabButton is not null && AudioTabButton is not null && OverlaysTabButton is not null)
        {
            var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
            var defaultStyle = (Style)Application.Current.Resources["DefaultButtonStyle"];

            VideoTabButton.Style = (tab == AdjustmentTab.Video) ? accentStyle : defaultStyle;
            AudioTabButton.Style = (tab == AdjustmentTab.Audio) ? accentStyle : defaultStyle;
            OverlaysTabButton.Style = (tab == AdjustmentTab.Overlays) ? accentStyle : defaultStyle;

            if (OverlaysCountBadge is not null)
            {
                OverlaysCountBadge.Background = (tab == AdjustmentTab.Overlays)
                    ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255))
                    : (Brush)Application.Current.Resources["AccentBrush"];
            }
        }

        if (VideoAdjustments is not null)
        {
            VideoAdjustments.Visibility = (isVideo && tab == AdjustmentTab.Video) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (AudioAdjustments is not null)
        {
            AudioAdjustments.Visibility = (!isVideo || tab == AdjustmentTab.Audio) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (OverlaysPanel is not null)
        {
            OverlaysPanel.Visibility = (isVideo && tab == AdjustmentTab.Overlays) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void InitializeColorPalette()
    {
        if (TextColorPalette is null) return;
        TextColorPalette.Children.Clear();
        foreach (var hex in PresetColors)
        {
            var col = ParseHexColor(hex);
            var circle = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(col),
                BorderBrush = ThemeBrush(Color.FromArgb(160, 120, 120, 120), Color.FromArgb(180, 200, 200, 200)),
                BorderThickness = new Thickness(1.5),
                Tag = hex,
                Margin = new Thickness(0, 0, 2, 0)
            };
            ToolTipService.SetToolTip(circle, hex);
            circle.PointerPressed += (s, e) =>
            {
                if (s is Border b && b.Tag is string colorHex)
                {
                    var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
                    if (selected is not null)
                    {
                        CaptureUndo();
                        selected.FontColorHex = colorHex;
                        UpdateColorPaletteSelection(colorHex);
                        DrawOverlays();
                    }
                }
            };
            TextColorPalette.Children.Add(circle);
        }
    }

    private void UpdateColorPaletteSelection(string activeHex)
    {
        if (TextColorPalette is null) return;
        foreach (var child in TextColorPalette.Children)
        {
            if (child is Border b && b.Tag is string hex)
            {
                var isSelected = string.Equals(hex, activeHex, StringComparison.OrdinalIgnoreCase);
                b.BorderThickness = new Thickness(isSelected ? 2.5 : 1.5);
                b.BorderBrush = isSelected ? (Brush)Application.Current.Resources["AccentBrush"] : ThemeBrush(Color.FromArgb(160, 120, 120, 120), Color.FromArgb(180, 200, 200, 200));
            }
        }
    }

    private void AddTextOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        var ovl = new MediaOverlay
        {
            Type = OverlayType.Text,
            Text = "Your Text Here",
            FontSize = 32,
            FontColorHex = "#FFFFFF",
            NormalizedX = 0.5,
            NormalizedY = 0.5,
            ShowEntireVideo = true,
            StartSeconds = 0,
            EndSeconds = Math.Max(5, _durationSeconds > 0 ? _durationSeconds : 10)
        };
        _overlays.Add(ovl);
        _selectedOverlayId = ovl.Id;
        CaptureUndo();
        UpdateOverlaysCount();
        UpdateOverlaysList();
        UpdateOverlayInspector();
        DrawOverlays();
    }

    private async void AddImageOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await CacheImageDimensionsAsync(file.Path);

        var ovl = new MediaOverlay
        {
            Type = OverlayType.Image,
            ImagePath = file.Path,
            ImageWidthPercent = 25,
            NormalizedX = 0.95,
            NormalizedY = 0.95,
            ShowEntireVideo = true,
            StartSeconds = 0,
            EndSeconds = Math.Max(5, _durationSeconds > 0 ? _durationSeconds : 10)
        };
        _overlays.Add(ovl);
        _selectedOverlayId = ovl.Id;
        CaptureUndo();
        UpdateOverlaysCount();
        UpdateOverlaysList();
        UpdateOverlayInspector();
        DrawOverlays();
    }

    private async void ChangeImageButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null || selected.Type != OverlayType.Image) return;

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await CacheImageDimensionsAsync(file.Path);
        CaptureUndo();
        selected.ImagePath = file.Path;
        if (ImageFileNameText is not null) ImageFileNameText.Text = Path.GetFileName(file.Path);
        UpdateOverlaysList();
        DrawOverlays();
    }

    private async Task CacheImageDimensionsAsync(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            _imageDimensions[path] = ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch
        {
            _imageDimensions[path] = (300, 300);
        }

        GetOrCreateOverlayBitmap(path);
    }

    private BitmapImage? GetOrCreateOverlayBitmap(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        if (_overlayBitmapCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            var bmp = new BitmapImage(new Uri(path));
            _overlayBitmapCache[path] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void DeleteOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        var idx = _overlays.FindIndex(o => o.Id == _selectedOverlayId);
        if (idx >= 0)
        {
            CaptureUndo();
            _overlays.RemoveAt(idx);
            _selectedOverlayId = _overlays.Count > 0 ? _overlays[^1].Id : null;
            UpdateOverlaysCount();
            UpdateOverlaysList();
            UpdateOverlayInspector();
            DrawOverlays();
        }
    }

    private void UpdateOverlaysCount()
    {
        if (OverlaysCountBadge is null || OverlaysCountText is null) return;
        var count = _overlays.Count;
        OverlaysCountText.Text = count.ToString();
        OverlaysCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverlaysCountBadge.Background = (_selectedAdjustmentTab == AdjustmentTab.Overlays)
            ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255))
            : (Brush)Application.Current.Resources["AccentBrush"];
    }

    private void UpdateOverlaysList()
    {
        if (OverlaysListPanel is null || NoOverlaysHint is null) return;
        NoOverlaysHint.Visibility = _overlays.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OverlaysListPanel.Children.Clear();

        foreach (var ovl in _overlays)
        {
            var isSelected = ovl.Id == _selectedOverlayId;
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 8, 6),
                Background = isSelected
                    ? (Brush)Application.Current.Resources["AccentBrush"]
                    : ThemeBrush(Color.FromArgb(120, 240, 243, 246), Color.FromArgb(120, 42, 42, 42)),
                BorderBrush = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : ThemeBrush(Color.FromArgb(80, 210, 215, 220), Color.FromArgb(80, 60, 60, 60)),
                BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                Tag = ovl.Id
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon
            {
                Glyph = ovl.Type == OverlayType.Text ? "\uE8D2" : "\uEB9F",
                FontSize = 13,
                Foreground = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : (Brush)Application.Current.Resources["AccentBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var title = ovl.Type == OverlayType.Text
                ? (string.IsNullOrWhiteSpace(ovl.Text) ? "Text caption" : ovl.Text.Replace('\n', ' '))
                : (Path.GetFileName(ovl.ImagePath ?? "Image overlay"));

            var label = new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : ThemeBrush(Color.FromArgb(255, 20, 20, 20), Color.FromArgb(255, 240, 240, 240)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var eyeBtn = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new FontIcon
                {
                    Glyph = ovl.IsVisible ? "\uE7B3" : "\uED1A",
                    FontSize = 11,
                    Foreground = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : ThemeBrush(Color.FromArgb(200, 100, 100, 100), Color.FromArgb(200, 180, 180, 180))
                }
            };
            ToolTipService.SetToolTip(eyeBtn, ovl.IsVisible ? "Hide on video" : "Show on video");
            var targetId = ovl.Id;
            eyeBtn.Click += (s, e) =>
            {
                var target = _overlays.FirstOrDefault(o => o.Id == targetId);
                if (target is not null)
                {
                    target.IsVisible = !target.IsVisible;
                    UpdateOverlaysList();
                    DrawOverlays();
                }
            };
            Grid.SetColumn(eyeBtn, 2);
            grid.Children.Add(eyeBtn);

            var delBtn = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new FontIcon
                {
                    Glyph = "\uE74D",
                    FontSize = 11,
                    Foreground = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : new SolidColorBrush(Color.FromArgb(220, 255, 59, 48))
                }
            };
            ToolTipService.SetToolTip(delBtn, "Delete overlay");
            delBtn.Click += (s, e) =>
            {
                var targetIdx = _overlays.FindIndex(o => o.Id == targetId);
                if (targetIdx >= 0)
                {
                    CaptureUndo();
                    _overlays.RemoveAt(targetIdx);
                    if (_selectedOverlayId == targetId)
                    {
                        _selectedOverlayId = _overlays.Count > 0 ? _overlays[^1].Id : null;
                    }
                    UpdateOverlaysCount();
                    UpdateOverlaysList();
                    UpdateOverlayInspector();
                    DrawOverlays();
                }
            };
            Grid.SetColumn(delBtn, 3);
            grid.Children.Add(delBtn);

            card.Child = grid;

            card.PointerPressed += (s, e) =>
            {
                _selectedOverlayId = targetId;
                UpdateOverlaysList();
                UpdateOverlayInspector();
                DrawOverlays();
            };

            OverlaysListPanel.Children.Add(card);
        }
    }

    private void UpdateOverlayInspector()
    {
        if (SelectedOverlayInspector is null) return;
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null)
        {
            SelectedOverlayInspector.Visibility = Visibility.Collapsed;
            return;
        }

        _updatingOverlayInspector = true;
        try
        {
            SelectedOverlayInspector.Visibility = Visibility.Visible;

            if (selected.Type == OverlayType.Text)
            {
                InspectorTitleText.Text = "Edit Text Overlay";
                TextSettingsPanel.Visibility = Visibility.Visible;
                ImageSettingsPanel.Visibility = Visibility.Collapsed;
                OverlayTextBox.Text = selected.Text;
                OverlayFontSizeSlider.Value = selected.FontSize;
                OverlayFontSizeText.Text = $"{(int)selected.FontSize} pt";
                OverlayHasBgCheckBox.IsChecked = selected.HasBackground;
                OverlayBgOpacityGroup.Visibility = selected.HasBackground ? Visibility.Visible : Visibility.Collapsed;
                OverlayBgOpacitySlider.Value = selected.BackgroundOpacity * 100;
                OverlayBgOpacityText.Text = $"{(int)(selected.BackgroundOpacity * 100)}%";
                UpdateColorPaletteSelection(selected.FontColorHex);
            }
            else
            {
                InspectorTitleText.Text = "Edit Image Overlay";
                TextSettingsPanel.Visibility = Visibility.Collapsed;
                ImageSettingsPanel.Visibility = Visibility.Visible;
                ImageFileNameText.Text = Path.GetFileName(selected.ImagePath ?? "No image");
                OverlayImageSizeSlider.Value = selected.ImageWidthPercent;
                OverlayImageSizeText.Text = $"{(int)selected.ImageWidthPercent}%";
            }

            OverlayOpacitySlider.Value = selected.Opacity * 100;
            OverlayOpacityText.Text = $"{(int)(selected.Opacity * 100)}%";

            OverlayEntireVideoCheckBox.IsChecked = selected.ShowEntireVideo;
            OverlayCustomTimingGroup.Visibility = selected.ShowEntireVideo ? Visibility.Collapsed : Visibility.Visible;
            OverlayStartTimeBox.Text = FormatTime(selected.StartSeconds);
            OverlayEndTimeBox.Text = FormatTime(selected.EndSeconds);
        }
        finally
        {
            _updatingOverlayInspector = false;
        }
    }

    private void OverlayText_TextChanged(object sender, TextChangedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null || selected.Type != OverlayType.Text) return;
        selected.Text = OverlayTextBox.Text;
        UpdateOverlaysList();
        DrawOverlays();
    }

    private void OverlayFontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;
        selected.FontSize = e.NewValue;
        if (OverlayFontSizeText is not null) OverlayFontSizeText.Text = $"{(int)e.NewValue} pt";
        DrawOverlays();
    }

    private void OverlayBg_Changed(object sender, RoutedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;
        selected.HasBackground = OverlayHasBgCheckBox.IsChecked ?? false;
        if (OverlayBgOpacityGroup is not null)
            OverlayBgOpacityGroup.Visibility = selected.HasBackground ? Visibility.Visible : Visibility.Collapsed;
        DrawOverlays();
    }

    private void OverlayBgOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;
        selected.BackgroundOpacity = e.NewValue / 100.0;
        if (OverlayBgOpacityText is not null) OverlayBgOpacityText.Text = $"{(int)e.NewValue}%";
        DrawOverlays();
    }

    private void OverlayImageSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingOverlayInspector || _overlayInteractionMode != OverlayInteractionMode.None) return;
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;
        selected.ImageWidthPercent = e.NewValue;
        if (OverlayImageSizeText is not null) OverlayImageSizeText.Text = $"{(int)e.NewValue}%";
        DrawOverlays();
    }

    private void OverlayOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;
        selected.Opacity = e.NewValue / 100.0;
        if (OverlayOpacityText is not null) OverlayOpacityText.Text = $"{(int)e.NewValue}%";
        DrawOverlays();
    }

    private void AlignButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;

        CaptureUndo();
        switch (tag)
        {
            case "TL": selected.NormalizedX = 0.05; selected.NormalizedY = 0.05; break;
            case "TC": selected.NormalizedX = 0.50; selected.NormalizedY = 0.05; break;
            case "TR": selected.NormalizedX = 0.95; selected.NormalizedY = 0.05; break;
            case "CL": selected.NormalizedX = 0.05; selected.NormalizedY = 0.50; break;
            case "CC": selected.NormalizedX = 0.50; selected.NormalizedY = 0.50; break;
            case "CR": selected.NormalizedX = 0.95; selected.NormalizedY = 0.50; break;
            case "BL": selected.NormalizedX = 0.05; selected.NormalizedY = 0.95; break;
            case "BC": selected.NormalizedX = 0.50; selected.NormalizedY = 0.95; break;
            case "BR": selected.NormalizedX = 0.95; selected.NormalizedY = 0.95; break;
        }

        DrawOverlays();
    }

    private void OverlayTiming_Changed(object sender, RoutedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;
        selected.ShowEntireVideo = OverlayEntireVideoCheckBox.IsChecked ?? true;
        if (OverlayCustomTimingGroup is not null)
            OverlayCustomTimingGroup.Visibility = selected.ShowEntireVideo ? Visibility.Collapsed : Visibility.Visible;
        DrawOverlays();
    }

    private void OverlayTimeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;
        if (double.TryParse(OverlayStartTimeBox.Text.Replace(":", ""), out var sVal))
        {
            selected.StartSeconds = ParseTimeInput(OverlayStartTimeBox.Text);
        }
        if (double.TryParse(OverlayEndTimeBox.Text.Replace(":", ""), out var eVal))
        {
            selected.EndSeconds = ParseTimeInput(OverlayEndTimeBox.Text);
        }
        DrawOverlays();
    }

    private static double ParseTimeInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Split(':');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var m) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var s))
        {
            return m * 60 + s;
        }
        if (double.TryParse(text, CultureInfo.InvariantCulture, out var sec))
        {
            return sec;
        }
        return 0;
    }

    private void SetOverlayStartToPlayhead_Click(object sender, RoutedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;
        selected.StartSeconds = _playheadSeconds;
        OverlayStartTimeBox.Text = FormatTime(selected.StartSeconds);
        DrawOverlays();
    }

    private void SetOverlayEndToPlayhead_Click(object sender, RoutedEventArgs e)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is null) return;
        selected.EndSeconds = Math.Max(selected.StartSeconds + 0.1, _playheadSeconds);
        OverlayEndTimeBox.Text = FormatTime(selected.EndSeconds);
        DrawOverlays();
    }

    private (double Width, double Height) MeasureOverlaySize(MediaOverlay overlay, Windows.Foundation.Rect frame)
    {
        if (overlay.Type == OverlayType.Image)
        {
            var w = Math.Max(20, frame.Width * (overlay.ImageWidthPercent / 100.0));
            var h = w;
            if (!string.IsNullOrEmpty(overlay.ImagePath) && _imageDimensions.TryGetValue(overlay.ImagePath, out var dims) && dims.Width > 0 && dims.Height > 0)
            {
                h = w * ((double)dims.Height / dims.Width);
            }
            return (w, h);
        }
        else
        {
            var tb = new TextBlock
            {
                Text = string.IsNullOrEmpty(overlay.Text) ? " " : overlay.Text,
                FontSize = Math.Max(11, overlay.FontSize * (frame.Height / 720.0)),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            var border = new Border
            {
                Padding = new Thickness(8, 4, 8, 4),
                Child = tb
            };
            border.Measure(new Windows.Foundation.Size(frame.Width, frame.Height));
            return (Math.Max(30, border.DesiredSize.Width), Math.Max(20, border.DesiredSize.Height));
        }
    }

    private void DrawOverlays()
    {
        if (OverlayCanvas is null) return;

        var isVideo = VideoExtensions.Contains(Path.GetExtension(_sourcePath ?? string.Empty));
        if (!isVideo || _overlays.Count == 0)
        {
            OverlayCanvas.Children.Clear();
            return;
        }

        var frame = GetVideoFrameBounds(ignoreCrop: false);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            OverlayCanvas.Children.Clear();
            return;
        }

        var isPlaying = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        var visibleOverlayIds = new HashSet<string>();

        foreach (var ovl in _overlays)
        {
            if (!ovl.IsVisible) continue;

            if (isPlaying && !ovl.ShowEntireVideo)
            {
                if (_playheadSeconds < ovl.StartSeconds || _playheadSeconds > ovl.EndSeconds)
                {
                    continue;
                }
            }

            visibleOverlayIds.Add(ovl.Id);
            var (elemW, elemH) = MeasureOverlaySize(ovl, frame);
            var posX = frame.Left + (frame.Width - elemW) * ovl.NormalizedX;
            var posY = frame.Top + (frame.Height - elemH) * ovl.NormalizedY;

            var elementTag = $"overlay_{ovl.Id}";
            var existingElement = OverlayCanvas.Children.OfType<FrameworkElement>().FirstOrDefault(c => (string?)c.Tag == elementTag);

            if (ovl.Type == OverlayType.Text)
            {
                Border border;
                TextBlock tb;
                if (existingElement is Border b && b.Child is TextBlock existingTb)
                {
                    border = b;
                    tb = existingTb;
                }
                else
                {
                    if (existingElement is not null) OverlayCanvas.Children.Remove(existingElement);
                    tb = new TextBlock
                    {
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    };
                    border = new Border
                    {
                        Tag = elementTag,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 4, 8, 4),
                        Child = tb,
                        IsHitTestVisible = false
                    };
                    OverlayCanvas.Children.Add(border);
                }

                var bg = ovl.HasBackground
                    ? new SolidColorBrush(ParseHexColor(ovl.BackgroundColorHex, ovl.BackgroundOpacity))
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                border.Background = bg;
                tb.Text = ovl.Text;
                tb.FontSize = Math.Max(11, ovl.FontSize * (frame.Height / 720.0));
                tb.Foreground = new SolidColorBrush(ParseHexColor(ovl.FontColorHex, ovl.Opacity));

                Canvas.SetLeft(border, posX);
                Canvas.SetTop(border, posY);
            }
            else
            {
                Image img;
                if (existingElement is Image existingImg)
                {
                    img = existingImg;
                }
                else
                {
                    if (existingElement is not null) OverlayCanvas.Children.Remove(existingElement);
                    img = new Image
                    {
                        Tag = elementTag,
                        Stretch = Stretch.Uniform,
                        IsHitTestVisible = false
                    };
                    OverlayCanvas.Children.Add(img);
                }

                var bmp = GetOrCreateOverlayBitmap(ovl.ImagePath);
                if (img.Source != bmp)
                {
                    img.Source = bmp;
                }
                img.Width = elemW;
                img.Height = elemH;
                img.Opacity = ovl.Opacity;

                Canvas.SetLeft(img, posX);
                Canvas.SetTop(img, posY);
            }
        }

        // Remove elements belonging to overlays that are no longer visible or deleted
        for (int i = OverlayCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (OverlayCanvas.Children[i] is FrameworkElement fe && fe.Tag is string tag && tag.StartsWith("overlay_") && !tag.StartsWith("overlay_sel") && !tag.StartsWith("overlay_handle"))
            {
                var id = tag["overlay_".Length..];
                if (!visibleOverlayIds.Contains(id))
                {
                    OverlayCanvas.Children.RemoveAt(i);
                }
            }
        }

        UpdateSelectionAdornments(frame, visibleOverlayIds);
    }

    private void UpdateSelectionAdornments(Windows.Foundation.Rect frame, HashSet<string> visibleOverlayIds)
    {
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        bool shouldShowSelection = selected is not null && selected.IsVisible && visibleOverlayIds.Contains(selected.Id);

        const string selBorderTag = "overlay_selection_border";
        const string hTLTag = "overlay_handle_TL";
        const string hTRTag = "overlay_handle_TR";
        const string hBLTag = "overlay_handle_BL";
        const string hBRTag = "overlay_handle_BR";

        var selBorder = OverlayCanvas.Children.OfType<Border>().FirstOrDefault(b => (string?)b.Tag == selBorderTag);
        var hTL = OverlayCanvas.Children.OfType<Border>().FirstOrDefault(b => (string?)b.Tag == hTLTag);
        var hTR = OverlayCanvas.Children.OfType<Border>().FirstOrDefault(b => (string?)b.Tag == hTRTag);
        var hBL = OverlayCanvas.Children.OfType<Border>().FirstOrDefault(b => (string?)b.Tag == hBLTag);
        var hBR = OverlayCanvas.Children.OfType<Border>().FirstOrDefault(b => (string?)b.Tag == hBRTag);

        const string snapVLineTag = "overlay_snap_guide_v";
        const string snapVBadgeTag = "overlay_snap_badge_v";
        const string snapHLineTag = "overlay_snap_guide_h";
        const string snapHBadgeTag = "overlay_snap_badge_h";

        var vLine = OverlayCanvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Line>().FirstOrDefault(l => (string?)l.Tag == snapVLineTag);
        var vBadge = OverlayCanvas.Children.OfType<Border>().FirstOrDefault(b => (string?)b.Tag == snapVBadgeTag);
        var hLine = OverlayCanvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Line>().FirstOrDefault(l => (string?)l.Tag == snapHLineTag);
        var hBadge = OverlayCanvas.Children.OfType<Border>().FirstOrDefault(b => (string?)b.Tag == snapHBadgeTag);

        if (!shouldShowSelection || selected is null)
        {
            if (selBorder is not null) OverlayCanvas.Children.Remove(selBorder);
            if (hTL is not null) OverlayCanvas.Children.Remove(hTL);
            if (hTR is not null) OverlayCanvas.Children.Remove(hTR);
            if (hBL is not null) OverlayCanvas.Children.Remove(hBL);
            if (hBR is not null) OverlayCanvas.Children.Remove(hBR);
            if (vLine is not null) OverlayCanvas.Children.Remove(vLine);
            if (vBadge is not null) OverlayCanvas.Children.Remove(vBadge);
            if (hLine is not null) OverlayCanvas.Children.Remove(hLine);
            if (hBadge is not null) OverlayCanvas.Children.Remove(hBadge);
            return;
        }

        var (elemW, elemH) = MeasureOverlaySize(selected, frame);
        var posX = frame.Left + (frame.Width - elemW) * selected.NormalizedX;
        var posY = frame.Top + (frame.Height - elemH) * selected.NormalizedY;

        if (selBorder is null)
        {
            selBorder = new Border
            {
                Tag = selBorderTag,
                BorderBrush = (Brush)Application.Current.Resources["AccentBrush"],
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false
            };
            OverlayCanvas.Children.Add(selBorder);
        }
        selBorder.Width = elemW + 6;
        selBorder.Height = elemH + 6;
        Canvas.SetLeft(selBorder, posX - 3);
        Canvas.SetTop(selBorder, posY - 3);

        if (selected.Type == OverlayType.Image)
        {
            hTL ??= CreateResizeHandle(hTLTag);
            hTR ??= CreateResizeHandle(hTRTag);
            hBL ??= CreateResizeHandle(hBLTag);
            hBR ??= CreateResizeHandle(hBRTag);

            const double hSize = 10;
            Canvas.SetLeft(hTL, posX - hSize / 2.0);
            Canvas.SetTop(hTL, posY - hSize / 2.0);

            Canvas.SetLeft(hTR, posX + elemW - hSize / 2.0);
            Canvas.SetTop(hTR, posY - hSize / 2.0);

            Canvas.SetLeft(hBL, posX - hSize / 2.0);
            Canvas.SetTop(hBL, posY + elemH - hSize / 2.0);

            Canvas.SetLeft(hBR, posX + elemW - hSize / 2.0);
            Canvas.SetTop(hBR, posY + elemH - hSize / 2.0);
        }
        else
        {
            if (hTL is not null) OverlayCanvas.Children.Remove(hTL);
            if (hTR is not null) OverlayCanvas.Children.Remove(hTR);
            if (hBL is not null) OverlayCanvas.Children.Remove(hBL);
            if (hBR is not null) OverlayCanvas.Children.Remove(hBR);
        }

        // Render Snap Guidelines
        if (_overlaySnapVerticalX is double snapX && _overlayInteractionMode != OverlayInteractionMode.None)
        {
            if (vLine is null)
            {
                vLine = new Microsoft.UI.Xaml.Shapes.Line
                {
                    Tag = snapVLineTag,
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0)),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                };
                OverlayCanvas.Children.Add(vLine);
            }
            vLine.X1 = snapX;
            vLine.Y1 = frame.Top;
            vLine.X2 = snapX;
            vLine.Y2 = frame.Bottom;

            bool isCenter = Math.Abs(snapX - (frame.X + frame.Width / 2.0)) < 1.0;
            var badgeText = isCenter ? "Center X" : (snapX <= frame.Left + 1 ? "Left" : "Right");

            if (vBadge is null)
            {
                vBadge = new Border
                {
                    Tag = snapVBadgeTag,
                    Background = new SolidColorBrush(Color.FromArgb(230, 24, 24, 24)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        FontSize = 10,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0))
                    }
                };
                OverlayCanvas.Children.Add(vBadge);
            }
            if (vBadge.Child is TextBlock tbV) tbV.Text = badgeText;
            Canvas.SetLeft(vBadge, Math.Clamp(snapX + 4, 0, Math.Max(0, OverlayCanvas.ActualWidth - 65)));
            Canvas.SetTop(vBadge, Math.Max(frame.Top + 6, 6));
        }
        else
        {
            if (vLine is not null) OverlayCanvas.Children.Remove(vLine);
            if (vBadge is not null) OverlayCanvas.Children.Remove(vBadge);
        }

        if (_overlaySnapHorizontalY is double snapY && _overlayInteractionMode != OverlayInteractionMode.None)
        {
            if (hLine is null)
            {
                hLine = new Microsoft.UI.Xaml.Shapes.Line
                {
                    Tag = snapHLineTag,
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0)),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                };
                OverlayCanvas.Children.Add(hLine);
            }
            hLine.X1 = frame.Left;
            hLine.Y1 = snapY;
            hLine.X2 = frame.Right;
            hLine.Y2 = snapY;

            bool isCenter = Math.Abs(snapY - (frame.Y + frame.Height / 2.0)) < 1.0;
            var badgeText = isCenter ? "Center Y" : (snapY <= frame.Top + 1 ? "Top" : "Bottom");

            if (hBadge is null)
            {
                hBadge = new Border
                {
                    Tag = snapHBadgeTag,
                    Background = new SolidColorBrush(Color.FromArgb(230, 24, 24, 24)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        FontSize = 10,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0))
                    }
                };
                OverlayCanvas.Children.Add(hBadge);
            }
            if (hBadge.Child is TextBlock tbH) tbH.Text = badgeText;
            Canvas.SetLeft(hBadge, Math.Max(frame.Left + 6, 6));
            Canvas.SetTop(hBadge, Math.Clamp(snapY + 4, 0, Math.Max(0, OverlayCanvas.ActualHeight - 25)));
        }
        else
        {
            if (hLine is not null) OverlayCanvas.Children.Remove(hLine);
            if (hBadge is not null) OverlayCanvas.Children.Remove(hBadge);
        }
    }

    private Border CreateResizeHandle(string tag)
    {
        const double hSize = 10;
        var handle = new Border
        {
            Tag = tag,
            Width = hSize,
            Height = hSize,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = (Brush)Application.Current.Resources["AccentBrush"],
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false
        };
        OverlayCanvas.Children.Add(handle);
        return handle;
    }

    private void OverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(OverlayCanvas).Position;
        var frame = GetVideoFrameBounds(ignoreCrop: false);
        if (frame.Width <= 0 || frame.Height <= 0) return;

        // Check if a resize handle was clicked on the currently selected image overlay
        var selected = _overlays.FirstOrDefault(o => o.Id == _selectedOverlayId);
        if (selected is not null && selected.Type == OverlayType.Image && selected.IsVisible)
        {
            var (w, h) = MeasureOverlaySize(selected, frame);
            var x = frame.Left + (frame.Width - w) * selected.NormalizedX;
            var y = frame.Top + (frame.Height - h) * selected.NormalizedY;

            const double hitR = 12.0;
            bool Near(double hx, double hy) => Math.Abs(point.X - hx) <= hitR && Math.Abs(point.Y - hy) <= hitR;

            if (Near(x + w, y + h))
            {
                StartOverlayInteraction(selected, OverlayInteractionMode.ResizeBottomRight, point, w, h, x, y, e.Pointer);
                e.Handled = true;
                return;
            }
            if (Near(x, y + h))
            {
                StartOverlayInteraction(selected, OverlayInteractionMode.ResizeBottomLeft, point, w, h, x, y, e.Pointer);
                e.Handled = true;
                return;
            }
            if (Near(x + w, y))
            {
                StartOverlayInteraction(selected, OverlayInteractionMode.ResizeTopRight, point, w, h, x, y, e.Pointer);
                e.Handled = true;
                return;
            }
            if (Near(x, y))
            {
                StartOverlayInteraction(selected, OverlayInteractionMode.ResizeTopLeft, point, w, h, x, y, e.Pointer);
                e.Handled = true;
                return;
            }
        }

        // Check if any overlay was clicked to move/select
        MediaOverlay? hitOverlay = null;
        double hitElemW = 0, hitElemH = 0, hitPosX = 0, hitPosY = 0;

        for (var i = _overlays.Count - 1; i >= 0; i--)
        {
            var ovl = _overlays[i];
            if (!ovl.IsVisible) continue;
            var (w, h) = MeasureOverlaySize(ovl, frame);
            var x = frame.Left + (frame.Width - w) * ovl.NormalizedX;
            var y = frame.Top + (frame.Height - h) * ovl.NormalizedY;

            if (point.X >= x - 4 && point.X <= x + w + 4 && point.Y >= y - 4 && point.Y <= y + h + 4)
            {
                hitOverlay = ovl;
                hitElemW = w;
                hitElemH = h;
                hitPosX = x;
                hitPosY = y;
                break;
            }
        }

        if (hitOverlay is not null)
        {
            StartOverlayInteraction(hitOverlay, OverlayInteractionMode.Moving, point, hitElemW, hitElemH, hitPosX, hitPosY, e.Pointer);
            e.Handled = true;
        }
        else
        {
            _selectedOverlayId = null;
            _draggingOverlay = null;
            _overlayInteractionMode = OverlayInteractionMode.None;
            UpdateOverlaysList();
            UpdateOverlayInspector();
            DrawOverlays();
        }
    }

    private void StartOverlayInteraction(MediaOverlay overlay, OverlayInteractionMode mode, Windows.Foundation.Point point, double elemW, double elemH, double posX, double posY, Pointer pointer)
    {
        _selectedOverlayId = overlay.Id;
        _draggingOverlay = overlay;
        _overlayInteractionMode = mode;
        _overlayDragStartPoint = point;
        _overlayStartWidth = elemW;
        _overlayStartHeight = elemH;
        _overlayStartPosX = posX;
        _overlayStartPosY = posY;
        _overlayStartNormX = overlay.NormalizedX;
        _overlayStartNormY = overlay.NormalizedY;
        _draggingElementWidth = elemW;
        _draggingElementHeight = elemH;

        OverlayCanvas.CapturePointer(pointer);
        CaptureUndo();
        SelectAdjustmentTab(AdjustmentTab.Overlays);
        UpdateOverlaysList();
        UpdateOverlayInspector();
        DrawOverlays();
    }

    private void OverlayCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(OverlayCanvas).Position;
        var frame = GetVideoFrameBounds(ignoreCrop: false);
        if (frame.Width <= 0 || frame.Height <= 0) return;

        if (_draggingOverlay is not null && _overlayInteractionMode != OverlayInteractionMode.None)
        {
            const double snapThreshold = 9.0;
            _overlaySnapVerticalX = null;
            _overlaySnapHorizontalY = null;

            if (_overlayInteractionMode == OverlayInteractionMode.Moving)
            {
                var deltaX = point.X - _overlayDragStartPoint.X;
                var deltaY = point.Y - _overlayDragStartPoint.Y;

                var spanX = Math.Max(1, frame.Width - _draggingElementWidth);
                var spanY = Math.Max(1, frame.Height - _draggingElementHeight);

                var rawPosX = _overlayStartPosX + deltaX;
                var rawPosY = _overlayStartPosY + deltaY;

                var clampedPosX = Math.Clamp(rawPosX, frame.Left, frame.Right - _draggingElementWidth);
                var clampedPosY = Math.Clamp(rawPosY, frame.Top, frame.Bottom - _draggingElementHeight);

                var centerX = clampedPosX + _draggingElementWidth / 2.0;
                var frameCenterX = frame.X + frame.Width / 2.0;

                // Vertical axis snap: center, left edge, right edge
                if (Math.Abs(centerX - frameCenterX) <= snapThreshold)
                {
                    clampedPosX = frameCenterX - _draggingElementWidth / 2.0;
                    _overlaySnapVerticalX = frameCenterX;
                }
                else if (Math.Abs(clampedPosX - frame.Left) <= snapThreshold)
                {
                    clampedPosX = frame.Left;
                    _overlaySnapVerticalX = frame.Left;
                }
                else if (Math.Abs(clampedPosX + _draggingElementWidth - frame.Right) <= snapThreshold)
                {
                    clampedPosX = frame.Right - _draggingElementWidth;
                    _overlaySnapVerticalX = frame.Right;
                }

                var centerY = clampedPosY + _draggingElementHeight / 2.0;
                var frameCenterY = frame.Y + frame.Height / 2.0;

                // Horizontal axis snap: center, top edge, bottom edge
                if (Math.Abs(centerY - frameCenterY) <= snapThreshold)
                {
                    clampedPosY = frameCenterY - _draggingElementHeight / 2.0;
                    _overlaySnapHorizontalY = frameCenterY;
                }
                else if (Math.Abs(clampedPosY - frame.Top) <= snapThreshold)
                {
                    clampedPosY = frame.Top;
                    _overlaySnapHorizontalY = frame.Top;
                }
                else if (Math.Abs(clampedPosY + _draggingElementHeight - frame.Bottom) <= snapThreshold)
                {
                    clampedPosY = frame.Bottom - _draggingElementHeight;
                    _overlaySnapHorizontalY = frame.Bottom;
                }

                _draggingOverlay.NormalizedX = Math.Clamp((clampedPosX - frame.Left) / spanX, 0, 1);
                _draggingOverlay.NormalizedY = Math.Clamp((clampedPosY - frame.Top) / spanY, 0, 1);
            }
            else
            {
                double aspect = 1.0;
                if (!string.IsNullOrEmpty(_draggingOverlay.ImagePath) &&
                    _imageDimensions.TryGetValue(_draggingOverlay.ImagePath, out var dims) &&
                    dims.Width > 0 && dims.Height > 0)
                {
                    aspect = (double)dims.Height / dims.Width;
                }

                double minW = Math.Max(20, frame.Width * 0.05);
                double maxW = frame.Width * 0.80;

                double newW = _overlayStartWidth;
                double newPosX = _overlayStartPosX;
                double newPosY = _overlayStartPosY;

                switch (_overlayInteractionMode)
                {
                    case OverlayInteractionMode.ResizeBottomRight:
                    {
                        var desiredW = point.X - _overlayStartPosX;
                        var maxByRight = frame.Right - _overlayStartPosX;
                        var maxByBottom = (frame.Bottom - _overlayStartPosY) / aspect;
                        var effectiveMax = Math.Min(maxW, Math.Min(maxByRight, maxByBottom));
                        newW = Math.Clamp(desiredW, minW, Math.Max(minW, effectiveMax));
                        newPosX = _overlayStartPosX;
                        newPosY = _overlayStartPosY;
                        break;
                    }
                    case OverlayInteractionMode.ResizeBottomLeft:
                    {
                        var anchorRight = _overlayStartPosX + _overlayStartWidth;
                        var desiredW = anchorRight - point.X;
                        var maxByLeft = anchorRight - frame.Left;
                        var maxByBottom = (frame.Bottom - _overlayStartPosY) / aspect;
                        var effectiveMax = Math.Min(maxW, Math.Min(maxByLeft, maxByBottom));
                        newW = Math.Clamp(desiredW, minW, Math.Max(minW, effectiveMax));
                        newPosX = anchorRight - newW;
                        newPosY = _overlayStartPosY;
                        break;
                    }
                    case OverlayInteractionMode.ResizeTopRight:
                    {
                        var anchorBottom = _overlayStartPosY + _overlayStartHeight;
                        var desiredW = point.X - _overlayStartPosX;
                        var maxByRight = frame.Right - _overlayStartPosX;
                        var maxByTop = (anchorBottom - frame.Top) / aspect;
                        var effectiveMax = Math.Min(maxW, Math.Min(maxByRight, maxByTop));
                        newW = Math.Clamp(desiredW, minW, Math.Max(minW, effectiveMax));
                        newPosX = _overlayStartPosX;
                        newPosY = anchorBottom - newW * aspect;
                        break;
                    }
                    case OverlayInteractionMode.ResizeTopLeft:
                    {
                        var anchorRight = _overlayStartPosX + _overlayStartWidth;
                        var anchorBottom = _overlayStartPosY + _overlayStartHeight;
                        var desiredW = anchorRight - point.X;
                        var maxByLeft = anchorRight - frame.Left;
                        var maxByTop = (anchorBottom - frame.Top) / aspect;
                        var effectiveMax = Math.Min(maxW, Math.Min(maxByLeft, maxByTop));
                        newW = Math.Clamp(desiredW, minW, Math.Max(minW, effectiveMax));
                        newPosX = anchorRight - newW;
                        newPosY = anchorBottom - newW * aspect;
                        break;
                    }
                }

                newW = Math.Clamp(newW, minW, maxW);
                double newH = newW * aspect;

                // Snapping during resize to frame center or edges
                var curCenterX = newPosX + newW / 2.0;
                var frameCenterX = frame.X + frame.Width / 2.0;
                var curCenterY = newPosY + newH / 2.0;
                var frameCenterY = frame.Y + frame.Height / 2.0;

                if (Math.Abs(newPosX - frame.Left) <= snapThreshold)
                {
                    _overlaySnapVerticalX = frame.Left;
                }
                else if (Math.Abs(newPosX + newW - frame.Right) <= snapThreshold)
                {
                    _overlaySnapVerticalX = frame.Right;
                }
                else if (Math.Abs(curCenterX - frameCenterX) <= snapThreshold)
                {
                    _overlaySnapVerticalX = frameCenterX;
                }

                if (Math.Abs(newPosY - frame.Top) <= snapThreshold)
                {
                    _overlaySnapHorizontalY = frame.Top;
                }
                else if (Math.Abs(newPosY + newH - frame.Bottom) <= snapThreshold)
                {
                    _overlaySnapHorizontalY = frame.Bottom;
                }
                else if (Math.Abs(curCenterY - frameCenterY) <= snapThreshold)
                {
                    _overlaySnapHorizontalY = frameCenterY;
                }

                var spanX = Math.Max(1, frame.Width - newW);
                var spanY = Math.Max(1, frame.Height - newH);

                _draggingOverlay.ImageWidthPercent = Math.Clamp((newW / frame.Width) * 100.0, 5.0, 80.0);
                _draggingOverlay.NormalizedX = Math.Clamp((newPosX - frame.Left) / spanX, 0.0, 1.0);
                _draggingOverlay.NormalizedY = Math.Clamp((newPosY - frame.Top) / spanY, 0.0, 1.0);

                if (OverlayImageSizeSlider is not null)
                {
                    OverlayImageSizeSlider.Value = _draggingOverlay.ImageWidthPercent;
                    if (OverlayImageSizeText is not null)
                    {
                        OverlayImageSizeText.Text = $"{(int)_draggingOverlay.ImageWidthPercent}%";
                    }
                }
            }

            DrawOverlays();
            e.Handled = true;
            return;
        }
    }

    private void OverlayCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingOverlay = null;
        _overlayInteractionMode = OverlayInteractionMode.None;
        _overlaySnapVerticalX = null;
        _overlaySnapHorizontalY = null;
        try { OverlayCanvas.ReleasePointerCaptures(); } catch { }
        DrawOverlays();
    }

    private void OverlayCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _draggingOverlay = null;
        _overlayInteractionMode = OverlayInteractionMode.None;
        _overlaySnapVerticalX = null;
        _overlaySnapHorizontalY = null;
        try { OverlayCanvas.ReleasePointerCaptures(); } catch { }
        DrawOverlays();
    }

    private void OverlayCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _draggingOverlay = null;
        _overlayInteractionMode = OverlayInteractionMode.None;
        _overlaySnapVerticalX = null;
        _overlaySnapHorizontalY = null;
        DrawOverlays();
    }

    private void OverlayCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
    }

    private static Windows.UI.Color ParseHexColor(string hex, double opacity = 1.0)
    {
        hex = hex.TrimStart('#');
        byte a = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
        byte r = 255, g = 255, b = 255;
        try
        {
            if (hex.Length == 6)
            {
                r = Convert.ToByte(hex[..2], 16);
                g = Convert.ToByte(hex.Substring(2, 2), 16);
                b = Convert.ToByte(hex.Substring(4, 2), 16);
            }
            else if (hex.Length == 8)
            {
                a = (byte)Math.Clamp((int)Math.Round(Convert.ToByte(hex[..2], 16) * opacity), 0, 255);
                r = Convert.ToByte(hex.Substring(2, 2), 16);
                g = Convert.ToByte(hex.Substring(4, 2), 16);
                b = Convert.ToByte(hex.Substring(6, 2), 16);
            }
        }
        catch
        {
        }
        return Windows.UI.Color.FromArgb(a, r, g, b);
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
        SelectCropRatio("free", applyToViewport: false);
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

        var keptSegments = GetKeptSegments();
        var keptDuration = keptSegments.Sum(s => Math.Max(0, s.End - s.Start));
        var totalCutDuration = (end - start) - keptDuration;

        if (_cutRegions.Count > 0 && totalCutDuration > 0.05)
        {
            var cutWord = _cutRegions.Count == 1 ? "1 cut" : $"{_cutRegions.Count} cuts";
            DurationText.Text = $"{FormatTime(keptDuration)} selected ({cutWord}: -{FormatTime(totalCutDuration)}) / {FormatTime(_durationSeconds)}";
        }
        else
        {
            DurationText.Text = $"{FormatTime(end - start)} selected / {FormatTime(_durationSeconds)}";
        }
    }

    private void UpdateCurrentPosition()
    {
        if (_sourcePath is not null && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            var playbackEnd = _showingEditedPreview
                ? _previewPlaybackDuration
                : _trimEndSeconds;
            var currentPos = _player.PlaybackSession.Position.TotalSeconds;

            if (currentPos >= playbackEnd)
            {
                _player.Pause();
                _audioEngine?.Pause();
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(playbackEnd);
                _audioEngine?.Seek(TimeSpan.FromSeconds(playbackEnd));
            }
            else if (!_showingEditedPreview && _cutRegions.Count > 0)
            {
                // YouTube Studio style cut skipping during live playback
                var activeCut = _cutRegions.FirstOrDefault(c => currentPos >= c.Start && currentPos < c.End - 0.04);
                if (activeCut is not null)
                {
                    var target = activeCut.End;
                    if (target >= _trimEndSeconds)
                    {
                        _player.Pause();
                        _audioEngine?.Pause();
                        _player.PlaybackSession.Position = TimeSpan.FromSeconds(_trimEndSeconds);
                        _audioEngine?.Seek(TimeSpan.FromSeconds(_trimEndSeconds));
                    }
                    else
                    {
                        _player.PlaybackSession.Position = TimeSpan.FromSeconds(target);
                        _audioEngine?.Seek(TimeSpan.FromSeconds(target));
                    }
                }
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
            EnsurePlayheadVisible(smooth: true);
            if (_overlays.Any(o => !o.ShowEntireVideo))
            {
                DrawOverlays();
            }
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