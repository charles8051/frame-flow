using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Pins that a playlist item made from a <see cref="MediaClip"/> plays from the clip's start: when it
/// opens, when it follows another item, when it loops, and when it follows another clip of the
/// same source (<c>docs/feature-specs/media-clips/spec.md</c>, requirements 1 to 4).
/// </summary>
/// <remarks>
/// <para>
/// The witness is the PTS of every frame the sink is handed. A clip that opened at zero, or a
/// replay that rewound to zero, hands the sink frames from before the clip's start, and frames are
/// presented in order, so once a frame past the start has arrived, every frame before it has too.
/// A frame within half a frame interval of the start counts as at it, because a PTS read back from
/// the stream can land a tick either side of the value it was written as.
/// </para>
/// <para>
/// The fixture is a 10 s 1080p60 file whose only keyframe is at zero, so every start here decodes
/// forward from zero, which is the case that would show a frame from before the start if the
/// pacer's floor were not armed.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MediaClipPlaybackTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Fixture = "test-1080p60-h264-aac.mp4";

    private static readonly TimeSpan HalfFrame = TimeSpan.FromSeconds(1.0 / 120);

    /// <summary>Two frames past a start, at 60 fps.</summary>
    private static readonly TimeSpan PastStart = TimeSpan.FromMilliseconds(30);

    /// <summary>Bounds a failure only.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly FfmpegBootstrapFixture _fixture;

    public MediaClipPlaybackTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task APlayerBuiltOnAClip_OpensAtItsStart_AndPresentsNothingBeforeIt()
    {
        var start = TimeSpan.FromSeconds(7);
        var video = new PtsRecordingVideoSink();
        await using var player = await FrameFlowPlayer
            .Create()
            .WithMedia(new MediaClip(Corpus(Fixture), start))
            .WithVideoSink(video)
            .WithAudioSink(new HarnessAudioSink())
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildPlayerAsync();

        Assert.Equal(start, player.Position);

        Assert.True((await player.PlayAsync()).IsSuccess);
        await video.WhenPresented(pts => pts >= start + PastStart).WaitAsync(Bound);

        AssertNothingBefore(start, video.PresentedPts);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AClipThatFollowsAnotherItem_OpensAtItsStart()
    {
        // Two source objects, so the hand-off opens the second rather than rewinding the first.
        // The first item is a clip near the end of the file, which ends on its own.
        var first = new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(9));
        var second = new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(2));
        var video = new PtsRecordingVideoSink();
        var coordinator = PlaylistCoordinator.Of([first, second], RepeatMode.Off);
        await using var controller = CreatePlaylist(coordinator, video);
        var onSecond = TransitionTo(coordinator, second.Source);

        Assert.True((await controller.LoadAsync(first.Source)).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await onSecond.WaitAsync(Bound);
        await video.WhenPresented(pts => pts >= second.Start + PastStart && pts < first.Start)
            .WaitAsync(Bound);

        // The first item presents from 9 s, so anything under 2 s came from the second item
        // opening at zero.
        AssertNothingBefore(second.Start, video.PresentedPts);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ALoopOfAClip_ReturnsToItsStart()
    {
        var clip = new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(8.5));
        var video = new PtsRecordingVideoSink();
        var coordinator = PlaylistCoordinator.Of([clip], RepeatMode.One);
        await using var controller = CreatePlaylist(coordinator, video, RepeatMode.One);

        var looped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loops = controller.LoopRestarted.Subscribe(
            new ActionObserver<LoopRestarted>(_ => looped.TrySetResult())
        );

        Assert.True((await controller.LoadAsync(clip.Source)).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await looped.Task.WaitAsync(Bound);

        var before = video.Count;
        await video.WhenPresented(pts => pts >= clip.Start + PastStart && pts < clip.Start + TimeSpan.FromSeconds(1), skip: before)
            .WaitAsync(Bound);

        AssertNothingBefore(clip.Start, video.PresentedPts);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AClipThatFollowsAClipOfTheSameSource_PlaysFromItsOwnStart()
    {
        // One source object for both, so the skip rewinds the open item in place rather than
        // opening the file again.
        var source = Corpus(Fixture);
        var first = new MediaClip(source, TimeSpan.FromSeconds(2));
        var second = new MediaClip(source, TimeSpan.FromSeconds(7));
        var video = new PtsRecordingVideoSink();
        var coordinator = PlaylistCoordinator.Of([first, second], RepeatMode.Off);
        await using var controller = CreatePlaylist(coordinator, video);

        var transitions = 0;
        var onSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = coordinator.SourceTransitioned.Subscribe(
            new ActionObserver<PlaylistTransition>(_ =>
            {
                if (Interlocked.Increment(ref transitions) == 2)
                    onSecond.TrySetResult();
            })
        );

        Assert.True((await controller.LoadAsync(source)).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await video.WhenPresented(pts => pts >= first.Start + PastStart).WaitAsync(Bound);

        coordinator.RequestSkip();
        await onSecond.Task.WaitAsync(Bound);
        var before = video.Count;
        await video.WhenPresented(pts => pts >= second.Start + PastStart, skip: before)
            .WaitAsync(Bound);

        // The rewind repositions before the hand-off is reported, so every frame after it is the
        // second clip's.
        AssertNothingBefore(second.Start, video.PresentedPts.Skip(before));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AClipThatStartsPastTheEnd_FailsToOpen()
    {
        var clip = new MediaClip(Corpus(Fixture), TimeSpan.FromSeconds(60));
        var coordinator = PlaylistCoordinator.Of([clip], RepeatMode.Off);
        await using var controller = CreatePlaylist(coordinator, new PtsRecordingVideoSink());

        var load = await controller.LoadAsync(clip.Source);

        Assert.False(load.IsSuccess);
        Assert.Contains("cannot start at", load.Error!.Message + load.Error.Inner?.Message, StringComparison.Ordinal);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AClipOfTheOpenSourceThatStartsAtTheEnd_FailsToStart()
    {
        // The hand-off rewinds the open item in place, which bypasses the open. The start is the
        // file's 10 s duration, so the item has nothing in it and must fail rather than end.
        var source = Corpus(Fixture);
        var first = new MediaClip(source, TimeSpan.FromSeconds(2));
        var atTheEnd = new MediaClip(source, TimeSpan.FromSeconds(10));
        var video = new PtsRecordingVideoSink();
        var coordinator = PlaylistCoordinator.Of([first, atTheEnd], RepeatMode.Off);
        await using var controller = CreatePlaylist(coordinator, video);

        var failed = new TaskCompletionSource<PlaylistItemFailed>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sub = controller.ItemFailed.Subscribe(
            new ActionObserver<PlaylistItemFailed>(f => failed.TrySetResult(f))
        );

        Assert.True((await controller.LoadAsync(source)).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await video.WhenPresented(pts => pts >= first.Start + PastStart).WaitAsync(Bound);

        coordinator.RequestSkip();
        var failure = await failed.Task.WaitAsync(Bound);

        Assert.Equal(atTheEnd.Start, failure.Item.Start);
        Assert.Equal(PlaylistItemFailure.CouldNotStart, failure.Failure);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task APausedHandOffToAClipOfTheOpenSource_RepositionsTheOpenFile_AndWaitsForPlay()
    {
        var source = Corpus(Fixture);
        var first = new MediaClip(source, TimeSpan.FromSeconds(2));
        var second = new MediaClip(source, TimeSpan.FromSeconds(7));
        var video = new PtsRecordingVideoSink();
        var coordinator = PlaylistCoordinator.Of([first, second], RepeatMode.Off);
        await using var controller = CreatePlaylist(coordinator, video);
        var onSecond = NthTransition(coordinator, 2);

        Assert.True((await controller.LoadAsync(source)).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await video.WhenPresented(pts => pts >= first.Start + PastStart).WaitAsync(Bound);
        Assert.True((await controller.PauseAsync()).IsSuccess);
        var seeksBefore = DemuxSeeks(controller);

        coordinator.RequestSkip();
        await onSecond.WaitAsync(Bound);

        // A rebuilt item has a new demuxer, which has sought once, to its start. The open one has
        // sought once more than before.
        Assert.Equal(seeksBefore + 1, DemuxSeeks(controller));
        Assert.Equal(PlaybackState.Paused, controller.State);
        Assert.Equal(second.Start, controller.Position);

        var before = video.Count;
        Assert.True((await controller.PlayAsync()).IsSuccess);
        await video.WhenPresented(pts => pts >= second.Start + PastStart, skip: before).WaitAsync(Bound);
        AssertNothingBefore(second.Start, video.PresentedPts.Skip(before));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AJumpBeforeTheFirstPlayToAClipOfTheOpenSource_RepositionsTheOpenFile_AndPlaysIt()
    {
        var source = Corpus(Fixture);
        var first = new MediaClip(source, TimeSpan.FromSeconds(2));
        var second = new MediaClip(source, TimeSpan.FromSeconds(7));
        var video = new PtsRecordingVideoSink();
        var coordinator = PlaylistCoordinator.Of([first, second], RepeatMode.Off);
        await using var controller = CreatePlaylist(coordinator, video);
        var onSecond = NthTransition(coordinator, 2);

        Assert.True((await controller.LoadAsync(source)).IsSuccess);
        var seeksBefore = DemuxSeeks(controller);
        Assert.Equal(JumpRequest.Pending, coordinator.RequestJump(coordinator.Queue.Playlist[1]));

        Assert.True((await controller.PlayAsync()).IsSuccess);
        await onSecond.WaitAsync(Bound);
        await video.WhenPresented(pts => pts >= second.Start + PastStart).WaitAsync(Bound);

        Assert.Equal(seeksBefore + 1, DemuxSeeks(controller));
        // Nothing of the first clip played: the jump was taken by the first play.
        AssertNothingBefore(second.Start, video.PresentedPts);
    }

    private static long DemuxSeeks(IPlaybackController controller) =>
        controller.GetDiagnostics().Pipeline.Stream.Demux.SeeksPerformed;

    private static Task NthTransition(PlaylistCoordinator coordinator, int n)
    {
        var count = 0;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = coordinator.SourceTransitioned.Subscribe(
            new ActionObserver<PlaylistTransition>(_ =>
            {
                if (Interlocked.Increment(ref count) == n)
                    signal.TrySetResult();
            })
        );
        return signal.Task.ContinueWith(_ => sub.Dispose(), TaskScheduler.Default);
    }

    private static MediaSource Corpus(string name) =>
        MediaSource.FromFile(IntegrationTestEnvironment.GetCorpusFile(name)!);

    private static IPlaybackController CreatePlaylist(
        PlaylistCoordinator coordinator,
        IVideoSink video,
        RepeatMode repeat = RepeatMode.Off
    ) =>
        PlaybackController.CreatePlaylist(
            coordinator,
            videoSink: video,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            initialRepeatMode: repeat
        );

    private static Task TransitionTo(PlaylistCoordinator coordinator, IMediaSource source)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = coordinator.SourceTransitioned.Subscribe(
            new ActionObserver<PlaylistTransition>(t =>
            {
                if (ReferenceEquals(t.Item.Source, source))
                    signal.TrySetResult();
            })
        );
        return signal.Task.ContinueWith(_ => sub.Dispose(), TaskScheduler.Default);
    }

    private static void AssertNothingBefore(TimeSpan start, IEnumerable<TimeSpan> presented)
    {
        var early = presented.Where(pts => pts < start - HalfFrame).ToList();
        Assert.True(
            early.Count == 0,
            $"{early.Count} frame(s) from before the clip's start of {start} reached the sink, "
                + $"the first at {early.FirstOrDefault()}"
        );
    }
}
