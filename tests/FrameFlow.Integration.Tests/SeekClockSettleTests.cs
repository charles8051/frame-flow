using System.Collections.Concurrent;
using System.Diagnostics;
using FrameFlow.Graph;
using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// Pins that the decode-forward a seek performs does not count as playback time
/// (issue #161), on the wallclock-mastered path.
/// </summary>
/// <remarks>
/// <para>
/// A seek repositions the demuxer to the keyframe at or before the target and seats the clocks
/// on the target. The decoder then has to walk from that keyframe to the target before anything
/// can be shown — 0.73 s on the fixture below, whose only keyframe is at 0.0. The clocks counted
/// that walk, so by the time the destination frame arrived they were already 0.73 s past it: the
/// picture held on the pre-seek frame and then covered 0.78 s of content in about 80 ms before
/// settling.
/// </para>
/// <para>
/// The assertion is on how much <i>content</i> has been presented past the destination frame,
/// against the wall time since that frame left the decoder, not on reported position and not on
/// frame cadence. The settle seats the clock on the destination frame after the frame has left
/// the decoder, and nothing is presented ahead of the clock, so nothing presented can be further
/// past the frame than the time measured since. With the walk counted as playback the clock was
/// already the walk's length past the frame when it arrived, and the picture raced to catch it
/// up. The time is taken where the configurator sits, ahead of the pacer, and read after the
/// content, so the bound holds however slowly the runner went; a slow runner only makes the walk
/// longer, which moves the defect further outside it.
/// </para>
/// <para>
/// Wallclock master only. An audio-mastered clock does not advance across the reposition —
/// the sink is deactivated and its sample counter moves only as the device consumes — so
/// there is nothing to correct there. <c>HarnessAudioSink</c> deliberately does not
/// implement <c>IClockSource</c>, which is what puts these on the wallclock path.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeekClockSettleTests : IClassFixture<FfmpegBootstrapFixture>
{
    private static readonly TimeSpan Target = TimeSpan.FromSeconds(7);

    /// <summary>Where playback has reached before the seek.</summary>
    private static readonly TimeSpan Played = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How much content past the destination frame to wait for before reading. A stall never
    /// gets there, so it fails at <see cref="Bound"/> rather than passing through a window that
    /// covered nothing.
    /// </summary>
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Bounds a failure only.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly FfmpegBootstrapFixture _fixture;

    public SeekClockSettleTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task AfterASeek_PlaybackResumesAtRealtimeInsteadOfCatchingUp()
    {
        var decoded = new DecodeTimes();
        var (controller, video) = Build(decoded);
        await using (controller)
        {
            await StartAsync(controller);
            await video.WhenPresented(pts => pts >= Played).WaitAsync(Bound);

            var seekedAt = Stopwatch.GetTimestamp();
            Assert.True((await controller.SeekAsync(Target)).IsSuccess);
            await video.WhenPresented(pts => pts >= Target + SampleWindow).WaitAsync(Bound);

            var reached = video.PresentedPts.Max();
            var destination = decoded.Destination(Target, after: seekedAt);
            var elapsed = Stopwatch.GetElapsedTime(destination.Timestamp);
            var advanced = reached - destination.Pts;

            Assert.True(
                advanced <= elapsed,
                $"{advanced.TotalMilliseconds:F0} ms of content past the seek's destination frame "
                    + $"was presented within {elapsed.TotalMilliseconds:F0} ms of that frame leaving "
                    + "the decoder — the decoder's walk from the keyframe was counted as playback, "
                    + "so the picture is racing to catch the clock up"
            );
        }
    }

    private static (IPlaybackController Controller, PtsRecordingVideoSink Video) Build(
        DecodeTimes decoded
    )
    {
        var video = new PtsRecordingVideoSink();
        return (
            PlaybackController.Create(
                videoSink: video,
                audioSink: new HarnessAudioSink(),
                hardwareDecodeMode: HardwareDecodeMode.Disabled,
                configureVideo: decoded.Configure
            ),
            video
        );
    }

    private static async Task StartAsync(IPlaybackController controller)
    {
        var source = MediaSource.FromFile(
            IntegrationTestEnvironment.GetCorpusFile("test-1080p60-h264-aac.mp4")!
        );
        Assert.True((await controller.LoadAsync(source)).IsSuccess);
        Assert.True((await controller.PlayAsync()).IsSuccess);
    }

    /// <summary>
    /// Records when each frame passes the configurator's place, between the decoder and the
    /// pacer, in the order the frames pass it.
    /// </summary>
    private sealed class DecodeTimes
    {
        private readonly ConcurrentQueue<(TimeSpan Pts, long Timestamp)> _frames = new();

        public GraphChain<IVideoFrame> Configure(GraphChain<IVideoFrame> chain) =>
            chain.Then(
                new OperatorNode<IVideoFrame, IVideoFrame>(
                    "decode-times",
                    (frame, _) =>
                    {
                        _frames.Enqueue((frame.Pts, Stopwatch.GetTimestamp()));
                        return ValueTask.FromResult<IVideoFrame?>(frame);
                    }
                )
            );

        /// <summary>
        /// The frame a seek landed on: the first at or past <paramref name="target"/> to pass
        /// after <paramref name="after"/>.
        /// </summary>
        /// <remarks>
        /// The decoder's walk from the keyframe never passes here, since the run's source
        /// refuses what comes before the seek's floor, and frames from before the seek that
        /// pass after it was asked for are short of the target.
        /// </remarks>
        public (TimeSpan Pts, long Timestamp) Destination(TimeSpan target, long after)
        {
            foreach (var frame in _frames)
            {
                if (frame.Timestamp >= after && frame.Pts >= target)
                    return frame;
            }

            Assert.Fail("no frame at or past the seek target left the decoder after the seek");
            return default;
        }
    }
}
