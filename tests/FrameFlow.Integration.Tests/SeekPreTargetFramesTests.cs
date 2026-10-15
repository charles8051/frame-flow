using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Pins that a seek does not present the frames between the keyframe and the target
/// (issue #157), end to end through real FFmpeg decoding.
/// </summary>
/// <remarks>
/// <para>
/// <b>What went wrong.</b> Step 4 of the discontinuity recipe repositions the demuxer to
/// the keyframe at or before the target, because the frames in between are needed as
/// references. Step 6 seats the master clock on the target exactly. Nothing discarded
/// those reference frames, so they arrived at the pacer already due, one at a time — each
/// the freshest due frame at its own moment, so the late-drop rule never saw two together
/// and never fired. The whole GOP presented at decode rate: 421 frames at 7.15x realtime
/// on the fixture below.
/// </para>
/// <para>
/// <b>Why this test exists alongside the unit tests.</b>
/// <c>ClockSelectVideoSinkTests</c> pins the floor itself, deterministically and without
/// FFmpeg. What it cannot pin is that <c>SubstrateSession</c> hands the seek target to
/// <c>ClockSelectVideoSink.Flush</c> — passing <see cref="TimeSpan.Zero"/> there compiles,
/// leaves every unit test green, and restores the defect in full. That wiring is what this
/// covers.
/// </para>
/// <para>
/// <b>Fixture choice.</b> <c>test-1080p60-h264-aac.mp4</c> has exactly one keyframe, at
/// 0.0, so every seek in it rewinds to the file start and the defect spans 420 frames
/// rather than the handful a normal GOP would produce. Chosen to make a regression
/// unmissable rather than marginal.
/// </para>
/// <para>
/// <b>What is read, and when.</b> Each wait completes on the sink receiving a frame, or on the
/// controller reporting the seek settled; the bound only stops a failing run. Where playback
/// had reached is read from what was presented rather than assumed from how long it ran, and
/// what the pacer presents before the seek's destination runs on without going back or
/// skipping ahead: it holds a few frames and presents the newest one due, and a frame that
/// leaked through the flush continues where playback was. The burst restarted from the
/// keyframe, so it goes back.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeekPreTargetFramesTests : IClassFixture<FfmpegBootstrapFixture>
{
    private static readonly TimeSpan Target = TimeSpan.FromSeconds(7);

    /// <summary>Where playback has reached before the seek.</summary>
    private static readonly TimeSpan Played = TimeSpan.FromSeconds(1.5);

    /// <summary>How far past the target to watch, so frames presented after it are seen.</summary>
    private static readonly TimeSpan Watched = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The most two frames presented one after the other may skip while playback runs on: a few
    /// the pacer dropped, or the frames between the last presented and one that leaked through
    /// the flush. The span the defect skipped from the keyframe to the target is seconds.
    /// </summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromMilliseconds(500);

    /// <summary>Bounds a failure only: the fixture's decode walk from its one keyframe to the target.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly FfmpegBootstrapFixture _fixture;

    public SeekPreTargetFramesTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekingIntoAGop_DoesNotPresentTheFramesBeforeTheTarget()
    {
        var video = new PtsRecordingVideoSink();
        var controller = PlaybackController.Create(
            videoSink: video,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            Assert.True((await controller.LoadAsync(Fixture())).IsSuccess);
            Assert.True((await controller.PlayAsync()).IsSuccess);

            // Reach steady playback well before the target, so the seek is a real forward
            // jump over a span the decoder has to walk.
            await video.WhenPresented(pts => pts >= Played).WaitAsync(Bound);
            Assert.True((await controller.SeekAsync(Target)).IsSuccess);
            await video.WhenPresented(pts => pts >= Target + Watched).WaitAsync(Bound);
            await controller.PauseAsync();

            var presented = video.PresentedPts;
            var firstAtTarget = FirstAtTarget(presented);

            // Everything from the seek destination onward must be at or after it.
            var afterArrival = presented.Skip(firstAtTarget).ToArray();
            Assert.All(
                afterArrival,
                pts =>
                    Assert.True(
                        pts >= Target,
                        $"a frame at {pts.TotalSeconds:F3}s presented after the seek reached "
                            + $"{Target.TotalSeconds:F1}s — pre-target reference frames are "
                            + "being displayed again"
                    )
            );

            // Frames before the destination are pre-seek content: playback up to where the
            // seek found it, plus at most one frame that leaked through the flush (a separate,
            // already-documented defect — see SubstrateSession's class doc and
            // docs/DEFERRED_WORK.md). They run on from the start of the clip. The defect
            // delivered the whole 0.0 -> 7.0 s run from the keyframe after them.
            AssertRunsOnUntilTheTarget(presented, from: 0, "before playback reached the target");
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekingBeforeTheFirstPlay_StillDiscardsThem()
    {
        // Load, seek, then play. The seek before the first play holds, and the first play starts
        // the run from where the clock stands, the one start that does not follow a reposition
        // directly, so it is where the floor is taken from the run's origin.
        var video = new PtsRecordingVideoSink();
        var controller = PlaybackController.Create(
            videoSink: video,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            Assert.True((await controller.LoadAsync(Fixture())).IsSuccess);
            Assert.True((await controller.SeekAsync(Target)).IsSuccess);
            Assert.True((await controller.PlayAsync()).IsSuccess);
            await video.WhenPresented(pts => pts >= Target + Watched).WaitAsync(Bound);
            await controller.PauseAsync();

            var presented = video.PresentedPts;
            Assert.NotEmpty(presented);

            // Nothing played before the seek here, so this one can assert the whole set.
            Assert.All(
                presented,
                pts =>
                    Assert.True(
                        pts >= Target,
                        $"a frame at {pts.TotalSeconds:F3}s presented on a first play that "
                            + $"was seeked to {Target.TotalSeconds:F1}s beforehand"
                    )
            );
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekingWhilePaused_StillDiscardsThemOnTheNextPlay()
    {
        // A seek taken while paused does not relaunch — step 7 reactivates audio in the
        // paused state and leaves the graph down, so the next PlayAsync is what starts the
        // run that plays from the target. The floor therefore has to survive from the seek
        // to that later launch, which is why it is carried on the session rather than
        // applied at the flush or at the seek's own relaunch.
        var video = new PtsRecordingVideoSink();
        var controller = PlaybackController.Create(
            videoSink: video,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            Assert.True((await controller.LoadAsync(Fixture())).IsSuccess);
            Assert.True((await controller.PlayAsync()).IsSuccess);
            await video.WhenPresented(pts => pts >= Played).WaitAsync(Bound);

            Assert.True((await controller.PauseAsync()).IsSuccess);
            await SeekAndSettleAsync(controller, Target);

            var beforeResume = video.Count;
            Assert.True((await controller.PlayAsync()).IsSuccess);
            await video
                .WhenPresented(pts => pts >= Target + Watched, skip: beforeResume)
                .WaitAsync(Bound);
            await controller.PauseAsync();

            var presented = video.PresentedPts;
            Assert.Contains(presented.Skip(beforeResume), pts => pts >= Target);

            // Asserted over the gap between where playback had reached and the target, not
            // over "everything is at or past the target". A frame that leaked through the
            // flush continues the pre-seek position and can land after the resume — a
            // separate, already-documented defect, and one this test would otherwise fail
            // on intermittently. The gap is where the burst lived: it delivered the whole
            // 0 -> 7 s run, hundreds of frames of it in here.
            AssertRunsOnUntilTheTarget(
                presented,
                from: beforeResume,
                "after resuming from a paused seek — the floor did not survive to the launch "
                    + "that used it"
            );
        }
    }

    private static MediaSource Fixture() =>
        MediaSource.FromFile(IntegrationTestEnvironment.GetCorpusFile("test-1080p60-h264-aac.mp4")!);

    private static int FirstAtTarget(IReadOnlyList<TimeSpan> presented)
    {
        for (var i = 0; i < presented.Count; i++)
        {
            if (presented[i] >= Target)
                return i;
        }

        Assert.Fail("playback never reached the seek target — the floor discarded everything");
        return -1;
    }

    /// <summary>
    /// Asserts that the frames presented from <paramref name="from"/> until the first at or past
    /// the target run on from the frame before them, never going back and never skipping more
    /// than <see cref="MaxGap"/>.
    /// </summary>
    private static void AssertRunsOnUntilTheTarget(
        IReadOnlyList<TimeSpan> presented,
        int from,
        string when
    )
    {
        var arrival = FirstAtTarget(presented);
        for (var i = Math.Max(from, 1); i < arrival; i++)
        {
            var step = presented[i] - presented[i - 1];
            Assert.True(
                step > TimeSpan.Zero && step <= MaxGap,
                $"a frame at {presented[i].TotalSeconds:F3}s followed one at "
                    + $"{presented[i - 1].TotalSeconds:F3}s {when}, and {arrival - i} more came "
                    + "before the target: frames between the keyframe and the target are being "
                    + "presented, so the seek is fast-forwarding through the GOP again"
            );
        }
    }

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
}
