// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Native.Core;

/// <summary>The operating-system families whose driver catalogues differ.</summary>
internal enum DriverHostOs
{
    Linux,
    Windows,
    MacOs,
}

/// <summary>
/// One driver library a backend needs, and the condition under which it needs it.
/// </summary>
/// <param name="Library">The name the platform loader is asked for.</param>
/// <param name="Because">
/// Why it is needed on this host when that is not simply "the backend uses it", or
/// <see langword="null"/>. Carried into the skip reason, so a human reading the probe
/// log can tell a conditional requirement from an unconditional one.
/// </param>
internal readonly record struct DriverRequirement(string Library, string? Because = null);

/// <summary>
/// What the shell observed about the host, as the inputs <see cref="HardwareDriverGate"/>
/// decides from.
/// </summary>
/// <param name="Os">The operating system.</param>
/// <param name="CanLoad">Whether the platform loader can load a named library here.</param>
/// <param name="HasOpenableRenderNode">
/// Whether a DRM render node that FFmpeg's VAAPI device creation would use can be opened.
/// Asked only when the answer matters (VAAPI on Linux).
/// </param>
internal sealed record DriverHost(
    DriverHostOs Os,
    Func<string, bool> CanLoad,
    Func<bool> HasOpenableRenderNode
);

/// <summary>
/// Decides which FFmpeg hardware-decode backends may be attempted, from the driver
/// libraries each one needs. Pure: every observation of the host comes in through
/// <see cref="DriverHost"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>CUDA needs more than <c>libcuda</c>.</b> nv-codec-headers' <c>dynlink_loader.h</c>
/// binds three separate driver libraries: <c>libcuda.so.1</c> (<c>cuda_load_functions</c>),
/// <c>libnvcuvid.so.1</c> (<c>cuvid_load_functions</c>) and <c>libnvidia-encode.so.1</c>
/// (<c>nvenc_load_functions</c>). FFmpeg's CUDA device creation (<c>hwcontext_cuda.c</c>)
/// loads only the first, so a probe that creates a device succeeds without the second. NVDEC
/// decode (<c>nvdec.c</c>, <c>nvdec_decoder_create</c>) loads <c>libnvcuvid.so.1</c> and fails
/// every stream without it. Driver installs that carry the compute libraries but not the video
/// ones exist, so the probe has to ask for both. Windows names them <c>nvcuda.dll</c> and
/// <c>nvcuvid.dll</c>.
/// </para>
/// <para>
/// <b>VAAPI needs the X11 libraries only when DRM finds nothing.</b> FFmpeg's
/// <c>vaapi_device_create</c> walks <c>/dev/dri/renderD128</c> to <c>renderD135</c> and uses
/// the first that opens (and is not passed over, see <see cref="HardwareDriverGate.RenderNodeKeepsVaapiOnDrm"/>)
/// through <c>vaGetDisplayDRM</c>. Once a node is chosen there is no way back to X11: a
/// <see langword="null"/> display from <c>vaGetDisplayDRM</c> returns <c>AVERROR_EXTERNAL</c>,
/// and a failing <c>vaInitialize</c> returns <c>AVERROR(EIO)</c>, both straight out of the
/// function. Only when no node is chosen does it try X11:
/// <c>XOpenDisplay</c> from <c>libX11.so.6</c>, then <c>vaGetDisplay</c> from
/// <c>libva-x11.so.2</c>. The Linux FFmpeg builds bind all four libva and X11 libraries through
/// <c>implib.so</c> stubs that abort when the library is missing, so the X11 pair is required
/// exactly when the X11 path would be taken. Requiring it unconditionally would cost a headless
/// host with a working render node its VAAPI for a library it never touches.
/// </para>
/// </remarks>
internal static class HardwareDriverGate
{
    /// <summary>The render nodes FFmpeg's VAAPI device creation tries, in order.</summary>
    /// <remarks>
    /// <c>hwcontext_vaapi.c</c> formats <c>/dev/dri/renderD%d</c> with <c>128 + n</c> for
    /// <c>n</c> below 8.
    /// </remarks>
    internal static IReadOnlyList<string> VaapiRenderNodePaths { get; } =
        [.. Enumerable.Range(128, 8).Select(n => $"/dev/dri/renderD{n}")];

