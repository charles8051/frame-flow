// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Identifies a hardware-decode backend family. Mirrors the subset of FFmpeg's
/// <c>AVHWDeviceType</c> enumeration that FrameFlow recognises by name; types
/// unknown to FrameFlow are surfaced as <see cref="Other"/>.
/// </summary>
/// <remarks>
/// The enum is independent of the FFmpeg integer values — those are kept inside
/// <c>FrameFlow.Native</c> per ADR-0005 (native concerns don't leak across layers).
/// </remarks>
public enum HardwareDecodeBackendKind
{
    /// <summary>NVIDIA CUDA (NVDEC). Cross-platform where the NVIDIA driver is installed.</summary>
    Cuda,

    /// <summary>Linux Video Acceleration API (Intel iGPU, AMD via Mesa, others).</summary>
    VaApi,

    /// <summary>Windows Direct3D 11 video acceleration.</summary>
    D3D11Va,

    /// <summary>Windows Direct3D 9 (DXVA2). Older path retained for legacy GPUs.</summary>
    Dxva2,

    /// <summary>Apple VideoToolbox (macOS / iOS).</summary>
    VideoToolbox,

    /// <summary>Intel Quick Sync Video.</summary>
    Qsv,

    /// <summary>Android MediaCodec.</summary>
    MediaCodec,

    /// <summary>Khronos Vulkan video.</summary>
    Vulkan,

    /// <summary>Linux Direct Rendering Manager (typically used in headless / embedded).</summary>
    Drm,

    /// <summary>NVIDIA VDPAU (legacy Linux path).</summary>
    Vdpau,

    /// <summary>Windows Direct3D 12 video acceleration.</summary>
    D3D12Va,

    /// <summary>OpenCL — included in some FFmpeg builds; rarely used as a decoder backend.</summary>
    OpenCl,

    /// <summary>An <c>AVHWDeviceType</c> that FrameFlow does not classify by name.</summary>
    Other,
}

/// <summary>
/// Describes one hardware-decode backend discovered during bootstrap.
/// </summary>
/// <param name="Kind">Classification of the backend (see <see cref="HardwareDecodeBackendKind"/>).</param>
/// <param name="DisplayName">A short human-readable name suitable for logs and UI.</param>
/// <param name="AvDeviceTypeName">
/// The string FFmpeg returns from <c>av_hwdevice_get_type_name</c> (e.g.
/// <c>"cuda"</c>, <c>"vaapi"</c>, <c>"d3d11va"</c>). Useful when callers
/// want to bypass FrameFlow's classification and match by raw FFmpeg name.
/// </param>
/// <param name="Initialized">
/// <see langword="true"/> when bootstrap successfully created a probe context
/// of this type. <see langword="false"/> when the backend is compiled in but
/// could not initialise on this host (no driver, no display, etc.).
/// </param>
/// <param name="DiagnosticMessage">
/// When <paramref name="Initialized"/> is <see langword="false"/>, a short
/// human-readable description of why initialisation failed. <see langword="null"/>
/// otherwise.
/// </param>
public sealed record HardwareDecodeBackend(
    HardwareDecodeBackendKind Kind,
    string DisplayName,
    string AvDeviceTypeName,
    bool Initialized,
    string? DiagnosticMessage
);

/// <summary>
/// The set of hardware-decode backends discovered at bootstrap time (ADR-0033).
/// </summary>
/// <remarks>
/// <para>
/// Computed once by <c>FrameFlowBootstrapper</c> and exposed on
/// <see cref="FrameFlowBootstrapResult.Capabilities"/>. Also registered as a
/// singleton service so consumers can inject it directly without retaining the
/// bootstrap result.
/// </para>
/// <para>
/// The list includes both initialised and non-initialised backends — consumers
/// filtering for "what can I actually use" should check
/// <see cref="HardwareDecodeBackend.Initialized"/>.
/// </para>
/// </remarks>
public sealed record HardwareDecodeCapabilities(IReadOnlyList<HardwareDecodeBackend> Available)
{
    /// <summary>
    /// Empty capabilities, used when probing is disabled or when bootstrap
    /// failed before reaching the probe phase.
    /// </summary>
    public static HardwareDecodeCapabilities Empty { get; } =
        new(Array.Empty<HardwareDecodeBackend>());
}
