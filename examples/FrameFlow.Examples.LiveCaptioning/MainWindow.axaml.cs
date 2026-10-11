using System.Collections.Concurrent;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FrameFlow.Audio;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Avalonia;
using FrameFlow.Graph;
using FrameFlow.Inference;
using FrameFlow.Inference.Cuda;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using FrameFlow.Whisper;
using FrameFlow.Yolo;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.LiveCaptioning;

/// <summary>
/// Plays a file with YOLOv8 boxes drawn over the picture and Whisper captions along its bottom edge.
/// </summary>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _windowCts = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly PresentedResults<IReadOnlyList<Detection>> _onScreen;
    private Yolov8Detector? _detector;

    // One player for the window: each file opened replaces its queue.
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private IMediaPlayer? _player;

    // Ours, not the player's: it serves each player built here and is disposed after the last.
    private readonly OpenAlAudioSink _audioSink;

    public string? StartupFilePath { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        _loggerFactory = ExampleLogging.CreateFactory(
            "livecaptioning.log",
            onFailure: ex => Title += $"  [no log file: {ex.Message}]"
        );
        _logger = _loggerFactory.CreateLogger<MainWindow>();
        _audioSink = new OpenAlAudioSink(_loggerFactory.CreateLogger<OpenAlAudioSink>());
        VideoView.LoggerFactory = _loggerFactory;

        // Detections arrive before their frame is on screen; this hands each on when it is.
        var videoSink = VideoView.EnsureSink();
        _onScreen = new PresentedResults<IReadOnlyList<Detection>>(videoSink);
        _onScreen.Presented += (_, result) => Dispatcher.UIThread.Post(() => DetectionOverlay.Show(result));
        _onScreen.Cleared += (_, _) => Dispatcher.UIThread.Post(() => DetectionOverlay.Show(null));
        videoSink.FramePresented += OnFramePresented;

        PlayerChrome.FileOpenRequested += async (_, e) => await OpenFileAsync(e.FilePath);
        Closing += OnWindowClosing;
    }

    private async Task OpenFileAsync(string path)
    {
        await _openGate.WaitAsync();
        try
        {
            if (_player is { State: PlaybackState.Error })
            {
                // A player in Error refuses ReplaceAsync, so start again with a new one.
                PlayerChrome.Transport = null;
                await _player.DisposeAsync();
                _player = null;
            }

            if (_player is null)
            {
                var detector = _detector;
                _player = await FrameFlowPlayer
                    .Create()
                    .WithMedia(path)
                    .WithAudioSink(_audioSink)
                    .WithAvaloniaVideoView(VideoView)
                    .WithLogger(_loggerFactory)
                    .ConfigureAudio(chain =>
                        chain.Then(SendToWhisper(_captionAudio.Writer, Interlocked.Increment(ref _audioGraph)))
                    )
                    .ConfigureVideo(chain =>
                        detector is null ? chain : chain.Infer("yolo", detector, _onScreen.Post)
                    )
                    .BuildPlayerAsync(_windowCts.Token);

                PlayerChrome.Transport = _player;
                ShowCaveats(_player.MediaInfo);
                _player.SourceTransitioned.ObserveOnUiThread().Subscribe(t => ShowCaveats(t.MediaInfo));
                _player.ItemFailed.ObserveOnUiThread()
                    .Subscribe(failed => SetStatus($"⚠ {failed.Error.Message}", Warning));
            }
            else
            {
                var replaced = await _player.ReplaceAsync([MediaSource.FromFile(path)], _windowCts.Token);
                if (!replaced.IsSuccess)
                {
                    SetStatus($"⚠ {replaced.Error.Message}", Warning);
                    return;
                }
            }

            Title = $"FrameFlow — Live Captioning + Detection — {Path.GetFileName(path)}";

            // A new player, or a replace while paused or ended, waits for Play; a replace while
            // playing moves to the new file at once.
            if (_player.State is PlaybackState.Paused or PlaybackState.Ended)
            {
                var played = await _player.PlayAsync(_windowCts.Token);
                if (!played.IsSuccess)
                    SetStatus($"⚠ {played.Error.Message}", Warning);
            }
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open {File}", path);
            SetStatus("Open failed (see log)", Error);
        }
        finally
        {
            _openGate.Release();
        }
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SetStatus("Loading models…", Warning);

        string whisperModel;
        try
        {
            whisperModel = await WhisperModelDownloader.EnsureModelAvailableAsync(_logger, _windowCts.Token);
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get the Whisper model.");
            SetStatus("Whisper model load failed (see log)", Error);
            return;
        }

        _captionGraph = Task.Run(() => RunCaptionGraphAsync(whisperModel, _windowCts.Token));
        var detector = await LoadDetectorAsync();
        if (_windowCts.IsCancellationRequested)
        {
            detector?.Dispose(); // the window closed while it loaded
            return;
        }
        _detector = detector;

        PlayerChrome.HasOpenButton = true;
        if (StartupFilePath is not null)
            await OpenFileAsync(StartupFilePath);
        else
            SetStatus("Click Open, or pass a file on the command line.", Neutral);
    }

    /// <summary>YOLOv8 on CUDA, or on the CPU where CUDA cannot open it; null if it cannot be loaded.</summary>
    private async Task<Yolov8Detector?> LoadDetectorAsync()
    {
        var sessions = InferenceSessionFactoryBuilder.Create(
            ExecutionProvider.Cuda,
            new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
            {
                [ExecutionProvider.Cuda] = path => new CudaInferenceSession(path),
                [ExecutionProvider.Cpu] = path => new CpuInferenceSession(path),
            },
            loggerFactory: _loggerFactory
        );

        try
        {
            // Off the UI thread: once the model is downloaded, the session opens synchronously.
            return await Task.Run(() =>
                Yolov8Detector.CreateAsync(sessions, ct: _windowCts.Token, loggerFactory: _loggerFactory)
            );
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YOLOv8 unavailable; playing with captions only.");
            return null;
        }
    }

    // ── Captions ────────────────────────────────────────────────────────
    //
    // Whisper runs in a graph of its own, fed from a tap on the player's audio chain. The player
    // rebuilds its graph after every seek, and Whisper on an audio branch would restart with it.

    // When Whisper falls behind, the oldest buffer is dropped rather than holding up playback.
    private readonly Channel<TappedAudio> _captionAudio = Channel.CreateBounded<TappedAudio>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
        static dropped => dropped.Buffer.Dispose()
    );

    // Counts the player's audio graphs: one per load, seek or file. Audio tapped from an older
    // graph never reaches Whisper, and captions still waiting are dropped once a newer graph's
    // frame is on screen. Whisper's own window can still hold up to 2.5 s from before; it has no reset.
    private int _audioGraph;
    private int _presentedGraph;

    private readonly record struct TappedAudio(int Graph, PcmAudioBuffer Buffer);

    private readonly ConcurrentQueue<Caption> _newCaptions = new();
    private Task _captionGraph = Task.CompletedTask;
    private CaptionTimeline _captions = NewCaptionTimeline();
    private TimeSpan _lastPresented;
    private string _shownCaption = "";

    private static CaptionTimeline NewCaptionTimeline() => new(displayDuration: TimeSpan.FromSeconds(6));

    /// <summary>Passes each audio buffer on to the speakers and sends a reference to it to Whisper.</summary>
    private static OperatorNode<PcmAudioBuffer, PcmAudioBuffer> SendToWhisper(
        ChannelWriter<TappedAudio> whisper,
        int graph
    ) =>
        new(
            "whisper-tap",
            (buffer, _) =>
            {
                buffer.AddRef();
                if (!whisper.TryWrite(new TappedAudio(graph, buffer)))
                    buffer.Dispose(); // the window is closing
                return ValueTask.FromResult<PcmAudioBuffer?>(buffer);
            }
        );

    private async Task RunCaptionGraphAsync(string whisperModel, CancellationToken ct)
    {
        var audio = _captionAudio.Reader;
        var graph = new Graph.Graph();
        graph
            .Pipeline(
                new SourceNode<PcmAudioBuffer>(
                    "whisper-audio",
                    async token =>
                    {
                        while (await audio.WaitToReadAsync(token))
                        {
                            while (audio.TryRead(out var tapped))
                            {
                                if (tapped.Graph == Volatile.Read(ref _audioGraph))
                                    return tapped.Buffer;
                                tapped.Buffer.Dispose();
                            }
                        }
                        return null;
                    }
                )
            )
            .Then(AudioOperators.Resample("whisper-resample", targetSampleRate: 16_000, targetChannels: 1))
            .Then(
                WhisperOperators.TranscribeWithWhisper(
                    "whisper",
                    whisperModel,
                    new WhisperOptions(Language: "en", WindowSize: TimeSpan.FromSeconds(2.5))
                )
            )
            .Then(CaptionOperators.SplitOnPunctuation("split-on-punctuation"))
            .Then(CaptionOperators.AnimatedReveal("animated-reveal", wordsPerSecond: 5))
            .To(
                new SinkNode<CaptionRef>(
                    "captions",
                    (caption, _) =>
                    {
                        _newCaptions.Enqueue(caption.Value);
                        return ValueTask.CompletedTask;
                    }
                )
            );

        try
        {
            await graph.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Captioning stopped.");
            Dispatcher.UIThread.Post(() => SetStatus("⚠ captioning stopped (see log)", Warning));
        }
    }

    /// <summary>
    /// Stamps new captions with the frame that has just reached the screen and shows the ones
    /// active at it. Raised on whichever thread presented the frame.
    /// </summary>
    private void OnFramePresented(object? sender, FramePresentedInfo frame)
    {
        var pts = frame.PresentationTime;
        var graph = Volatile.Read(ref _audioGraph);
        if (pts < _lastPresented || graph != _presentedGraph)
        {
            // A seek, a loop or the next file: a timeline only runs forward.
            _captions = NewCaptionTimeline();
            _newCaptions.Clear();
            _presentedGraph = graph;
        }
        _lastPresented = pts;

        while (_newCaptions.TryDequeue(out var caption))
            _captions.Add(caption, pts);

        var text = string.Join('\n', _captions.GetActive(pts).Select(c => c.Text));
        if (text == _shownCaption)
            return;
        _shownCaption = text;
        Dispatcher.UIThread.Post(() =>
        {
            CaptionText.Text = text;
            CaptionBackdrop.IsVisible = text.Length > 0;
        });
    }

    // ── Status ──────────────────────────────────────────────────────────

    private const string Neutral = "#888";
    private const string Warning = "#d0c07a";
    private const string Error = "#d07a7a";

    private void ShowCaveats(MediaInfo? info)
    {
        var caveats = new List<string>();
        if (info is { AudioStreams.Count: 0 })
            caveats.Add("no audio, so no captions");
        if (_detector is null)
            caveats.Add("no detection (YOLO unavailable)");

        if (caveats.Count > 0)
            SetStatus("⚠ " + string.Join(" · ", caveats), Warning);
        else
            AppStatusPill.IsVisible = false;
    }

    private void SetStatus(string text, string color)
    {
        AppStatusText.Text = text;
        AppStatusText.Foreground = SolidColorBrush.Parse(color);
        AppStatusPill.IsVisible = true;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Keep the window open until playback and captioning have stopped.
        e.Cancel = true;
        Closing -= OnWindowClosing;
        _windowCts.Cancel();

        try
        {
            // Held from here on, so no open can start after the player is gone.
            await _openGate.WaitAsync();
            if (_player is not null)
                await _player.DisposeAsync();
            await _audioSink.DisposeAsync(); // after the player that drives it
            _onScreen.Dispose();
            _captionAudio.Writer.TryComplete();
            await _captionGraph;
            while (_captionAudio.Reader.TryRead(out var unread))
                unread.Buffer.Dispose();
            _detector?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shutdown threw.");
        }
        finally
        {
            _loggerFactory.Dispose();
            Close();
        }
    }
}
