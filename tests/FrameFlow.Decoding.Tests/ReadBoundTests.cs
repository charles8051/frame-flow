using FrameFlow.Decoding.Internal;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Where a pump bounded to an end stops feeding each stream and stops reading (#483). Pure: the
/// packets are their routed outcome and decode timestamp.
/// </summary>
public sealed class ReadBoundTests
{
    [Fact]
    public void Unbounded_PassesEveryOutcomeThrough()
    {
        var bound = ReadBound.Unbounded;
        foreach (var routed in Enum.GetValues<ReadOutcome>())
        {
            var (next, outcome) = bound.Offer(routed, long.MaxValue);
            Assert.Equal(routed, outcome);
            Assert.Equal(bound, next);
        }
    }

    [Fact]
    public void APacketBeforeItsStreamsCutoff_IsFed()
    {
        var bound = new ReadBound(VideoCutoff: 100, AudioCutoff: 1000);

        Assert.Equal(ReadOutcome.SelectedVideo, bound.Offer(ReadOutcome.SelectedVideo, 99).Outcome);
        Assert.Equal(ReadOutcome.SelectedAudio, bound.Offer(ReadOutcome.SelectedAudio, 999).Outcome);
    }

    [Fact]
    public void APacketWithNoDecodeTimestamp_IsFed()
    {
        var bound = new ReadBound(VideoCutoff: 100, AudioCutoff: null);

        var (next, outcome) = bound.Offer(ReadOutcome.SelectedVideo, null);

        Assert.Equal(ReadOutcome.SelectedVideo, outcome);
        Assert.False(next.VideoPast);
    }

    [Fact]
    public void AStreamAtItsCutoff_IsNotFed_WhileAnotherIsStillBeforeIts()
    {
        var bound = new ReadBound(VideoCutoff: 100, AudioCutoff: 1000);

        var (next, outcome) = bound.Offer(ReadOutcome.SelectedVideo, 100);

        Assert.Equal(ReadOutcome.Unselected, outcome);
        Assert.True(next.VideoPast);

        // Past is past: a later video packet is not fed whatever it says, and audio still is.
        Assert.Equal(ReadOutcome.Unselected, next.Offer(ReadOutcome.SelectedVideo, 50).Outcome);
        Assert.Equal(ReadOutcome.Unselected, next.Offer(ReadOutcome.SelectedVideo, null).Outcome);
        Assert.Equal(ReadOutcome.SelectedAudio, next.Offer(ReadOutcome.SelectedAudio, 999).Outcome);
    }

    [Fact]
    public void TheReadEnds_OnceEveryFedStreamHasPassed()
    {
        var bound = new ReadBound(VideoCutoff: 100, AudioCutoff: 1000);

        (bound, _) = bound.Offer(ReadOutcome.SelectedVideo, 100);
        var (next, outcome) = bound.Offer(ReadOutcome.SelectedAudio, 1000);

        Assert.Equal(ReadOutcome.EndOfStream, outcome);
        Assert.True(next.VideoPast && next.AudioPast);
    }

    [Fact]
    public void AStreamThePumpDoesNotFeed_DoesNotHoldTheReadOpen()
    {
        var bound = new ReadBound(VideoCutoff: 100, AudioCutoff: null);

        Assert.Equal(ReadOutcome.EndOfStream, bound.Offer(ReadOutcome.SelectedVideo, 250).Outcome);
    }

    [Fact]
    public void UnselectedPacketsAndReadResults_AreLeftAlone()
    {
        var bound = new ReadBound(VideoCutoff: 100, AudioCutoff: null);

        Assert.Equal(ReadOutcome.Unselected, bound.Offer(ReadOutcome.Unselected, 500).Outcome);
        Assert.Equal(ReadOutcome.EndOfStream, bound.Offer(ReadOutcome.EndOfStream, null).Outcome);
        Assert.Equal(ReadOutcome.Fault, bound.Offer(ReadOutcome.Fault, null).Outcome);
    }

    [Theory]
    // 90 kHz: 2.5 s is exactly 225000.
    [InlineData(25_000_000L, 1, 90_000, 225_000L)]
    // 1/24: 1 s is exactly 24; a hair over rounds up, so a timestamp at the cutoff is past the time.
    [InlineData(10_000_000L, 1, 24, 24L)]
    [InlineData(10_000_001L, 1, 24, 25L)]
    // 1001/30000: 1 s is 29.97 units, rounded up to 30.
    [InlineData(10_000_000L, 1001, 30_000, 30L)]
    // A degenerate time base has no cutoff a timestamp reaches.
    [InlineData(10_000_000L, 0, 90_000, long.MaxValue)]
    [InlineData(10_000_000L, 1, 0, long.MaxValue)]
    public void CutoffIn_RoundsUp_IntoTheStreamsTimeBase(long ticks, int num, int den, long expected)
    {
        Assert.Equal(expected, ReadBound.CutoffIn(TimeSpan.FromTicks(ticks), num, den));
    }

    [Fact]
    public void CutoffIn_ThatDoesNotFit_IsOneNoTimestampReaches()
    {
        Assert.Equal(long.MaxValue, ReadBound.CutoffIn(TimeSpan.MaxValue, 1, int.MaxValue));
    }
}
