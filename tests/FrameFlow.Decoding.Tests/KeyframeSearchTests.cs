using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The search for a seek from which decoding reaches the nearest keyframe at or before a position
/// (#483, #495). Pure: a packet is its keyframe flag and timestamps. <c>DemuxSeekTests</c> runs it
/// over real containers.
/// </summary>
public sealed class KeyframeSearchTests
{
    private static readonly TimeSpan Position = TimeSpan.FromSeconds(10);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>One packet of the searched stream, in seconds.</summary>
    public sealed record Packet(bool Key, double? Pts, double? Dts)
    {
        public override string ToString() =>
            $"{(Key ? "K" : "F")}{Pts?.ToString() ?? "-"}/{Dts?.ToString() ?? "-"}";
    }

    private static Packet K(double? pts, double? dts) => new(true, pts, dts);

    private static Packet F(double? pts) => new(false, pts, pts);

    private static TimeSpan? S(double? seconds) => seconds is { } s ? S(s) : null;

    private static KeyframeSearch Read(KeyframeSearch search, params Packet[] packets)
    {
        foreach (var p in packets)
            search = search.Read(p.Key, S(p.Pts), S(p.Dts));
        return search;
    }

    [Fact]
    public void TheFirstProbe_SeeksToThePosition()
    {
        Assert.Equal(Position, KeyframeSearch.For(Position).Probe);
    }

    /// <summary>
    /// The first probe after seeking to the position: the packets it reads, whether it has read
    /// enough, and where the search lands (null: it goes further back).
    /// </summary>
    public static TheoryData<Packet[], bool, double?> FirstProbes =>
        new()
        {
            // Landed on a keyframe at or before the position: that is the nearest, so one packet.
            { [K(9, 9)], true, 10 },
            { [K(10, 10)], true, 10 },
            // Landed on a frame that is not a keyframe, then read to the next keyframe: after the
            // position, so there is none at or before it this probe can reach.
            { [F(9.5), F(9.96), K(11, 11)], true, null },
            // A packet presented after the position ends the probe, keyframe or not.
            { [F(10.04)], true, null },
            { [K(10.04, 10.04)], true, null },
            // Landed just before a keyframe at or before the position: sought by its decode
            // time, and read on to a packet after the position.
            { [F(9.9), K(9.95, 9.94)], false, 9.94 },
            { [F(9.9), K(9.95, 9.94), F(10.04)], true, 9.94 },
            // A later keyframe at or before the position replaces an earlier one.
            { [F(9.1), K(9.2, 9.2), F(9.5), K(9.8, 9.79), F(10.1)], true, 9.79 },
            // Without a decode time, the keyframe is sought by the probe.
            { [F(9.9), K(9.95, null), F(10.04)], true, 10 },
            // A packet with no presentation time reads on, and is still where the probe landed.
            { [K(null, null)], false, null },
            { [K(null, null), K(9.5, 9.4), F(10.04)], true, 9.4 },
            // The source ended before any packet of the stream.
            { [], false, null },
        };

    [Theory]
    [MemberData(nameof(FirstProbes))]
    public void TheFirstProbe(Packet[] packets, bool finished, double? landing)
    {
        var search = Read(KeyframeSearch.For(Position), packets);

        Assert.Equal(finished, search.Finished);
        Assert.Equal(S(landing), search.Landing);
    }

    /// <summary>
    /// A later probe, one second back: a keyframe it lands on is not necessarily the nearest, so
    /// it reads on to a packet after the position.
    /// </summary>
    public static TheoryData<Packet[], bool, double?> LaterProbes =>
        new()
        {
            { [K(8, 8)], false, 9 },
            { [K(8, 8), F(9), F(10.04)], true, 9 },
            { [K(8, 8), F(9), K(9.5, 9.46), F(10.04)], true, 9.46 },
            { [F(8.5), F(9), F(10.04)], true, null },
            { [F(8.5), K(10.5, 10.5)], true, null },
        };

    [Theory]
    [MemberData(nameof(LaterProbes))]
    public void ALaterProbe(Packet[] packets, bool finished, double? landing)
    {
        var search = Read(KeyframeSearch.For(Position).Back(), packets);

        Assert.Equal(S(9), search.Probe);
        Assert.Equal(finished, search.Finished);
        Assert.Equal(S(landing), search.Landing);
    }

    [Fact]
    public void APacketAfterTheProbeHasReadEnough_ChangesNothing()
    {
        var search = Read(KeyframeSearch.For(Position), K(9, 9));

        Assert.Equal(search, Read(search, K(9.5, 9.5), F(10.04)));
    }

    [Fact]
    public void ProbesThatFindNothing_StepBackTwiceAsFarEachTime_ToTheStart_WhichIsWhereTheyLand()
    {
        var probes = new List<TimeSpan>();
        var search = KeyframeSearch.For(Position);
        while (true)
        {
            probes.Add(search.Probe);
            search = Read(search, F(search.Probe.TotalSeconds + 0.5), F(10.04));
            if (search.Landing is { } landing)
            {
                Assert.Equal(TimeSpan.Zero, landing);
                break;
            }
            search = search.Back();
        }

        Assert.Equal([S(10), S(9), S(7), S(3), TimeSpan.Zero], probes);
    }

    [Fact]
    public void TheNextProbe_StartsReadingAfresh()
    {
        var back = Read(KeyframeSearch.For(Position), F(9.5), F(10.04)).Back();

        Assert.Equal(KeyframeSearch.For(Position) with { Probe = S(9), Step = S(2) }, back);
        Assert.False(back.Landed);
        Assert.False(back.Finished);
        Assert.Null(back.Found);
    }

    [Theory]
    [InlineData(false, 0.04)]
    [InlineData(true, 0.04)]
    public void ASearchFromTheStart_LandsThere_WhenItsFirstPacketPresentsAfterIt(bool key, double pts)
    {
        var search = Read(KeyframeSearch.For(TimeSpan.Zero), new Packet(key, pts, pts));

        Assert.Equal(TimeSpan.Zero, search.Landing);
    }

    [Fact]
    public void TheStep_StopsGrowingBeforeItOverflows()
    {
        var search = new KeyframeSearch(TimeSpan.MaxValue, TimeSpan.MaxValue, TimeSpan.MaxValue / 2 + TimeSpan.FromTicks(1));

        Assert.Equal(TimeSpan.MaxValue, search.Back().Step);
    }
}
