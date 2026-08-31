using System.Diagnostics;
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
/// A seek repositions the demuxer to the keyframe at or before the target and seats the
/// clocks on the target. The decoder then has to walk from that keyframe to the target
/// before anything can be shown — 0.73 s on the fixture below, whose only keyframe is at
/// 0.0. The clocks counted that walk, so by the time the destination frame arrived they
/// were already 0.73 s past it: the picture held on the pre-seek frame and then covered
/// 0.78 s of content in about 80 ms before settling.
/// </para>
/// <para>
/// The assertion is on how much <i>content</i> the half second after the destination frame
/// covers, not on reported position and not on frame cadence. Position is no good: it and
/// the picture reconverge within ~80 ms either way, so sampling it is a race. Cadence is no
/// good either — that is a timing test. Content-per-wall-second separates the two cases by
/// 730 ms against a 250 ms margin, and a busy machine moves it the safe way.
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

    /// <summary>How long to watch after the destination frame appears.</summary>
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Content the window may cover beyond its own measured duration. Paced playback covers
    /// the window and no more. With the decode-forward counted as playback the picture also
    /// has to make up the walk — ~730 ms on this fixture — so 250 sits at roughly a third
    /// of the gap, clear of both sides.
    /// </summary>
    private static readonly TimeSpan MaxOverrun = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Fraction of the window that must be covered for the run to mean anything. Paced
    /// playback covers all of it; a stall covers none. Well under 1 so a slow start does
    /// not fail, well over 0 so a stalled window cannot pass.
    /// </summary>
    private const double StallFloor = 0.4;

    private readonly FfmpegBootstrapFixture _fixture;

    public SeekClockSettleTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    [RequiresFfmpegAndCorpusFact]
    public async Task AfterASeek_PlaybackResumesAtRealtimeInsteadOfCatchingUp()
    {
        var (controller, video) = Build();
        await using (controller)
        {
            await StartAsync(controller);
            await Task.Delay(1500);

            Assert.True((await controller.SeekAsync(Target)).IsSuccess);

            // Wait for the destination frame, then see how much content the next half
            // second of wall time covers. Measured this way rather than on reported
            // position because the two converge again within ~80 ms either way: the
            // quantity the defect moves is how much content is spent getting there.
            var atTarget = await WaitForTargetFrameAsync(video);
            var sampledFrom = Stopwatch.GetTimestamp();
            await Task.Delay(SampleWindow);

            // Measured, not assumed. Under load the continuation can come back well after
            // the nominal window while playback carried on, and bounding correctly paced
            // playback by a fixed number would then fail for the scheduler's reasons rather
            // than the code's.
            var elapsed = Stopwatch.GetElapsedTime(sampledFrom);
            var advanced = video.PresentedPts[^1] - atTarget;

            // Checked from below as well as above. A window in which playback stalled
            // covers no content at all, which would satisfy the upper bound while proving
            // nothing — the test has to fail on a stall rather than pass through it.
            Assert.True(
                advanced > elapsed * StallFloor,
                $"only {advanced.TotalMilliseconds:F0} ms of content played in "
                    + $"{elapsed.TotalMilliseconds:F0} ms after the seek target appeared — "
                    + "playback stalled, so this run says nothing about the catch-up "
                    + "behaviour either way"
            );

            Assert.True(
                advanced < elapsed + MaxOverrun,
                $"{advanced.TotalMilliseconds:F0} ms of content played in "
                    + $"{elapsed.TotalMilliseconds:F0} ms after the seek target appeared — "
                    + "the decoder's walk from the keyframe was counted as playback time, "
                    + "so the picture is racing to catch the clock up"
            );
        }
    }

    /// <summary>Polls until a frame at or past the target reaches the sink.</summary>
    private static async Task<TimeSpan> WaitForTargetFrameAsync(PtsRecordingVideoSink video)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
        {
            var hit = video.PresentedPts.FirstOrDefault(pts => pts >= Target);
            if (hit != default)
                return hit;
            await Task.Delay(10);
        }

        Assert.Fail("no frame at or past the seek target arrived within 15 s");
        return TimeSpan.Zero;
    }

    private static (IPlaybackController Controller, PtsRecordingVideoSink Video) Build()
    {
        var video = new PtsRecordingVideoSink();
        return (
            PlaybackController.Create(
                videoSink: video,
                audioSink: new HarnessAudioSink(),
                hardwareDecodeMode: HardwareDecodeMode.Disabled
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
}
