using System.Collections.Concurrent;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FrameFlow.Audio;
using FrameFlow.Media;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Avalonia;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Decoding;
using FrameFlow.Player;
using FrameFlow.Video;
using FrameFlow.Whisper;
using FrameFlow.Yolo;
using FrameFlow.Inference.Cuda;
using Microsoft.Extensions.Logging;
using FrameFlow.Graph;

namespace FrameFlow.Examples.LiveCaptioning;

/// <summary>
/// Live-captioning + object-detection demo on the player surface
/// (<see cref="MediaPlayer"/>).
/// The decoded video stream fans out at the edge into two consumers
/// running at their own rates: a display branch carrying frames to the
/// presenter with overlaid captions, and an inference branch running
/// YOLOv8 behind a <c>LatestWins(1)</c> edge that drops whatever
/// arrives mid-detection. Detections rejoin the display path by frame
/// PTS through a <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}"/>
/// (the sync-window-join ADR). Captions come from the existing
/// <see cref="FrameFlow.Whisper"/> caption pipeline, fed over a bounded
/// channel from a tap on the audio side.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> Builds the demo on
/// <see cref="MediaPlayer"/> without rewriting the Whisper /
/// caption / YOLO bits. Strategy:
/// </para>
/// <list type="bullet">
///   <item><b>Audio tap.</b> A 1→1 audio operator
///     AddRefs each decoded buffer and pushes it through the
///     <see cref="PipelineBridge{T}"/> writer. <c>PipelineBridge</c>
///     is a domain-agnostic Crossbar primitive that survives the
///     substrate change; it bridges from the audio
///     flow to the unchanged FrameFlow.Whisper caption pipeline.
///     OpenAL playback path is untouched — the operator is a
///     pass-through tap.</item>
///   <item><b>Caption pipeline.</b> Unchanged from the old version:
///     <c>Resample → TranscribeWithWhisper → SplitOnPunctuation →
///     AnimatedReveal</c> off the bridge's <c>Pipeline</c>. The
///     extensions take the old <c>FramePipeline&lt;T&gt;</c> but
///     that's fine — they're consuming domain types, not substrate
///     types.</item>
///   <item><b>Caption timeline.</b> A background task drains the
///     caption pipeline into a <see cref="CaptionTimeline"/> stamped
///     with the latest video PTS. Replaces the old
///     <c>OverlayOnto</c>'s metadata-bag pattern with a shared
///     concurrent state — the substrate doesn't have a metadata
///     bag (per Crossbar ADR-0014 §"What goes away").</item>
///   <item><b>Detection branch.</b> The decoded video output fans out
///     to a YOLO operator on a <c>LatestWins(1)</c> cloner edge. That
///     edge's drop-oldest is the skip-while-busy behaviour: frames
///     arriving during a detection are dropped and disposed by the
///     channel. Detections rejoin the display path through a
///     <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}"/> keyed on
///     frame PTS (the sync-window-join ADR), so the overlay tracks the
///     than whenever inference happened to finish.</item>
///   <item><b>Terminal sink.</b> Per frame: reads
///     <see cref="CaptionTimeline"/> for the frame's PTS, marshals the
///     active captions to the UI thread, and presents (skip-while-busy
///     in CPU mode, zero-copy in GPU mode).</item>
/// </list>
/// <para>
/// <b>Three concurrent rates preserved.</b> Audio runs at decode rate
/// through OpenAL. Video is clock-paced before the configurator sees
/// it — <c>SubstrateSession.BuildGraph</c> inserts <c>PaceUntil</c>
/// ahead of the configurator-only path — so both the display branch and
/// the detection branch see frames at presentation rate. Detection then
/// runs at its own much slower rate, bounded by the LatestWins edge
/// rather than by a flag.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _windowCts = new();
    private ILoggerFactory? _loggerFactory;
    private ILogger<MainWindow>? _logger;

    // Models — loaded once and reused across files.
    private string? _whisperModelPath;
    private Yolov8Detector? _yoloDetector;

    // Per-playback resources.
    private IMediaPlayer? _player;
    private OpenAlAudioSink? _audioSink;
    private Channel<PcmAudioBufferRef>? _pcmBridge;
    private Graph.Graph? _captionGraph;
    private CaptionTimeline? _captionTimeline;
    private Task? _captionGraphTask;
    private CancellationTokenSource? _captionPumpCts;

    // YOLO detection runs as a graph branch on a LatestWins(1) edge; the
    // edge's drop-oldest gives one-detection-at-a-time for free, so there is
    // no busy flag here any more. Results rejoin the display path
    // through a sync join keyed on frame PTS.
    private static readonly TimeSpan DetectionWindow = TimeSpan.FromSeconds(2);

    // View-sink presentation state. If the previous
    // AvaloniaVideoSink.PresentAsync is still running (UI-thread bursts
    // after seek, large-frame upload contention, etc.), drop the new
    // frame rather than await it. Without this, the SinkNode body blocks while the
    // present completes; the bounded video-source edge (cap=1)
    // fills; the video decoder's packet queue (cap=64) fills; the
    // shared demux pump blocks on SendPacketAsync(video); the
    // audio decoder runs dry; audio crawls or cuts out. Symptom
    // in pre-fix logs: post-seek, audio advanced ~1s of media in
    // ~5s wallclock with underruns=0 (intermittent feed, not a
    // clean stop). See docs/DEFERRED_WORK.md for the longer-term split-
    // demux-pump fix.
    private int _presentBusy;
    private long _droppedPresentBusyCount;

    public string? StartupFilePath { get; set; }
    public string? StartupLogFilePath { get; set; }

    /// <summary>
    /// When true (and on Windows), route the display branch through the Windows
    /// zero-copy composition-interop presenter: the decoder yields one
    /// <see cref="GpuVideoFrame"/> per picture, the display branch gets an
    /// <c>AddRef</c>'d ref (presented zero-copy, no WriteableBitmap upload), and the
    /// YOLO branch gets a CPU readback for its CPU-side preprocessing. Set by
    /// <c>--gpu</c>. The inference readback stays until the GPU-inference path lands
    /// (ADR-0038 Phase B).
    /// </summary>
    public bool UseGpu { get; set; }

    /// <summary>When &gt; 0, auto-close after N seconds (<c>--exit-after</c>) for autonomous runs.</summary>
    public int ExitAfterSeconds { get; set; }

    // Resolved in OnLoaded: UseGpu gated on actually being on Windows.
    private bool _useGpu;

    // GPU display mode: the zero-copy presenter that replaces the CPU VideoView, and
    // its sink (fed an AddRef'd GpuVideoFrame per picture by the fan-out). Null in CPU mode.
    private CompositionInteropVideoView? _gpuView;
    private CompositionInteropVideoSink? _gpuSink;
    private bool _warnedNonGpuFrame;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
        // Chrome publishes file picks (Open button) and forwards them
        // through the same path as the CLI-arg autoplay — tear down
        // any prior player and build a fresh pipeline for the new
        // file. Without this hook the Open button would be a no-op.
        PlayerChrome.FileOpenRequested += OnChromeFileOpenRequested;
    }

    private async void OnChromeFileOpenRequested(object? sender, FileOpenRequestedEventArgs e)
    {
        try
        {
            await OpenFileAsync(e.FilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Chrome-driven file open failed for {File}", e.FilePath);
            SetStatus($"Open failed: {ex.GetType().Name}", "#d07a7a");
        }
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new TextBoxLoggerProvider(LogOutput, LogLevel.Information));
            if (!string.IsNullOrEmpty(StartupLogFilePath))
                b.AddProvider(new FileLoggerProvider(ExampleLogPaths.Resolve(StartupLogFilePath), LogLevel.Debug));
        });
        _logger = _loggerFactory.CreateLogger<MainWindow>();
        VideoView.LoggerFactory = _loggerFactory;

        // --gpu: swap the CPU VideoView for the Windows zero-copy presenter, dropped
        // into the same Grid cell beneath the detection + caption overlays.
        _useGpu = UseGpu && OperatingSystem.IsWindows();
        if (UseGpu && !_useGpu)
            _logger.LogWarning("--gpu requested but not on Windows; using the CPU video surface.");
        if (_useGpu)
            SetupGpuDisplay();

        if (ExitAfterSeconds > 0)
            ScheduleAutoExit(ExitAfterSeconds);

        SetStatus("Loading models…", "#d0c07a");

        // Whisper is required (captioning is the headline feature).
        // YOLO is optional — on a machine without CUDA + cuDNN the
        // detector throws inside ORT's CUDA EP append. We swallow that
        // and continue in captioning-only mode rather than killing the
        // whole app, which is what the previous shared try/catch did.
        try
        {
            _whisperModelPath = await WhisperModelDownloader
                .EnsureModelAvailableAsync(
                    _windowCts.Token,
                    logger: _loggerFactory.CreateLogger(nameof(WhisperModelDownloader))
                );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Whisper model.");
            SetStatus("Whisper model load failed (see log)", "#d07a7a");
            return;
        }

        try
        {
            _yoloDetector = await Yolov8Detector.CreateAsync(
                sessionFactory: path => new CudaInferenceSession(path),
                ct: _windowCts.Token,
                loggerFactory: _loggerFactory);
            _logger.LogInformation(
                "Whisper + YOLOv8 ready. WhisperModelPath={ModelPath}",
                _whisperModelPath
            );
        }
        catch (Exception ex)
        {
            // Most common cause: ORT CUDA EP can't load because the
            // CUDA Toolkit + cuDNN aren't installed system-wide. The
            // captioning pipeline is independent (Whisper.net is
            // CPU-only), so degrade gracefully.
            _logger.LogWarning(
                ex,
                "YOLOv8 unavailable; running in captioning-only mode (no detection overlay)."
            );
            _yoloDetector = null;
        }

        if (!string.IsNullOrEmpty(StartupFilePath) && File.Exists(StartupFilePath))
        {
            await OpenFileAsync(StartupFilePath);
        }
        else
        {
            SetStatus("Ready — click Open or pass a file on the command line.", "#888");
        }
    }

    /// <summary>
    /// GPU display mode: drop a <see cref="CompositionInteropVideoView"/> into the video
    /// Grid cell beneath the detection + caption overlays, wire its logger + sink, and
    /// hide the now-unused CPU <c>VideoView</c>. The sink is what the AddRef fork feeds.
    /// </summary>
    private void SetupGpuDisplay()
    {
        _logger!.LogInformation(
            "GPU display mode: zero-copy composition-interop presenter (AddRef fork — "
                + "GPU frame to display, CPU readback to YOLO)."
        );

        var view = new CompositionInteropVideoView
        {
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch,
        };
        // Index 0 renders first (bottom), so it sits under DetectionOverlay + captions.
        VideoLayer.Children.Insert(0, view);
        VideoView.IsVisible = false;

        view.Initialize(_loggerFactory!);
        _gpuView = view;
        _gpuSink = view.EnsureSink();
    }

    /// <summary>One-shot auto-close after N seconds for autonomous run-and-read-the-log loops.</summary>
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

    private async Task OpenFileAsync(string path)
    {
        if (_loggerFactory is null || _logger is null || _whisperModelPath is null)
            return;
        // _yoloDetector may be null — captioning-only mode. The video
        // sink-node below branches on it.

        await TeardownPlayerAsync();

        Title = $"FrameFlow — Live Captioning + Detection — {Path.GetFileName(path)}";
        SetStatus("Opening…", "#d0c07a");

        // ── Audio side: bridge to the Whisper graph ──
        //
        // The substrate has no FramePipeline<T> / PipelineBridge —
        // we bridge via a bounded Channel<PcmAudioBufferRef>. The
        // audio configurator AddRefs each PCM buffer into the channel;
        // a separate FrameFlow.Graph.Graph below consumes from the channel
        // and runs Resample → Whisper → SplitOnPunctuation →
        // AnimatedReveal → caption sink.
        //
        // DropOldest semantics: ASR back-pressure can't stall the
        // OpenAL audio path; under load, the oldest queued buffer
        // gets dropped before the newest. Whisper may miss buffers
        // but the speakers don't stutter.
        var pcmBridge = Channel.CreateBounded<PcmAudioBufferRef>(
            new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }
        );
        _pcmBridge = pcmBridge;

        // ── Caption timeline + queue (replaces OverlayOnto metadata) ──
        //
        // The substrate has no metadata bag, so we can't attach
        // ActiveCaptions to each video frame's metadata as OverlayOnto
        // did. Instead: a shared CaptionTimeline stamped with the
        // latest video PTS, queried per-frame by the terminal sink
        // node below.
        var captionQueue = new ConcurrentQueue<Caption>();
        var captionTimeline = new CaptionTimeline(
            displayDuration: TimeSpan.FromSeconds(6),
            maxStackedLines: 1
        );
        _captionTimeline = captionTimeline;

        // Caption graph: source pulls AddRef'd PCM buffers off the
        // channel; the chain runs the captioning operators; the
        // terminal sink enqueues each finished Caption into
        // captionQueue for the video-side fan-out to read.
        _captionPumpCts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
        var captionPumpCt = _captionPumpCts.Token;

        var captionGraph = new FrameFlow.Graph.Graph();
        var captionSource = new SourceNode<PcmAudioBufferRef>(
            "whisper-channel-source",
            async (ct) =>
            {
                try
                {
                    while (await pcmBridge.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        if (pcmBridge.Reader.TryRead(out var buf))
                            return buf;
                    }
                    return null;
                }
                catch (ChannelClosedException)
                {
                    return null;
                }
            }
        );
        captionGraph.Pipeline(captionSource)
            .Then(AudioOperators.Resample("whisper-resample", targetSampleRate: 16_000, targetChannels: 1))
            .Then(WhisperOperators.TranscribeWithWhisper(
                "whisper-transcribe",
                _whisperModelPath,
                new WhisperOptions(Language: "en", WindowSize: TimeSpan.FromSeconds(2.5))))
            .Then(CaptionOperators.SplitOnPunctuation("split-on-punctuation"))
            .Then(CaptionOperators.AnimatedReveal("animated-reveal", wordsPerSecond: 5))
            .To(new SinkNode<CaptionRef>(
                "caption-enqueue",
                (item, _) =>
                {
                    captionQueue.Enqueue(item.Value);
                    return ValueTask.CompletedTask;
                }));

        _captionGraph = captionGraph;
        _captionGraphTask = Task.Run(
            async () =>
            {
                try
                {
                    await captionGraph.RunAsync(captionPumpCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (captionPumpCt.IsCancellationRequested)
                {
                    // Expected on teardown.
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Caption graph faulted.");
                }
            },
            CancellationToken.None
        );

        // ── Sinks (substrate) ──
        _audioSink = new OpenAlAudioSink(_loggerFactory.CreateLogger<OpenAlAudioSink>());
        // CPU mode: the stock Avalonia view-sink (WriteableBitmap upload). GPU mode: the
        // composition-interop presenter's sink, fed an AddRef'd GpuVideoFrame per picture.
        IVideoSink viewSink = _useGpu ? _gpuSink! : VideoView.EnsureSink();
        var yoloDetector = _yoloDetector; // capture for the closure
        if (yoloDetector is null)
            DetectionOverlay.IsVisible = false;

        // Reset the present busy flag. It must start at 0 (idle) so the first
        // frame of a fresh playback actually presents — a stuck _presentBusy=1
        // from a prior session would silently drop every frame.
        Interlocked.Exchange(ref _droppedPresentBusyCount, 0);
        Interlocked.Exchange(ref _presentBusy, 0);

        try
        {
            // ── Build the player ──
            //
            // configureAudio: tap each decoded audio buffer for the
            // Whisper bridge before it reaches the OpenAL sink. The
            // 1→1 operator AddRefs the buffer (PcmAudioBuffer supports
            // refcounting, unlike Media.CpuVideoFrame) and pushes the
            // AddRef'd ref into the bridge; the original passes through
            // unchanged to OpenAL.
            //
            // configureVideo: configurator-terminated. The video sink is null
            // because the configurator wires its own topology — the detection
            // branch, the join, and the terminal sink that presents.
            _player = await MediaPlayer.CreateAsync(
                source: MediaSource.FromFile(path),
                videoSink: null, // configurator-terminated — see below
                audioSink: _audioSink,
                hardwareDecodeMode: HardwareDecodeMode.Auto,
                // GPU mode: keep hardware frames on the GPU so the display branch can
                // AddRef one GpuVideoFrame and present it zero-copy.
                yieldHardwareFrames: _useGpu,
                initialRepeatMode: RepeatMode.Off,
                loggerFactory: _loggerFactory,
                configureAudio: chain =>
                    chain.Then(CreateWhisperTapOperator(pcmBridge)),
                configureVideo: chain =>
                {
                    // GPU fork keeps pictures on the GPU (no ConvertPixelFormat) so the
                    // display can present one AddRef'd GpuVideoFrame zero-copy. CPU fork
                    // converts once, feeding both the view sink and YOLO preprocessing.
                    var head = _useGpu
                        ? chain
                        : chain.Then(
                            VideoOperators.ConvertPixelFormat(
                                "caption-convert",
                                PixelFormat.Bgra32
                            )
                        );

                    var terminal = _useGpu
                        ? CreateGpuTerminalSink(viewSink, captionQueue, captionTimeline)
                        : CreateCpuTerminalSink(viewSink, captionQueue, captionTimeline);

                    if (yoloDetector is null)
                    {
                        head.To(terminal);
                        return chain;
                    }

                    // ── Detection branch and rejoin ──
                    //
                    // YOLO is a sibling branch on a LatestWins(1) edge. That edge's
                    // drop-oldest IS the skip-while-busy behaviour the hand-rolled
                    // _inferenceBusy flag used to provide: frames arriving while a
                    // detection is still running are dropped and disposed by the
                    // channel, not by an Interlocked dance in the sink body.
                    //
                    // The join then pairs each displayed frame with the newest
                    // detection at or before its PTS, so the overlay tracks the
                    // picture rather than being posted from whenever inference
                    // happened to finish.
                    var graph = head.Graph;
                    var detect = CreateDetectOperator(yoloDetector);
                    var join = CreateDetectionJoin();

                    // Primary first: it carries no cloner, so it inherits the incoming
                    // ref and the sibling branch is the one that clones (ADR-0054).
                    graph.Connect(head.Output, join.Primary);
                    graph.Connect(
                        head.Output,
                        detect.Input,
                        EdgeOptions.LatestWins(1).WithCloner<VideoFrameRef>(CloneForInference)
                    );
                    graph.Connect(detect.Output, join.Secondary, EdgeOptions.Buffered(4));
                    graph.Pipeline(join.Output).To(terminal);
                    return chain;
                },
                cancellationToken: _windowCts.Token
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open {File}", Path.GetFileName(path));
            SetStatus("Open failed (see log)", "#d07a7a");
            pcmBridge.Writer.TryComplete();
            return;
        }

        // Hand the freshly built player to chrome — its sub-controls
        // bind off this property. Mode caveats (no audio, no detection)
        // go to the AppStatusPill; player state is chrome's job via
        // FrameFlowStateBadge.
        PlayerChrome.MediaPlayer = _player;

        var hasAudio = _player.MediaInfo.AudioStreams.Count > 0;
        var caveats = new List<string>();
        if (!hasAudio)
            caveats.Add("no audio — captioning disabled");
        if (_yoloDetector is null)
            caveats.Add("no detection (YOLO unavailable)");
        if (caveats.Count > 0)
            SetStatus("⚠ " + string.Join(" · ", caveats), "#d0c07a");
        else
            HideStatus();

        var played = await _player.PlayAsync(_windowCts.Token);
        if (!played.IsSuccess)
            SetStatus($"⚠ playback refused — {played.Error.Message}", "#d08a8a");
    }

    /// <summary>
    /// One frame's YOLO detections, keyed by that frame's PTS. Point-valued on
    /// the media timeline, which is why the join matches it with
    /// <see cref="SyncMatch.MostRecentAtOrBefore"/> rather than
    /// <see cref="SyncMatch.Within"/> — a zero-width interval never matches
    /// under the latter.
    /// </summary>
    private sealed record DetectionSet(
        TimeSpan Pts,
        IReadOnlyList<Detection> Detections,
        int Width,
        int Height
    );

    /// <summary>
    /// Per-branch cloner for the inference edge. A GPU picture is refcountable,
    /// so the branch takes its own ref; a one-shot CPU frame (decoder or
    /// converter output) has to be deep-copied instead, per ADR-0054. Handles
    /// the <c>--gpu</c> fallback where hardware decode did not engage and the
    /// decoder yielded CPU frames after all.
    /// </summary>
    private static VideoFrameRef CloneForInference(VideoFrameRef frame) =>
        frame.Frame is GpuVideoFrame
            ? (VideoFrameRef)frame.AddRef()
            : new VideoFrameRef(frame.Frame.CloneCpu());

    /// <summary>
    /// The detection branch's operator: one YOLO pass per frame the
    /// LatestWins(1) edge lets through, emitting detections stamped with that
    /// frame's PTS. Holding the pump for the duration of the inference is what
    /// makes the upstream edge drop frames, which is the intended
    /// one-at-a-time behaviour.
    /// </summary>
    private OperatorNode<VideoFrameRef, RefBox<DetectionSet>> CreateDetectOperator(
        Yolov8Detector detector
    )
    {
        return new OperatorNode<VideoFrameRef, RefBox<DetectionSet>>(
            "yolo-detect",
            async (item, ct) =>
            {
                try
                {
                    // In --gpu mode the terminal sink presents nothing unless the
                    // decoder actually yielded a GpuVideoFrame. Skip inference on
                    // the software-fallback path for the same reason: detections
                    // over a view that never draws are worse than none, and
                    // before this branch existed the sink's guard sat ahead of
                    // both present and inference. The sink logs the one warning.
                    if (_useGpu && item.Frame is not GpuVideoFrame)
                        return null;

                    // YOLO preprocesses on the CPU, so a GPU picture reads back
                    // first. ADR-0038 Phase B removes this round-trip.
                    //
                    // Detach and release the GPU frame as soon as the readback
                    // has its copy: holding it across the whole Detect would pin
                    // a second hwframe-pool slice for the duration, where the
                    // display branch already holds one. ADR-0057 ties one extra
                    // held lease to pool exhaustion.
                    if (item.Frame is GpuVideoFrame)
                    {
                        CpuVideoFrame cpu;
                        using (var gpu = (GpuVideoFrame)item.Detach()!)
                            cpu = gpu.ReadbackToCpuBgra32();
                        using (cpu)
                            return await DetectAsync(detector, cpu, ct).ConfigureAwait(false);
                    }
                    return await DetectAsync(detector, item.Frame, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Drop this frame's detections rather than faulting playback.
                    // The overlay keeps the previous set until it ages past
                    // MaxStaleness, at which point the join's null match clears it.
                    _logger?.LogWarning(ex, "YOLO inference faulted on a frame.");
                    return null;
                }
            }
        );

        static async ValueTask<RefBox<DetectionSet>?> DetectAsync(
            Yolov8Detector detector,
            IVideoFrame frame,
            CancellationToken ct
        )
        {
            var pts = frame.Pts;
            var width = frame.Width;
            var height = frame.Height;
            var detections = await Task.Run(() => detector.Detect(frame), ct)
                .ConfigureAwait(false);
            return RefBox.Of(new DetectionSet(pts, detections, width, height));
        }
    }

    /// <summary>
    /// Rejoins the detection branch onto the display path. Every frame fires
    /// the body exactly once, paired with the newest detection at or before its
    /// PTS, or with <see langword="null"/> when none is in the window — before
    /// the first detection lands, or after the last one ages past
    /// <see cref="SyncJoinNode{TPrimary, TSecondary, TOut}.MaxStaleness"/>.
    /// The frame passes through untouched; the only side effect is posting the
    /// detections to the overlay.
    /// </summary>
    /// <remarks>
    /// The body posts only when the matched set changes. The join fires per
    /// frame and detection is several times slower, so posting unconditionally
    /// would re-marshal the same set to the UI thread for every frame in
    /// between. A null match posts an empty set once, which clears the boxes
    /// rather than leaving the last ones drawn over frames they do not
    /// describe.
    /// </remarks>
    private SyncJoinNode<VideoFrameRef, RefBox<DetectionSet>, VideoFrameRef>
        CreateDetectionJoin()
    {
        // Owned by the join's pump, which is single-threaded, so no interlock.
        DetectionSet? lastPosted = null;

        return new(
            "detection-overlay",
            (frame, detected, _) =>
            {
                var set = detected?.Value;
                if (!ReferenceEquals(set, lastPosted))
                {
                    lastPosted = set;
                    var detections = set?.Detections ?? Array.Empty<Detection>();
                    var width = set?.Width ?? frame.Frame.Width;
                    var height = set?.Height ?? frame.Frame.Height;
                    Dispatcher.UIThread.Post(
                        () => DetectionOverlay.Update(detections, width, height),
                        DispatcherPriority.Background
                    );
                }

                // Pass-through: the substrate forwards this same ref downstream
                // rather than disposing and re-wrapping.
                return ValueTask.FromResult<VideoFrameRef?>(frame);
            },
            new SyncJoinKeys<VideoFrameRef, RefBox<DetectionSet>>(
                f => f.Frame.Pts,
                s => (s.Value.Pts, s.Value.Pts)
            ),
            SyncMatch.MostRecentAtOrBefore,
            window: DetectionWindow,
            maxStaleness: DetectionWindow
        );
    }

    /// <summary>
    /// CPU-mode terminal sink: caption timeline upkeep plus a skip-while-busy
    /// present. Detection overlay updates happen upstream in the join.
    /// </summary>
    private SinkNode<VideoFrameRef> CreateCpuTerminalSink(
        IVideoSink viewSink,
        ConcurrentQueue<Caption> captionQueue,
        CaptionTimeline captionTimeline
    ) =>
        new(
            "caption+present",
            (item, ct) =>
            {
                var pts = item.Frame.Pts;
                PublishCaptions(captionQueue, captionTimeline, pts);

                // Fire-and-forget, skip-while-busy. Awaiting the present here
                // would let UI-thread bursts back-pressure the shared demux pump
                // and starve audio — see the _presentBusy field comment.
                //
                // Detach rather than CloneCpu: this sink is now the sole holder
                // of the converter's one-shot frame, so ownership transfers to
                // PresentAsync with no copy — 8.3 MB per frame at 1080p. The
                // clone was there for the old in-sink fan-out, which the
                // detection branch replaced.
                if (Interlocked.CompareExchange(ref _presentBusy, 1, 0) == 0)
                {
                    IVideoFrame? presented = null;
                    try
                    {
                        presented = item.Detach()!;
                        var toPresent = presented;
                        _ = Task.Run(() => RunPresentAsync(viewSink, toPresent, ct), ct);
                    }
                    catch
                    {
                        // The frame is detached from the wrapper, so the
                        // substrate will not dispose it for us.
                        presented?.Dispose();
                        Interlocked.Exchange(ref _presentBusy, 0);
                        Interlocked.Increment(ref _droppedPresentBusyCount);
                    }
                }
                else
                {
                    Interlocked.Increment(ref _droppedPresentBusyCount);
                }

                return ValueTask.CompletedTask;
            }
        );

    /// <summary>
    /// GPU-mode terminal sink: caption timeline upkeep plus a zero-copy present
    /// of the decoder's own <see cref="GpuVideoFrame"/>.
    /// </summary>
    private SinkNode<VideoFrameRef> CreateGpuTerminalSink(
        IVideoSink viewSink,
        ConcurrentQueue<Caption> captionQueue,
        CaptionTimeline captionTimeline
    ) =>
        new(
            "caption+present-gpu",
            async (item, ct) =>
            {
                var pts = item.Frame.Pts;
                PublishCaptions(captionQueue, captionTimeline, pts);

                if (item.Frame is not GpuVideoFrame)
                {
                    if (!_warnedNonGpuFrame)
                    {
                        _warnedNonGpuFrame = true;
                        _logger?.LogWarning(
                            "GPU mode: decoder yielded {Type} (not a D3D11VA "
                                + "GpuVideoFrame) — hardware decode didn't engage. Run on a "
                                + "box with D3D11VA, or drop --gpu for the CPU display.",
                            item.Frame.GetType().Name
                        );
                    }
                    return;
                }

                // Zero-copy. PresentAsync is non-blocking (latest-wins), so
                // awaiting it never back-pressures the shared demux pump.
                await viewSink.PresentAsync(item.Frame.AddRef(), ct).ConfigureAwait(false);
            }
        );

    /// <summary>
    /// Drains newly-arrived captions into the timeline stamped with the current
    /// frame's PTS, then marshals the active set to the UI thread.
    /// </summary>
    private void PublishCaptions(
        ConcurrentQueue<Caption> captionQueue,
        CaptionTimeline captionTimeline,
        TimeSpan pts
    )
    {
        while (captionQueue.TryDequeue(out var caption))
            captionTimeline.Add(caption, pts);

        var active = new ActiveCaptions(captionTimeline.GetActive(pts));
        Dispatcher.UIThread.Post(() => UpdateCaptionsUi(active), DispatcherPriority.Background);
    }

    /// <summary>
    /// 1→1 audio operator that taps each decoded PCM buffer for the
    /// Whisper graph. Wraps the buffer in a fresh AddRef'd
    /// <see cref="PcmAudioBufferRef"/> and writes it to the bridge
    /// channel; the original buffer ref continues downstream to the
    /// OpenAL sink. The bridge channel's DropOldest policy means
    /// Whisper-side back-pressure can't stall the OpenAL audio path.
    /// </summary>
    private static OperatorNode<PcmAudioBufferRef, PcmAudioBufferRef> CreateWhisperTapOperator(
        Channel<PcmAudioBufferRef> bridge
    )
    {
        return new OperatorNode<PcmAudioBufferRef, PcmAudioBufferRef>(
            "whisper-tap",
            (item, ct) =>
            {
                // AddRef the underlying buffer, wrap it in a new
                // PcmAudioBufferRef, and write to the bridge channel
                // (non-blocking via DropOldest policy).
                var tap = new PcmAudioBufferRef((PcmAudioBuffer)item.Buffer.AddRef());
                if (!bridge.Writer.TryWrite(tap))
                {
                    // Channel writer closed (teardown race) — dispose
                    // the AddRef so it doesn't leak.
                    tap.Dispose();
                }
                return ValueTask.FromResult<PcmAudioBufferRef?>(item);
            }
        );
    }


    /// <summary>
    /// Fire-and-forget present worker for the view-sink branch: hands
    /// the cloned frame to <paramref name="sink"/> via
    /// <see cref="IVideoSink.PresentAsync"/>, lets the sink dispose
    /// it per the sink-owns-input contract, and clears
    /// <see cref="_presentBusy"/> when done so the next frame can
    /// fire. Errors are logged at warning and don't crash the
    /// SinkNode body.
    /// </summary>
    private async Task RunPresentAsync(
        IVideoSink sink,
        IVideoFrame frame,
        CancellationToken ct
    )
    {
        try
        {
            await sink.PresentAsync(frame, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on teardown / seek. Sink is contracted to
            // dispose the frame even on cancel, so no double-dispose
            // here.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "View present faulted on a frame.");
        }
        finally
        {
            Interlocked.Exchange(ref _presentBusy, 0);
        }
    }

    /// <summary>
    /// Presenter-side UI hook invoked by the terminal sink for each
    /// video frame: receives the captions currently active for that
    /// frame's PTS, already marshalled to the UI thread via
    /// <see cref="Dispatcher.UIThread.Post"/>.
    /// </summary>
    private void UpdateCaptionsUi(ActiveCaptions active)
    {
        var captionText = active.Captions.Count switch
        {
            0 => string.Empty,
            1 => active.Captions[0].Text,
            _ => string.Join('\n', active.Captions.Select(c => c.Text)),
        };
        if (CaptionText.Text != captionText)
        {
            CaptionText.Text = captionText;
            CaptionBackdrop.IsVisible = !string.IsNullOrEmpty(captionText);
        }
    }

    /// <summary>
    /// Show an app-level message in the top-left pill. Use for things
    /// the chrome's <see cref="FrameFlowStateBadge"/> can't surface:
    /// model loading, error reasons, mode caveats (captioning-only,
    /// no-detection). Do NOT use for player state — chrome already
    /// shows Playing/Paused/Ended/Error in real time.
    /// </summary>
    private void SetStatus(string text, string colorHex)
    {
        AppStatusText.Text = text;
        AppStatusText.Foreground = new global::Avalonia.Media.SolidColorBrush(
            global::Avalonia.Media.Color.Parse(colorHex)
        );
        AppStatusPill.IsVisible = true;
    }

    /// <summary>
    /// Hide the app-status pill (e.g. once playback starts with no
    /// mode caveats to display).
    /// </summary>
    private void HideStatus() => AppStatusPill.IsVisible = false;

    /// <summary>
    /// Disposes the current playback graph (player + audio sink +
    /// PCM bridge + caption pump) without touching the app-level
    /// model objects (Whisper model path, YOLO detector) which are
    /// reused across files. Idempotent.
    /// </summary>
    private async Task TeardownPlayerAsync()
    {
        // Detach chrome from the doomed player first so its sub-
        // controls don't poke a half-disposed player.
        PlayerChrome.MediaPlayer = null;

        // Stop the caption pump first: cancelling the linked CTS
        // makes the captionPipeline.RunAsync exit; the bridge's
        // pipeline naturally completes when the writer is closed.
        if (_captionPumpCts is not null)
        {
            try
            {
                _captionPumpCts.Cancel();
            }
            catch { }
        }

        // Close the bridge writer so the SourceNode's WaitToReadAsync
        // observes channel completion and the caption graph drains
        // naturally even if the cancellation didn't reach it first.
        if (_pcmBridge is not null)
        {
            _pcmBridge.Writer.TryComplete();
            _pcmBridge = null;
        }

        // Await the caption graph after cancellation + bridge close so
        // we don't race its sink callback writing to a disposed queue.
        if (_captionGraphTask is not null)
        {
            try
            {
                await _captionGraphTask.ConfigureAwait(false);
            }
            catch { /* swallow — diagnostics already logged */ }
            _captionGraphTask = null;
        }
        _captionGraph = null;
        _captionPumpCts?.Dispose();
        _captionPumpCts = null;

        if (_player is not null)
        {
            try
            {
                await _player.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Player dispose threw");
            }
            _player = null;
        }

        if (_audioSink is not null)
        {
            try
            {
                await _audioSink.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Audio sink dispose threw");
            }
            _audioSink = null;
        }

        _captionTimeline = null;
    }

    private bool _isClosing;

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosing)
            return;
        e.Cancel = true;
        _isClosing = true;
        Closing -= OnWindowClosing;

        _windowCts.Cancel();

        await TeardownPlayerAsync();

        // Player teardown stopped the pump (no more fan-out PresentAsync), so the
        // presenter can be disposed; its owned sink releases any pending GpuVideoFrame,
        // dropping the last ref on the decode-texture slice.
        if (_gpuView is not null)
        {
            try
            {
                await _gpuView.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "GPU presenter teardown threw");
            }
            _gpuView = null;
            _gpuSink = null;
        }

        _yoloDetector?.Dispose();
        _loggerFactory?.Dispose();
        _windowCts.Dispose();

        Close();
    }
}
