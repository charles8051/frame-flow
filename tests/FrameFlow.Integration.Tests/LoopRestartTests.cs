using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Loop-restart coverage for the
/// controller. The state-machine loop logic
/// (<c>LastFrameRendered</c> → internal seek to 0 when
/// <see cref="RepeatMode.One"/> is set) is inherited unchanged from
/// the shared <c>PlaybackController</c> via
/// <c>InternalsVisibleTo</c>; this test pins that the seek-driven
/// loop dance on the substrate's <c>SubstrateSession</c> (gate
/// close → demux seek → flush → fresh graph) survives multiple
/// iterations on real FFmpeg decoders.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LoopRestartTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public LoopRestartTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task RepeatOne_TwoLoops_FireLoopEvents()
    {
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            var loopEvents = new List<LoopRestarted>();
            var twoLoopsTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var sub = controller.LoopRestarted.Subscribe(
                new ActionObserver<LoopRestarted>(e =>
                {
                    loopEvents.Add(e);
                    if (loopEvents.Count >= 2)
                        twoLoopsTcs.TrySetResult();
                })
            );

            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");

            await controller.SetRepeatModeAsync(RepeatMode.One);
            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

            // Two loops at ~3s each + seek overhead — generous timeout.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await twoLoopsTcs.Task.WaitAsync(cts.Token);

            // The 2nd loop's audio re-activate lands just AFTER its LoopRestarted event
            // fires; with the presenter-side select-by-clock pacer (ADR-0057) the EOS
            // drain gate can delay it slightly past the event. Wait for that activate to
            // land before unloading — otherwise the unload races the in-flight activate
            // and ActivateCount reads 2 instead of 3 (the loops themselves are fine; this
            // is purely an async-side-effect ordering window).
            while (audioSink.ActivateCount < 3 && !cts.IsCancellationRequested)
                await Task.Delay(25, cts.Token);

            // Unload to halt the in-flight 3rd pass cleanly.
            var unload = await controller.UnloadAsync();
            Assert.True(unload.IsSuccess, $"Unload failed: {unload.Error?.Message}");
            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);

            // Verify event shape: incrementing counts, total ≥ 2.
            Assert.True(loopEvents.Count >= 2, $"Expected ≥2 loop events, got {loopEvents.Count}.");
            Assert.Equal(1, loopEvents[0].LoopCount);
            Assert.Equal(2, loopEvents[1].LoopCount);

            // Audio sink saw activate/deactivate cycles for each loop seek.
            // Initial activate + 2 seek-driven activate/deactivate pairs.
            Assert.True(
                audioSink.ActivateCount >= 3,
                $"Expected ≥3 activations (1 initial + 2 loop restarts); got {audioSink.ActivateCount}."
            );
            Assert.True(
                audioSink.DeactivateCount >= 2,
                $"Expected ≥2 deactivations (one per loop restart's seek dance); got {audioSink.DeactivateCount}."
            );
        }
    }
}
