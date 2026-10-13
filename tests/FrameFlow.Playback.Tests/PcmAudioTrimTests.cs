using FrameFlow.Media;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Cutting a run's first audio at the run's start (media-clips decision 8). A rate of 1000 Hz makes
/// a frame a millisecond.
/// </summary>
public sealed class PcmAudioTrimTests
{
    private const int Rate = 1000;

    [Theory]
    [InlineData(1000, 1000, 0)] // starts at the floor
    [InlineData(1000, 900, 0)] // starts past it
    [InlineData(1000, 1030, 30)] // the floor falls on a frame
    [InlineData(1000, 1030.5, 31)] // inside one: that frame starts before it
    [InlineData(1000, 1100, 100)]
    public void FramesBefore_CountsTheFramesThatStartBeforeTheFloor(double startMs, double floorMs, long expected)
    {
        var frames = PcmAudioTrim.FramesBefore(
            TimeSpan.FromMilliseconds(startMs),
            Rate,
            TimeSpan.FromMilliseconds(floorMs)
        );

        Assert.Equal(expected, frames);
    }

    [Fact]
    public void FramesBefore_IsZeroWithoutASampleRate() =>
        Assert.Equal(0, PcmAudioTrim.FramesBefore(TimeSpan.Zero, 0, TimeSpan.FromSeconds(1)));

    [Fact]
    public void Cut_KeepsTheFramesFromTheCut_AndStampsTheFirstOfThem()
    {
        // Stereo, 100 frames from 1.0 s; each sample holds its own index.
        using var buffer = Buffer(frames: 100, channels: 2, start: TimeSpan.FromSeconds(1));

        using var cut = PcmAudioTrim.Cut(buffer, 31);

        Assert.Equal(TimeSpan.FromMilliseconds(1031), cut.PresentationTime);
        Assert.Equal(69, cut.FrameCount);
        Assert.Equal(2, cut.Channels);
        Assert.Equal(Rate, cut.SampleRate);
        Assert.Equal(Enumerable.Range(62, 138).Select(i => (short)i), cut.Samples.ToArray());

        // The original is left as it was, for its owner to release.
        Assert.Equal(200, buffer.SampleCount);
    }

    [Fact]
    public void Cut_NeverStampsTheBufferBeforeTheFrameItStartsWith()
    {
        // At 44.1 kHz a frame is not a whole number of ticks.
        using var buffer = PcmAudioBuffer.Create(
            1024,
            44_100,
            1,
            TimeSpan.Zero,
            0,
            static (samples, _) => samples.Length
        );

        using var cut = PcmAudioTrim.Cut(buffer, 1);

        Assert.True(cut.PresentationTime.TotalSeconds >= 1.0 / 44_100, $"{cut.PresentationTime.Ticks} ticks");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Cut_RefusesToLeaveNothingOut_OrNothingIn(int frames)
    {
        using var buffer = Buffer(frames: 100, channels: 1, start: TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => PcmAudioTrim.Cut(buffer, frames));
    }

    private static PcmAudioBuffer Buffer(int frames, int channels, TimeSpan start) =>
        PcmAudioBuffer.Create(
            frames * channels,
            Rate,
            channels,
            start,
            frames * channels,
            static (samples, count) =>
            {
                for (var i = 0; i < count; i++)
                    samples[i] = (short)i;
                return count;
            }
        );
}