    /// <summary>
    /// Whether an openable render node whose kernel driver is <paramref name="kernelDriver"/>
    /// is evidence that FFmpeg's VAAPI device creation stays on the DRM path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FFmpeg names the driver with <c>drmGetVersion</c> and passes over <c>vgem</c>, a
    /// virtual GEM provider with no video engine, and over any node whose version it cannot
    /// read. Either way it moves on, and with no other node, to X11.
    /// </para>
    /// <para>
    /// An unknown driver (<see langword="null"/>) is therefore not evidence: counting it
    /// would drop the X11 requirement on a host where FFmpeg may still take the X11 path,
    /// and that path aborts when its libraries are missing. The cost of the conservative
    /// answer is a host whose driver cannot be read and that lacks X11 losing VAAPI, which is
    /// recoverable where the abort is not.
    /// </para>
    /// </remarks>
    internal static bool RenderNodeKeepsVaapiOnDrm(string? kernelDriver) =>
        kernelDriver is not null && !string.Equals(kernelDriver, "vgem", StringComparison.Ordinal);

    private const string X11Fallback =
        "no DRM render node can be opened, so FFmpeg's VAAPI device creation falls back to X11";

    /// <summary>
    /// The driver libraries <paramref name="kind"/> needs on <paramref name="host"/> before
    /// <c>av_hwdevice_ctx_create</c>, and decoding on the device it makes, could succeed.
    /// Empty when nothing is catalogued for it: on Windows and macOS that means the OS
    /// supplies it, and on Linux that it is not known to be safe to attempt.
    /// </summary>
    internal static IReadOnlyList<DriverRequirement> RequirementsFor(
        HardwareDecodeBackendKind kind,
        DriverHost host
    ) =>
        host.Os switch
        {
            DriverHostOs.Linux => LinuxRequirementsFor(kind, host),
            DriverHostOs.Windows => kind switch
            {
                HardwareDecodeBackendKind.Cuda => [new("nvcuda.dll"), new("nvcuvid.dll")],
                HardwareDecodeBackendKind.Vulkan => [new("vulkan-1.dll")],
                _ => [],
            },
            // macOS: VideoToolbox is part of the OS, and CUDA has not existed since 10.14.
            _ => [],
        };

    private static IReadOnlyList<DriverRequirement> LinuxRequirementsFor(
        HardwareDecodeBackendKind kind,
        DriverHost host
    ) =>
        kind switch
        {
            HardwareDecodeBackendKind.Cuda => [new("libcuda.so.1"), new("libnvcuvid.so.1")],
            HardwareDecodeBackendKind.VaApi => host.HasOpenableRenderNode()
                ? [new("libva.so.2"), new("libva-drm.so.2")]
                :
                [
                    new("libva.so.2"),
                    new("libva-drm.so.2"),
                    new("libX11.so.6", X11Fallback),
                    new("libva-x11.so.2", X11Fallback),
                ],
            HardwareDecodeBackendKind.Vdpau => [new("libvdpau.so.1")],
            HardwareDecodeBackendKind.Vulkan => [new("libvulkan.so.1")],
            HardwareDecodeBackendKind.OpenCl => [new("libOpenCL.so.1")],
            HardwareDecodeBackendKind.Drm => [new("libdrm.so.2")],
            _ => [],
        };

    /// <summary>
    /// Why <paramref name="kind"/> must not be attempted on <paramref name="host"/>, or
    /// <see langword="null"/> to attempt it.
    /// </summary>
    /// <param name="kind">The backend under consideration.</param>
    /// <param name="host">What the shell observed about the host.</param>
    /// <param name="probeUncatalogued">
    /// When <see langword="true"/>, a backend with no catalogued drivers is attempted anyway.
    /// Off by default on Linux, where an uncatalogued attempt is how the process dies.
    /// </param>
    internal static string? SkipReason(
        HardwareDecodeBackendKind kind,
        DriverHost host,
        bool probeUncatalogued
    )
    {
        var requirements = RequirementsFor(kind, host);

        if (requirements.Count == 0)
        {
            // Nothing catalogued. On Windows and macOS that means the OS supplies it and
            // there is nothing to check. On Linux it means we cannot know whether the
            // attempt aborts, and the attempt is the thing that kills the process.
            return host.Os == DriverHostOs.Linux && !probeUncatalogued
                ? "no catalogued driver libraries, and an uncatalogued backend cannot be "
                    + "attempted safely on this platform (see ProbeUncataloguedBackends)"
                : null;
        }

        foreach (var requirement in requirements)
        {
            if (host.CanLoad(requirement.Library))
                continue;

            return requirement.Because is { } because
                ? $"driver library '{requirement.Library}' is not loadable on this host ({because})"
                : $"driver library '{requirement.Library}' is not loadable on this host";
        }

        return null;
    }
}
