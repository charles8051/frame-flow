using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Pins a seek taken before an item's first play (#639).
/// </summary>
/// <remarks>
/// <para>
/// Such a seek went wrong twice. The reposition chose between holding and resuming from the
/// clock's paused flag, and a clock that has never started is stopped rather than paused, so
/// it resumed: it activated the audio sink and opened the gates while nothing had asked to
/// play. The first play then seated the wallclock on the target and started it, and starting
/// a wallclock runs it from zero. Every frame after the target waited out the pacer's cap,
/// one frame per five seconds, while the reported position advanced normally.
/// </para>
/// <para>
/// Both halves are asserted on signals the playback stack raises itself. The controller
/// returns <see cref="IPlaybackController.SeekStateChanged"/> to
/// <see cref="SeekState.NotSeeking"/> only after the session's reposition has returned, so an
/// activation inside the reposition has happened by then. The pacer logs a frame it forces
/// past its cap before presenting that frame, so once a frame past the target has arrived,
/// any cap expiry before it is in the log.
/// </para>
/// <para>
/// <c>HarnessAudioSink</c> does not implement <c>IClockSource</c>, which keeps these on the
/// wallclock path, where the freeze was.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeekBeforeFirstPlayTests : IClassFixture<FfmpegBootstrapFixture>
{
    private static readonly TimeSpan Target = TimeSpan.FromSeconds(7);

    /// <summary>
    /// Two frames past the target on the 60 fps fixture. Before the fix the first frame after
    /// the target was the first one the cap forced out.
    /// </summary>
    private static readonly TimeSpan PastTarget = Target + TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// Bounds a failure only. Before the fix a forced frame still arrived, five seconds apart,
    /// so a broken build reaches the cap assertion rather than this bound.
    /// </summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly FfmpegBootstrapFixture _fixture;

    public SeekBeforeFirstPlayTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task ASeekBeforeTheFirstPlay_StartsNothing_AndPlaysOnFromTheTarget()
    {
        var video = new PtsRecordingVideoSink();
        var audio = new HarnessAudioSink();
        var log = new PaceCapLog();
        await using var controller = PlaybackController.Create(
            videoSink: video,
            audioSink: audio,
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            loggerFactory: log
        );

        var load = await controller.LoadAsync(Corpus("test-1080p60-h264-aac.mp4"));
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

        await SeekAndSettleAsync(controller, Target);

        Assert.True(
            audio.ActivateCount == 0,
            $"the seek activated the audio sink {audio.ActivateCount} time(s) before the first play"
        );

        await PlayPastTargetAsync(controller, video);

        Assert.True(log.CapExpiries == 0, CapMessage(log));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ASeekOnAnItemSkippedToWhilePaused_StartsNothing_AndPlaysOnFromTheTarget()
    {
        // The first item has no audio, so nothing but the second item touches the audio sink.
        var first = Corpus("test-video-only.mp4");
        var second = Corpus("test-1080p60-h264-aac.mp4");
        var coordinator = new PlaylistCoordinator([first, second], RepeatMode.Off);

        var video = new PtsRecordingVideoSink();
        var audio = new HarnessAudioSink();
        var log = new PaceCapLog();
        await using var controller = PlaybackController.CreatePlaylist(
            coordinator,
            videoSink: video,
            audioSink: audio,
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: RepeatMode.Off,
            loggerFactory: log
        );

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var onSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stateSub = controller.PlaybackStateChanged.Subscribe(
            new ActionObserver<StateTransition<PlaybackState>>(t =>
            {
                if (t.Current == PlaybackState.Paused)
                    paused.TrySetResult();
            })
        );
        using var transitionSub = coordinator.SourceTransitioned.Subscribe(
            new ActionObserver<PlaylistTransition>(t =>
            {
                if (ReferenceEquals(t.Item.Source, second))
                    onSecond.TrySetResult();
            })
        );

        Assert.True((await controller.LoadAsync(first)).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        Assert.True((await controller.PauseAsync()).IsSuccess);
        await paused.Task.WaitAsync(Bound);

        // A skip while paused makes the next item current and leaves it waiting for a play.
        coordinator.RequestSkip();
        await onSecond.Task.WaitAsync(Bound);

        await SeekAndSettleAsync(controller, Target);

        Assert.True(
            audio.ActivateCount == 0,
            $"the seek activated the audio sink {audio.ActivateCount} time(s) before the item played"
        );

        await PlayPastTargetAsync(controller, video);

        Assert.True(log.CapExpiries == 0, CapMessage(log));
    }

    private static MediaSource Corpus(string name) =>
        MediaSource.FromFile(IntegrationTestEnvironment.GetCorpusFile(name)!);

    /// <summary>Seeks, and returns once the controller reports the seek finished.</summary>
    private static async Task SeekAndSettleAsync(IPlaybackController controller, TimeSpan target)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = controller.SeekStateChanged.Subscribe(
            new ActionObserver<StateTransition<SeekState>>(t =>
            {
                if (t.Current == SeekState.NotSeeking)
                    settled.TrySetResult();
            })
        );

        var seek = await controller.SeekAsync(target);
        Assert.True(seek.IsSuccess, $"Seek failed: {seek.Error?.Message}");
        await settled.Task.WaitAsync(Bound);
    }

    /// <summary>
    /// Plays, and returns once a frame past the target has been presented since the play.
    /// Frames from before it, including an earlier playlist item's, are not considered.
    /// </summary>
    private static async Task PlayPastTargetAsync(
        IPlaybackController controller,
        PtsRecordingVideoSink video
    )
    {
        var before = video.Count;
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await video.WhenPresented(pts => pts >= PastTarget, skip: before).WaitAsync(Bound);
    }

    private static string CapMessage(PaceCapLog log) =>
        $"the pacer forced {log.CapExpiries} frame(s) past its wait cap after the first play: "
        + "the wallclock was not running from the seek target";

    /// <summary>Counts the pacer's wait-cap expiries.</summary>
    private sealed class PaceCapLog : ILoggerFactory, ILogger
    {
        private int _capExpiries;

        public int CapExpiries => Volatile.Read(ref _capExpiries);

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider) { }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (eventId.Name == "LogPaceCapExceeded")
                Interlocked.Increment(ref _capExpiries);
        }

        public void Dispose() { }
    }
}
