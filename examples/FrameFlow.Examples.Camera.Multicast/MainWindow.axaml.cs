using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FrameFlow.Avalonia;
using FrameFlow.Camera;
using FrameFlow.Graph;
using FrameFlow.Inference.Cuda;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Video;
using FrameFlow.Yolo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery;
using Periphery.Camera;

namespace FrameFlow.Examples.Camera.Multicast;

/// <summary>
/// Three-pane multicast demo driven by a live <see cref="CameraSession"/>
/// instead of a media file. One camera frame fans out to three independent
/// sinks: direct preview, YOLOv8 detection overlay, and a color-filter
/// visualization. Resurrects the proven <c>Periphery.Camera.Multicast.Example</c>
/// design — same UX (Refresh/Disconnect, ObservableCollection picker,
/// <see cref="DeviceSessionHost{TSession}"/>-managed lifecycle) but inside
/// FrameFlow's graph substrate so the fan-out is a substrate
/// <c>SinkNode&lt;VideoFrameRef&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Push-style source.</b> Built via
/// <see cref="CameraSessionSourceExtensions.AsPushVideoFrameSource"/>: a
/// background pump drains <see cref="CameraSession.CaptureAsync"/> into a
/// <see cref="CameraFramePushBridge"/> the graph reads from — the shape
/// downstream camera-inference consumers use, where a router subscriber
/// callback hands borrowed frames to the bridge. Demonstrating the
/// push path in tree lets the bridge's lifecycle (channel completion,
/// drain on dispose, single-source enforcement) be exercised against a
/// real camera. The pull-style adapter
/// (<see cref="CameraSourceAdapters.AsVideoFrameSourceNode"/>) is still
/// covered transitively because the bridge wraps it internally.
/// </para>
/// <para>
/// <b>Lifecycle.</b> A <see cref="DeviceSessionHost{TSession}"/> tracks
/// the selected device. On connect (selection in the picker), the host
/// opens a <see cref="CameraSession"/> via the fluent
/// <see cref="CameraSession.For(DeviceInfo)"/> builder (prefer BGRA32,
/// max 1280×720) and invokes <see cref="RunGraphAsync"/>. The graph
/// runs until the device disappears or the user clicks Disconnect — the
/// host then closes the session and waits for the next selection.
/// </para>
/// <para>
/// <b>Pixel format.</b> The panes consume <see cref="IVideoFrame"/> in
/// BGRA32. The builder's <c>PreferPixelFormat</c> picks BGRA32 when the
/// device offers it; for cameras that don't, an in-pipeline
/// <see cref="VideoOperators.ConvertPixelFormat"/> stage normalises to
/// BGRA32 before the fan-out. The periphery demo asked for MJPEG and let
/// each pane decode; FrameFlow's pane controls expect already-decoded
/// frames so the normalisation moves upstream of the broadcast.
/// </para>
/// <para>
/// <b>Resilience claim (carried over from periphery).</b> YOLO bootstrap
/// failure (missing CUDA EP, missing model, deliberate
/// <c>--break-yolo</c>) flips pane 2 to Unavailable while panes 1 and 3
/// continue normally. The fan-out's per-pane queue-of-one decouples slow
/// consumers from fast ones — a slow filter pass in pane 3 doesn't stall
/// pane 1.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _windowCts = new();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private ILogger<MainWindow> _logger = NullLogger<MainWindow>.Instance;
    private DeviceSessionHost<CameraSession>? _host;
    private DeviceInfo? _selectedDevice;

    private Yolov8Detector? _yoloDetector;
    private AvaloniaVideoSink? _pane1Sink;

    private DispatcherTimer? _statsTimer;
    private long _lastP1, _lastP2, _lastP3;
    private long _broadcastFrameCount;
    private long _lastBroadcastFrames;
    private long _broadcastBranchErrors;
    private DateTime _lastStatsAt;

    public ObservableCollection<DeviceInfo> Cameras { get; } = new();

    public string? StartupLogFilePath { get; set; }
    public bool BreakYolo { get; set; }

    /// <summary>
    /// When true, programmatically selects the first enumerated camera as
    /// soon as enumeration completes — no UI interaction needed. Enabled
    /// by the <c>--auto-pick</c> CLI flag; used for autonomous diagnostic
    /// runs where the agent launches the app to harvest log output.
    /// </summary>
    public bool AutoPickFirstCamera { get; set; }

    /// <summary>
    /// Zero-based index into the enumerated camera list used by
    /// <see cref="AutoPickFirstCamera"/>. Default 0 (first camera);
    /// set via <c>--auto-pick &lt;index&gt;</c>.
    /// </summary>
    public int AutoPickIndex { get; set; }

    /// <summary>
    /// When positive, schedules a self-close timer that fires after
    /// <c>N</c> seconds of wall-clock time post-construction. The
    /// shutdown path runs the same teardown as a manual close — the
    /// file logger flushes on dispose, so the log is complete by the
    /// time the process exits. Enabled by the <c>--exit-after &lt;n&gt;</c>
    /// CLI flag for non-interactive runs.
    /// </summary>
    public double ExitAfterSeconds { get; set; }

    public MainWindow()
    {
        StartupClock.Mark("MainWindow ctor entered");
        InitializeComponent();
        StartupClock.Mark("MainWindow ctor: InitializeComponent done");

        DevicePicker.ItemsSource = Cameras;
        DevicePicker.SelectionChanged += OnDeviceSelected;
        Closing += OnClosing;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        StartupClock.Mark("MainWindow.OnLoaded entered");
        base.OnLoaded(e);

        // Real logger factory so pipeline errors surface. Off by default
        // (no provider added), but --log-file <path> writes a full
        // debug-level trace so multicast-branch failures don't vanish.
        // Mirrors the periphery reference + FrameFlow file Multicast.
        _loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            if (!string.IsNullOrEmpty(StartupLogFilePath))
                b.AddProvider(new FileLoggerProvider(ExampleLogPaths.Resolve(StartupLogFilePath), LogLevel.Debug));
        });
        _logger = _loggerFactory.CreateLogger<MainWindow>();
        StartupClock.AttachLogger(_logger);
        StartupClock.Mark("LoggerFactory ready");
        _logger.LogInformation(
            "FrameFlow Camera Multicast ready. logFile={LogFile} breakYolo={BreakYolo}",
            StartupLogFilePath ?? "(none)",
            BreakYolo
        );

        // FFmpeg native bootstrap. The graph uses
        // VideoOperators.ConvertPixelFormat (libswscale) to normalise
        // arbitrary camera pixel formats to BGRA32 before the fan-out.
        // Unlike the file Multicast sibling, this example doesn't go
        // through MediaPlayer.CreateAsync — so we never get the implicit
        // FrameFlowBootstrapper.Initialize() that path runs, and the
        // DllImportResolver that maps "swscale" → "swscale-8.dll" never
        // registers. Without this explicit Initialize the first sws_*
        // call throws DllNotFoundException at the first frame.
        // Skip the HW probe — cameras don't decode, so HW-decode caps
        // aren't relevant here.
        StartupClock.Mark("FFmpeg bootstrap starting");
        var ffmpegBootstrap = new FrameFlowBootstrapper(
            new FrameFlowNativeOptions { SkipHardwareProbe = true },
            _loggerFactory).Initialize();
        if (!ffmpegBootstrap.IsSuccess)
        {
            _logger.LogError("FFmpeg bootstrap failed: {Message}", ffmpegBootstrap.Message);
            GlobalStatsText.Text =
                $"FFmpeg bootstrap failed: {ffmpegBootstrap.Message}";
            return;
        }
        StartupClock.Mark("FFmpeg bootstrap complete");

        FilterPicker.ItemsSource = Enum.GetValues<FilteredPreview.FilterMode>();
        FilterPicker.SelectedIndex = 0;
        FilterPicker.SelectionChanged += (_, _) =>
        {
            if (FilterPicker.SelectedItem is FilteredPreview.FilterMode mode)
                Pane3Preview.Filter = mode;
        };

        _statsTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnStatsTick
        );
        _statsTimer.Start();
        _lastStatsAt = DateTime.UtcNow;

        await BootstrapYoloAsync().ConfigureAwait(true);
        await RefreshDevicesAsync().ConfigureAwait(true);

        StartupClock.Mark("OnLoaded complete (ready for camera selection)");

        // --auto-pick: programmatically select the first camera so an
        // autonomous diagnostic run can exercise the full capture path
        // without waiting on UI input.
        if (AutoPickFirstCamera && Cameras.Count > 0)
        {
            var idx = Math.Clamp(AutoPickIndex, 0, Cameras.Count - 1);
            _logger.LogInformation(
                "--auto-pick {Idx}: selecting camera {Name}",
                idx,
                Cameras[idx].Name ?? "(unnamed)");
            DevicePicker.SelectedIndex = idx; // fires OnDeviceSelected
        }

        // --exit-after N: schedule a clean shutdown after N seconds so
        // the file logger flushes and the process exits without manual
        // intervention. Use UI-thread DispatcherTimer so Close() runs
        // on the dispatcher.
        if (ExitAfterSeconds > 0)
        {
            _logger.LogInformation(
                "--exit-after: scheduling self-close in {Seconds:F1}s.",
                ExitAfterSeconds);
            var exitTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(ExitAfterSeconds),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    _logger.LogInformation("--exit-after: closing window.");
                    Close();
                });
            exitTimer.Start();
        }
    }

    // ── YOLO bootstrap ────────────────────────────────────────────────

    /// <summary>
    /// Bootstrap the host-owned YOLOv8 detector at startup. On failure
    /// (missing CUDA EP, model download error, deliberate --break-yolo)
    /// pane 2 is flipped to Unavailable mode while panes 1 and 3
    /// continue normally — the resilience claim made visible.
    /// </summary>
    private async Task BootstrapYoloAsync()
    {
        if (BreakYolo)
        {
            _logger.LogWarning(
                "YOLO bootstrap deliberately skipped (--break-yolo). Pane 2 will be unavailable; panes 1 and 3 should continue normally."
            );
            Pane2Preview.SetUnavailable("--break-yolo flag");
            return;
        }

        try
        {
            StartupClock.Mark("YOLO bootstrap: starting (CUDA EP)");
            _yoloDetector = await Yolov8Detector.CreateAsync(
                sessionFactory: path =>
                {
                    StartupClock.Mark("YOLO bootstrap: constructing CudaInferenceSession");
                    var s = new CudaInferenceSession(path);
                    StartupClock.Mark("YOLO bootstrap: CudaInferenceSession constructed");
                    return s;
                },
                ct: _windowCts.Token,
                loggerFactory: _loggerFactory).ConfigureAwait(true);
            StartupClock.Mark("YOLO bootstrap: detector ready (post-warmup)");
            Pane2Preview.SetDetector(_yoloDetector);
            _logger.LogInformation("YOLOv8 detector ready for pane 2.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "YOLOv8 unavailable; pane 2 will render no detections. Panes 1 and 3 are unaffected."
            );
            Pane2Preview.SetUnavailable($"{ex.GetType().Name}");
        }
    }

    // ── Device discovery ──────────────────────────────────────────────

    private async Task RefreshDevicesAsync()
    {
        try
        {
            StartupClock.Mark("Camera enumeration starting");
            var devices = await CameraDevice
                .EnumerateAsync(_windowCts.Token).ConfigureAwait(true);
            StartupClock.Mark($"Camera enumeration complete ({devices.Count} device(s))");
            Cameras.Clear();
            foreach (var d in devices) Cameras.Add(d);
            _logger.LogInformation("Enumerated {Count} camera device(s).", devices.Count);
            if (devices.Count == 0)
                GlobalStatsText.Text = "No cameras detected. Plug one in and click Refresh.";
            else
                GlobalStatsText.Text =
                    $"{devices.Count} camera(s) found. Pick one to begin.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device enumeration failed.");
            GlobalStatsText.Text = $"Failed to enumerate cameras: {ex.Message}";
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) =>
        await RefreshDevicesAsync().ConfigureAwait(true);

    // ── Connect / Disconnect ──────────────────────────────────────────

    private async void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not DeviceInfo device) return;
        if (ReferenceEquals(device, _selectedDevice)) return;
        _selectedDevice = device;
        await ConnectAsync(device).ConfigureAwait(true);
    }

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        await DisconnectAsync().ConfigureAwait(true);
        _selectedDevice = null;
        DevicePicker.SelectedItem = null;
        DisconnectButton.IsEnabled = false;
        GlobalStatsText.Text = "Disconnected.";
    }

    private async Task ConnectAsync(DeviceInfo device)
    {
        await DisconnectAsync().ConfigureAwait(true);
        DisconnectButton.IsEnabled = true;
        GlobalStatsText.Text = $"Connecting to {device.Name ?? "(unnamed)"}…";
        _logger.LogInformation(
            "Connecting to camera {DeviceName} (Id={DeviceId}).",
            device.Name ?? "(unnamed)",
            // DeviceInfo.Id is a non-nullable DeviceId struct as of Periphery 4.x,
            // so there is no null to coalesce away. Name is still a nullable string.
            device.Id
        );

        try
        {
            _host = await DeviceSessionHost<CameraSession>.ForDeviceAsync(
                device,
                createSession: CreateSessionAsync,
                onSessionEnded: _ =>
                {
                    _logger.LogInformation("Camera session ended.");
                    return Task.CompletedTask;
                },
                whileSessionActive: RunGraphAsync,
                ct: _windowCts.Token).ConfigureAwait(true);
            _logger.LogInformation("Camera host started; awaiting session.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera host failed to start.");
            GlobalStatsText.Text = $"Failed to start host: {ex.Message}";
            DisconnectButton.IsEnabled = false;
        }
    }

    private async Task DisconnectAsync()
    {
        if (_host is null) return;
        var host = _host;
        _host = null;
        try { await host.DisposeAsync().ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Host teardown threw"); }
    }

    // ── Session factory + graph wireup ────────────────────────────────

    private Task<CameraSession> CreateSessionAsync(DeviceInfo device, CancellationToken ct)
    {
        StartupClock.Mark($"Opening camera: {device.Name ?? "(unnamed)"}");
        // Fluent builder — same shape as the proven Periphery demo. The
        // AllowOnlyPixelFormats list is the set FrameFlow.Camera's
        // CameraVideoFrame bridge can map onto FrameFlow.Media's
        // PixelFormat enum (Bgra32 / Nv12 / Yuyv422 / Uyvy422 / Rgba32).
        // The fluent PreferPixelFormat(Bgra32) biases ordering toward
        // the zero-conversion path; an upstream
        // VideoOperators.ConvertPixelFormat in RunGraphAsync normalises
        // whatever survives the filter to BGRA32 before the fan-out.
        return CameraSession.For(device)
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
    }

    private async Task RunGraphAsync(CameraSession session, CancellationToken ct)
    {
        StartupClock.Mark("Camera session opened; building graph");

        _pane1Sink = Pane1Preview.EnsureSink();
        var pane1 = _pane1Sink;
        var pane2 = Pane2Preview;
        var pane3 = Pane3Preview;

        Interlocked.Exchange(ref _broadcastFrameCount, 0);
        Interlocked.Exchange(ref _broadcastBranchErrors, 0);

        // Push-style camera source (library): a background pump drains
        // session.CaptureAsync into a capacity-1 DropOldest bridge (LatestOnly,
        // so a slow graph never sees stale frames), exposed as a graph source —
        // same shape downstream camera-inference consumers use.
        await using var camSource = session.AsPushVideoFrameSource(ct, _loggerFactory);

        // Substrate graph: source → convert(BGRA32) → count → fanout.
        // No PaceUntil — cameras produce at sensor rate which is already
        // display-rate (≈30 fps typical).
        var graph = new Graph.Graph();
        var source = camSource.Source;
        var convert = VideoOperators.ConvertPixelFormat(
            "camera-convert",
            PixelFormat.Bgra32);
        var counter = new OperatorNode<VideoFrameRef, VideoFrameRef>(
            "broadcast-count",
            (item, _) =>
            {
                Interlocked.Increment(ref _broadcastFrameCount);
                return ValueTask.FromResult<VideoFrameRef?>(item);
            });

        // Terminal fan-out: clone the BGRA32 frame three times (each
        // pane disposes its own independently), dispatch to the three
        // pane sinks. PresentAsync on each pane returns immediately
        // (queue-of-one with displacement), so Task.WhenAll completes
        // at the speed of the *slowest queueing op* — not the slowest
        // render. Same shape as the file Multicast sibling.
        var fanout = new SinkNode<VideoFrameRef>(
            "broadcast-fanout",
            async (item, ct2) =>
            {
                var clone1 = item.Frame.CloneCpu();
                var clone2 = item.Frame.CloneCpu();
                var clone3 = item.Frame.CloneCpu();
                try
                {
                    await Task.WhenAll(
                        pane1.PresentAsync(clone1, ct2).AsTask(),
                        pane2.PresentAsync(clone2, ct2).AsTask(),
                        pane3.PresentAsync(clone3, ct2).AsTask()
                    ).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Per-branch isolation: swallow + count so one bad
                    // pane doesn't tear down the graph. Mirrors the
                    // periphery BroadcastFanOut.SafePresentAsync claim;
                    // here it's aggregate rather than per-branch (file
                    // Multicast doesn't split either — see ADR-0014
                    // Phase 4).
                    Interlocked.Increment(ref _broadcastBranchErrors);
                    _logger.LogWarning(ex, "Broadcast fan-out branch threw.");
                }
            });

        graph
            .Connect(source.Output, convert.Input)
            .Connect(convert.Output, counter.Input)
            .Connect(counter.Output, fanout.Input);

        StartupClock.Mark("Graph wired; entering RunAsync");
        Dispatcher.UIThread.Post(() =>
            GlobalStatsText.Text = $"Capturing from {session.DeviceInfo.Name ?? "(unnamed)"}.");

        try
        {
            await graph.RunAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Capture graph completed (source drained).");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Capture graph cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture graph faulted.");
            Dispatcher.UIThread.Post(() =>
                GlobalStatsText.Text = $"Capture error: {ex.GetType().Name}: {ex.Message}");
        }

        // `await using camSource` (above) awaits the pump's teardown here.
    }

    // ── Stats tick ─────────────────────────────────────────────────────

    private void OnStatsTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastStatsAt).TotalSeconds;
        if (dt < 0.5) return;
        _lastStatsAt = now;

        var p1 = Pane1Preview.RenderedFrameCount;
        var p2 = Pane2Preview.RenderedFrameCount;
        var p3 = Pane3Preview.RenderedFrameCount;

        var fps1 = (p1 - _lastP1) / dt;
        var fps2 = (p2 - _lastP2) / dt;
        var fps3 = (p3 - _lastP3) / dt;

        _lastP1 = p1; _lastP2 = p2; _lastP3 = p3;

        var p1Dropped = _pane1Sink?.DroppedFrameCount ?? 0;
        Pane1Stats.Text = $"FPS: {fps1,5:F1}   Dropped: {p1Dropped}";
        Pane2Stats.Text =
            $"FPS: {fps2,5:F1}   Dropped: {Pane2Preview.DroppedWhileBusyCount}   "
            + $"{Pane2Preview.StatusText}\n"
            + $"[ms] {Pane2Preview.TimingBreakdown}";
        Pane3Stats.Text =
            $"FPS: {fps3,5:F1}   Dropped: {Pane3Preview.DroppedFrameCount}   "
            + $"{Pane3Preview.StatusText}";

        var broadcastFrames = Interlocked.Read(ref _broadcastFrameCount);
        var broadcastFps = (broadcastFrames - _lastBroadcastFrames) / dt;
        _lastBroadcastFrames = broadcastFrames;

        var branchErrors = Interlocked.Read(ref _broadcastBranchErrors);
        var errorSuffix = branchErrors == 0 ? "" : $"   ⚠ Branch errors: {branchErrors}";

        // Surface session/host status alongside the source FPS so the
        // user can see "no session yet" vs "session active" without
        // having to scrutinise the panes.
        var hostStatus = _host is null
            ? "no host"
            : _host.StatusDescription;
        GlobalStatsText.Text =
            $"Source: {broadcastFps,5:F1} fps   "
            + $"Host: {hostStatus}{errorSuffix}";

        // Mirror per-pane stats to the logger at debug level so
        // autonomous diagnostic runs (--auto-pick + --exit-after) can
        // verify panes are actually rendering — not just that the
        // capture graph ran without throwing. A non-zero FPS on every
        // pane is the load-bearing signal that the BGRA32 conversion
        // and broadcast fan-out are working end-to-end.
        if (_logger.IsEnabled(LogLevel.Debug) && broadcastFps > 0)
        {
            _logger.LogDebug(
                "Stats: source={SourceFps:F1}fps  pane1={P1Fps:F1}fps(drops={P1Drops})  "
                + "pane2={P2Fps:F1}fps(drops={P2Drops})  pane3={P3Fps:F1}fps(drops={P3Drops})  "
                + "branchErrors={Errors}",
                broadcastFps,
                fps1, p1Dropped,
                fps2, Pane2Preview.DroppedWhileBusyCount,
                fps3, Pane3Preview.DroppedFrameCount,
                branchErrors);
        }
    }

    // ── Window lifecycle ──────────────────────────────────────────────

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _statsTimer?.Stop();
        try { _windowCts.Cancel(); } catch { /* idempotent */ }
        _logger.LogInformation("Camera multicast example shutting down.");
        await DisconnectAsync().ConfigureAwait(true);
        _yoloDetector?.Dispose();
        _yoloDetector = null;
        _loggerFactory.Dispose();
    }
}
