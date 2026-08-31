using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FrameFlow.Graph;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Avalonia;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using FrameFlow.Video;
using FrameFlow.Yolo;
using FrameFlow.Inference.Cuda;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.Multicast;

/// <summary>
/// Three-pane multicast demo on top of the Layer-2
/// <see cref="FrameFlowPlayer"/> fluent API and the consumer pipeline
/// configurator seam (ADR-0043). One decoded video stream fans out to
/// three independent <see cref="IFrameSink{TFrame}"/> implementations
/// (direct render, face detection, color filter), each running at its
/// own rate via Crossbar's <see cref="FramePipelineExtensions.Broadcast"/>
/// per-branch bounded channels.
/// </summary>
/// <remarks>
/// <para>
/// <b>GPU mode (<c>--gpu</c>).</b> Swaps the three heterogeneous CPU
/// panes for three zero-copy <c>CompositionInteropVideoView</c>
/// presenters and runs the decoder with <c>yieldHardwareFrames: true</c>.
/// One D3D11VA <c>GpuVideoFrame</c> per picture is fanned out to all three
/// panes via <c>GpuVideoFrame.AddRef</c> — no per-pane readback or clone;
/// the shared decode-texture slice is pinned by ref counting until the
/// last pane releases it. Windows-only (the presenter is D3D11-based);
/// falls back to the CPU panes with a warning elsewhere. This is the GPU
/// analog of the CPU path's per-pane <c>CloneCpu</c> fan-out below.
/// </para>
/// <para>
/// <b>Configurator owns termination (ADR-0045).</b> Crossbar's
/// <c>Broadcast</c> is a terminal-shaped operator: it distributes
/// upstream packets to per-branch channels and yields nothing
/// downstream. The player's pump drives whatever the configurator
/// returns via <c>RunAsync</c>, so the multicast configurator just
/// composes <c>.Broadcast(...)</c> and the pump runs it. No
/// <c>WithVideoSink</c> is needed — the consumer's pipeline says
/// where frames go.
/// </para>
/// <para>
/// <b>Pacing.</b> The player paces video against the master clock
/// (the OpenAL audio sink, when audio is present; the wall clock
/// otherwise) before the configurator runs. This is a behavioral
/// improvement over the pre-refactor demo, which consumed
/// <c>controller.VideoFrames</c> at decode rate. Files with audio
/// now stay in sync; files without audio fall back to wall-clock
/// pacing via <see cref="WallClockSource"/>.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _windowCts = new();
    private ILoggerFactory? _loggerFactory;
    private ILogger<MainWindow>? _logger;

    private IMediaPlayer? _player;
    private OpenAlAudioSink? _audioSink;
    private long _broadcastFrameCount;
    private long _broadcastBranchDrops;
    private long _broadcastBranchErrors;
    private Yolov8Detector? _yoloDetector;
    private AvaloniaVideoSink? _pane1Sink;

    private DispatcherTimer? _statsTimer;
    private long _lastP1,
        _lastP2,
        _lastP3;
    private long _lastBroadcastFrames;
    private DateTime _lastStatsAt;

    public string? StartupFilePath { get; set; }
    public string? StartupLogFilePath { get; set; }

    /// <summary>
    /// When true, deliberately skip YOLO bootstrap and flip pane 2 to
    /// the "Unavailable" state. Set by the <c>--break-yolo</c>
    /// CLI flag. Used to verify the resilience claim that panes 1 and
    /// 3 stay healthy when pane 2's detector fails to initialise.
    /// </summary>
    public bool BreakYolo { get; set; }

    /// <summary>
    /// When true (and on Windows), swap the three heterogeneous CPU panes
    /// for three zero-copy composition-interop presenters and fan ONE
    /// D3D11VA decode out to all of them via <see cref="GpuVideoFrame.AddRef"/>
    /// — the GPU analog of the CPU multicast's per-pane <c>CloneCpu</c>.
    /// Set by the <c>--gpu</c> CLI flag.
    /// </summary>
    public bool UseGpu { get; set; }

    /// <summary>
    /// When &gt; 0, auto-close the window after this many seconds. Set by
    /// <c>--exit-after &lt;seconds&gt;</c> for autonomous run-and-read-the-log loops.
    /// </summary>
    public int ExitAfterSeconds { get; set; }

    // Resolved at OnLoaded: UseGpu gated on actually being on Windows (the
    // composition-interop presenter is D3D11-only).
    private bool _useGpu;

    // GPU mode: the three presenters that replace the CPU pane controls, and
    // the sinks the AddRef fan-out feeds. Null in CPU mode.
    private CompositionInteropVideoView[]? _gpuPanes;
    private CompositionInteropVideoSink[]? _gpuSinks;
    private bool _warnedNonGpuFrame;

    public MainWindow()
    {
        StartupClock.Mark("MainWindow ctor entered");
        InitializeComponent();
        StartupClock.Mark("MainWindow ctor: InitializeComponent done");
        Closing += OnWindowClosing;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        StartupClock.Mark("MainWindow.OnLoaded entered");
        base.OnLoaded(e);

        // Real logger factory so pipeline errors surface. Off by
        // default (no provider added), but --log-file <path> writes
        // a full debug-level trace so multicast-branch failures don't
        // vanish into the void the old NullLoggerFactory swallowed.
        // Built in OnLoaded (not the ctor) for parity with the other
        // Avalonia example windows — controls are guaranteed
        // materialised by the time we install dependencies on them.
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
            "FrameFlow Multicast ready. logFile={LogFile} autoplay={Autoplay}",
            StartupLogFilePath ?? "(none)",
            StartupFilePath ?? "(none)"
        );

        // --gpu: the composition-interop presenter is Windows/D3D11-only, so the
        // GPU multicast path is gated on actually being on Windows.
        _useGpu = UseGpu && OperatingSystem.IsWindows();
        if (UseGpu && !_useGpu)
            _logger.LogWarning("--gpu requested but not on Windows; falling back to the CPU panes.");

        if (ExitAfterSeconds > 0)
            ScheduleAutoExit(ExitAfterSeconds);

        if (_useGpu)
        {
            // Replace the three heterogeneous CPU panes (direct / YOLO / filter)
            // with three identical zero-copy presenters. One decode fans out to
            // all of them — see the fan-out in PlayFileAsync.
            SetupGpuPanes();
        }
        else
        {
            FilterPicker.ItemsSource = Enum.GetValues<FilteredPreview.FilterMode>();
            FilterPicker.SelectedIndex = 0;
            FilterPicker.SelectionChanged += (_, _) =>
            {
                if (FilterPicker.SelectedItem is FilteredPreview.FilterMode mode)
                    Pane3Preview.Filter = mode;
            };
        }

        _statsTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnStatsTick
        );
        _statsTimer.Start();
        _lastStatsAt = DateTime.UtcNow;

        // YOLO bootstrap for pane 2. Mirrors LiveCaptioning's pattern:
        // detector creation can throw (missing CUDA EP, missing ONNX
        // model download, ORT init failure). We swallow the throw and
        // flip pane 2 to Unavailable rather than killing the app —
        // panes 1 and 3 are independent of YOLO, so a broken detector
        // shouldn't take down the whole multicast.
        //
        // --break-yolo forces the failure path so we can demonstrate
        // the resilience claim deterministically.
        if (_useGpu)
        {
            // GPU mode: pane 2 is a zero-copy presenter, not a detector — nothing
            // to bootstrap here.
        }
        else if (BreakYolo)
        {
            _logger.LogWarning(
                "YOLO bootstrap deliberately skipped (--break-yolo). Pane 2 will be unavailable; panes 1 and 3 should continue normally."
            );
            Pane2Preview.SetUnavailable("--break-yolo flag");
        }
        else
        {
            try
            {
                StartupClock.Mark("YOLO bootstrap: starting (CUDA EP)");
                _yoloDetector = await Yolov8Detector.CreateAsync(
                    sessionFactory: path =>
                    {
                        StartupClock.Mark("YOLO bootstrap: model path resolved, constructing CudaInferenceSession");
                        var s = new CudaInferenceSession(path);
                        StartupClock.Mark("YOLO bootstrap: CudaInferenceSession constructed");
                        return s;
                    },
                    ct: _windowCts.Token,
                    loggerFactory: _loggerFactory);
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

        StartupClock.Mark("OnLoaded complete (ready for file open)");
        if (!string.IsNullOrEmpty(StartupFilePath) && File.Exists(StartupFilePath))
        {
            StartupClock.Mark("Autoplay PlayFileAsync starting");
            await PlayFileAsync(StartupFilePath);
            StartupClock.Mark("Autoplay PlayFileAsync returned");
        }
    }

    /// <summary>
    /// GPU mode: tear the CPU pane controls out of their host borders and drop a
    /// <see cref="CompositionInteropVideoView"/> into each, then wire each one's
    /// logger + sink. The three sinks are what the AddRef fan-out in
    /// <see cref="PlayFileAsync"/> feeds. Each view owns its sink and disposes it
    /// on detach, so teardown is just disposing the views.
    /// </summary>
    private void SetupGpuPanes()
    {
        _logger!.LogInformation(
            "GPU mode: replacing the 3 heterogeneous panes with zero-copy composition-interop presenters."
        );

        Pane1Title.Text = "Pane 1 — Zero-copy GPU presenter";
        Pane2Title.Text = "Pane 2 — Zero-copy GPU presenter";
        Pane3Title.Text = "Pane 3 — Zero-copy GPU presenter";
        FilterPicker.IsVisible = false;

        var hosts = new[] { Pane1Host, Pane2Host, Pane3Host };
        _gpuPanes = new CompositionInteropVideoView[hosts.Length];
        _gpuSinks = new CompositionInteropVideoSink[hosts.Length];

        for (var i = 0; i < hosts.Length; i++)
        {
            var view = new CompositionInteropVideoView();
            // Setting Child attaches the view to the (already-loaded) visual tree;
            // Initialize then wires the logger, owned sink, render tick, and
            // compositor interop.
            hosts[i].Child = view;
            view.Initialize(_loggerFactory!);
            _gpuPanes[i] = view;
            _gpuSinks[i] = view.EnsureSink();
        }
    }

    /// <summary>
    /// Schedules a one-shot window close after <paramref name="seconds"/> for
    /// autonomous run-and-read-the-log loops (<c>--exit-after</c>).
    /// </summary>
    private void ScheduleAutoExit(int seconds)
    {
        var timer = new DispatcherTimer(
            TimeSpan.FromSeconds(seconds),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _logger?.LogInformation("--exit-after {Seconds}s elapsed; closing window.", seconds);
                Close();
            }
        );
        timer.Start();
    }

    private async Task PlayFileAsync(string filePath)
    {
        if (_loggerFactory is null || _logger is null)
            return; // OnLoaded hasn't fired yet — nothing to play with.

        // PlayFileAsync runs on the UI thread (called from OnLoaded and
        // OnOpenClick). Direct UI-control access is therefore safe.
        await TeardownPlayerAsync();

        FileNameText.Text = $"Playing: {Path.GetFileName(filePath)}";
        Title = $"FrameFlow Multicast — {Path.GetFileName(filePath)}";

        Interlocked.Exchange(ref _broadcastFrameCount, 0);
        Interlocked.Exchange(ref _broadcastBranchDrops, 0);
        Interlocked.Exchange(ref _broadcastBranchErrors, 0);

        // Pane 1 uses the stock FrameFlow.Avalonia surface as a Crossbar
        // terminal sink. EnsureSink() lazily materialises an owned
        // AvaloniaVideoSink the first time it's needed; we capture it
        // so we can both feed the broadcast branch (via .Consumer) and
        // read stats off it (RenderedFrameCount / DroppedFrameCount).
        // The view disposes the owned sink on detach, so we don't have
        // to.
        _pane1Sink = Pane1Preview.EnsureSink();

        try
        {
            // Built on MediaPlayer.CreateAsync. The video configurator
            // replaces the retired Broadcast operator — it
            // builds a StorageNode that fans the converted frames out
            // to three sinks, each running at its own rate via the
            // bounded edge channels (LowLatency=DropIncoming overflow).
            //
            // This is the configurator-terminated path: no main video
            // sink is passed to MediaPlayer (videoSink: null), so
            // SubstrateSession skips the default pace+gate+sink chain
            // and lets the configurator wire everything itself.
            //
            // Pacing: the convert→clone→storage chain doesn't pace
            // (the substrate session's PaceUntil isn't appended in the
            // configurator-terminated path). For the visual demo this
            // is OK — fans-out at decode rate so all three panes
            // refresh together; if you want clock-synced playback
            // insert PaceUntil.Create before the StorageNode.
            StartupClock.Mark("PlayFileAsync: constructing OpenAlAudioSink");
            _audioSink = new OpenAlAudioSink(_loggerFactory.CreateLogger<OpenAlAudioSink>());
            _pane1Sink = Pane1Preview.EnsureSink();
            var pane1 = _pane1Sink;
            var pane2 = Pane2Preview;
            var pane3 = Pane3Preview;

            StartupClock.Mark("PlayFileAsync: MediaPlayer.CreateAsync starting");
            _player = await MediaPlayer.CreateAsync(
                source: MediaSource.FromFile(filePath),
                videoSink: null, // configurator-terminated — see below
                audioSink: _audioSink,
                hardwareDecodeMode: HardwareDecodeMode.Auto,
                // GPU mode: keep hardware frames on the GPU so the fan-out can
                // AddRef one GpuVideoFrame to every presenter (zero-copy). CPU
                // mode leaves this false and gets readback CpuVideoFrames as before.
                yieldHardwareFrames: _useGpu,
                initialRepeatMode: LoopButton.IsChecked == true ? RepeatMode.One : RepeatMode.Off,
                loggerFactory: _loggerFactory,
                configureVideo: chain =>
                {
                    if (_useGpu)
                    {
                        // GPU fan-out: the decoder yields ONE GpuVideoFrame per
                        // picture (yieldHardwareFrames: true). We hand each pane its
                        // own AddRef'd reference to that SAME frame — no convert, no
                        // readback, no clone. All three presenters read the same
                        // D3D11VA decode slice, which stays pinned until the last
                        // pane releases its ref (GpuVideoFrame ref counting). This is
                        // the literal realization of the CPU path's aspiration below:
                        // "each frame's refcount peaks at N (one per pane)."
                        var counted = chain.Then(
                            new OperatorNode<VideoFrameRef, VideoFrameRef>(
                                "gpu-broadcast-count",
                                (item, ct) =>
                                {
                                    Interlocked.Increment(ref _broadcastFrameCount);
                                    return ValueTask.FromResult<VideoFrameRef?>(item);
                                }
                            )
                        );

                        counted.To(
                            new SinkNode<VideoFrameRef>(
                                "gpu-broadcast-fanout",
                                async (item, ct) =>
                                {
                                    var sinks = _gpuSinks;
                                    if (sinks is null)
                                        return;

                                    if (item.Frame is GpuVideoFrame gpu)
                                    {
                                        // One decode → N presenters, each owning its
                                        // own ref on the SAME frame.
                                        var tasks = new Task[sinks.Length];
                                        for (var i = 0; i < sinks.Length; i++)
                                            tasks[i] = sinks[i]
                                                .PresentAsync(gpu.AddRef(), ct)
                                                .AsTask();
                                        await Task.WhenAll(tasks).ConfigureAwait(false);
                                    }
                                    else if (!_warnedNonGpuFrame)
                                    {
                                        _warnedNonGpuFrame = true;
                                        _logger?.LogWarning(
                                            "GPU mode: decoder yielded {Type} (not a D3D11VA GpuVideoFrame) — "
                                                + "hardware decode did not engage, so there's nothing to fan out "
                                                + "zero-copy. Run on a box with D3D11VA, or drop --gpu for the "
                                                + "software CPU panes.",
                                            item.Frame.GetType().Name
                                        );
                                    }
                                }
                            )
                        );

                        return chain;
                    }

                    // chain: source. Add convert → clone-and-fan-out
                    // operators that hand a fresh deep-clone to each
                    // pane. Decoder + converter outputs are one-shot
                    // frames so we can't use the substrate's
                    // StorageNode (which AddRefs); the clone operator
                    // is the analog of the old
                    // Broadcast(duplicate: frame => frame.CloneCpu()).
                    var afterConvert = chain.Then(
                        VideoOperators.ConvertPixelFormat(
                            "broadcast-convert",
                            PixelFormat.Bgra32
                        )
                    );
                    var afterCount = afterConvert.Then(
                        new OperatorNode<VideoFrameRef, VideoFrameRef>(
                            "broadcast-count",
                            (item, ct) =>
                            {
                                Interlocked.Increment(ref _broadcastFrameCount);
                                return ValueTask.FromResult<VideoFrameRef?>(item);
                            }
                        )
                    );

                    // Terminal fan-out: a sink-node that clones the
                    // incoming frame N times and dispatches each clone
                    // to one pane's IVideoSink. Returns nothing
                    // (substrate-pure terminal).
                    afterCount.To(
                        new SinkNode<VideoFrameRef>(
                            "broadcast-fanout",
                            async (item, ct) =>
                            {
                                // pane1 is an IVideoSink (post-ADR-0014
                                // Phase 4: invoke PresentAsync directly);
                                // pane2/pane3 are custom Avalonia
                                // controls with public PresentAsync
                                // methods of the same shape.
                                // Each pane gets an independently-
                                // disposable CloneCpu so they can dispose
                                // on their own cadence.
                                var clone1 = item.Frame.CloneCpu();
                                var clone2 = item.Frame.CloneCpu();
                                var clone3 = item.Frame.CloneCpu();
                                await Task.WhenAll(
                                    pane1.PresentAsync(clone1, ct).AsTask(),
                                    pane2.PresentAsync(clone2, ct).AsTask(),
                                    pane3.PresentAsync(clone3, ct).AsTask()
                                ).ConfigureAwait(false);
                            }
                        )
                    );

                    return chain; // returned chain ignored — configurator terminated
                },
                cancellationToken: _windowCts.Token
            );
            StartupClock.Mark("PlayFileAsync: MediaPlayer.CreateAsync returned");

            // Bind the standalone chrome panel to the freshly built
            // player. From this point Play/Pause/Stop/seek/volume on
            // the chrome row drive the upstream source — so the
            // single chrome instance controls all three panes
            // simultaneously (they're downstream consumers of one
            // decoded stream).
            PlayerChrome.MediaPlayer = _player;

            StartupClock.Mark("PlayFileAsync: PlayAsync starting");
            await _player.PlayAsync(_windowCts.Token);
            StartupClock.Mark("PlayFileAsync: PlayAsync returned");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PlayFileAsync failed for {File}", Path.GetFileName(filePath));
            GlobalStatsText.Text = $"Open error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider
            .OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Open Video File",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Video Files")
                        {
                            Patterns = ["*.mp4", "*.mkv", "*.webm", "*.avi", "*.mov"],
                        },
                        new FilePickerFileType("All Files") { Patterns = ["*.*"] },
                    ],
                }
            )
            ;

        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (path is null)
            return;

        await PlayFileAsync(path);
    }

    private async void OnLoopClick(object? sender, RoutedEventArgs e)
    {
        if (_player is null)
            return;
        var mode = LoopButton.IsChecked == true ? RepeatMode.One : RepeatMode.Off;
        await _player.SetRepeatModeAsync(mode);
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastStatsAt).TotalSeconds;
        if (dt < 0.5)
            return;
        _lastStatsAt = now;

        if (_useGpu)
        {
            UpdateGpuStats(dt);
            return;
        }

        // Pane 1 stats come off the sink (drops) and the view
        // (rendered count) — the same surface a production consumer
        // would read. Pane 2 / 3 are bespoke classes with their own
        // counters because they do per-frame work the stock sink
        // doesn't (YOLO inference, OpenCV filtering).
        var p1 = Pane1Preview.RenderedFrameCount;
        var p2 = Pane2Preview.RenderedFrameCount;
        var p3 = Pane3Preview.RenderedFrameCount;

        var fps1 = (p1 - _lastP1) / dt;
        var fps2 = (p2 - _lastP2) / dt;
        var fps3 = (p3 - _lastP3) / dt;

        _lastP1 = p1;
        _lastP2 = p2;
        _lastP3 = p3;

        var p1Dropped = _pane1Sink?.DroppedFrameCount ?? 0;
        Pane1Stats.Text =
            $"FPS: {fps1, 5:F1}   Dropped: {p1Dropped}{BranchErrorSuffix(0)}";
        Pane2Stats.Text =
            $"FPS: {fps2, 5:F1}   Dropped: {Pane2Preview.DroppedWhileBusyCount}   "
            + $"{Pane2Preview.StatusText}{BranchErrorSuffix(1)}\n"
            + $"[ms] {Pane2Preview.TimingBreakdown}";
        Pane3Stats.Text =
            $"FPS: {fps3, 5:F1}   Dropped: {Pane3Preview.DroppedFrameCount}   "
            + $"{Pane3Preview.StatusText}{BranchErrorSuffix(2)}";

        var broadcastFrames = Interlocked.Read(ref _broadcastFrameCount);
        var broadcastFps = (broadcastFrames - _lastBroadcastFrames) / dt;
        _lastBroadcastFrames = broadcastFrames;

        var branchErrors = Interlocked.Read(ref _broadcastBranchErrors);
        var branchDrops = Interlocked.Read(ref _broadcastBranchDrops);

        var errorSuffix = branchErrors == 0 ? "" : $"   ⚠ Branch errors: {branchErrors}";

        GlobalStatsText.Text =
            $"video pipeline: {broadcastFps, 5:F1} fps   "
            + $"Branch drops: {branchDrops}   "
            + $"Each frame's refcount peaks at 3 (one per pane)."
            + errorSuffix;
    }

    /// <summary>
    /// GPU-mode stats: per-pane present FPS + count off each
    /// <see cref="CompositionInteropVideoView"/>, plus the shared decode rate.
    /// Reuses the <c>_lastP*</c> deltas (unused by the CPU pane controls in this mode).
    /// </summary>
    private void UpdateGpuStats(double dt)
    {
        var panes = _gpuPanes;
        if (panes is null || panes.Length < 3)
            return;

        long p1 = panes[0].FramesPresented;
        long p2 = panes[1].FramesPresented;
        long p3 = panes[2].FramesPresented;

        var fps1 = (p1 - _lastP1) / dt;
        var fps2 = (p2 - _lastP2) / dt;
        var fps3 = (p3 - _lastP3) / dt;
        _lastP1 = p1;
        _lastP2 = p2;
        _lastP3 = p3;

        Pane1Stats.Text = $"FPS: {fps1,5:F1}   Presented: {p1}   Dropped: {panes[0].FramesDropped}";
        Pane2Stats.Text = $"FPS: {fps2,5:F1}   Presented: {p2}   Dropped: {panes[1].FramesDropped}";
        Pane3Stats.Text = $"FPS: {fps3,5:F1}   Presented: {p3}   Dropped: {panes[2].FramesDropped}";

        var broadcastFrames = Interlocked.Read(ref _broadcastFrameCount);
        var broadcastFps = (broadcastFrames - _lastBroadcastFrames) / dt;
        _lastBroadcastFrames = broadcastFrames;

        GlobalStatsText.Text =
            $"ONE D3D11VA decode → 3 zero-copy presenters via GpuVideoFrame.AddRef   "
            + $"decode: {broadcastFps,5:F1} fps   "
            + $"each frame's refcount peaks at 4 (decode + 3 panes), freed at the last release.";
    }

    private string BranchErrorSuffix(int branchIndex)
    {
        // Per-branch error tracking was removed alongside the legacy
        // BroadcastDiagnostics in Crossbar ADR-0014 Phase 4. The fan-out's
        // try/catch increments _broadcastBranchErrors as an aggregate
        // counter; recovering per-branch detail would mean a small
        // dedicated state object, deferred until needed.
        return "";
    }

    private async Task TeardownPlayerAsync()
    {
        // Clear the chrome binding first so its sub-controls don't
        // touch a player mid-dispose.
        PlayerChrome.MediaPlayer = null;
        if (_player is not null)
        {
            try
            {
                await _player.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Player teardown threw");
            }
            _player = null;
        }
        _pane1Sink = null;

        // caller-owned audio sink (the old DI path
        // had the container dispose it). DisposeAsync releases the
        // OpenAL device handle.
        if (_audioSink is not null)
        {
            try
            {
                await _audioSink.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Audio sink teardown threw");
            }
            _audioSink = null;
        }
    }

    private bool _isClosing;

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosing)
            return;
        e.Cancel = true;
        _isClosing = true;
        Closing -= OnWindowClosing;

        _statsTimer?.Stop();
        _windowCts.Cancel();
        await TeardownPlayerAsync();

        // Player teardown stopped the pump (no more fan-out PresentAsync calls),
        // so the presenters can now be disposed. Each view owns its sink; its
        // DisposeAsync releases any AddRef'd GpuVideoFrame still pending, which
        // drops the last refs and frees the decode-texture slices.
        if (_gpuPanes is not null)
        {
            foreach (var pane in _gpuPanes)
            {
                try
                {
                    await pane.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "GPU pane teardown threw");
                }
            }
            _gpuPanes = null;
            _gpuSinks = null;
        }

        _yoloDetector?.Dispose();
        _yoloDetector = null;
        _loggerFactory?.Dispose();
        _windowCts.Dispose();
        Close();
    }
}
