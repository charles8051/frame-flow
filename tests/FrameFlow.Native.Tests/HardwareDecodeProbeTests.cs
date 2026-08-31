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
