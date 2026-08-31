using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FrameFlow.Camera;
using FrameFlow.Face;
using FrameFlow.Graph;
using FrameFlow.Inference.Dml;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Video;
using FrameFlow.Yolo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery;
using Periphery.Camera;

namespace FrameFlow.Examples.Camera.Inference.Dml;

/// <summary>
/// Dead-simple single-pane camera inference demo. One live
/// <see cref="CameraSession"/> frame flows through a BGRA32 convert into a
/// single <see cref="ObjectDetectionPreview"/> pane that runs YOLOv8 on the
/// DirectML EP and overlays detection boxes — the post-inference image
/// output. The model is selectable with <c>--model &lt;path.onnx&gt;</c>;
/// the detector is shape-aware (ADR-0050) so any minted variant (640/416/320,
/// FP16, person-only) loads without code changes. Built to A/B models on a
/// low-power Intel iGPU.
/// </summary>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _windowCts = new();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private ILogger<MainWindow> _logger = NullLogger<MainWindow>.Instance;
    private DeviceSessionHost<CameraSession>? _host;
    private DeviceInfo? _selectedDevice;
    private Yolov8Detector? _yoloDetector;
    private BlazeFaceDetector? _faceDetector;
    // The pane the camera graph + stats drive. YOLO object pane by default;
    // the BlazeFace pane when --face is passed.
    private IInferencePreview? _activePane;

    private DispatcherTimer? _statsTimer;
    private long _lastRendered;
    private long _frameCount;
    private long _lastFrameCount;
    private DateTime _lastStatsAt;
    private string _modelLabel = "(none)";
    private string _cameraLabel = "(none)";

    public ObservableCollection<DeviceInfo> Cameras { get; } = new();

    /// <summary>Optional ONNX model path (<c>--model</c>); null downloads stock yolov8n.</summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Optional BlazeFace ONNX path (<c>--face</c>). When set, the example
    /// runs face detection (box + 6 keypoints) instead of YOLO; takes
    /// precedence over <see cref="ModelPath"/>.
    /// </summary>
    public string? FaceModelPath { get; set; }

    public string? StartupLogFilePath { get; set; }

    /// <summary>
    /// Which enumerated camera to auto-select (<c>--camera &lt;idx&gt;</c>,
    /// default 0). The first camera is connected automatically on startup;
    /// the picker still lets you switch.
    /// </summary>
    public int CameraIndex { get; set; }

    public double ExitAfterSeconds { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        DevicePicker.ItemsSource = Cameras;
        DevicePicker.SelectionChanged += OnDeviceSelected;
        Closing += OnClosing;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            if (!string.IsNullOrEmpty(StartupLogFilePath))
                b.AddProvider(new FileLoggerProvider(ExampleLogPaths.Resolve(StartupLogFilePath), LogLevel.Debug));
        });
        _logger = _loggerFactory.CreateLogger<MainWindow>();
        var faceMode = !string.IsNullOrEmpty(FaceModelPath);
        _modelLabel = faceMode
            ? $"blazeface ({Path.GetFileName(FaceModelPath)})"
            : ModelPath is null ? "yolov8n (auto-download)" : Path.GetFileName(ModelPath);
        _logger.LogInformation(
            "Camera inference ready. mode={Mode} model={Model} logFile={LogFile}",
            faceMode ? "face" : "object",
            (faceMode ? FaceModelPath : ModelPath) ?? "(stock yolov8n)",
            StartupLogFilePath ?? "(none)");

        // FFmpeg bootstrap for the BGRA32 ConvertPixelFormat (libswscale)
        // stage. This example never goes through MediaPlayer.CreateAsync, so
        // the DllImportResolver that maps "swscale" → "swscale-8.dll" must be
        // registered explicitly or the first frame throws DllNotFoundException.
        // Skip the HW-decode probe — cameras don't decode.
        var ffmpeg = new FrameFlowBootstrapper(
            new FrameFlowNativeOptions { SkipHardwareProbe = true },
            _loggerFactory).Initialize();
        if (!ffmpeg.IsSuccess)
        {
            _logger.LogError("FFmpeg bootstrap failed: {Message}", ffmpeg.Message);
            StatusText.Text = $"FFmpeg bootstrap failed: {ffmpeg.Message}";
            return;
        }

        _statsTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnStatsTick);
        _statsTimer.Start();
        _lastStatsAt = DateTime.UtcNow;

        if (faceMode)
        {
            DetectionPane.IsVisible = false;
            FacePane.IsVisible = true;
            _activePane = FacePane;
            await BootstrapFaceAsync().ConfigureAwait(true);
        }
        else
        {
            _activePane = DetectionPane;
            await BootstrapYoloAsync().ConfigureAwait(true);
        }

        // RefreshDevicesAsync auto-selects the first camera (CameraIndex)
        // when nothing is connected yet — no UI click needed.
        await RefreshDevicesAsync().ConfigureAwait(true);

        if (ExitAfterSeconds > 0)
        {
            var exitTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(ExitAfterSeconds), DispatcherPriority.Background,
                (_, _) => { _logger.LogInformation("--exit-after: closing."); Close(); });
            exitTimer.Start();
        }
    }

    private async Task BootstrapYoloAsync()
    {
        try
        {
            _yoloDetector = await Yolov8Detector.CreateAsync(
                sessionFactory: path => new DmlInferenceSession(path),
                overrideModelPath: ModelPath,
                ct: _windowCts.Token,
                loggerFactory: _loggerFactory).ConfigureAwait(true);
            DetectionPane.SetDetector(_yoloDetector);
            _logger.LogInformation("YOLOv8 detector ready (model={Model}).", _modelLabel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "YOLOv8 bootstrap failed for model {Model}.", _modelLabel);
            DetectionPane.SetUnavailable($"{ex.GetType().Name}");
            StatusText.Text = $"Model load failed ({_modelLabel}): {ex.Message}";
        }
    }

    private async Task BootstrapFaceAsync()
    {
        try
        {
            // BlazeFace ships no downloader (ADR-0051) — FaceModelPath must
            // point at a supplied ONNX. Session construction + warmup run off
            // the UI thread (DML PSO compile is ~100-300 ms).
            _faceDetector = await Task.Run(
                () => BlazeFaceDetector.Create(
                    new DmlInferenceSession(FaceModelPath!),
                    _loggerFactory)).ConfigureAwait(true);
            FacePane.SetDetector(_faceDetector);
            _logger.LogInformation("BlazeFace detector ready (model={Model}).", _modelLabel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BlazeFace bootstrap failed for model {Model}.", _modelLabel);
            FacePane.SetUnavailable($"{ex.GetType().Name}");
            StatusText.Text = $"Face model load failed ({_modelLabel}): {ex.Message}";
        }
    }

    // ── Device discovery + connect ────────────────────────────────────

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var devices = await CameraDevice.EnumerateAsync(_windowCts.Token).ConfigureAwait(true);
            Cameras.Clear();
            foreach (var d in devices) Cameras.Add(d);
            _logger.LogInformation("Enumerated {Count} camera(s).", devices.Count);
            if (devices.Count == 0)
            {
                StatusText.Text = "No cameras detected. Plug one in and click Refresh.";
                return;
            }

            // Auto-select the first camera (or CameraIndex) when nothing is
            // connected yet — the dead-simple default. Setting SelectedIndex
            // fires OnDeviceSelected, which connects. Re-selecting in the
            // picker switches cameras manually.
            if (DevicePicker.SelectedItem is null)
            {
                var idx = Math.Clamp(CameraIndex, 0, Cameras.Count - 1);
                _logger.LogInformation(
                    "Auto-selecting camera [{Idx}] {Name}.", idx, Cameras[idx].Name ?? "(unnamed)");
                DevicePicker.SelectedIndex = idx;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera enumeration failed.");
            StatusText.Text = $"Enumeration failed: {ex.Message}";
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) =>
        await RefreshDevicesAsync().ConfigureAwait(true);

    private async void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not DeviceInfo device) return;
        if (ReferenceEquals(device, _selectedDevice)) return;
        _selectedDevice = device;
        await ConnectAsync(device).ConfigureAwait(true);
    }

    private async Task ConnectAsync(DeviceInfo device)
    {
        await DisconnectAsync().ConfigureAwait(true);
        _cameraLabel = device.Name ?? "(unnamed)";
        StatusText.Text = $"Connecting to {_cameraLabel}…";
        try
        {
            _host = await DeviceSessionHost<CameraSession>.ForDeviceAsync(
                device,
                createSession: CreateSessionAsync,
                onSessionEnded: _ => Task.CompletedTask,
                whileSessionActive: RunGraphAsync,
                ct: _windowCts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera host failed to start.");
            StatusText.Text = $"Failed to start: {ex.Message}";
        }
    }

    private async Task DisconnectAsync()
    {
        if (_host is null) return;
        var host = _host;
        _host = null;
        try { await host.DisposeAsync().ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Host teardown threw."); }
    }

    private Task<CameraSession> CreateSessionAsync(DeviceInfo device, CancellationToken ct) =>
        CameraSession.For(device)
            .PreferPixelFormat(CameraPixelFormat.Bgra32)
            .AllowOnlyPixelFormats(
                CameraPixelFormat.Bgra32,
                CameraPixelFormat.Rgba32,
                CameraPixelFormat.Nv12,
                CameraPixelFormat.Yuy2,
                CameraPixelFormat.Uyvy)
            .MaxResolution(1280, 720)
            .WithLogger(_loggerFactory.CreateLogger<CameraSession>())
            .OpenAsync(ct);

    private async Task RunGraphAsync(CameraSession session, CancellationToken ct)
    {
        Interlocked.Exchange(ref _frameCount, 0);

        // Push-style source (library): a background pump drains
        // session.CaptureAsync into a capacity-1 DropOldest bridge (LatestOnly —
        // a slow inference never sees stale frames), exposed as a graph source.
        await using var camSource = session.AsPushVideoFrameSource(ct, _loggerFactory);

        // source → convert(BGRA32) → count → single detection-overlay sink.
        var graph = new FrameFlow.Graph.Graph();
        var source = camSource.Source;
        var convert = VideoOperators.ConvertPixelFormat("camera-convert", PixelFormat.Bgra32);
        var counter = new OperatorNode<VideoFrameRef, VideoFrameRef>(
            "count",
            (item, _) =>
            {
                Interlocked.Increment(ref _frameCount);
                return ValueTask.FromResult<VideoFrameRef?>(item);
            });
        var sink = new SinkNode<VideoFrameRef>(
            "detect-sink",
            async (item, ct2) =>
            {
                // CloneCpu so the pane owns/disposes its frame independently
                // of the graph's VideoFrameRef. PresentAsync returns
                // immediately (queue-of-one), so this sink never blocks on
                // inference — slow models drop frames, they don't stall.
                var pane = _activePane;
                if (pane is null)
                    return;
                var clone = item.Frame.CloneCpu();
                await pane.PresentAsync(clone, ct2).ConfigureAwait(false);
            });

        graph
            .Connect(source.Output, convert.Input)
            .Connect(convert.Output, counter.Input)
            .Connect(counter.Output, sink.Input);

        Dispatcher.UIThread.Post(() => StatusText.Text = $"Capturing from {_cameraLabel}.");

        try
        {
            await graph.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture graph faulted.");
            Dispatcher.UIThread.Post(() => StatusText.Text = $"Capture error: {ex.Message}");
        }

        // `await using camSource` (above) awaits the pump's teardown here.
    }

    // ── Stats ─────────────────────────────────────────────────────────

    private void OnStatsTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastStatsAt).TotalSeconds;
        if (dt < 0.5) return;
        _lastStatsAt = now;

        var pane = _activePane;
        if (pane is null)
            return;

        var rendered = pane.RenderedFrameCount;
        var renderFps = (rendered - _lastRendered) / dt;
        _lastRendered = rendered;

        var frames = Interlocked.Read(ref _frameCount);
        var sourceFps = (frames - _lastFrameCount) / dt;
        _lastFrameCount = frames;

        StatusText.Text =
            $"model={_modelLabel}   cam={_cameraLabel}   "
            + $"source={sourceFps,5:F1} fps   detect={renderFps,5:F1} fps   "
            + $"dropped={pane.DroppedWhileBusyCount}   {pane.StatusText}\n"
            + $"[ms] {pane.TimingBreakdown}";

        if (_logger.IsEnabled(LogLevel.Debug) && sourceFps > 0)
        {
            _logger.LogDebug(
                "Stats: model={Model} source={SourceFps:F1}fps detect={DetectFps:F1}fps "
                + "dropped={Dropped} {Timing}",
                _modelLabel, sourceFps, renderFps, pane.DroppedWhileBusyCount,
                pane.TimingBreakdown);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _statsTimer?.Stop();
        try { _windowCts.Cancel(); } catch { /* idempotent */ }
        await DisconnectAsync().ConfigureAwait(true);
        _yoloDetector?.Dispose();
        _yoloDetector = null;
        _faceDetector?.Dispose();
        _faceDetector = null;
        _loggerFactory.Dispose();
    }
}
