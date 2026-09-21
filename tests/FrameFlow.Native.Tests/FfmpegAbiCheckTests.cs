using FrameFlow.Native.Core;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Unit tests for <see cref="FfmpegAbiCheck"/>. Pure logic over packed version integers,
/// so no FFmpeg binary and no bootstrap are involved.
/// </summary>
public sealed class FfmpegAbiCheckTests
{
    /// <summary>Packs a library version the way the <c>*_version()</c> entry points do.</summary>
    private static uint Pack(int major, int minor = 8, int micro = 100) =>
        ((uint)major << 16) | ((uint)minor << 8) | (uint)micro;

    /// <summary>A set where every library matches, as the bundled FFmpeg 7.1 does.</summary>
    private static List<FfmpegLibraryVersion> MatchingSet() =>
        [
            .. FfmpegAbiCheck.RequiredLibraries.Select(lib => new FfmpegLibraryVersion(
                lib,
                Pack(FfmpegAbiCheck.ExpectedMajor(lib))
            )),
        ];

    /// <summary>
    /// The binding package supplies the expected majors, so these are the assertions that pin
    /// literals. They fail when the <c>FFmpeg.AutoGen.Abstractions</c> reference in
    /// <c>FrameFlow.Native.csproj</c> crosses a major version, which is the moment
    /// <c>scripts/fetch-ffmpeg.cs</c>, <c>scripts/runtime-manifest.json</c> and
    /// <c>THIRD-PARTY-NOTICES.md</c> have to move with it. The numbers are the sonames the
    /// fetched payload carries: avutil-59, swresample-5, swscale-8, avcodec-61,
    /// avformat-61, avdevice-61, avfilter-10.
    ///
    /// All seven, including the two nothing loads, because SonameConsistencyTests holds the
    /// file names on disk to these values.
    /// </summary>
    [Fact]
    public void ExpectedMajor_MatchesFFmpeg7Sonames()
    {
        Assert.Equal(59, FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvUtil));
        Assert.Equal(5, FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.SwResample));
        Assert.Equal(8, FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.SwScale));
        Assert.Equal(61, FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvCodec));
        Assert.Equal(61, FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvFormat));
        Assert.Equal(61, FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvDevice));
        Assert.Equal(10, FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvFilter));
    }

    [Fact]
    public void ExpectedAvutilMajor_AgreesWithExpectedMajor()
    {
        Assert.Equal(
            FfmpegAbiCheck.ExpectedAvutilMajor,
            FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvUtil)
        );
    }

    // --- Single-library overload (the shape the bootstrapper's own gate uses) ---

    [Fact]
    public void Check_MatchingAvutilMajor_IsCompatible()
    {
        var verdict = FfmpegAbiCheck.Check(Pack(FfmpegAbiCheck.ExpectedAvutilMajor));

        Assert.True(verdict.IsCompatible);
        Assert.Null(verdict.Message);
        Assert.Empty(verdict.Mismatched);
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
    public void Check_DifferentAvutilMajor_IsIncompatible(int offset)
    {
        var detected = FfmpegAbiCheck.ExpectedAvutilMajor + offset;

        var verdict = FfmpegAbiCheck.Check(Pack(detected));

        Assert.False(verdict.IsCompatible);
        var entry = Assert.Single(verdict.Mismatched);
        Assert.Equal(FfmpegLibrary.AvUtil, entry.Library);
    }

    /// <summary>
    /// The message has to carry both numbers. A diagnostic naming only one of them leaves
    /// the reader unable to tell which side to change.
    /// </summary>
    [Fact]
    public void Check_DifferentAvutilMajor_MessageNamesBothVersions()
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

    // --- Whole-set overload ---

    [Fact]
    public void Check_AllLibrariesMatching_IsCompatible()
    {
        var verdict = FfmpegAbiCheck.Check(MatchingSet());

        Assert.True(verdict.IsCompatible);
        Assert.Empty(verdict.Mismatched);
    }

    /// <summary>
    /// The case a libavutil-only check cannot see. Each library is resolved independently, so
    /// a search path can serve avutil from one FFmpeg generation and avcodec from another.
    /// avcodec owns <c>AVCodecContext</c> and <c>AVPacket</c>, which FrameFlow overlays.
    /// </summary>
    [Fact]
    public void Check_CompanionLibraryFromAnotherGeneration_IsIncompatible()
    {
        var mixed = MatchingSet();
        var index = mixed.FindIndex(v => v.Library == FfmpegLibrary.AvCodec);
        mixed[index] = new FfmpegLibraryVersion(
            FfmpegLibrary.AvCodec,
            Pack(FfmpegAbiCheck.ExpectedMajor(FfmpegLibrary.AvCodec) + 1)
        );

        var verdict = FfmpegAbiCheck.Check(mixed);

        Assert.False(verdict.IsCompatible);
        var entry = Assert.Single(verdict.Mismatched);
        Assert.Equal(FfmpegLibrary.AvCodec, entry.Library);
        Assert.Contains("libavcodec", verdict.Message);
    }

    [Fact]
    public void Check_SeveralMismatches_MessageNamesEachLibrary()
    {
        List<FfmpegLibraryVersion> mixed =
        [
            new(FfmpegLibrary.AvUtil, Pack(FfmpegAbiCheck.ExpectedAvutilMajor)),
            new(FfmpegLibrary.SwScale, Pack(99)),
            new(FfmpegLibrary.AvFormat, Pack(98)),
        ];

        var verdict = FfmpegAbiCheck.Check(mixed);

        Assert.False(verdict.IsCompatible);
        Assert.Equal(2, verdict.Mismatched.Count);
        Assert.Contains("libswscale", verdict.Message);
        Assert.Contains("libavformat", verdict.Message);
        Assert.DoesNotContain("libavutil", verdict.Message);
    }

    [Fact]
    public void Check_EmptySet_IsCompatible()
    {
        // Nothing probed is nothing to refuse. The loader always passes the full set; this
        // pins the behaviour rather than leaving it to the reader.
        var verdict = FfmpegAbiCheck.Check([]);

        Assert.True(verdict.IsCompatible);
    }

    [Fact]
    public void Check_NullSet_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FfmpegAbiCheck.Check((IReadOnlyList<FfmpegLibraryVersion>)null!)
        );
    }

    [Fact]
    public void Name_CoversEveryLibrary()
    {
        foreach (FfmpegLibrary library in Enum.GetValues<FfmpegLibrary>())
            Assert.StartsWith("lib", FfmpegAbiCheck.Name(library));
    }

    /// <summary>
    /// Every enum value, not just the required five. AvDevice and AvFilter are never
    /// loaded, but SonameConsistencyTests reads their majors to check the file names on
    /// disk, and an unhandled case there would throw rather than fail an assertion.
    /// </summary>
    [Fact]
    public void ExpectedMajor_CoversEveryLibrary()
    {
        foreach (FfmpegLibrary library in Enum.GetValues<FfmpegLibrary>())
            Assert.True(FfmpegAbiCheck.ExpectedMajor(library) > 0);
    }

    /// <summary>
    /// The ABI gate enforces five. Adding a name to the enum must not quietly enlist a
    /// library the loader never loads into the compatibility check.
    /// </summary>
    [Fact]
    public void RequiredLibraries_ExcludesTheOnesNothingLoads()
    {
        Assert.DoesNotContain(FfmpegLibrary.AvDevice, FfmpegAbiCheck.RequiredLibraries);
        Assert.DoesNotContain(FfmpegLibrary.AvFilter, FfmpegAbiCheck.RequiredLibraries);
        Assert.Equal(5, FfmpegAbiCheck.RequiredLibraries.Count);
    }
}
