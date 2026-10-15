using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Regression coverage for the seek discipline — the
/// 8-step sequence in <see cref="FrameFlow.Playback.SubstrateSession.SeekAsync"/>
/// (close gates → stop graph → deactivate audio → demux seek → reset
/// decoder queues + flush + drain + discard pending packet → seek clock
/// → fresh graph + open gates).
/// </summary>
/// <remarks>
/// <para>
/// Asserts the seek discipline catches state-survival bugs in
/// long-lived participants. Each bug in the recent debugging stretch
/// (commits <c>1a45f83</c>, <c>1e19ca2</c>, <c>35323ad</c>, <c>d03e4b0</c>)
/// was state owned by a session-lifetime component that survived
/// across a pump-run boundary the seek dance didn't invalidate.
/// </para>
/// <para>
/// <b>What the assertions target.</b>
/// </para>
/// <list type="bullet">
///   <item><see cref="MultiSeek_PlaybackKeepsFlowing"/> — performs
///   several seeks in sequence and asserts video frames keep flowing
///   throughout. Catches cumulative state leaks across pump runs that
///   no single-seek test would surface.</item>
///   <item><see cref="ForwardSeek_PostSeekVideoFramesAdvanceWithoutStalling"/> —
///   asserts video continues after a forward seek and plays on past the
///   target to the end.</item>
/// </list>
/// <para>
/// <b>What's NOT asserted.</b> Exact post-seek first-frame PTS — the
/// short test corpus has sparse keyframes and the demuxer may rewind
/// to position 0 for any forward seek target. The discipline asserted
/// here is liveness ("frames keep flowing"), not PTS positioning.
/// </para>
/// <para>
/// Nor how soon frames come back. Each wait completes on the sink receiving a frame, and its
/// bound only stops a failing run: a loaded runner resumes later on a correct build. The
/// <c>d03e4b0</c> bug class (a retained pre-seek packet sent into the post-seek timeline) froze
/// video for the gap between the pre-seek position and the target, which only a wall-clock
/// window could see here. That the seek drops the retained packet is pinned without a clock by
/// <c>DemuxPumpTests</c> (<c>Seek_WithPending_DropsItAndReturnsToNeedRead</c>,
/// <c>Drive_SeekBeforeResume_DropsPending_ThenReads</c>).
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeekDisciplineTests : IClassFixture<FfmpegBootstrapFixture>
{
    /// <summary>Bounds a failure only.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly FfmpegBootstrapFixture _fixture;

    public SeekDisciplineTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Performs five seeks in sequence during playback (each to a
    /// different position) and asserts video frames continue to flow
    /// after every seek. A cumulative state leak across pump runs —
    /// retained packets, stale codec buffers, gate misconfiguration,
    /// clock-source state drift — manifests as a freeze somewhere in
    /// the seek sequence. The test asserts at least one new video
    /// frame is presented after each seek, plus an overall liveness
    /// floor of N seeks worth of forward progress.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task MultiSeek_PlaybackKeepsFlowing()
    {
        var videoSink = new PtsRecordingVideoSink();
        var audioSink = new HarnessAudioSink();
        var controller = FrameFlow.Playback.PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            var playingTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var stateSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Playing)
                        playingTcs.TrySetResult();
                })
            );

            var errors = new List<PlaybackError>();
            using var errSub = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(errors.Add)
            );

            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

            using var playingCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await playingTcs.Task.WaitAsync(playingCts.Token);

            // Seek targets — mix of forward, backward, and zero so the
            // discipline is exercised against the full position-range
            // variety. Five seeks is enough to surface cumulative
            // leaks; more would just add wallclock time.
            var seekTargets = new[]
            {
                TimeSpan.FromSeconds(1.5),
                TimeSpan.FromSeconds(0.5),
                TimeSpan.FromSeconds(2.0),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1.0),
            };

            foreach (var target in seekTargets)
            {
                var framesBeforeSeek = videoSink.Count;

                var seek = await controller.SeekAsync(target);
                Assert.True(
                    seek.IsSuccess,
                    $"Seek to {target} failed: {seek.Error?.Message}"
                );

                // Wait for the seek state machine to settle.
                await WaitForSeekStateAsync(controller, SeekState.NotSeeking);

                // Liveness assertion: a new frame must arrive. A freeze here means seek N
                // invalidated long-lived state that seek N-1 didn't.
                await WaitForFrameAsync(
                    videoSink,
                    framesBeforeSeek,
                    () => $"No new frames presented after seek to {target}. "
                        + $"Presented count stayed at {framesBeforeSeek}. "
                        + $"Likely a long-lived-state leak across the {seekTargets.Length}-seek sequence."
                );
            }

            // Allow playback to reach a terminal state so disposal is
            // clean. Tolerate either Ended (file completed) or Paused
            // (final seek landed shortly before EOS, gates open).
            var endedTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var endedSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (
                        t.Current
                        is PlaybackState.Ended
                            or PlaybackState.Unloaded
                            or PlaybackState.Error
                    )
                    {
                        endedTcs.TrySetResult();
                    }
                })
            );

            using var endedCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await endedTcs.Task.WaitAsync(endedCts.Token);
            }
            catch (OperationCanceledException)
            {
                // The last seek may land near EOS — playback might be
                // mid-stream when we observe. Not a freeze if frames
                // are still flowing; check below.
            }

            Assert.Empty(errors);

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink: null);
        }

        // Final accumulated liveness check: each seek opens up another window of forward
        // playback. Not a count load can push down: the pacer holds at most three frames and
        // presents the newest one due, so it presents at least one in every three it is given,
        // and the 2 s played on after the last seek alone is 60 frames.
        Assert.True(
            videoSink.Count >= 10,
            $"Expected at least 10 presented frames across the 5-seek sequence; "
                + $"got {videoSink.Count}. Indicates playback froze somewhere mid-sequence."
        );
    }

    /// <summary>
    /// Single forward seek mid-playback, then asserts video frames continue to arrive and play
    /// on past the target to the end.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task ForwardSeek_PostSeekVideoFramesAdvanceWithoutStalling()
    {
        var videoSink = new PtsRecordingVideoSink();
        var audioSink = new HarnessAudioSink();
        var controller = FrameFlow.Playback.PlaybackController.Create(
            videoSink: videoSink,
            audioSink: audioSink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled
        );

        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            var playingTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var stateSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Playing)
                        playingTcs.TrySetResult();
                })
            );

            var errors = new List<PlaybackError>();
            using var errSub = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(errors.Add)
            );

            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess);

            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess);

            using var playingCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await playingTcs.Task.WaitAsync(playingCts.Token);

            // Let playback produce a handful of pre-seek frames so the
            // seek isn't first-frame (the regression target is the
            // mid-stream seek case where the demux pump has retained a
            // pre-seek packet across the cancellation).
            await WaitForFrameAsync(
                videoSink,
                skip: 2,
                () => $"Expected at least 3 pre-seek frames, got {videoSink.Count}."
            );

            var framesBeforeSeek = videoSink.Count;
            var lastPtsBeforeSeek = videoSink.PresentedPts[^1];

            // Forward seek to mid-stream, from around the 0.3 s mark.
            var seek = await controller.SeekAsync(TimeSpan.FromSeconds(1.5));
            Assert.True(seek.IsSuccess, $"Seek failed: {seek.Error?.Message}");

            await WaitForSeekStateAsync(controller, SeekState.NotSeeking);

            // Liveness assertion: at least 3 NEW frames must arrive.
            await WaitForFrameAsync(
                videoSink,
                skip: framesBeforeSeek + 2,
                () => $"Expected at least 3 new frames after forward seek to 1.5 s; "
                    + $"got {videoSink.Count - framesBeforeSeek} new frames."
            );

            // Wait for natural completion.
            var endedTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var endedSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (
                        t.Current
                        is PlaybackState.Ended
                            or PlaybackState.Unloaded
                            or PlaybackState.Error
                    )
                    {
                        endedTcs.TrySetResult();
                    }
                })
            );

            using var endedCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await endedTcs.Task.WaitAsync(endedCts.Token);

            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Empty(errors);

            // Final sanity: the last captured PTS should be past the
            // seek target (we played from 1.5 s forward to EOS, so
            // last-frame-PTS should be > 1.5 s by some margin).
            Assert.True(
                videoSink.PresentedPts[^1] > TimeSpan.FromSeconds(1.5),
                $"Expected last presented PTS to exceed seek target 1.5 s, got {videoSink.PresentedPts[^1].TotalSeconds:F3} s. "
                    + $"Pre-seek last PTS was {lastPtsBeforeSeek.TotalSeconds:F3} s."
            );

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink: null);
        }
    }

    private static async Task WaitForSeekStateAsync(
        IPlaybackController controller,
        SeekState expectedState,
        int timeoutMilliseconds = 15000
    )
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));

        while (controller.SeekingState != expectedState)
        {
            await Task.Delay(25, cts.Token);
        }
    }

    /// <summary>
    /// Completes when the sink is handed a frame after the first <paramref name="skip"/>, and
    /// fails with <paramref name="failureMessage"/> at <see cref="Bound"/>.
    /// </summary>
    private static async Task WaitForFrameAsync(
        PtsRecordingVideoSink videoSink,
        int skip,
        Func<string> failureMessage
    )
    {
        try
        {
            await videoSink.WhenPresented(_ => true, skip).WaitAsync(Bound);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(failureMessage());
        }
    }
}
