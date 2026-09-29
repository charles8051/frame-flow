using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The search for a seek from which decoding reaches a keyframe at or before a position (#483).
/// Pure: a packet is its keyframe flag and timestamps. <c>DemuxSeekToKeyframeTests</c> runs it
/// over a container that seeks by timestamp.
/// </summary>
public sealed class KeyframeSearchTests
{
    private static readonly TimeSpan Position = TimeSpan.FromSeconds(10);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void TheFirstProbe_SeeksToThePosition()
    {
        Assert.Equal(Position, KeyframeSearch.For(Position).Probe);
    }

    [Fact]
    public void AKeyframeAtOrBeforeThePosition_IsFound()
    {
        var search = KeyframeSearch.For(Position);

        Assert.Equal(KeyframeScan.Found, search.Scan(isKeyframe: true, S(9)));
        Assert.Equal(KeyframeScan.Found, search.Scan(isKeyframe: true, Position));
    }

    [Fact]
    public void AFrameThatIsNotAKeyframe_ReadsOn()
    {
        Assert.Equal(KeyframeScan.ReadOn, KeyframeSearch.For(Position).Scan(isKeyframe: false, S(9.5)));
    }

    [Fact]
    public void APacketWithNoPresentationTime_ReadsOn()
    {
        Assert.Equal(KeyframeScan.ReadOn, KeyframeSearch.For(Position).Scan(isKeyframe: true, null));
    }

    [Fact]
    public void AnythingPresentedAfterThePosition_EndsTheScan_KeyframeOrNot()
    {
        var search = KeyframeSearch.For(Position);

        Assert.Equal(KeyframeScan.Passed, search.Scan(isKeyframe: false, S(10.04)));
        Assert.Equal(KeyframeScan.Passed, search.Scan(isKeyframe: true, S(11)));
    }

    [Fact]
    public void AKeyframeTheProbeLandedOn_IsSoughtByTheProbe()
    {
        var search = KeyframeSearch.For(Position);

        Assert.Equal(Position, search.SeekFor(landedOnIt: true, S(8.9)));
    }

    [Fact]
    public void AKeyframeReadAfterTheLanding_IsSoughtByItsDecodeTime_OrByTheProbeWithoutOne()
    {
        var search = KeyframeSearch.For(Position);

        Assert.Equal(S(8.9), search.SeekFor(landedOnIt: false, S(8.9)));
        Assert.Equal(Position, search.SeekFor(landedOnIt: false, null));
    }

    [Fact]
    public void ProbesThatFindNothing_StepBackTwiceAsFarEachTime_ToTheStart()
    {
        var probes = new List<TimeSpan>();
        for (KeyframeSearch? search = KeyframeSearch.For(Position); search is { } s; search = s.Back())
            probes.Add(s.Probe);

        Assert.Equal([S(10), S(9), S(7), S(3), TimeSpan.Zero], probes);
    }

    [Fact]
    public void ASearchFromTheStart_HasNowhereFurtherBack()
    {
        Assert.Null(KeyframeSearch.For(TimeSpan.Zero).Back());
    }

    [Fact]
    public void TheStep_StopsGrowingBeforeItOverflows()
    {
        var search = new KeyframeSearch(TimeSpan.MaxValue, TimeSpan.MaxValue, TimeSpan.MaxValue / 2 + TimeSpan.FromTicks(1));

        var back = search.Back();

        Assert.NotNull(back);
        Assert.Equal(TimeSpan.MaxValue, back.Value.Step);
    }
}
