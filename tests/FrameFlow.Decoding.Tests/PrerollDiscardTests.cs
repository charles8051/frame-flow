using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// What a decoder skips between a keyframe and the start of a run (#784). Pure: every discard
/// level against every position of a packet relative to the start, with and without a start,
/// with and without non-reference frames in the stream.
/// </summary>
public sealed class PrerollDiscardTests
{
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(5);

    public static TheoryData<DecodeDiscardLevel> Levels() =>
        [.. Enum.GetValues<DecodeDiscardLevel>()];

    [Theory]
    [MemberData(nameof(Levels))]
    public void APacketBeforeTheStartSkipsAtLeastTheNonReferenceFrames(DecodeDiscardLevel level)
    {
        var expected = level >= DecodeDiscardLevel.NonReference ? level : DecodeDiscardLevel.NonReference;

        Assert.Equal(expected, PrerollDiscard.For(level, Start, Start - TimeSpan.FromTicks(1), true));
        Assert.Equal(expected, PrerollDiscard.For(level, Start, TimeSpan.Zero, true));
        Assert.Equal(expected, PrerollDiscard.For(level, Start, TimeSpan.FromSeconds(-1), true));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void APacketAtOrPastTheStartKeepsTheLevelItWasGiven(DecodeDiscardLevel level)
    {
        Assert.Equal(level, PrerollDiscard.For(level, Start, Start, true));
        Assert.Equal(level, PrerollDiscard.For(level, Start, Start + TimeSpan.FromTicks(1), true));
        Assert.Equal(level, PrerollDiscard.For(level, Start, TimeSpan.FromHours(1), true));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void AStreamWithNoNonReferenceFramesIsLeftAlone(DecodeDiscardLevel level)
    {
        Assert.Equal(level, PrerollDiscard.For(level, Start, TimeSpan.Zero, false));
        Assert.Equal(level, PrerollDiscard.For(level, Start, Start, false));
        Assert.Equal(level, PrerollDiscard.For(level, Start, null, false));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void ARunWithNoStartToReachIsLeftAlone(DecodeDiscardLevel level)
    {
        Assert.Equal(level, PrerollDiscard.For(level, null, TimeSpan.Zero, true));
        Assert.Equal(level, PrerollDiscard.For(level, null, Start, true));
        Assert.Equal(level, PrerollDiscard.For(level, null, null, true));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void APacketOfUnknownTimeIsDecodedAtTheLevelItWasGiven(DecodeDiscardLevel level) =>
        Assert.Equal(level, PrerollDiscard.For(level, Start, null, true));

    [Fact]
    public void ItNeverSkipsLessThanItWasGiven()
    {
        TimeSpan?[] floors = [null, TimeSpan.Zero, Start];
        TimeSpan?[] times = [null, TimeSpan.Zero, Start - TimeSpan.FromTicks(1), Start, Start + TimeSpan.FromTicks(1)];
        foreach (var level in Enum.GetValues<DecodeDiscardLevel>())
        foreach (var floor in floors)
        foreach (var time in times)
        foreach (var nonRef in (bool[])[false, true])
        {
            var result = PrerollDiscard.For(level, floor, time, nonRef);
            Assert.True(result >= level, $"{level} {floor} {time} {nonRef} gave {result}");
            Assert.True(
                result == level || result == DecodeDiscardLevel.NonReference,
                $"{level} {floor} {time} {nonRef} gave {result}");
        }
    }

    [Fact]
    public void BFramesBeforeTheStartAreSkippedEvenAfterAReferenceFramePastIt()
    {
        // Decode order of a stream with two B-frames per P-frame, 1 s apart, start at 4.5 s:
        // I0 P3 B1 B2 P6 B4 B5. B4 comes after P6 in decode order and before the start.
        TimeSpan S(double s) => TimeSpan.FromSeconds(s);
        var floor = (TimeSpan?)S(4.5);
        (double Pts, DecodeDiscardLevel Expected)[] decodeOrder =
        [
            (0, DecodeDiscardLevel.NonReference),
            (3, DecodeDiscardLevel.NonReference),
            (1, DecodeDiscardLevel.NonReference),
            (2, DecodeDiscardLevel.NonReference),
            (6, DecodeDiscardLevel.None),
            (4, DecodeDiscardLevel.NonReference),
            (5, DecodeDiscardLevel.None),
        ];

        foreach (var (pts, expected) in decodeOrder)
            Assert.Equal(expected, PrerollDiscard.For(DecodeDiscardLevel.None, floor, S(pts), true));
    }

    [Fact]
    public void TheFloorHoldsUntilAFrameAtOrPastIt()
    {
        Assert.Equal(Start, PrerollDiscard.After(Start, TimeSpan.Zero));
        Assert.Equal(Start, PrerollDiscard.After(Start, Start - TimeSpan.FromTicks(1)));
        Assert.Null(PrerollDiscard.After(Start, Start));
        Assert.Null(PrerollDiscard.After(Start, Start + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void ASpentFloorStaysSpent()
    {
        Assert.Null(PrerollDiscard.After(null, TimeSpan.Zero));
        Assert.Null(PrerollDiscard.After(null, Start));
    }

    [Fact]
    public void OnlyAStartPastZeroArmsAFloor()
    {
        Assert.Null(PrerollDiscard.Arm(TimeSpan.Zero));
        Assert.Null(PrerollDiscard.Arm(TimeSpan.FromSeconds(-1)));
        Assert.Equal(Start, PrerollDiscard.Arm(Start));
        Assert.Equal(TimeSpan.FromTicks(1), PrerollDiscard.Arm(TimeSpan.FromTicks(1)));
    }
}
