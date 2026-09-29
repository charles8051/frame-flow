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

    /// <summary>A packet with a decode time and no presentation time.</summary>
    private static Packet D(double dts, bool key = false) => new(key, null, dts);

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
            // The source ended with no keyframe at or before the position: further back.
            { [F(9.5)], false, null },
            // Without presentation times, decode times place the packets.
            { [D(9.9, key: true)], true, 10 },
            { [D(9.5), D(9.8, key: true), D(10.04)], true, 9.8 },
            { [D(9.5), D(10.04)], true, null },
            // A packet with neither timestamp ends the search, on a keyframe found before it or
            // where a seek to the position lands.
            { [F(9.5), K(9.7, 9.7), K(null, null)], true, 9.7 },
            { [K(null, null), K(9.5, 9.4), F(10.04)], true, 10 },
            { [F(9.5), K(null, null)], true, 10 },
            // No decode time to stop the next probe at: where a seek to the position lands.
            { [], false, 10 },
            { [new Packet(false, 9.5, null), new Packet(false, 10.04, null)], true, 10 },
            { [new Packet(false, 9.5, null)], false, 10 },
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
    /// A later probe, one second back, after the first landed at 9.5 and found nothing: a keyframe
    /// it lands on is not necessarily the nearest, so it reads on, and stops at the first packet
    /// that decodes after where the first landed.
    /// </summary>
    public static TheoryData<Packet[], bool, double?> LaterProbes =>
        new()
        {
            { [K(8, 8)], false, 9 },
            { [K(8, 8), F(9)], false, 9 },
            { [K(8, 8), F(9), F(9.5)], false, 9 },
            { [K(8, 8), F(9), F(9.5), F(9.54)], true, 9 },
            { [K(8, 8), F(9), K(9.2, 9.18), F(9.5), F(9.54)], true, 9.18 },
            { [F(8.5), F(9), F(9.5), F(9.54)], true, null },
            // Landed where the first probe did: one packet it has not read.
            { [F(9.5), F(9.54)], true, null },
            // Landed after where the first probe did: nothing it has not read.
            { [F(9.54)], true, null },
            // A packet that shares the decode time the first probe landed at is still read.
            { [F(8.5), K(9.5, 9.5), F(9.54)], true, 9.5 },
        };

    [Theory]
    [MemberData(nameof(LaterProbes))]
    public void ALaterProbe(Packet[] packets, bool finished, double? landing)
    {
        var first = Read(KeyframeSearch.For(Position), F(9.5), F(10.04));
        var search = Read(first.Back(), packets);

        Assert.Equal(S(9), search.Probe);
        Assert.Equal(finished, search.Finished);
        Assert.Equal(S(landing), search.Landing);
    }

    /// <summary>
    /// The search over a container that seeks by timestamp, as MPEG-TS does: a probe lands on the
    /// last packet whose decode time is at or before it.
    /// </summary>
    /// <returns>Where the search lands, and how many packets each probe read.</returns>
    private static (TimeSpan Landing, List<int> Reads) SearchByTimestamp(Packet[] stream, double position)
    {
        var reads = new List<int>();
        for (var search = KeyframeSearch.For(S(position)); ; search = search.Back())
        {
            var at = Math.Max(0, Array.FindLastIndex(stream, p => p.Dts <= search.Probe.TotalSeconds));
            var read = 0;
            for (var i = at; i < stream.Length && !search.Finished; i++, read++)
                search = Read(search, stream[i]);
            reads.Add(read);

            if (search.Landing is { } landing)
                return (landing, reads);
        }
    }

    /// <summary>Three seconds at 24 fps with a keyframe a second, like the MPEG-TS corpus clip.</summary>
    private static readonly Packet[] ThreeSeconds = Enumerable
        .Range(0, 72)
        .Select(i => new Packet(i % 24 == 0, i / 24.0, i / 24.0))
        .ToArray();

    [Theory]
    [InlineData(1.52, 1.0, new[] { 2, 26 })]
    [InlineData(1.0, 1.0, new[] { 1 })]
    [InlineData(0.99, 0.0, new[] { 2, 25 })]
    [InlineData(2.9, 2.0, new[] { 2, 26 })]
    [InlineData(10.0, 2.0, new[] { 1, 1, 1, 1, 72 })]
    public void OnAContainerThatSeeksByTimestamp_EachProbeStopsWhereTheOneBeforeItLanded(
        double position,
        double keyframe,
        int[] reads
    )
    {
        var result = SearchByTimestamp(ThreeSeconds, position);

        Assert.Equal(S(keyframe), result.Landing);
        Assert.Equal(reads, result.Reads);
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
    public void TheNextProbe_StartsReadingAfresh_AndStopsWhereThisOneLanded()
    {
        var back = Read(KeyframeSearch.For(Position), F(9.5), F(10.04)).Back();

        Assert.Equal((S(9), S(2)), (back.Probe, back.Step));
        Assert.False(back.Landed);
        Assert.Null(back.LandedAt);
        Assert.False(back.Finished);
        Assert.Null(back.Found);
        Assert.Equal(S(9.5), back.ScannedFrom);
    }

    [Fact]
    public void AProbeThatReadsOnlyWhatAnEarlierOneRead_KeepsItsBound()
    {
        var second = Read(Read(KeyframeSearch.For(Position), F(9.5), F(10.04)).Back(), F(9.54));

        Assert.Equal(S(9.5), second.Back().ScannedFrom);
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
