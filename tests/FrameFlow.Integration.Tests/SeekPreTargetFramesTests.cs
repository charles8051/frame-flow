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
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeekPreTargetFramesTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public SeekPreTargetFramesTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekingIntoAGop_DoesNotPresentTheFramesBeforeTheTarget()
    {
        var target = TimeSpan.FromSeconds(7);
        var video = new PtsRecordingVideoSink();
        var controller = PlaybackController.Create(
            videoSink: video,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-1080p60-h264-aac.mp4")!
            );
            Assert.True((await controller.LoadAsync(source)).IsSuccess);
            Assert.True((await controller.PlayAsync()).IsSuccess);

            // Reach steady playback well before the target, so the seek is a real forward
            // jump over a span the decoder has to walk.
            await Task.Delay(1500);
            Assert.True((await controller.SeekAsync(target)).IsSuccess);
            await Task.Delay(2500);
            await controller.PauseAsync();

            var presented = video.PresentedPts;
            var firstAtTarget = presented
                .Select((pts, i) => (pts, i))
                .Where(x => x.pts >= target)
                .Select(x => (int?)x.i)
                .FirstOrDefault();

            Assert.True(
                firstAtTarget is not null,
                "playback never reached the seek target — the floor discarded everything"
            );

            // Everything from the seek destination onward must be at or after it. Frames
            // before firstAtTarget are pre-seek content: playback up to 1.5 s, plus at most
            // one frame that leaked through the flush (a separate, already-documented
            // defect — see SubstrateSession's class doc and docs/DEFERRED_WORK.md).
            var afterArrival = presented.Skip(firstAtTarget!.Value).ToArray();
            Assert.All(
                afterArrival,
                pts =>
                    Assert.True(
                        pts >= target,
                        $"a frame at {pts.TotalSeconds:F3}s presented after the seek reached "
                            + $"{target.TotalSeconds:F1}s — pre-target reference frames are "
                            + "being displayed again"
                    )
            );

            // The defect delivered the entire 0.0 -> 7.0 s run, so the count is the signal
            // that matters: 420 pre-target frames then, at most a handful of pre-seek ones
            // now.
            var beforeArrival = presented.Take(firstAtTarget.Value).Count(pts => pts >= TimeSpan.Zero);
            var preSeekTail = presented.Take(firstAtTarget.Value).Count(pts => pts > TimeSpan.FromSeconds(2));
            Assert.True(
                preSeekTail <= 2,
                $"{preSeekTail} frames between 2 s and the target presented before playback "
                    + $"reached it (of {beforeArrival} before arrival) — the seek is "
                    + "fast-forwarding through the GOP again"
            );
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekingBeforeTheFirstPlay_StillDiscardsThem()
    {
        // Load, seek, then play. This launches through PlayAsync's first-play branch, the
        // one path that seats the timeline without going through RepositionAsync, so it is
        // where the floor is re-derived from the run's origin rather than carried.
        var target = TimeSpan.FromSeconds(7);
        var video = new PtsRecordingVideoSink();
        var controller = PlaybackController.Create(
            videoSink: video,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-1080p60-h264-aac.mp4")!
            );
            Assert.True((await controller.LoadAsync(source)).IsSuccess);
            Assert.True((await controller.SeekAsync(target)).IsSuccess);
            Assert.True((await controller.PlayAsync()).IsSuccess);
            await Task.Delay(2500);
            await controller.PauseAsync();

            var presented = video.PresentedPts;
            Assert.NotEmpty(presented);

            // Nothing played before the seek here, so this one can assert the whole set.
            Assert.All(
                presented,
                pts =>
                    Assert.True(
                        pts >= target,
                        $"a frame at {pts.TotalSeconds:F3}s presented on a first play that "
                            + $"was seeked to {target.TotalSeconds:F1}s beforehand"
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
        var target = TimeSpan.FromSeconds(7);
        var video = new PtsRecordingVideoSink();
        var controller = PlaybackController.Create(
            videoSink: video,
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-1080p60-h264-aac.mp4")!
            );
            Assert.True((await controller.LoadAsync(source)).IsSuccess);
            Assert.True((await controller.PlayAsync()).IsSuccess);
            await Task.Delay(1000);

            Assert.True((await controller.PauseAsync()).IsSuccess);
            await Task.Delay(200);
            Assert.True((await controller.SeekAsync(target)).IsSuccess);

            // Let any frame still in flight from before the seek land, so the snapshot
            // below is a clean boundary rather than a race.
            await Task.Delay(200);
            var beforeResume = video.PresentedPts.Count;
            Assert.True((await controller.PlayAsync()).IsSuccess);
            await Task.Delay(2500);
            await controller.PauseAsync();

            var afterResume = video.PresentedPts.Skip(beforeResume).ToArray();
            Assert.NotEmpty(afterResume);
            Assert.Contains(afterResume, pts => pts >= target);

            // Asserted over the gap between where playback had reached and the target, not
            // over "everything is at or past the target". A frame that leaked through the
            // flush continues the pre-seek position and can land after this snapshot — a
            // separate, already-documented defect, and one this test would otherwise fail
            // on intermittently. The gap is where the burst lived: it delivered the whole
            // 0 -> 7 s run, hundreds of frames of it in here.
            var inTheGap = afterResume
                .Where(pts => pts > TimeSpan.FromSeconds(2) && pts < target)
                .ToArray();
            Assert.True(
                inTheGap.Length == 0,
                $"{inTheGap.Length} frames between 2 s and the {target.TotalSeconds:F1}s "
                    + "target presented after resuming from a paused seek — the floor did "
                    + "not survive to the launch that used it"
            );
        }
    }
}
