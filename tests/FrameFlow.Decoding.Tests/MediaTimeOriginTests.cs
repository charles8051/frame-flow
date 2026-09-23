using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for <see cref="MediaTimeOrigin"/>, the conversion between a container's
/// timestamp domain and media time (#358).
/// </summary>
/// <remarks>
/// Containers need not start at zero — MPEG-TS conventionally starts at 1.4s — while
/// everything above the demuxer works in media time, which does. The arithmetic is pure
/// and FFmpeg-free precisely so the cases below can be enumerated without a container in
/// the room; the integration coverage that a real MPEG-TS file plays is
/// <c>StartTimeOffsetTests</c>.
/// </remarks>
public sealed class MediaTimeOriginTests
{
    // 1.4s, the MPEG-TS convention, expressed in AV_TIME_BASE.
    private const long MpegTsStart = 1_400_000;

    [Fact]
    public void Zero_LeavesTimestampsAlone()
    {
        Assert.True(MediaTimeOrigin.Zero.IsZero);
        Assert.Equal(0, MediaTimeOrigin.Zero.InStreamUnits(1, 90_000));
        Assert.Equal(TimeSpan.FromSeconds(5), MediaTimeOrigin.Zero.ToMediaTime(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void FromContainerStartTime_WithAStartTime_CarriesIt() =>
        Assert.Equal(
            MpegTsStart,
            MediaTimeOrigin.FromContainerStartTime(isKnown: true, MpegTsStart).Microseconds
        );

    [Fact]
    public void FromContainerStartTime_WithoutOne_IsZero() =>
        Assert.True(MediaTimeOrigin.FromContainerStartTime(isKnown: false, MpegTsStart).IsZero);

    /// <summary>
    /// A negative start time is ignored rather than applied: subtracting it would push
    /// every timestamp ahead of the clock, which is the failure this type exists to
    /// prevent rather than one to reproduce in the other direction.
    /// </summary>
    [Fact]
    public void FromContainerStartTime_WhenNegative_IsZero() =>
        Assert.True(MediaTimeOrigin.FromContainerStartTime(isKnown: true, -500_000).IsZero);

    [Theory]
    // 1.4s in 1/90000 units (the MPEG-TS time base).
    [InlineData(1, 90_000, 126_000)]
    // ... in milliseconds (Matroska).
    [InlineData(1, 1_000, 1_400)]
    // ... in frame units at 25 fps.
    [InlineData(1, 25, 35)]
    // A non-unit numerator scales the other way.
    [InlineData(2, 90_000, 63_000)]
    public void InStreamUnits_ScalesByTheStreamTimeBase(int num, int den, long expected) =>
        Assert.Equal(
            expected,
            MediaTimeOrigin.FromContainerStartTime(true, MpegTsStart).InStreamUnits(num, den)
        );

    [Theory]
    [InlineData(0, 90_000)]
    [InlineData(1, 0)]
    [InlineData(-1, 90_000)]
    public void InStreamUnits_WithADegenerateTimeBase_IsZero(int num, int den) =>
        Assert.Equal(
            0,
            MediaTimeOrigin.FromContainerStartTime(true, MpegTsStart).InStreamUnits(num, den)
        );

    /// <summary>
    /// A nanosecond time base and a start time of hours overflow a 64-bit intermediate
    /// while the offset itself fits comfortably. Matroska states timestamps in
    /// nanoseconds, and a recording pulled off a live stream can start hours in.
    /// </summary>
    [Fact]
    public void InStreamUnits_WithALargeOffsetAndANanosecondTimeBase_DoesNotOverflow()
    {
        // 3 hours, which times 1e9 exceeds long.MaxValue as a 64-bit product.
        var origin = MediaTimeOrigin.FromContainerStartTime(true, 10_800_000_000);

        Assert.Equal(10_800_000_000_000, origin.InStreamUnits(1, 1_000_000_000));
    }

    /// <summary>
    /// An offset that still does not fit after scaling yields no offset rather than a
    /// wrapped one: it cannot be subtracted from timestamps in a domain that cannot
    /// express it, and leaving them alone is the bounded failure.
    /// </summary>
    [Fact]
    public void InStreamUnits_WhenTheScaledOffsetCannotFit_IsZero() =>
        Assert.Equal(
            0,
            MediaTimeOrigin
                .FromContainerStartTime(true, long.MaxValue / 2)
                .InStreamUnits(1, 1_000_000_000)
        );

    [Fact]
    public void ToMediaTimestamp_SubtractsTheOffset() =>
        Assert.Equal(9_000, MediaTimeOrigin.ToMediaTimestamp(135_000, hasTimestamp: true, 126_000));

    /// <summary>
    /// A timestamp the container did not supply keeps its sentinel value. Shifting it
    /// would turn <c>AV_NOPTS_VALUE</c> into an ordinary-looking number, and the decoder
    /// tests the sentinel to decide whether to synthesise.
    /// </summary>
    [Fact]
    public void ToMediaTimestamp_WithNoTimestamp_IsUnchanged() =>
        Assert.Equal(
            long.MinValue,
            MediaTimeOrigin.ToMediaTimestamp(long.MinValue, hasTimestamp: false, 126_000)
        );

    [Fact]
    public void ToMediaTime_AndBack_RoundTrips()
    {
        var origin = MediaTimeOrigin.FromContainerStartTime(true, MpegTsStart);
        var mediaTime = TimeSpan.FromSeconds(6);

        long containerMicroseconds = origin.ToContainerMicroseconds(mediaTime);

        Assert.Equal(7_400_000, containerMicroseconds);
        Assert.Equal(
            mediaTime,
            origin.ToMediaTime(TimeSpan.FromMicroseconds(containerMicroseconds))
        );
    }

    /// <summary>
    /// The first frame of a container that starts at its stated start time lands at zero,
    /// which is the property the pacing clock depends on.
    /// </summary>
    [Fact]
    public void ToMediaTime_AtTheStartTime_IsZero() =>
        Assert.Equal(
            TimeSpan.Zero,
            MediaTimeOrigin
                .FromContainerStartTime(true, MpegTsStart)
                .ToMediaTime(TimeSpan.FromMicroseconds(MpegTsStart))
        );

    /// <summary>
    /// Seeking to the position a frame reported has to land on that frame, so the two
    /// conversions must be inverses at whatever position the caller names.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(600)]
    public void SeekConversion_IsTheInverseOfThePacketShift(int seconds)
    {
        var origin = MediaTimeOrigin.FromContainerStartTime(true, MpegTsStart);
        var position = TimeSpan.FromSeconds(seconds);

        long container = origin.ToContainerMicroseconds(position);

        Assert.Equal(position, origin.ToMediaTime(TimeSpan.FromMicroseconds(container)));
    }
}
