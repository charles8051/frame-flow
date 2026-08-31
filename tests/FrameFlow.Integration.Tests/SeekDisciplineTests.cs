using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Integration.Tests.Harness.Capture;
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
///   asserts video continues within a tight wall-clock window after a
///   forward seek. The <c>d03e4b0</c> bug class (retained pre-seek
///   packet contaminating post-seek timeline) freezes video for the
///   pre-seek-to-target gap of wallclock time; if it regresses, the
///   "did we get new frames within 2 s" assertion catches it.</item>
/// </list>
/// <para>
/// <b>What's NOT asserted.</b> Exact post-seek first-frame PTS — the
/// short test corpus has sparse keyframes and the demuxer may rewind
/// to position 0 for any forward seek target. The discipline asserted
/// here is liveness ("frames keep flowing"), not PTS positioning.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeekDisciplineTests : IClassFixture<FfmpegBootstrapFixture>
{
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
    /// frame is captured after each seek, plus an overall liveness
    /// budget of N seeks worth of forward progress.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task MultiSeek_PlaybackKeepsFlowing()
    {
        var videoSink = new CapturingVideoSink();
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
                var framesBeforeSeek = videoSink.Captures.Count;

                var seek = await controller.SeekAsync(target);
                Assert.True(
                    seek.IsSuccess,
                    $"Seek to {target} failed: {seek.Error?.Message}"
                );

                // Wait for the seek state machine to settle.
                await WaitForSeekStateAsync(controller, SeekState.NotSeeking);

                // Liveness assertion: at least one new frame must
                // arrive within a generous wallclock budget. A freeze
                // here means seek N invalidated long-lived state that
                // seek N-1 didn't.
                await WaitForConditionAsync(
                    () => videoSink.Captures.Count > framesBeforeSeek,
                    timeoutMilliseconds: 5000,
                    failureMessage: $"No new frames captured within 5 s after seek to {target}. "
                        + $"Captured count stayed at {framesBeforeSeek}. "
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

        // Final accumulated liveness check: total captured frames
        // should exceed the pre-seek baseline by a generous margin —
        // each seek opens up another window of forward playback.
        Assert.True(
            videoSink.Captures.Count >= 10,
            $"Expected at least 10 captured frames across the 5-seek sequence; "
                + $"got {videoSink.Captures.Count}. Indicates playback froze somewhere mid-sequence."
        );
    }

    /// <summary>
    /// Single forward seek mid-playback, then asserts video frames
    /// continue to arrive within 2 s of wallclock. The
    /// <c>d03e4b0</c> bug class (retained pre-seek packet) freezes
    /// video for the pre-seek-to-target gap measured in real seconds
    /// — a 1.5 s forward seek with the bug active produced ~1.5 s of
    /// no-new-frames before the master clock caught up. A 2 s budget
    /// catches the regression with margin without being flaky on
    /// slow hosts.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task ForwardSeek_PostSeekVideoFramesAdvanceWithoutStalling()
    {
        var videoSink = new CapturingVideoSink();
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
            await WaitForConditionAsync(
                () => videoSink.Captures.Count >= 3,
                timeoutMilliseconds: 5000,
                failureMessage: $"Expected at least 3 pre-seek frames, got {videoSink.Captures.Count}."
            );

            var framesBeforeSeek = videoSink.Captures.Count;
            var lastPtsBeforeSeek = videoSink.Captures[^1].Pts;

            // Forward seek to mid-stream. The d03e4b0 bug surfaced as
            // a freeze whose duration matched (seek-target - last-pre-
            // seek-frame-pts). A 1.5 s target on a file playing
            // around the 0.3 s mark would freeze for ~1.2 s; the 2 s
            // wallclock budget below catches that with margin.
            var seek = await controller.SeekAsync(TimeSpan.FromSeconds(1.5));
            Assert.True(seek.IsSuccess, $"Seek failed: {seek.Error?.Message}");

            await WaitForSeekStateAsync(controller, SeekState.NotSeeking);

            // Liveness assertion: at least 3 NEW frames must arrive
            // within 2 s of wallclock. With the d03e4b0 bug active,
            // PaceUntil would freeze waiting for the master clock to
            // advance through the stale-PTS gap and no new frames
            // would land in this window.
            await WaitForConditionAsync(
                () => videoSink.Captures.Count >= framesBeforeSeek + 3,
                timeoutMilliseconds: 2000,
                failureMessage: $"Expected at least 3 new frames within 2 s after forward seek to 1.5 s; "
                    + $"got {videoSink.Captures.Count - framesBeforeSeek} new frames. "
                    + $"Likely a regression of the pending-packet retention bug (d03e4b0) — "
                    + $"the post-seek pipeline is waiting on stale-PTS frames instead of producing forward frames."
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
                videoSink.Captures[^1].Pts > TimeSpan.FromSeconds(1.5),
                $"Expected last captured PTS to exceed seek target 1.5 s, got {videoSink.Captures[^1].Pts.TotalSeconds:F3} s. "
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

    private static async Task WaitForConditionAsync(
        Func<bool> predicate,
        int timeoutMilliseconds,
        string failureMessage
    )
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));

        while (!predicate())
        {
            try
            {
                await Task.Delay(25, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException(failureMessage);
            }
        }
    }
}
