using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Avalonia;
using FrameFlow.Media;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.DualPlayer;

/// <summary>
/// Hosts two fully independent players side by side in one process.
/// </summary>
/// <remarks>
/// <para>
/// Each pane owns a private decode → present chain: its own
/// <see cref="AvaloniaVideoSink"/> (+ frame pool, supplied by the
/// <see cref="FrameFlowVideoView"/>), optionally its own
/// <see cref="OpenAlAudioSink"/>, and its own <see cref="IMediaPlayer"/>
/// backed by a separate playback controller. Nothing is shared between the
/// two except process-global state (the FFmpeg bootstrap, the OpenAL device
/// layer, the metrics meters) — which is precisely the surface that
/// multi-player-per-process bugs live on, so running two at once is the point
/// of the example, not an accident of it.
/// </para>
/// <para>
/// Clock-source selection follows the digital-signage pattern: the clock is
/// chosen by <i>whether an audio sink is attached</i>,
/// not by an explicit clock knob.
/// </para>
/// <list type="bullet">
///   <item><b>Wall clock</b> (<see cref="ClockSourceKind.Wall"/>): attach no
///         audio sink. Video paces off the <c>WallClockSource</c> fallback
///         (ADR-0003); the clip's audio stream is discarded at the demuxer
///         (ADR-0059) so the pump never backpressures.</item>
///   <item><b>Audio master clock</b> (<see cref="ClockSourceKind.Audio"/>):
///         attach an audible <see cref="OpenAlAudioSink"/>, which masters the
///         clock off its sample counter.</item>
/// </list>
/// <para>
/// Two concurrent OpenAL sinks would be safe here (ADR-0058, shared device +
/// context), but in the canonical profile only the audio-master pane has one —
/// the wall-clock pane is silent by construction, so the two never compete for
/// the speakers.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly DualPlayerOptions _options;
    private ILoggerFactory? _loggerFactory;
    private ILogger<MainWindow>? _logger;
    private readonly List<Pane> _panes = [];
    private DispatcherTimer? _statusTimer;
    private bool _isClosing;

    /// <summary>Per-pane runtime state.</summary>
    private sealed class Pane
    {
        public required PlayerConfig Config { get; init; }
        public required FrameFlowVideoView Video { get; init; }
        public required AvaloniaVideoSink VideoSink { get; init; }
        public required TextBlock Status { get; init; }

        /// <summary>The OpenAL sink — present only for an audio-master pane;
        /// <see langword="null"/> for a wall-clock pane (no sink attached).
        /// Kept for disposal.</summary>
        public OpenAlAudioSink? AudioSink { get; set; }

        public IMediaPlayer? Player { get; set; }
    }

    /// <summary>Parameterless ctor for the Avalonia XAML previewer.</summary>
    public MainWindow()
        : this(DualPlayerOptions.Parse([])) { }

    public MainWindow(DualPlayerOptions options)
    {
        _options = options;
        InitializeComponent();

        // Build the logger factory in the ctor (before the window attaches to
        // the visual tree) so the FrameFlowVideoView's attach-time EnsureSink()
        // picks up our factory rather than the silent NullLoggerFactory.
        _loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new TextBoxLoggerProvider(LogOutput, LogLevel.Information));

            // A bare --log-file filename resolves under <repo>/logs/ so the log
            // lands at a consistent, workspace-agnostic path.
            var logPath = ExampleLogPaths.Resolve(_options.LogFilePath);
            if (!string.IsNullOrEmpty(logPath))
                b.AddProvider(new FileLoggerProvider(logPath, LogLevel.Debug));
        });
        _logger = _loggerFactory.CreateLogger<MainWindow>();

        LeftVideo.LoggerFactory = _loggerFactory;
        RightVideo.LoggerFactory = _loggerFactory;

        ApplyConfigLabels();
        Closing += OnWindowClosing;
    }

    private void ApplyConfigLabels()
    {
        LeftTitle.Text = $"LEFT  —  {DescribeClock(_options.Left.ClockSource)}";
        RightTitle.Text = $"RIGHT  —  {DescribeClock(_options.Right.ClockSource)}";
        LeftConfig.Text = DescribeConfig(_options.Left);
        RightConfig.Text = DescribeConfig(_options.Right);
    }

    private static string DescribeClock(ClockSourceKind clock) =>
        clock == ClockSourceKind.Wall ? "wall clock (no audio sink)" : "audio master clock";

    private static string DescribeConfig(PlayerConfig c) =>
        $"corpus={Path.GetFileName(c.CorpusPath)} · hw={c.HardwareDecodeMode} · "
        + $"clock={(c.ClockSource == ClockSourceKind.Wall ? "WALL" : "AUDIO")} · "
        + $"loop={(c.Loop ? "on" : "off")}";

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_loggerFactory is null || _logger is null)
            return;

        _logger.LogInformation(
            "Dual-player example ready. Launching two independent players in one process."
        );

        // Schedule an unattended clean shutdown so an agent can launch, let the
        // failing path surface, and read the flushed log (--exit-after N).
        if (_options.ExitAfterSeconds > 0)
        {
            _logger.LogInformation(
                "Auto-exit scheduled in {Seconds}s (--exit-after).",
                _options.ExitAfterSeconds
            );
            _ = Task.Delay(TimeSpan.FromSeconds(_options.ExitAfterSeconds))
                .ContinueWith(
                    _ => Dispatcher.UIThread.Post(Close),
                    TaskScheduler.Default
                );
        }

        _panes.Add(
            new Pane
            {
                Config = _options.Left,
                Video = LeftVideo,
                VideoSink = LeftVideo.EnsureSink(),
                Status = LeftStatus,
            }
        );
        _panes.Add(
            new Pane
            {
                Config = _options.Right,
                Video = RightVideo,
                VideoSink = RightVideo.EnsureSink(),
                Status = RightStatus,
            }
        );

        StartStatusTimer();

        // Build + start BOTH players concurrently — the genuine "side by side"
        // path. Running CreateAsync for both at once is what stresses the
        // shared process-global state (FFmpeg bootstrap, OpenAL device init).
        await Task.WhenAll(_panes.Select(BuildPaneAsync));
    }

    private async Task BuildPaneAsync(Pane pane)
    {
        if (_loggerFactory is null || _logger is null)
            return;

        var cfg = pane.Config;
        if (!File.Exists(cfg.CorpusPath))
        {
            _logger.LogError("[{Label}] corpus file not found: {Path}", cfg.Label, cfg.CorpusPath);
            Dispatcher.UIThread.Post(() => pane.Status.Text = $"missing file: {cfg.CorpusPath}");
            return;
        }

        try
        {
            // Clock-source selection by sink presence (signage parity):
            //   Wall  -> audioSink: null. Video paces off WallClockSource
            //            (ADR-0003); the audio stream is discarded at the
            //            demuxer (ADR-0059), so the pump can't backpressure.
            //   Audio -> an audible OpenAlAudioSink masters the clock off its
            //            sample counter.
            IAudioSink? audioSink = null;
            if (cfg.ClockSource == ClockSourceKind.Audio)
            {
                var openAl = new OpenAlAudioSink(_loggerFactory.CreateLogger<OpenAlAudioSink>());
                pane.AudioSink = openAl;
                audioSink = openAl;
            }

            _logger.LogInformation(
                "[{Label}] building player: {Config}",
                cfg.Label,
                DescribeConfig(cfg)
            );

            var player = await MediaPlayer.CreateAsync(
                source: MediaSource.FromFile(cfg.CorpusPath),
                videoSink: pane.VideoSink,
                audioSink: audioSink,
                hardwareDecodeMode: cfg.HardwareDecodeMode,
                initialRepeatMode: cfg.Loop ? RepeatMode.One : RepeatMode.Off,
                loggerFactory: _loggerFactory
            );
            pane.Player = player;

            await player.PlayAsync();
            _logger.LogInformation("[{Label}] playing.", cfg.Label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Label}] failed to start player.", cfg.Label);
            Dispatcher.UIThread.Post(() => pane.Status.Text = $"error: {ex.Message}");
        }
    }

    private void StartStatusTimer()
    {
        _statusTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) => UpdateStatus()
        );
        _statusTimer.Start();
    }

    private void UpdateStatus()
    {
        foreach (var pane in _panes)
        {
            var player = pane.Player;
            if (player is null)
                continue;

            var audio = pane.AudioSink is null ? "no audio" : "audio";
            pane.Status.Text =
                $"{player.State} · {Fmt(player.Position)}/{Fmt(player.Duration)} · "
                + $"rendered {pane.Video.RenderedFrameCount} dropped {pane.VideoSink.DroppedFrameCount} · "
                + audio;
        }
    }

    private static string Fmt(TimeSpan t) =>
        $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosing)
            return;
        e.Cancel = true;
        _isClosing = true;
        Closing -= OnWindowClosing;

        _statusTimer?.Stop();
        _statusTimer = null;

        foreach (var pane in _panes)
        {
            if (pane.Player is not null)
            {
                try
                {
                    await pane.Player.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[{Label}] player teardown threw", pane.Config.Label);
                }
                pane.Player = null;
            }

            // The controller deactivated the sink during its dispose; here we
            // release the native OpenAL device handle (only the audio-master
            // pane has a sink to release).
            if (pane.AudioSink is not null)
            {
                try
                {
                    await pane.AudioSink.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[{Label}] audio sink teardown threw", pane.Config.Label);
                }
                pane.AudioSink = null;
            }
        }

        _loggerFactory?.Dispose();
        Close();
    }
}
