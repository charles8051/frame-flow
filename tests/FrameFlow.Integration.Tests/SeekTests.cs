using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Seek-during-playback coverage for the
/// controller. Exercises the gate-protected seek dance
/// in <see cref="FrameFlow.Playback.SubstrateSession.SeekAsync"/>
/// (close gates → stop graph → deactivate audio → demux seek →
/// reset decoder queues + flush → seek clock → fresh graph + open
/// gates) against real FFmpeg decoders.
/// </summary>
/// <remarks>
/// Unlike the original <see cref="SeekTests"/>, this file doesn't
/// assert tight PTS positioning post-seek — the short test corpus
/// has sparse keyframes and the demuxer may rewind to keyframe 0
/// for any seek target. The asserted contract is the controller-
/// level one: seek completes, observable state transitions through
/// <see cref="SeekState"/> cleanly, no errors, playback continues
/// to the natural end.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeekTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly FfmpegBootstrapFixture _fixture;

    public SeekTests(FfmpegBootstrapFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekMidStream_DuringPlay_CompletesAndContinues()
    {
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            var playingTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var seekTransitions = new List<StateTransition<SeekState>>();
            var errors = new List<PlaybackError>();

            using var stateSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (t.Current == PlaybackState.Playing)
                        playingTcs.TrySetResult();
                })
            );
            using var seekSub = controller.SeekStateChanged.Subscribe(
                new ActionObserver<StateTransition<SeekState>>(seekTransitions.Add)
            );
            using var errSub = controller.ErrorOccurred.Subscribe(
                new ActionObserver<PlaybackError>(errors.Add)
            );

            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess);

            var play = await controller.PlayAsync();
            Assert.True(play.IsSuccess);

            using var playingCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await playingTcs.Task.WaitAsync(playingCts.Token);

            // Let some playback happen so the seek isn't first-frame.
            await Task.Delay(300);

            var seek = await controller.SeekAsync(TimeSpan.FromSeconds(1.5));
            Assert.True(seek.IsSuccess, $"Seek failed: {seek.Error?.Message}");

            // After seek, controller may be Playing or transition states
            // through the seek state machine. Wait for SeekCompleted to
            // ensure SeekingState normalises.
            using var seekCompletedCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (
                controller.SeekingState != SeekState.NotSeeking
                && !seekCompletedCts.IsCancellationRequested
            )
            {
                await Task.Delay(50);
            }
            Assert.Equal(SeekState.NotSeeking, controller.SeekingState);

            // Continue playback to terminal state.
            var endedTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var endedSub = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(t =>
                {
                    if (
                        t.Current
                        is PlaybackState.Ended
                            or PlaybackState.Error
                            or PlaybackState.Unloaded
                    )
                    {
                        endedTcs.TrySetResult();
                    }
                })
            );

            using var endedCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await endedTcs.Task.WaitAsync(endedCts.Token);

            Assert.Equal(PlaybackState.Ended, controller.State);
            Assert.Empty(errors);

            // Seek state machine should have observed the standard
            // transitions: NotSeeking → SeekPending → SeekInProgress
            // → NotSeeking.
            Assert.Contains(
                seekTransitions,
                t => t.Current == SeekState.SeekPending
            );
            Assert.Contains(
                seekTransitions,
                t => t.Current == SeekState.SeekInProgress
            );
            Assert.Contains(
                seekTransitions,
                t => t.Current == SeekState.NotSeeking && t.Previous == SeekState.SeekInProgress
            );

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task SeekWhilePaused_DoesNotRestartPlayback()
    {
        var (controller, audioSink, videoSink) = IntegrationTestHelper.CreateController();
        await using (controller)
        {
            var source = MediaSource.FromFile(
                IntegrationTestEnvironment.GetCorpusFile("test-av-h264-aac.mp4")!
            );

            var load = await controller.LoadAsync(source);
            Assert.True(load.IsSuccess);

            // Controller is Paused after load. Seek without playing.
            Assert.Equal(PlaybackState.Paused, controller.State);

            var seek = await controller.SeekAsync(TimeSpan.FromSeconds(1.0));
            Assert.True(seek.IsSuccess, $"Seek failed: {seek.Error?.Message}");

            // After seek-while-paused, controller should still be Paused
            // and SeekingState should normalise to NotSeeking.
            using var settleCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (
                controller.SeekingState != SeekState.NotSeeking
                && !settleCts.IsCancellationRequested
            )
            {
                await Task.Delay(50);
            }
            Assert.Equal(SeekState.NotSeeking, controller.SeekingState);
            Assert.Equal(PlaybackState.Paused, controller.State);

            await IntegrationTestHelper.StabilizeForDisposeAsync(controller, audioSink, videoSink);
        }
    }
}
