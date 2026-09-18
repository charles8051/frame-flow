using FrameFlow.Media;
using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Tests for the hardware-decode capability probe wired into
/// <see cref="FrameFlowBootstrapper"/> (ADR-0033).
/// </summary>
/// <remarks>
/// These tests assert behavioural invariants rather than the exact set of
/// backends present — that varies by CI runner. A CI matrix should layer
/// additional, runner-specific assertions on top.
/// </remarks>
public sealed class HardwareDecodeProbeTests
{
    [RequiresFfmpegFact]
    public void Initialize_PopulatesCapabilities_WhenProbingEnabled()
    {
        var ffmpegDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (ffmpegDir is null)
            return;

        var options = new FrameFlowNativeOptions { CustomFfmpegPath = ffmpegDir };
        var bootstrapper = new FrameFlowBootstrapper(options, NullLoggerFactory.Instance);

        var result = bootstrapper.Initialize();

        Assert.True(result.IsSuccess, $"Bootstrap failed: {result.Message}");
        // The capabilities object is never null — even on platforms where no
        // backends initialise it returns an empty list, not null.
        Assert.NotNull(result.Capabilities);
        // The list itself must be present (may be empty on stripped builds).
        Assert.NotNull(result.Capabilities.Available);
    }

    [RequiresFfmpegFact]
    public void Initialize_ReturnsEmptyCapabilities_WhenProbingDisabled()
    {
        var ffmpegDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (ffmpegDir is null)
            return;

        var options = new FrameFlowNativeOptions
        {
            CustomFfmpegPath = ffmpegDir,
            SkipHardwareProbe = true,
        };
        var bootstrapper = new FrameFlowBootstrapper(options, NullLoggerFactory.Instance);

        var result = bootstrapper.Initialize();

        Assert.True(result.IsSuccess);
        // Empty.Available must be the empty singleton — caps are skipped.
        Assert.Same(HardwareDecodeCapabilities.Empty, result.Capabilities);
        Assert.Empty(result.Capabilities.Available);
    }

