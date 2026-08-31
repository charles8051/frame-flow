namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for <see cref="DemuxPacket"/> — contract verification without
/// requiring FFmpeg. Per-property `_StoresX` tautologies removed; a
/// single round-trip test covers positional-record property storage.
/// </summary>
public sealed class DemuxPacketTests : IClassFixture<FfmpegBootstrapFixture>
{
    /// <summary>
    /// Round-trip all positional-record properties through one
    /// construction. Replaces ~11 per-property `_StoresX` tests.
    /// </summary>
    [Fact]
    public void DemuxPacket_RoundTripsAllProperties()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var pts = TimeSpan.FromSeconds(1.5);
        var dts = TimeSpan.FromSeconds(2.0);
        var duration = TimeSpan.FromMilliseconds(33.3);

        var p = new DemuxPacket(
            streamIndex: 3,
            pts: pts,
            hasPts: true,
            dts: dts,
            hasDts: true,
            duration: duration,
            data: data,
            isKeyFrame: true
        );

        Assert.Equal(3, p.StreamIndex);
        Assert.Equal(pts, p.Pts);
        Assert.True(p.HasPts);
        Assert.Equal(dts, p.Dts);
        Assert.True(p.HasDts);
        Assert.Equal(duration, p.Duration);
        Assert.Equal(data, p.Data);
        Assert.True(p.IsKeyFrame);
    }

    /// <summary>
    /// Null-data must throw — guards the only invariant the
    /// constructor actually enforces.
    /// </summary>
    [Fact]
    public void DemuxPacket_NullData_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DemuxPacket(
                0,
                TimeSpan.Zero,
                false,
                TimeSpan.Zero,
                false,
                TimeSpan.Zero,
                data: null!,
                isKeyFrame: false
            )
        );
    }

    /// <summary>
    /// `HasPts = false` with `Pts = TimeSpan.Zero` is the canonical
    /// "missing PTS" sentinel and is structurally distinct from
    /// `HasPts = true` with the same Pts value (downstream sync code
    /// branches on this).
    /// </summary>
    [Fact]
    public void DemuxPacket_ZeroPts_HasPtsDistinguishesMissingFromPresent()
    {
        var present = MakePacket(pts: TimeSpan.Zero, hasPts: true);
        var missing = MakePacket(pts: TimeSpan.Zero, hasPts: false);

        Assert.True(present.HasPts);
        Assert.False(missing.HasPts);
    }

    /// <summary>Empty data is valid (some packets carry only metadata).</summary>
    [Fact]
    public void DemuxPacket_EmptyData_IsValid()
    {
        var p = MakePacket(data: []);
        Assert.Empty(p.Data);
    }

    private static DemuxPacket MakePacket(
        int streamIndex = 0,
        TimeSpan pts = default,
        bool hasPts = true,
        TimeSpan dts = default,
        bool hasDts = true,
        TimeSpan duration = default,
        byte[]? data = null,
        bool isKeyFrame = false
    )
    {
        return new DemuxPacket(
            streamIndex,
            pts,
            hasPts,
            dts,
            hasDts,
            duration,
            data ?? [],
            isKeyFrame
        );
    }
}
