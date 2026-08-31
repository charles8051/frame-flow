using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Perf item B5 — the audio-mastered (panel) signage surface must seat its clock
/// origin <b>deterministically to the loop epoch</b> at every gapless-loop
/// boundary instead of re-discovering it from the first post-loop buffer's PTS.
/// </summary>
/// <remarks>
/// <para>
/// An audio-mastered signage panel surface loops a <i>single</i> clip in-player
/// with <see cref="RepeatMode.One"/> over a warm <c>OpenAlAudioSink</c> that
/// masters the pacing clock: the audio mode is not <c>None</c>, and the view
/// calls <c>MediaPlayer.CreateAsync</c> with <c>RepeatMode.One</c> rather than
/// building a playlist. The <see cref="RepeatMode.One"/> loop
/// is a seek-to-zero through the seek state machine
/// (<c>PlaybackControllerCore</c>: <c>LastFrameRendered</c> →
/// <c>StartSeekRunner(session, TimeSpan.Zero)</c> →
/// <c>SubstrateSession.SeekAsync(0)</c>).
/// </para>
/// <para>
/// The pre-fix failure mode (the gapless-loop epoch drift): the audio
/// sample-counter clock's origin was left to be <i>rediscovered</i> from the first
/// post-loop buffer's PTS at each boundary, so it sat at a device-paced,
/// buffer-PTS-dependent origin while the already-decoded video frames carried
/// climbing PTS — <c>PaceUntil</c> drifted behind across every loop and, on a weak
/// box, repeatedly hit the 5 s wait-cap (the observed micro-hitch). The fix routes the
/// loop-seek's <c>SeekBaseline(0)</c> through the audio sink — the same
/// <see cref="ISeekableClock"/> reseat the user-seek path already uses — so the
/// audio clock and the per-item frame PTS agree from the first frame at every loop
/// boundary, with no rediscovery transient.
/// </para>
/// <para>
/// Assertion is at the clock-master seam (device-free): a clock-mastering audio
/// sink (<see cref="ClockMasteringReseatAudioSink"/>) records its
/// <see cref="ISeekableClock.SeekBaseline"/> reseats. The first play and every loop
/// boundary must each produce a reseat to the loop epoch
/// (<see cref="TimeSpan.Zero"/>), and <b>every</b> recorded baseline must be exactly
/// <see cref="TimeSpan.Zero"/> — proving the origin is seated to the loop epoch and
/// does not drift across loops. Left to rediscover (pre-fix), the loop boundary
/// produces no reseat and this fails.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AudioMasteredLoopOriginTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public AudioMasteredLoopOriginTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RepeatOneAudioMastered_SeatsClockOriginToLoopEpoch_EveryLoop()
    {
        var audioSink = new ClockMasteringReseatAudioSink();
        var (controller, videoSink) = IntegrationTestHelper.CreateController(
            audioSink,
            RepeatMode.One
        );

        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            // The clock-mastering sink must actually be selected as the master clock
            // (the audio-mastered path); otherwise this test would silently exercise
            // the wallclock path and prove nothing about B5.
            Assert.IsAssignableFrom<IClockSource>(audioSink);
            Assert.IsAssignableFrom<ISeekableClock>(audioSink);

            var loopEvents = new List<LoopRestarted>();
            var twoLoopsTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var loopSub = controller.LoopRestarted.Subscribe(
                new ActionObserver<LoopRestarted>(e =>
                {
                    lock (loopEvents)
                    {
                        loopEvents.Add(e);
                        if (loopEvents.Count >= 2)
                            twoLoopsTcs.TrySetResult();
                    }
                })
            );

            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

            // First play must already seat the master-clock origin to the item start
            // (the loop epoch, 0) rather than leaving it to first-buffer discovery.
            await WaitForConditionAsync(
                () => audioSink.SeekBaselineCount >= 1,
                TimeSpan.FromSeconds(15),
                () =>
                    "First play did not seat the audio-mastered clock origin "
                    + $"(expected a SeekBaseline to the loop epoch; got {audioSink.SeekBaselineCount})."
            );
            Assert.Contains(TimeSpan.Zero, audioSink.SeekBaselines);

            // Drive two RepeatMode.One loop boundaries. Each loop-seek to zero must
            // reseat the audio-mastered clock origin again — the per-loop epoch
            // realignment that, when absent (pre-fix), is the gapless-loop drift.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await twoLoopsTcs.Task.WaitAsync(cts.Token);

            Assert.True(
                loopEvents.Count >= 2,
                $"Expected at least 2 RepeatMode.One loop boundaries, got {loopEvents.Count}."
            );

            // One reseat per loop boundary must have fired (the loop-seek's
            // SeekBaseline(0)) on top of the first-play seat: at least 1 + loops.
            await WaitForConditionAsync(
                () => audioSink.SeekBaselineCount >= 1 + loopEvents.Count,
                TimeSpan.FromSeconds(15),
                () =>
                    "A loop boundary did not reseat the audio-mastered clock origin "
                    + "(the gapless-loop epoch drift): "
                    + $"reseats={audioSink.SeekBaselineCount}, loops={loopEvents.Count}."
            );

            // Halt the in-flight next pass cleanly before asserting on the record.
            var unload = await controller.UnloadAsync();
            Assert.True(unload.IsSuccess, $"Unload failed: {unload.Error?.Message}");

            // No per-loop drift: every seated origin is exactly the loop epoch (0).
            // A rediscovered origin would land at a non-zero, buffer-PTS-dependent
            // value; a drifting origin would climb loop over loop. Both are excluded
            // by requiring every recorded baseline to equal TimeSpan.Zero.
            var baselines = audioSink.SeekBaselines;
            Assert.All(
                baselines,
                b =>
                    Assert.True(
                        b == TimeSpan.Zero,
                        $"Audio-mastered loop origin drifted: seated {b} instead of the "
                        + "loop epoch (0). Baselines: "
                        + string.Join(", ", baselines)
                    )
            );

            await IntegrationTestHelper.StabilizeForDisposeAsync(
                controller,
                audioSink: null,
                videoSink: videoSink
            );
        }
    }

    private static async Task WaitForConditionAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        Func<string> failureMessage
    )
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            try
            {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Assert.Fail(failureMessage());
            }
        }
    }
}
