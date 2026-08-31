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
/// Live-captioning + multicast object-detection demo on the
/// player surface (<see cref="MediaPlayer"/>).
/// The decoded video stream fans out (via a configurator-terminated
/// SinkNode that clones each frame) into two independent consumers
/// at different rates: a presenter branch that renders frames at
/// display rate with overlaid captions, and an inference branch
/// that runs YOLOv8 at its own (much slower) rate via a skip-while-
/// busy worker. Captions come from the existing
/// <see cref="FrameFlow.Whisper"/> caption pipeline, fed from a
/// <see cref="PipelineBridge{T}"/> on the audio side.
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
///     bag (per ADR-0014 §"What goes away").</item>
///   <item><b>Video fan-out.</b> A configurator-terminated SinkNode
///     (same pattern as <c>AvaloniaMulticast</c>) per-frame: reads
///     <see cref="CaptionTimeline"/> for the frame's PTS, marshals
///     the active captions to the UI thread, clones the frame for
///     the presenter sink, and (skip-while-busy) clones again for
///     the YOLO inference worker.</item>
/// </list>
/// <para>
/// <b>Three concurrent rates preserved.</b> Audio runs at decode
/// rate through OpenAL. Video runs at display rate through the
/// presenter sink (paced via the substrate's <c>PaceUntil</c>
/// operator inserted by the controller? — only when a main
/// <c>videoSink</c> is set; configurator-terminated demos handle
/// pacing themselves if they want it. For this demo we accept
/// decode-rate video so the YOLO inference branch sees frames as
/// fast as they're produced, matching the old broadcast's behaviour
/// where each branch ran at its own pace). YOLO inference is gated
/// by a single skip-while-busy flag — frames arriving while the
/// previous detection is still running are dropped.
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

    // YOLO inference state. _inferenceBusy is 0/1, flipped via
    // Interlocked.CompareExchange so only one detection runs at a
    // time. Per-frame counters live alongside for status display.
    private int _inferenceBusy;
    private long _inferencedFrameCount;
    private long _droppedWhileBusyCount;

    // View-sink presentation state. Mirrors the YOLO skip-while-
    // busy pattern: if the previous AvaloniaVideoSink.PresentAsync
    // is still running (UI-thread bursts after seek, large-frame
    // upload contention, etc.), drop the new frame rather than
    // await it. Without this, the SinkNode body blocks while the
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

        // Reset per-session counters + busy flags. The flags must
        // start at 0 (idle) so the first frame of a fresh playback
        // actually fires both the present and the YOLO branches —
        // a stuck _presentBusy=1 from a prior session would silently
        // drop every frame.
        Interlocked.Exchange(ref _inferencedFrameCount, 0);
        Interlocked.Exchange(ref _droppedWhileBusyCount, 0);
        Interlocked.Exchange(ref _inferenceBusy, 0);
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
            // configureVideo: configurator-terminated. The video sink
            // is null because the terminal SinkNode handles both the
            // presenter (view) and inference (YOLO) branches itself.
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
                    if (_useGpu)
                    {
                        // GPU fork: no ConvertPixelFormat (frames stay GPU NV12). The terminal
                        // sink hands the display its own AddRef'd ref — presented zero-copy off
                        // the pump thread by the presenter's render timer, so the CPU path's
                        // skip-while-busy present workaround isn't needed — and reads back a CPU
                        // copy for YOLO's CPU-side preprocessing.
                        chain.To(
                            new SinkNode<VideoFrameRef>(
                                "caption+fanout-gpu",
                                async (item, ct) =>
                                {
                                    var pts = item.Frame.Pts;
                                    while (captionQueue.TryDequeue(out var c))
                                        captionTimeline.Add(c, pts);
                                    var active = new ActiveCaptions(captionTimeline.GetActive(pts));
                                    Dispatcher.UIThread.Post(
                                        () => UpdateCaptionsUi(active),
                                        DispatcherPriority.Background
                                    );

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

                                    // Display: zero-copy. PresentAsync is non-blocking (latest-wins),
                                    // so awaiting it never back-pressures the shared demux pump.
                                    await viewSink
                                        .PresentAsync(item.Frame.AddRef(), ct)
                                        .ConfigureAwait(false);

                                    // YOLO: hand the worker its own GPU ref; it reads back to CPU
                                    // for preprocessing. Skip-while-busy (inference is the slow path).
                                    if (yoloDetector is not null
                                        && Interlocked.CompareExchange(ref _inferenceBusy, 1, 0) == 0)
                                    {
                                        try
                                        {
                                            var yoloRef = (GpuVideoFrame)item.Frame.AddRef();
                                            _ = Task.Run(
                                                () => RunInferenceGpuAsync(yoloDetector, yoloRef),
                                                ct
                                            );
                                        }
                                        catch
                                        {
                                            Interlocked.Exchange(ref _inferenceBusy, 0);
                                            Interlocked.Increment(ref _droppedWhileBusyCount);
                                        }
                                    }
                                    else if (yoloDetector is not null)
                                    {
                                        Interlocked.Increment(ref _droppedWhileBusyCount);
                                    }
                                }
                            )
                        );
                        return chain;
                    }

                    chain
                        .Then(
                            VideoOperators.ConvertPixelFormat(
                                "caption-convert",
                                PixelFormat.Bgra32
                            )
                        )
                        .To(
                            new SinkNode<VideoFrameRef>(
                                "caption+fanout",
                                async (item, ct) =>
                                {
                                    var pts = item.Frame.Pts;

                                    // Drain newly-arrived captions into
                                    // the timeline, stamped with this
                                    // frame's PTS (replaces
                                    // OverlayOnto's per-frame Enrich
                                    // callback).
                                    while (captionQueue.TryDequeue(out var c))
                                        captionTimeline.Add(c, pts);

                                    var active = new ActiveCaptions(
                                        captionTimeline.GetActive(pts)
                                    );

                                    // Marshal caption text to the UI
                                    // thread (replaces the old
                                    // OnMetadataOnUiThread operator).
                                    Dispatcher.UIThread.Post(
                                        () => UpdateCaptionsUi(active),
                                        DispatcherPriority.Background
                                    );

                                    // Hand frame clone(s) to the
                                    // presenter sink + (when not busy)
                                    // YOLO worker. CloneCpu handles
                                    // one-shot decoder + converter
                                    // frames; the original passes
                                    // through to the substrate's
                                    // standard wrapper-dispose.
                                    //
                                    // BOTH branches are skip-while-busy
                                    // and fire-and-forget. The SinkNode
                                    // body returns immediately once the
                                    // clones are launched. Awaiting the
                                    // present here would let UI-thread
                                    // bursts (post-seek chrome rebuild,
                                    // etc.) back-pressure the shared
                                    // demux pump and starve audio — see
                                    // the _presentBusy field comment.
                                    if (Interlocked.CompareExchange(
                                            ref _presentBusy, 1, 0
                                        ) == 0)
                                    {
                                        try
                                        {
                                            var viewClone = item.Frame.CloneCpu();
                                            _ = Task.Run(
                                                () => RunPresentAsync(
                                                    viewSink,
                                                    viewClone,
                                                    ct
                                                ),
                                                ct
                                            );
                                        }
                                        catch
                                        {
                                            Interlocked.Exchange(ref _presentBusy, 0);
                                            Interlocked.Increment(ref _droppedPresentBusyCount);
                                        }
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref _droppedPresentBusyCount);
                                    }

                                    if (yoloDetector is not null
                                        && Interlocked.CompareExchange(
                                            ref _inferenceBusy, 1, 0
                                        ) == 0)
                                    {
                                        try
                                        {
                                            var yoloClone = item.Frame.CloneCpu();
                                            _ = Task.Run(
                                                () => RunInferenceAsync(yoloDetector, yoloClone),
                                                ct
                                            );
                                        }
                                        catch
                                        {
                                            Interlocked.Exchange(ref _inferenceBusy, 0);
                                            Interlocked.Increment(ref _droppedWhileBusyCount);
                                        }
                                    }
                                    else if (yoloDetector is not null)
                                    {
                                        Interlocked.Increment(ref _droppedWhileBusyCount);
                                    }

                                    // No await needed — both branches are
                                    // fire-and-forget. The trailing await
                                    // keeps the async lambda's compiler-
                                    // generated state machine happy
                                    // without changing semantics.
                                    await ValueTask.CompletedTask;
                                }
                            )
                        );
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

        await _player.PlayAsync(_windowCts.Token);
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

    private async Task RunInferenceAsync(Yolov8Detector detector, IVideoFrame frame)
    {
        try
        {
            var detections = await Task.Run(() => detector.Detect(frame)).ConfigureAwait(false);
            Interlocked.Increment(ref _inferencedFrameCount);
            var w = frame.Width;
            var h = frame.Height;
            Dispatcher.UIThread.Post(
                () => DetectionOverlay.Update(detections, w, h),
                DispatcherPriority.Background
            );
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "YOLO inference faulted on a frame.");
        }
        finally
        {
            frame.Dispose();
            Interlocked.Exchange(ref _inferenceBusy, 0);
        }
    }

    /// <summary>
    /// GPU-mode inference worker: reads the AddRef'd <see cref="GpuVideoFrame"/> back to a
    /// CPU BGRA frame (YOLO preprocesses on the CPU), runs detection, and posts the result
    /// to the overlay. Releases the GPU ref + clears the busy flag when done. The readback
    /// is the one GPU→CPU round-trip that remains until the GPU-inference path lands
    /// (ADR-0038 Phase B feeds the D3D11 texture straight to CUDA, dropping it).
    /// </summary>
    private async Task RunInferenceGpuAsync(Yolov8Detector detector, GpuVideoFrame gpuFrame)
    {
        try
        {
            using var cpuFrame = gpuFrame.ReadbackToCpuBgra32();
            var detections = await Task.Run(() => detector.Detect(cpuFrame)).ConfigureAwait(false);
            Interlocked.Increment(ref _inferencedFrameCount);
            var w = cpuFrame.Width;
            var h = cpuFrame.Height;
            Dispatcher.UIThread.Post(
                () => DetectionOverlay.Update(detections, w, h),
                DispatcherPriority.Background
            );
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "YOLO inference (GPU readback) faulted on a frame.");
        }
        finally
        {
            gpuFrame.Dispose(); // release the AddRef'd GPU ref → may free the decode slice
            Interlocked.Exchange(ref _inferenceBusy, 0);
        }
    }

    /// <summary>
    /// Fire-and-forget present worker. Mirrors
    /// <see cref="RunInferenceAsync"/> for the view-sink branch: hands
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
    /// Presenter-side UI hook invoked by the fan-out sink for each
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
