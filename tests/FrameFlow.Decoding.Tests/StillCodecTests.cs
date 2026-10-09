using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding.Core;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Which decoder a bare image's first bytes name, for the formats <c>image2</c> cannot name from
/// the extension (#575). Pure: no FFmpeg, no file.
/// </summary>
public sealed class StillCodecTests
{
    [Theory]
    [InlineData("GIF87a")]
    [InlineData("GIF89a")]
    public void AGifHeaderNamesTheGifDecoder(string header) =>
        Assert.Equal(
            (int)AVCodecID.AV_CODEC_ID_GIF,
            StillCodec.ForHeader(System.Text.Encoding.ASCII.GetBytes(header))
        );

    [Fact]
    public void ABytesPrefixLongerThanTheHeaderStillNamesIt() =>
        Assert.Equal(
            (int)AVCodecID.AV_CODEC_ID_GIF,
            StillCodec.ForHeader("GIF89a \u0000"u8)
        );

    [Theory]
    [InlineData("GIF90a")]
    [InlineData("GIF8")]
    [InlineData("GIF")]
    [InlineData("")]
    [InlineData("\u0089PNG\r\n")]
    [InlineData("RIFF....WEBP")]
    public void ThingsThatAreNotAGifNameNothing(string header) =>
        Assert.Null(StillCodec.ForHeader(System.Text.Encoding.Latin1.GetBytes(header)));

    [Fact]
    public void AJpegHeaderNamesNothing() =>
        Assert.Null(StillCodec.ForHeader([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]));
}
