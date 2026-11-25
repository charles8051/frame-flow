using FrameFlow.Native.Core;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Unit tests for <see cref="FfmpegLoadFailure"/>, the message a caller sees when no candidate
/// path could load an FFmpeg library. Pure: strings in, a string out.
/// </summary>
public sealed class FfmpegLoadFailureTests
{
    private static readonly string[] Searched =
    [
        "/app/runtimes/linux-x64/native/libavcodec.so.63",
        "libavcodec.so.63",
    ];

    /// <summary>
    /// The message named FFmpeg 7.x after the bindings had moved to FFmpeg 9, so a reader who
    /// followed it installed an FFmpeg the resolver could not load either. The release it
    /// names is the one the bindings were generated for.
    /// </summary>
    [Fact]
    public void Describe_NamesTheReleaseTheBindingsWereGeneratedFor()
    {
        var message = FfmpegLoadFailure.Describe("avcodec", Searched);

        var expected = FfmpegAbiCheck.ReleaseName(
            FfmpegLibrary.AvUtil,
            FfmpegAbiCheck.ExpectedAvutilMajor
        );
        Assert.NotNull(expected);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("FFmpeg 7.x", message);
    }

    /// <summary>
    /// The per-library majors are what a reader can check against the files on disk, since
    /// they do not match the release number.
    /// </summary>
    [Fact]
    public void Describe_NamesEveryRequiredLibraryMajor()
    {
        var message = FfmpegLoadFailure.Describe("avcodec", Searched);

        foreach (var lib in FfmpegAbiCheck.RequiredLibraries)
            Assert.Contains($"{FfmpegAbiCheck.Name(lib)} {FfmpegAbiCheck.ExpectedMajor(lib)}", message);
    }

    /// <summary>
    /// Installing a system FFmpeg does not help when it is another major, so the message names
    /// the two fixes that do: the runtime package, which carries linux-x64, and a custom path.
    /// </summary>
    [Fact]
    public void Describe_NamesTheRuntimePackageAndTheCustomPath()
    {
        var message = FfmpegLoadFailure.Describe("avcodec", Searched);

        Assert.Contains("FrameFlow.Native.Runtime", message);
        Assert.Contains("linux-x64", message);
        Assert.Contains("FrameFlowNativeOptions.CustomFfmpegPath", message);
    }

    [Fact]
    public void Describe_NamesTheLibraryAndEverythingSearched()
    {
        var message = FfmpegLoadFailure.Describe("avcodec", Searched);

        Assert.Contains("'avcodec'", message);
        Assert.Contains($"[{string.Join(", ", Searched)}]", message);
    }

    /// <summary>
    /// FFmpeg 9.0's majors, written out rather than derived, so a binding bump that this
    /// message has not caught up with fails here instead of shipping a release name the
    /// table does not know.
    /// </summary>
    [Fact]
    public void DescribeRequiredFfmpeg_ForTheFFmpeg9Bindings()
    {
        Assert.Equal(
            "FFmpeg 9.x (libavutil 61, libswresample 7, libswscale 10, libavcodec 63, libavformat 63)",
            FfmpegAbiCheck.DescribeRequiredFfmpeg()
        );
    }

    /// <summary>
    /// A loadable FFmpeg of the wrong major, such as one the OS loader found under its bare
    /// name, passes the load and fails the ABI check instead, so that message has to name the
    /// same fixes.
    /// </summary>
    [Fact]
    public void AbiMismatchMessage_NamesTheRuntimePackageAndTheCustomPath()
    {
        var message = FfmpegAbiCheck.Check(
            ((uint)(FfmpegAbiCheck.ExpectedAvutilMajor - 3) << 16) | (1u << 8) | 100u
        ).Message;

        Assert.NotNull(message);
        Assert.Contains("FrameFlow.Native.Runtime", message);
        Assert.Contains("FrameFlowNativeOptions.CustomFfmpegPath", message);
        Assert.Contains($"one {FfmpegAbiCheck.RequiredRelease()} build", message);
    }

    [Fact]
    public void ReleaseName_UnknownMajor_IsNull()
    {
        Assert.Null(FfmpegAbiCheck.ReleaseName(FfmpegLibrary.AvUtil, 999));
    }
}
