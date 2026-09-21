using FrameFlow.Native.Core;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Unit tests for <see cref="FfmpegAbiCheck"/>. Pure logic over a packed version integer,
/// so no FFmpeg binary and no bootstrap are involved.
/// </summary>
public sealed class FfmpegAbiCheckTests
{
    /// <summary>Packs a libavutil version the way <c>avutil_version()</c> does.</summary>
    private static uint Pack(int major, int minor = 8, int micro = 100) =>
        ((uint)major << 16) | ((uint)minor << 8) | (uint)micro;

    /// <summary>
    /// The binding package supplies the expected major, so this is the one assertion that
    /// pins a literal. It fails when the <c>FFmpeg.AutoGen.Abstractions</c> reference in
    /// <c>FrameFlow.Native.csproj</c> crosses a major version, which is the moment
    /// <c>scripts/fetch-ffmpeg.cs</c>, <c>scripts/runtime-manifest.json</c> and
    /// <c>THIRD-PARTY-NOTICES.md</c> have to move with it.
    /// </summary>
    [Fact]
    public void ExpectedAvutilMajor_IsFFmpeg7()
    {
        Assert.Equal(59, FfmpegAbiCheck.ExpectedAvutilMajor);
    }

    [Fact]
    public void Check_MatchingMajor_IsCompatible()
    {
        var verdict = FfmpegAbiCheck.Check(Pack(FfmpegAbiCheck.ExpectedAvutilMajor));

        Assert.True(verdict.IsCompatible);
        Assert.Null(verdict.Message);
        Assert.Equal(FfmpegAbiCheck.ExpectedAvutilMajor, verdict.DetectedMajor);
        Assert.Equal(FfmpegAbiCheck.ExpectedAvutilMajor, verdict.ExpectedMajor);
    }

    [Fact]
    public void Check_MatchingMajor_IgnoresMinorAndMicro()
    {
        // Minor and micro move within a major without changing struct layouts, so a
        // point-release difference must not be reported as a mismatch.
        var verdict = FfmpegAbiCheck.Check(
            Pack(FfmpegAbiCheck.ExpectedAvutilMajor, minor: 0, micro: 0)
        );

        Assert.True(verdict.IsCompatible);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    public void Check_DifferentMajor_IsIncompatible(int offset)
    {
        var detected = FfmpegAbiCheck.ExpectedAvutilMajor + offset;

        var verdict = FfmpegAbiCheck.Check(Pack(detected));

        Assert.False(verdict.IsCompatible);
        Assert.Equal(detected, verdict.DetectedMajor);
        Assert.Equal(FfmpegAbiCheck.ExpectedAvutilMajor, verdict.ExpectedMajor);
    }

    /// <summary>
    /// The message has to carry both numbers. A diagnostic naming only one of them leaves
    /// the reader unable to tell which side to change.
    /// </summary>
    [Fact]
    public void Check_DifferentMajor_MessageNamesBothVersions()
    {
        var detected = FfmpegAbiCheck.ExpectedAvutilMajor + 2;

        var message = FfmpegAbiCheck.Check(Pack(detected, minor: 1, micro: 100)).Message;

        Assert.NotNull(message);
        Assert.Contains($"{detected}.1.100", message);
        Assert.Contains($"{FfmpegAbiCheck.ExpectedAvutilMajor}.x", message);
    }

    /// <summary>
    /// libavutil majors do not match FFmpeg release numbers, so the message translates the
    /// ones it knows. 61 is FFmpeg 9.x, which is what a macOS box running an unversioned
    /// <c>brew install ffmpeg</c> supplies today.
    /// </summary>
    [Fact]
    public void Check_Avutil61_MessageNamesFFmpeg9()
    {
        var message = FfmpegAbiCheck.Check(Pack(61)).Message;

        Assert.NotNull(message);
        Assert.Contains("FFmpeg 9.x", message);
        Assert.Contains("FFmpeg 7.x", message);
    }
}
