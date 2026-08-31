using FrameFlow.Decoding;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Pins <see cref="ReadAheadCapacity"/>, which sizes the video packet queue so it holds more
/// <i>time</i> than the audio queue (issue #145).
/// </summary>
/// <remarks>
/// The invariant: audio must be the stream that fills first and blocks the shared demux pump.
/// Both queues were fixed at 512 packets, which is ~10.9 s of AAC but only ~8.5 s of 60 fps
/// video, so video filled first and its last-resort drop path became routine.
/// </remarks>
public sealed class ReadAheadCapacityTests
{
    private static readonly TimeSpan Target = ReadAheadCapacity.DefaultVideoReadAhead;

    [Theory]
    [InlineData(24, 1)]
    [InlineData(25, 1)]
    [InlineData(30000, 1001)] // 29.97
    [InlineData(50, 1)]
    [InlineData(60000, 1001)] // 59.94 — the rate in the reproduction
    [InlineData(60, 1)]
    [InlineData(120, 1)]
    public void EveryCommonFrameRateOutlastsTheAudioQueue(int num, int den)
    {
        // The audio queue is 128 packets. Its span for the codecs we handle:
        //   AAC  1024 @ 48 kHz    -> 2.7 s
        //   MP3  1152 @ 44.1 kHz  -> 3.3 s
        //   Opus 20 ms            -> 2.6 s
        // Video has to beat the worst of those at every frame rate, or the drop path is
        // reachable again.
        //
        // Checked at two packets per frame, not one. This derives a packet count from a
        // frame rate, and avg_frame_rate is an average rather than a guarantee — the margin
        // is the point, so the test asserts the margin rather than the happy case.
        const double worstAudioSpanSeconds = 3.3;
        const int packetsPerFrame = 2;

        var capacity = ReadAheadCapacity.ForVideo(num, den, Target);
        var fps = num / (double)den;
        var videoSpanSeconds = capacity / fps / packetsPerFrame;

        Assert.True(
            videoSpanSeconds > worstAudioSpanSeconds,
            $"{fps:F2} fps: {capacity} packets is {videoSpanSeconds:F1}s at "
                + $"{packetsPerFrame} packets/frame, which does not outlast a "
                + $"{worstAudioSpanSeconds}s audio queue"
        );
    }

    [Fact]
    public void TheShippedConfigurationWasTheBug()
    {
        // 512 packets at 59.94 fps is 8.5 s against audio's 10.9 s. This is the arithmetic
        // that made the defect, kept as the reason the derivation exists.
        const double fps = 60000 / 1001.0;
        var shippedVideoSpan = 512 / fps;
        const double shippedAudioSpan = 10.9; // 512 AAC packets @ 48 kHz

        Assert.True(
            shippedVideoSpan < shippedAudioSpan,
            $"512 video packets was {shippedVideoSpan:F1}s against audio's {shippedAudioSpan}s — "
                + "video filled first, which is the defect"
        );
    }

    [Fact]
    public void TheCapacityIsTheRequestedDuration()
    {
        Assert.Equal(1200, ReadAheadCapacity.ForVideo(60, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(720, ReadAheadCapacity.ForVideo(60, 1, TimeSpan.FromSeconds(12)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(60, 0)]
    [InlineData(-60, 1)]
    [InlineData(60, -1)]
    public void AnUnusableFrameRateFallsBackToWhatShipped(int num, int den)
    {
        // Degrade to the previous behaviour rather than to something new and untested.
        Assert.Equal(ReadAheadCapacity.MinCapacity, ReadAheadCapacity.ForVideo(num, den, Target));
    }

    [Fact]
    public void ANonPositiveTargetFallsBackToo()
    {
        Assert.Equal(
            ReadAheadCapacity.MinCapacity,
            ReadAheadCapacity.ForVideo(60, 1, TimeSpan.Zero)
        );
    }

    [Fact]
    public void ALowFrameRateNeverGoesBelowWhatShipped()
    {
        // 24 fps x 12 s is 288, under the floor. Shrinking the queue is not this change's
        // business; it only ever needs to grow it.
        Assert.Equal(ReadAheadCapacity.MinCapacity, ReadAheadCapacity.ForVideo(24, 1, Target));
    }

    [Fact]
    public void AnAbsurdFrameRateIsCapped()
    {
        Assert.Equal(ReadAheadCapacity.MaxCapacity, ReadAheadCapacity.ForVideo(100_000, 1, Target));
    }
}
