// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Native;
using Xunit;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Tests for the gate that decides which hardware backends <see cref="HardwareDecodeProbe"/>
/// is willing to attempt.
/// </summary>
/// <remarks>
/// <para>
/// The probe itself needs FFmpeg and is not exercised here. What is testable, and what
/// carries the decision, is the gate: on Linux it is an allowlist, everywhere else it is a
/// pre-check.
/// </para>
/// <para>
/// The failure being guarded is an abort, not an error. The Linux FFmpeg builds bind
/// optional drivers through <c>implib.so</c> stubs that call abort when a library is
/// missing, which took down three test assemblies. Two narrower attempts failed first:
/// guarding CUDA moved the abort to <c>libva-drm.so.2</c>, and guarding VAAPI too did not
/// stop it, because the caller was a different backend whose own libraries were present.
/// </para>
/// </remarks>
public sealed class HardwareDecodeProbeDriverTests
{
    private static readonly HardwareDecodeBackendKind[] AllKinds = Enum.GetValues<HardwareDecodeBackendKind>();

    // ── The Linux allowlist ───────────────────────────────────────────────────────────────

    [Fact]
    public void OnLinux_AnUncataloguedBackendIsNotAttempted()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // QSV is the specific one believed to have aborted: it reaches libva through
        // libmfx, so its own headline library being present proves nothing.
        Assert.NotNull(
            HardwareDecodeProbe.SkipReason(
                HardwareDecodeBackendKind.Qsv,
                probeUncatalogued: false
            )
        );
        Assert.NotNull(
            HardwareDecodeProbe.SkipReason(
                HardwareDecodeBackendKind.Other,
                probeUncatalogued: false
            )
        );
    }

    [Fact]
    public void OnLinux_TheEscapeHatchAllowsUncataloguedBackends()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Without a way back in, a host with working QSV loses it permanently.
        Assert.Null(
            HardwareDecodeProbe.SkipReason(HardwareDecodeBackendKind.Qsv, probeUncatalogued: true)
        );
    }

    [Fact]
    public void OffLinux_AnUncataloguedBackendIsAttempted()
    {
        if (OperatingSystem.IsLinux())
            return;

        // No abort has been seen on Windows or macOS, and their important backends are
        // OS-integrated with nothing separate to load. Skipping them would lose detection
        // for no benefit.
        Assert.Null(
            HardwareDecodeProbe.SkipReason(
                HardwareDecodeBackendKind.VideoToolbox,
                probeUncatalogued: false
            )
        );
        Assert.Null(
            HardwareDecodeProbe.SkipReason(
                HardwareDecodeBackendKind.D3D11Va,
                probeUncatalogued: false
            )
        );
    }

    [Fact]
    public void TheEscapeHatchChangesNothingOffLinux()
    {
        if (OperatingSystem.IsLinux())
            return;

        foreach (var kind in AllKinds)
        {
            Assert.Equal(
                HardwareDecodeProbe.SkipReason(kind, probeUncatalogued: false),
                HardwareDecodeProbe.SkipReason(kind, probeUncatalogued: true)
            );
        }
    }

    // ── The catalogue ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCatalogueCoversTheBackendsWorthKeepingOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        HardwareDecodeBackendKind[] catalogued =
        [
            HardwareDecodeBackendKind.Cuda,
            HardwareDecodeBackendKind.VaApi,
            HardwareDecodeBackendKind.Vdpau,
            HardwareDecodeBackendKind.Vulkan,
            HardwareDecodeBackendKind.OpenCl,
            HardwareDecodeBackendKind.Drm,
        ];

        foreach (var kind in catalogued)
            Assert.NotEmpty(HardwareDecodeProbe.DriverLibrariesFor(kind));
    }

    [Fact]
    public void VaApi_ListsBothLibraries_NotJustTheHeadlineOne()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // libva.so.2 is present on hosts that lack libva-drm.so.2, and the latter is what
        // aborted. Checking only the headline library would not have caught it.
        var libraries = HardwareDecodeProbe.DriverLibrariesFor(HardwareDecodeBackendKind.VaApi);

        Assert.Contains("libva.so.2", libraries);
        Assert.Contains("libva-drm.so.2", libraries);
    }

    [Fact]
    public void EveryCataloguedLibrary_LooksLikeAPlatformLoaderName()
    {
        // A typo silently disables a working backend, since a name that cannot load is
        // indistinguishable from a driver that is not installed.
        foreach (var kind in AllKinds)
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

    [Fact]
    public void MacOs_CataloguesNothing()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // VideoToolbox is part of the OS and CUDA has not existed there since 10.14.
        foreach (var kind in AllKinds)
            Assert.Empty(HardwareDecodeProbe.DriverLibrariesFor(kind));
    }

    // ── Skip reasons are for humans ───────────────────────────────────────────────────────

    [Fact]
    public void ASkipReason_NamesWhatIsMissing()
    {
        foreach (var kind in AllKinds)
        {
            if (HardwareDecodeProbe.SkipReason(kind, probeUncatalogued: false) is not { } reason)
                continue;

            Assert.NotEmpty(reason);
            // Either it names the library that would not load, or it says the backend is
            // uncatalogued and points at the way back in.
            var libraries = HardwareDecodeProbe.DriverLibrariesFor(kind);
            if (libraries.Count > 0)
                Assert.Contains(libraries.First(l => reason.Contains(l, StringComparison.Ordinal)), reason);
            else
                Assert.Contains("ProbeUncataloguedBackends", reason, StringComparison.Ordinal);
        }
    }
}
