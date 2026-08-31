using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FrameFlow.Graph;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Avalonia;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using FrameFlow.Video;
using FrameFlow.Yolo;
using FrameFlow.Inference;
using FrameFlow.Inference.Dml;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.Multicast.Dml;

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

        // YOLO bootstrap for pane 2. Mirrors LiveCaptioning's pattern:
        // detector creation can throw (missing CUDA EP, missing ONNX
        // model download, ORT init failure). We swallow the throw and
        // flip pane 2 to Unavailable rather than killing the app —
        // panes 1 and 3 are independent of YOLO, so a broken detector
        // shouldn't take down the whole multicast.
        //
        // --break-yolo forces the failure path so we can demonstrate
        // the resilience claim deterministically.
        if (BreakYolo)
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
                StartupClock.Mark("YOLO bootstrap: starting (DirectML EP)");
                // Build the EP-resolving factory (same shape a signage host uses) so
                // the load reports its sub-phases — provider probe → session
                // open → warmup — through the optional progress reporter that
                // landed with the session-progress feature.
                var inferenceFactory = InferenceSessionFactoryBuilder.Create(
                    preferred: ExecutionProvider.DirectML,
                    providers: new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
                    {
                        [ExecutionProvider.DirectML] = path =>
                        {
                            StartupClock.Mark("YOLO bootstrap: constructing DmlInferenceSession");
                            return new DmlInferenceSession(path);
                        },
                    },
                    loggerFactory: _loggerFactory);

                // Stamp each load sub-phase on the startup timeline. Synchronous
                // reporter (not System.Progress) so a mark lands at the instant
                // the phase occurs rather than after the awaited load resumes.
                var loadProgress = new SyncProgress<InferenceSessionProgress>(p =>
                    StartupClock.Mark(
                        $"YOLO bootstrap: {p.Phase}"
                        + (p.Provider is { } ep ? $" ({ep})" : string.Empty)
                        + (p.Message is { } m ? $" — {m}" : string.Empty)));

                // Offload the load to the thread pool. OnLoaded runs on the
                // Avalonia UI thread and CreateAsync does the DmlInferenceSession
                // construct + warmup *synchronously*; on a warm model cache
                // there's no download await to yield, so without Task.Run the
                // ~600 ms session open freezes the window until the detector is
                // ready. The continuation (SetDetector — a UI-control touch)
                // resumes back on the UI thread. The detector/session aren't
                // thread-affine, so constructing off the UI thread is safe.
                _yoloDetector = await Task.Run(() => Yolov8Detector.CreateAsync(
                    inferenceFactory,
                    ct: _windowCts.Token,
                    loggerFactory: _loggerFactory,
                    progress: loadProgress));
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
                initialRepeatMode: LoopButton.IsChecked == true ? RepeatMode.One : RepeatMode.Off,
                loggerFactory: _loggerFactory,
                configureVideo: chain =>
                {
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
        _yoloDetector?.Dispose();
        _yoloDetector = null;
        _loggerFactory?.Dispose();
        _windowCts.Dispose();
        Close();
    }
}
