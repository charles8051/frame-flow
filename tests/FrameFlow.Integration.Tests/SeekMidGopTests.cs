using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A seek into the middle of a GOP on a container that seeks by timestamp reaches the frame at
/// the target, not the next keyframe (#495).
/// </summary>
/// <remarks>
/// <para>
/// The MPEG-TS demuxer has no index, so <c>av_seek_frame</c> lands on the packet nearest the
/// target whether or not it is a keyframe. The decoder drops everything from there to the next
/// keyframe, so the first frame the player had to show was that keyframe, up to a GOP past the
/// target. On the clip below, a seek to 1.52 s reached 2.0 s first.
/// </para>
/// <para>
/// The frames are recorded where the configurator sits, between the pause gate and the pacer.
/// That is what the decoder delivered, before the pacer's post-seek floor and its late-frame drop,
/// so the assertion does not depend on how promptly the pacer ran. A frame from before the seek
/// can still reach it (a known defect of the seek path); those are all near the start of the clip,
/// below the target, which is why the assertion looks at the first frame at or after the target.
/// </para>
/// <para>
/// The clip has a keyframe a second, at 0, 1 and 2 s of media time, at 24 fps. Its container
/// timestamps start at 1.4 s, so the target is also a check that the search runs in media time.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeekMidGopTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-start-offset.ts";
    private const string HowToGenerate = "Run: dotnet run scripts/generate-test-corpus.cs";
    private const double FramesPerSecond = 24;

    private readonly FfmpegBootstrapFixture _fixture;

    public SeekMidGopTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresCorpusFileFact(Clip, HowToGenerate)]
    public async Task SeekingBetweenKeyframes_DeliversTheFrameAtTheTargetFirst()
    {
        var target = TimeSpan.FromSeconds(1.52);
        var firstAtTarget = TimeSpan.FromSeconds(37 / FramesPerSecond);
        var runs = new RunRecorder();
        var controller = PlaybackController.Create(
            videoSink: new PtsRecordingVideoSink(),
            audioSink: new HarnessAudioSink(),
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            configureVideo: runs.Configure
        );

        await using (controller)
        {
            var source = MediaSource.FromFile(IntegrationTestEnvironment.GetCorpusFile(Clip)!);
            Assert.True((await controller.LoadAsync(source)).IsSuccess);

            // Seek before the first play, so the last run is the one the seek started.
            Assert.True((await controller.SeekAsync(target)).IsSuccess);

            // Barrier on the controller's own end-of-playback transition, subscribed before
            // Play so it cannot be missed.
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = controller.PlaybackStateChanged.Subscribe(
                new ActionObserver<StateTransition<PlaybackState>>(transition =>
                {
                    if (
                        transition.Current
                        is PlaybackState.Ended
                            or PlaybackState.Unloaded
                            or PlaybackState.Error
                    )
                        ended.TrySetResult();
                })
            );

            Assert.True((await controller.PlayAsync()).IsSuccess);

            // The timeout only bounds a failure.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ended.Task.WaitAsync(cts.Token);
            Assert.Equal(PlaybackState.Ended, controller.State);

            var delivered = runs.LastRun.Where(pts => pts >= target).ToArray();
            Assert.NotEmpty(delivered);
            Assert.True(
                Math.Abs((delivered[0] - firstAtTarget).TotalMilliseconds) < 1,
                $"the first frame at or after {target.TotalSeconds:F2}s delivered after the seek "
                    + $"was at {delivered[0].TotalSeconds:F4}s, not at "
                    + $"{firstAtTarget.TotalSeconds:F4}s. The seek landed mid-GOP and the "
                    + "decoder dropped the frames up to the next keyframe."
            );
        }
    }

    /// <summary>Records the timestamps of the frames each run of the video graph delivers.</summary>
    private sealed class RunRecorder
    {
        private readonly object _gate = new();
        private List<TimeSpan> _run = [];

        /// <summary>The frames of the most recent run, in delivery order.</summary>
        public IReadOnlyList<TimeSpan> LastRun
        {
            get
            {
                lock (_gate)
                    return _run.ToArray();
            }
        }

        public GraphChain<IVideoFrame> Configure(GraphChain<IVideoFrame> chain)
        {
            // Runs before each run's pump starts.
            chain.Graph.BeforeEachRun(() =>
            {
                lock (_gate)
                    _run = [];
            });

            return chain.Then(
                new OperatorNode<IVideoFrame, IVideoFrame>(
                    "record-timestamps",
                    (frame, _) =>
                    {
                        lock (_gate)
                            _run.Add(frame.Pts);
                        return ValueTask.FromResult<IVideoFrame?>(frame);
                    }
                )
            );
        }
    }
}
