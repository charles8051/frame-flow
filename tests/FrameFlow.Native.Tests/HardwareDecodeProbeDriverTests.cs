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
/// The probe itself needs FFmpeg and is not exercised here. What is testable, and what
/// carries the decision, is which backends get pre-checked and with which libraries.
/// <para>
/// The failure being guarded is an abort, not an error: these FFmpeg builds bind optional
/// drivers through <c>implib.so</c> stubs, and a stub that cannot <c>dlopen</c> its library
/// calls abort. On a Linux runner that took down three test assemblies —
/// <c>libcuda.so.1</c> first, then <c>libva-drm.so.2</c> once CUDA was guarded.
/// </para>
/// </remarks>
public sealed class HardwareDecodeProbeDriverTests
{
    [Fact]
    public void EveryBackendWithAStubbedDriver_IsPreChecked()
    {
        // The set is platform-specific, so assert the platform this test is running on.
        // Each entry is a backend whose driver FFmpeg binds through an implib stub.
        HardwareDecodeBackendKind[] expected = OperatingSystem.IsLinux()
            ?
            [
                HardwareDecodeBackendKind.Cuda,
                HardwareDecodeBackendKind.VaApi,
                HardwareDecodeBackendKind.Vdpau,
                HardwareDecodeBackendKind.Vulkan,
                HardwareDecodeBackendKind.OpenCl,
                HardwareDecodeBackendKind.Drm,
            ]
            : OperatingSystem.IsWindows()
                ? [HardwareDecodeBackendKind.Cuda, HardwareDecodeBackendKind.Vulkan]
                : [];

        foreach (var kind in expected)
        {
            Assert.NotEmpty(HardwareDecodeProbe.DriverLibrariesFor(kind));
        }
    }

    [Fact]
    public void VaApi_ChecksBothLibraries_NotJustTheHeadlineOne()
    {
        // The one that actually aborted was libva-drm.so.2, which is absent on hosts that
        // do have libva.so.2. Checking only the headline library would not have caught it.
        if (!OperatingSystem.IsLinux())
            return;

        var libraries = HardwareDecodeProbe.DriverLibrariesFor(HardwareDecodeBackendKind.VaApi);

        Assert.Contains("libva.so.2", libraries);
        Assert.Contains("libva-drm.so.2", libraries);
    }

    [Theory]
    [InlineData(HardwareDecodeBackendKind.D3D11Va)]
    [InlineData(HardwareDecodeBackendKind.Dxva2)]
    [InlineData(HardwareDecodeBackendKind.VideoToolbox)]
    [InlineData(HardwareDecodeBackendKind.MediaCodec)]
    [InlineData(HardwareDecodeBackendKind.Other)]
    public void OsIntegratedBackends_KeepThePlainAttemptAndReportPath(
        HardwareDecodeBackendKind kind
    )
    {
        // Nothing separate to dlopen, so nothing to pre-check. Skipping these would lose
        // detection on the platforms where they are the only backend that works.
        Assert.Empty(HardwareDecodeProbe.DriverLibrariesFor(kind));
    }

    [Fact]
    public void MacOs_PreChecksNothing()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        foreach (HardwareDecodeBackendKind kind in Enum.GetValues<HardwareDecodeBackendKind>())
        {
            Assert.Empty(HardwareDecodeProbe.DriverLibrariesFor(kind));
        }
    }

    [Fact]
    public void EveryListedLibrary_LooksLikeAPlatformLoaderName()
    {
        // A typo here would silently skip a working backend, since a name that cannot load
        // is indistinguishable from a driver that is not installed.
        foreach (HardwareDecodeBackendKind kind in Enum.GetValues<HardwareDecodeBackendKind>())
        {
            foreach (var library in HardwareDecodeProbe.DriverLibrariesFor(kind))
            {
                Assert.NotEmpty(library);

                if (OperatingSystem.IsWindows())
                    Assert.EndsWith(".dll", library, StringComparison.Ordinal);
                else
                    Assert.Contains(".so", library, StringComparison.Ordinal);
            }
        }
    }
}
