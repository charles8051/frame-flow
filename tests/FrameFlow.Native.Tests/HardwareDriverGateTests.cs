// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Native.Core;
using Xunit;

namespace FrameFlow.Native.Tests;

/// <summary>
/// The driver gate as a pure decision: every host here is a fake, so each case runs on every
/// OS, whatever drivers the machine running the tests has.
/// </summary>
/// <remarks>
/// <see cref="HardwareDecodeProbeDriverTests"/> covers the same gate wired to the real host.
/// </remarks>
public sealed class HardwareDriverGateTests
{
    private static readonly HardwareDecodeBackendKind[] AllKinds =
        Enum.GetValues<HardwareDecodeBackendKind>();

    /// <summary>A host on which exactly <paramref name="installed"/> load.</summary>
    private static DriverHost Host(DriverHostOs os, bool renderNode, params string[] installed) =>
        new(os, installed.Contains, () => renderNode);

    private static DriverHost Linux(bool renderNode, params string[] installed) =>
        Host(DriverHostOs.Linux, renderNode, installed);

    // ── CUDA needs NVDEC's library, not only the device's ─────────────────────────────────

    [Fact]
    public void OnLinux_CudaWithoutLibnvcuvidIsSkipped()
    {
        // libcuda alone is enough for av_hwdevice_ctx_create to succeed, and NVDEC then fails
        // every stream loading libnvcuvid. A compute-only driver install looks like this.
        var host = Linux(renderNode: false, "libcuda.so.1");

        var reason = HardwareDriverGate.SkipReason(HardwareDecodeBackendKind.Cuda, host, false);

        Assert.NotNull(reason);
        Assert.Contains("libnvcuvid.so.1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnLinux_CudaWithBothLibrariesIsAttempted() =>
        Assert.Null(
            HardwareDriverGate.SkipReason(
                HardwareDecodeBackendKind.Cuda,
                Linux(renderNode: false, "libcuda.so.1", "libnvcuvid.so.1"),
                probeUncatalogued: false
            )
        );

    [Fact]
    public void OnLinux_CudaWithoutLibcudaIsSkipped()
    {
        var reason = HardwareDriverGate.SkipReason(
            HardwareDecodeBackendKind.Cuda,
            Linux(renderNode: false, "libnvcuvid.so.1"),
            probeUncatalogued: false
        );

        Assert.NotNull(reason);
        Assert.Contains("libcuda.so.1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWindows_CudaWithoutNvcuvidIsSkipped()
    {
        // The same loader binds nvcuvid.dll separately from nvcuda.dll.
        var reason = HardwareDriverGate.SkipReason(
            HardwareDecodeBackendKind.Cuda,
            Host(DriverHostOs.Windows, renderNode: false, "nvcuda.dll"),
            probeUncatalogued: false
        );

        Assert.NotNull(reason);
        Assert.Contains("nvcuvid.dll", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWindows_CudaWithBothLibrariesIsAttempted() =>
        Assert.Null(
            HardwareDriverGate.SkipReason(
                HardwareDecodeBackendKind.Cuda,
                Host(DriverHostOs.Windows, renderNode: false, "nvcuda.dll", "nvcuvid.dll"),
                probeUncatalogued: false
            )
        );

    // ── VAAPI needs X11 only on the path FFmpeg takes without a render node ───────────────

    [Fact]
    public void OnLinux_VaapiWithARenderNodeNeedsOnlyTheDrmLibraries() =>
        // A headless host with a working render node and no X11 keeps VAAPI.
        Assert.Null(
            HardwareDriverGate.SkipReason(
                HardwareDecodeBackendKind.VaApi,
                Linux(renderNode: true, "libva.so.2", "libva-drm.so.2"),
                probeUncatalogued: false
            )
        );

    [Fact]
    public void OnLinux_VaapiWithoutARenderNodeAndWithoutX11IsSkipped()
    {
        // FFmpeg would fall through to XOpenDisplay, whose stub aborts without libX11.
        var reason = HardwareDriverGate.SkipReason(
            HardwareDecodeBackendKind.VaApi,
            Linux(renderNode: false, "libva.so.2", "libva-drm.so.2"),
            probeUncatalogued: false
        );

        Assert.NotNull(reason);
        Assert.Contains("libX11.so.6", reason, StringComparison.Ordinal);
        Assert.Contains("render node", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnLinux_VaapiWithoutARenderNodeNeedsLibvaX11AsWell()
    {
        var reason = HardwareDriverGate.SkipReason(
            HardwareDecodeBackendKind.VaApi,
            Linux(renderNode: false, "libva.so.2", "libva-drm.so.2", "libX11.so.6"),
            probeUncatalogued: false
        );

        Assert.NotNull(reason);
        Assert.Contains("libva-x11.so.2", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnLinux_VaapiWithoutARenderNodeButWithX11IsAttempted() =>
        Assert.Null(
            HardwareDriverGate.SkipReason(
                HardwareDecodeBackendKind.VaApi,
                Linux(
                    renderNode: false,
                    "libva.so.2",
                    "libva-drm.so.2",
                    "libX11.so.6",
                    "libva-x11.so.2"
                ),
                probeUncatalogued: false
            )
        );

    [Fact]
    public void OnLinux_VaapiWithoutLibvaDrmIsSkippedEvenWithARenderNode()
    {
        // The library that aborted first: present libva, absent libva-drm.
        var reason = HardwareDriverGate.SkipReason(
            HardwareDecodeBackendKind.VaApi,
            Linux(renderNode: true, "libva.so.2"),
            probeUncatalogued: false
        );

        Assert.NotNull(reason);
        Assert.Contains("libva-drm.so.2", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRenderNodeIsOnlyAskedAboutForVaapiOnLinux()
    {
        // Opening device files is the shell's cost; no other backend's decision depends on it.
        int asked = 0;
        foreach (var os in Enum.GetValues<DriverHostOs>())
        {
            var host = new DriverHost(os, _ => true, () => { asked++; return true; });
            foreach (var kind in AllKinds.Where(k => k != HardwareDecodeBackendKind.VaApi))
                HardwareDriverGate.SkipReason(kind, host, probeUncatalogued: false);
        }

        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData("i915", true)]
    [InlineData("amdgpu", true)]
    [InlineData("nvidia", true)]
    // FFmpeg passes over vgem by name.
    [InlineData("vgem", false)]
    // FFmpeg passes over a node it cannot name, so an unknown driver is no evidence either.
    [InlineData(null, false)]
    public void OnlyANamedNonVgemNodeKeepsVaapiOnDrm(string? driver, bool keepsDrm) =>
        Assert.Equal(keepsDrm, HardwareDriverGate.RenderNodeKeepsVaapiOnDrm(driver));

    [Fact]
    public void TheRenderNodesAreTheEightFfmpegWalks() =>
        Assert.Equal(
            [
                "/dev/dri/renderD128",
                "/dev/dri/renderD129",
                "/dev/dri/renderD130",
                "/dev/dri/renderD131",
                "/dev/dri/renderD132",
                "/dev/dri/renderD133",
                "/dev/dri/renderD134",
                "/dev/dri/renderD135",
            ],
            HardwareDriverGate.VaapiRenderNodePaths
        );

    // ── The catalogue as a whole ──────────────────────────────────────────────────────────

    [Fact]
    public void AHostWithEveryLibraryAttemptsEveryCataloguedBackend()
    {
        foreach (var os in Enum.GetValues<DriverHostOs>())
        {
            foreach (bool renderNode in new[] { true, false })
            {
                var host = new DriverHost(os, _ => true, () => renderNode);
                foreach (var kind in AllKinds)
                {
                    if (HardwareDriverGate.RequirementsFor(kind, host).Count > 0)
                        Assert.Null(HardwareDriverGate.SkipReason(kind, host, false));
                }
            }
        }
    }

    [Fact]
    public void AHostWithNoLibrariesSkipsEveryCataloguedBackend_NamingTheFirstRequirement()
    {
        foreach (var os in Enum.GetValues<DriverHostOs>())
        {
            var host = new DriverHost(os, _ => false, () => true);
            foreach (var kind in AllKinds)
            {
                var requirements = HardwareDriverGate.RequirementsFor(kind, host);
                if (requirements.Count == 0)
                    continue;

                var reason = HardwareDriverGate.SkipReason(kind, host, false);
                Assert.NotNull(reason);
                Assert.Contains(requirements[0].Library, reason, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void OnLinux_AnUncataloguedBackendIsSkippedUnlessAsked()
    {
        var host = Linux(renderNode: true);

        Assert.NotNull(HardwareDriverGate.SkipReason(HardwareDecodeBackendKind.Qsv, host, false));
        Assert.Null(HardwareDriverGate.SkipReason(HardwareDecodeBackendKind.Qsv, host, true));
    }

    [Fact]
    public void OffLinux_AnUncataloguedBackendIsAttempted()
    {
        foreach (var os in new[] { DriverHostOs.Windows, DriverHostOs.MacOs })
        {
            var host = Host(os, renderNode: false);
            Assert.Null(HardwareDriverGate.SkipReason(HardwareDecodeBackendKind.D3D11Va, host, false));
            Assert.Null(
                HardwareDriverGate.SkipReason(HardwareDecodeBackendKind.VideoToolbox, host, false)
            );
        }
    }

    [Fact]
    public void MacOs_CataloguesNothing()
    {
        var host = new DriverHost(DriverHostOs.MacOs, _ => true, () => true);
        foreach (var kind in AllKinds)
            Assert.Empty(HardwareDriverGate.RequirementsFor(kind, host));
    }

    [Fact]
    public void EveryLibraryNameLooksLikeItsPlatformsLoaderName()
    {
        // A typo silently disables a working backend: a name that cannot load is
        // indistinguishable from a driver that is not installed.
        foreach (bool renderNode in new[] { true, false })
        {
            foreach (var kind in AllKinds)
            {
                var linux = new DriverHost(DriverHostOs.Linux, _ => true, () => renderNode);
                foreach (var r in HardwareDriverGate.RequirementsFor(kind, linux))
                    Assert.Matches(@"^lib[A-Za-z0-9_-]+\.so\.\d+$", r.Library);

                var windows = new DriverHost(DriverHostOs.Windows, _ => true, () => renderNode);
                foreach (var r in HardwareDriverGate.RequirementsFor(kind, windows))
                    Assert.Matches(@"^[a-z0-9_-]+\.dll$", r.Library);
            }
        }
    }
}