    [RequiresFfmpegFact]
    public void BackendEntries_HaveDisplayNamesAndAvNames()
    {
        var ffmpegDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (ffmpegDir is null)
            return;

        var options = new FrameFlowNativeOptions { CustomFfmpegPath = ffmpegDir };
        var bootstrapper = new FrameFlowBootstrapper(options, NullLoggerFactory.Instance);

        var result = bootstrapper.Initialize();
        Assert.True(result.IsSuccess);

        foreach (var backend in result.Capabilities.Available)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(backend.DisplayName),
                $"Backend {backend.Kind} has no display name."
            );
            Assert.False(
                string.IsNullOrWhiteSpace(backend.AvDeviceTypeName),
                $"Backend {backend.Kind} has no AvDeviceTypeName."
            );
            // Invariant: when Initialized is true, DiagnosticMessage is null;
            // when false, DiagnosticMessage is populated.
            if (backend.Initialized)
                Assert.Null(backend.DiagnosticMessage);
            else
                Assert.False(string.IsNullOrWhiteSpace(backend.DiagnosticMessage));
        }
    }

    [RequiresFfmpegFact]
    public void Initialize_SeparateBootstrappers_ShareOneProbe()
    {
        // #37: the player builder builds a new bootstrapper for every player, so a
        // probe that belonged to the bootstrapper ran again for every player. The walk
        // depends on the loaded FFmpeg and the host, which do not change within a
        // process, so a second bootstrapper must get the first one's result.
        var ffmpegDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (ffmpegDir is null)
            return;

        var options = new FrameFlowNativeOptions { CustomFfmpegPath = ffmpegDir };

        var first = new FrameFlowBootstrapper(options, NullLoggerFactory.Instance).Initialize();
        var second = new FrameFlowBootstrapper(options, NullLoggerFactory.Instance).Initialize();

        Assert.True(first.IsSuccess, $"Bootstrap failed: {first.Message}");
        Assert.True(second.IsSuccess, $"Bootstrap failed: {second.Message}");
        Assert.Same(first.Capabilities, second.Capabilities);
    }

    [RequiresFfmpegFact]
    public void Initialize_DifferentBuildAfterLoad_FailsWithoutCachedCapabilities()
    {
        // The probe cache is only correct because FFmpeg loads once per process. A
        // bootstrapper that asks for a different build after a load has succeeded must fail
        // before it reaches the probe, not receive the capabilities of a build it did not get.
        var ffmpegDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (ffmpegDir is null)
            return;

        var loaded = new FrameFlowBootstrapper(
            new FrameFlowNativeOptions { CustomFfmpegPath = ffmpegDir },
            NullLoggerFactory.Instance
        ).Initialize();
        var other = new FrameFlowBootstrapper(
            new FrameFlowNativeOptions
            {
                CustomFfmpegPath = Path.Combine(Path.GetTempPath(), "not-the-loaded-ffmpeg"),
            },
            NullLoggerFactory.Instance
        ).Initialize();

        Assert.True(loaded.IsSuccess, $"Bootstrap failed: {loaded.Message}");
        Assert.False(other.IsSuccess);
        Assert.Same(HardwareDecodeCapabilities.Empty, other.Capabilities);
    }

    [RequiresFfmpegFact]
    public void GetOrRun_KeepsOneResultPerUncataloguedSetting()
    {
        // The two settings walk different sets of backends on Linux, so they cannot
        // share a result. Each is walked once and then reused.
        //
        // The uncatalogued walk is exercised only off Linux. On a Linux host without the
        // drivers it attempts backends whose lazy-loading stubs abort the process, which is
        // what HardwareDecodeProbe's remarks describe and why the setting defaults to false.
        // A GPU-less CI runner is exactly that host: running it there crashed the test host.
        var ffmpegDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (ffmpegDir is null)
            return;
        Assert.True(
            new FrameFlowBootstrapper(
                new FrameFlowNativeOptions { CustomFfmpegPath = ffmpegDir, SkipHardwareProbe = true },
                NullLoggerFactory.Instance
            )
                .Initialize()
                .IsSuccess
        );

        var catalogued = HardwareDecodeProbe.GetOrRun(NullLogger.Instance, probeUncatalogued: false, out _);
        var cataloguedAgain = HardwareDecodeProbe.GetOrRun(
            NullLogger.Instance,
            probeUncatalogued: false,
            out var reusedCatalogued
        );
        Assert.Same(catalogued, cataloguedAgain);
        Assert.True(reusedCatalogued);

        if (OperatingSystem.IsLinux())
            return;

        var uncatalogued = HardwareDecodeProbe.GetOrRun(NullLogger.Instance, probeUncatalogued: true, out _);
        var uncataloguedAgain = HardwareDecodeProbe.GetOrRun(
            NullLogger.Instance,
            probeUncatalogued: true,
            out var reusedUncatalogued
        );

        Assert.Same(uncatalogued, uncataloguedAgain);
        Assert.True(reusedUncatalogued);
        Assert.NotSame(catalogued, uncatalogued);
    }

    [Fact]
    public void HardwareDecodeCapabilities_Empty_IsSingleton()
    {
        Assert.Same(HardwareDecodeCapabilities.Empty, HardwareDecodeCapabilities.Empty);
        Assert.Empty(HardwareDecodeCapabilities.Empty.Available);
    }

    [Fact]
    public void ClassifyBackend_KnownTypes_RoundTrip()
    {
        // Test every known kind round-trips through the bridge unchanged.
        foreach (
            var kind in new[]
            {
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.VaApi,
                HardwareDecodeBackendKind.D3D11Va,
                HardwareDecodeBackendKind.Dxva2,
                HardwareDecodeBackendKind.VideoToolbox,
                HardwareDecodeBackendKind.Qsv,
                HardwareDecodeBackendKind.MediaCodec,
                HardwareDecodeBackendKind.Vulkan,
                HardwareDecodeBackendKind.Drm,
                HardwareDecodeBackendKind.Vdpau,
                HardwareDecodeBackendKind.D3D12Va,
                HardwareDecodeBackendKind.OpenCl,
            }
        )
        {
            var avType = HardwareDecodeProbe.ToAvHwDeviceType(kind);
            var roundTripped = HardwareDecodeProbe.ClassifyBackend(avType);
            Assert.Equal(kind, roundTripped);
        }
    }

    [Fact]
    public void ClassifyBackend_UnknownType_ReturnsOther()
    {
        // Pick a value far outside FFmpeg's enum range. ClassifyBackend must
        // never throw — forward-compatible with new FFmpeg releases.
        var result = HardwareDecodeProbe.ClassifyBackend(9999);
        Assert.Equal(HardwareDecodeBackendKind.Other, result);
    }
}
