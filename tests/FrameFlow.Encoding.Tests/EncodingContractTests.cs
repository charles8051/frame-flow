namespace FrameFlow.Encoding.Tests;

/// <summary>
/// Pure contract tests for the encoding surface that do not require FFmpeg —
/// they always run, providing coverage in environments without the native
/// shared libraries.
/// </summary>
public sealed class EncodingContractTests
{
    [Fact]
    public void H264EncoderOptions_HasSensibleDefaults()
    {
        var options = new H264EncoderOptions();

        Assert.Equal(0, options.Width); // infer from first frame
        Assert.Equal(0, options.Height);
        Assert.Equal(30, options.FrameRateNumerator);
        Assert.Equal(1, options.FrameRateDenominator);
        Assert.True(options.BitRate > 0);
        Assert.True(options.GopSize > 0);
        // No encoder is pinned by default -- it is resolved against the loaded FFmpeg.
        // libopenh264 is still what resolves wherever FrameFlow ships its own build; it
        // stopped being a hard default because macOS uses a Homebrew FFmpeg that has none.
        Assert.Null(options.EncoderName);
        Assert.Equal("libopenh264", H264EncoderOptions.DefaultEncoderPreference[0]);
    }

    [Fact]
    public void EncodedPacket_ExposesItsMetadata()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var packet = new EncodedPacket(
            bytes,
            pts: 5,
            dts: 5,
            duration: 1,
            timeBaseNumerator: 1,
            timeBaseDenominator: 30,
            isKeyFrame: true,
            streamIndex: 0
        );

        Assert.Equal(4, packet.Data.Length);
        Assert.Equal(5, packet.Pts);
        Assert.Equal(5, packet.Dts);
        Assert.Equal(1, packet.Duration);
        Assert.Equal(1, packet.TimeBaseNumerator);
        Assert.Equal(30, packet.TimeBaseDenominator);
        Assert.True(packet.IsKeyFrame);
        Assert.Equal(0, packet.StreamIndex);
    }

    [Fact]
    public void EncodedPacket_AddRef_ReturnsSameInstance()
    {
        var packet = new EncodedPacket([0], 0, 0, 0, 1, 30, isKeyFrame: false);

        IRefCounted other = packet.AddRef();

        Assert.Same(packet, other);

        // Two refs (initial + AddRef) → two disposes balance the count.
        packet.Dispose();
        other.Dispose();
    }

    [Fact]
    public void EncoderInfo_RoundTripsValues()
    {
        var info = new EncoderInfo("libopenh264", 1280, 720, 30, 1);

        Assert.Equal("libopenh264", info.CodecName);
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal(30, info.FrameRateNumerator);
        Assert.Equal(1, info.FrameRateDenominator);
    }
}
