// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Native;
using Xunit;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Tests for the driver pre-check that keeps <see cref="HardwareDecodeProbe"/> from
/// attempting a backend whose driver library is absent.
/// </summary>
/// <remarks>
/// The probe itself needs FFmpeg, so it is not exercised here. What is testable, and what
/// carries the decision, is which backends get pre-checked and with what library name. The
/// crash this guards against was a Linux host with no NVIDIA driver: FFmpeg logged
/// "Cannot load libcuda.so.1" from inside av_hwdevice_ctx_create and the process died,
/// taking three test assemblies with it.
/// </remarks>
public sealed class HardwareDecodeProbeDriverTests
{
    [Fact]
    public void Cuda_IsPreChecked_OnTheTwoPlatformsThatHaveIt()
    {
        var driver = HardwareDecodeProbe.DriverLibraryFor(HardwareDecodeBackendKind.Cuda);

        if (OperatingSystem.IsWindows())
            Assert.Equal("nvcuda.dll", driver);
        else if (OperatingSystem.IsLinux())
            Assert.Equal("libcuda.so.1", driver);
        else
            Assert.Null(driver); // no CUDA on macOS since 10.14
    }

    [Theory]
    [InlineData(HardwareDecodeBackendKind.VaApi)]
    [InlineData(HardwareDecodeBackendKind.Vdpau)]
    [InlineData(HardwareDecodeBackendKind.D3D11Va)]
    [InlineData(HardwareDecodeBackendKind.Dxva2)]
    [InlineData(HardwareDecodeBackendKind.VideoToolbox)]
    [InlineData(HardwareDecodeBackendKind.Qsv)]
    [InlineData(HardwareDecodeBackendKind.MediaCodec)]
    [InlineData(HardwareDecodeBackendKind.Vulkan)]
    [InlineData(HardwareDecodeBackendKind.Drm)]
    [InlineData(HardwareDecodeBackendKind.D3D12Va)]
    [InlineData(HardwareDecodeBackendKind.OpenCl)]
    [InlineData(HardwareDecodeBackendKind.Other)]
    public void EveryOtherBackend_IsLeftToTheOrdinaryAttemptAndReportPath(
        HardwareDecodeBackendKind kind
    )
    {
        // Deliberate: only CUDA has been observed to crash. Guessing loader names for the
        // rest would skip backends that work, which is worse than the failure being guarded.
        Assert.Null(HardwareDecodeProbe.DriverLibraryFor(kind));
    }
}
