using FrameFlow.Native.Core;
using FrameFlow.Native.Tests.Doubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Tests that <see cref="FrameFlowBootstrapper"/> refuses an FFmpeg whose libavutil major
/// is not the one the struct bindings were generated for.
/// </summary>
/// <remarks>
/// The stub loader reports a version without loading anything, so these run with no FFmpeg
/// present. <c>SkipHardwareProbe</c> is set for the same reason as in
/// <see cref="FrameFlowBootstrapperTests"/>: the stub does not put avutil on the search path.
/// </remarks>
public sealed class FrameFlowBootstrapperAbiGateTests
{
    private static uint Pack(int major, int minor = 8, int micro = 100) =>
        ((uint)major << 16) | ((uint)minor << 8) | (uint)micro;

    private static FrameFlowBootstrapper Create(StubFfmpegLibraryLoader loader) =>
        new(
            new FrameFlowNativeOptions { SkipHardwareProbe = true },
            NullLogger<FrameFlowBootstrapper>.Instance,
            loader
        );

    [Fact]
    public void Initialize_MatchingAvutilMajor_Succeeds()
    {
        var loader = new StubFfmpegLibraryLoader
        {
            AvutilVersion = Pack(FfmpegAbiCheck.ExpectedAvutilMajor),
        };

        var result = Create(loader).Initialize();

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Initialize_MismatchedAvutilMajor_Fails()
    {
        // 61 is FFmpeg 9.x. The bindings are generated for 7.x, and every function this
        // project P/Invokes still links against 9, so nothing else in the load path
        // notices.
        var loader = new StubFfmpegLibraryLoader { AvutilVersion = Pack(61) };

        var result = Create(loader).Initialize();

        Assert.False(result.IsSuccess);
        Assert.Contains("ABI mismatch", result.Message);
    }

    /// <summary>
    /// The failure message is the only thing a consumer sees, so it has to name the version
    /// that loaded and the version FrameFlow wanted.
    /// </summary>
    [Fact]
    public void Initialize_MismatchedAvutilMajor_MessageNamesBothVersions()
    {
        var loader = new StubFfmpegLibraryLoader { AvutilVersion = Pack(61, minor: 1, micro: 100) };

        var result = Create(loader).Initialize();

        Assert.Contains("61.1.100", result.Message);
        Assert.Contains($"{FfmpegAbiCheck.ExpectedAvutilMajor}.x", result.Message);
    }

    /// <summary>
    /// A mismatch reports the source it resolved, not <c>Unknown</c>: the libraries did load,
    /// and the operator needs to know which ones to replace.
    /// </summary>
    [Fact]
    public void Initialize_MismatchedAvutilMajor_ReportsResolvedBinarySource()
    {
        var loader = new StubFfmpegLibraryLoader { AvutilVersion = Pack(61) };

        var result = Create(loader).Initialize();

        Assert.False(result.IsSuccess);
        Assert.Equal(FrameFlow.Media.FfmpegBinarySource.Bundled, result.BinarySource);
    }

    /// <summary>
    /// The cached fast path has to serve the failure too. A second caller that got a success
    /// result here would proceed to decode against the wrong struct layouts.
    /// </summary>
    [Fact]
    public void Initialize_MismatchedAvutilMajor_SecondCallAlsoFails()
    {
        var loader = new StubFfmpegLibraryLoader { AvutilVersion = Pack(61) };
        var bootstrapper = Create(loader);

        bootstrapper.Initialize();
        var second = bootstrapper.Initialize();

        Assert.False(second.IsSuccess);
    }
}
