// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Native;

public sealed class FrameFlowNativeOptions
{
    public bool UseBundledBinaries { get; set; } = true;

    public bool ProbeSystemLibraries { get; set; } = true;

    public string? CustomFfmpegPath { get; set; }

    /// <summary>
    /// When <see langword="true"/>, skips the hardware-decode capability probe
    /// during bootstrap. The resulting <c>FrameFlowBootstrapResult.Capabilities</c>
    /// is <c>HardwareDecodeCapabilities.Empty</c>, and
    /// <c>HardwareDecodeMode.Auto</c> will fall through to software decode.
    /// </summary>
    /// <remarks>
    /// Useful for constrained environments (containers without GPU access, smoke
    /// tests) where the probe's "no device available" diagnostics would be noisy.
    /// Default is <see langword="false"/> — probing is on per ADR-0033.
    /// </remarks>
    public bool SkipHardwareProbe { get; set; }

    /// <summary>
    /// On Linux, attempt hardware backends whose driver libraries FrameFlow does not
    /// catalogue. Off by default. Has no effect on Windows or macOS, which attempt every
    /// uncatalogued backend regardless.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Linux FFmpeg builds bind optional drivers through <c>implib.so</c> stubs that
    /// <b>abort the process</b> when a library is missing, so an uncatalogued backend is
    /// not a probe that might fail — it is a probe that might kill the application. The
    /// default declines that trade.
    /// </para>
    /// <para>
    /// Set this on a host whose drivers are known to be installed, when a backend
    /// FrameFlow does not catalogue is wanted (Intel QSV is the likely one). The
    /// catalogued set already covers CUDA, VAAPI, VDPAU, Vulkan, OpenCL and DRM.
    /// </para>
    /// <para>
    /// It does not disable the driver check for backends that <i>are</i> catalogued, on
    /// any platform: one whose libraries will not load is still skipped, because it could
    /// not have initialised. This option is about the backends FrameFlow knows nothing
    /// about, not about overriding what it does know.
    /// </para>
    /// </remarks>
    public bool ProbeUncataloguedBackends { get; set; }
}
