using FrameFlow.Media;
using FrameFlow.Native.Core;
using FrameFlow.Native.Tests.Doubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Tests that <see cref="FrameFlowBootstrapper"/> refuses an FFmpeg whose library majors are
/// not the ones the struct bindings were generated for, and that it treats that refusal as
/// terminal.
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

    private static FrameFlowBootstrapper Create(
        StubFfmpegLibraryLoader loader,
        FrameFlowNativeOptions? options = null
    )
    {
        var opts = options ?? new FrameFlowNativeOptions();
        opts.SkipHardwareProbe = true;
        return new(opts, NullLogger<FrameFlowBootstrapper>.Instance, loader);
    }

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
        // project P/Invokes still links against 9, so nothing else in the load path notices.
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
        var loader = new StubFfmpegLibraryLoader
        {
            AvutilVersion = Pack(61, minor: 1, micro: 100),
        };

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
        Assert.Equal(FfmpegBinarySource.Bundled, result.BinarySource);
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

    // --- An ABI mismatch is terminal, not a reason to try the system path ---

    /// <summary>
    /// FFmpeg loads once per process. A mismatch means the libraries are already mapped, so a
    /// second <c>TryLoad</c> against the system path cannot replace them: it would skip every
    /// loaded library, re-probe the same binaries, and fail again.
    /// </summary>
    [Fact]
    public void Initialize_AbiMismatch_DoesNotRetryTheSystemPath()
    {
        var loader = new StubFfmpegLibraryLoader { SimulateAbiMismatch = true };

        var result = Create(
            loader,
            new FrameFlowNativeOptions { UseBundledBinaries = true, ProbeSystemLibraries = true }
        ).Initialize();

        Assert.False(result.IsSuccess);
        Assert.Equal(1, loader.CallCount);
    }

    /// <summary>
    /// The reported source has to be the one whose binaries actually loaded. Falling back
    /// would relabel a bundled mismatch as <c>System</c> and hand the reader a search path
    /// that was never used.
    /// </summary>
    [Fact]
    public void Initialize_AbiMismatch_ReportsTheSourceThatLoaded()
    {
        var loader = new StubFfmpegLibraryLoader { SimulateAbiMismatch = true };

        var result = Create(
            loader,
            new FrameFlowNativeOptions { UseBundledBinaries = true, ProbeSystemLibraries = true }
        ).Initialize();

        Assert.Equal(FfmpegBinarySource.Bundled, result.BinarySource);
        Assert.Equal(FfmpegBinarySource.Bundled, loader.LastSource);
    }

    /// <summary>
    /// An ordinary load failure still falls back, which is the behaviour the ABI case is
    /// carved out of. Without this the carve-out could silently disable the fallback.
    /// </summary>
    [Fact]
    public void Initialize_OrdinaryLoadFailure_StillRetriesTheSystemPath()
    {
        var loader = new StubFfmpegLibraryLoader { SimulateSuccess = false };

        var result = Create(
            loader,
            new FrameFlowNativeOptions { UseBundledBinaries = true, ProbeSystemLibraries = true }
        ).Initialize();

        Assert.False(result.IsSuccess);
        Assert.Equal(2, loader.CallCount);
        Assert.Equal(FfmpegBinarySource.System, loader.LastSource);
    }

    [Fact]
    public void Initialize_AbiMismatch_CarriesTheLoaderDiagnostic()
    {
        var loader = new StubFfmpegLibraryLoader
        {
            SimulateAbiMismatch = true,
            AbiMismatchMessage = "Stub: libavcodec is 62.0.100, expected 61.x.",
        };

        var result = Create(loader).Initialize();

        Assert.Contains("libavcodec is 62.0.100", result.Message);
    }
}
