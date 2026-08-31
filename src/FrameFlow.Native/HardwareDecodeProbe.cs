// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Native;

/// <summary>
/// Discovers which FFmpeg hardware-decode backends are compiled into the
/// loaded build and which of those actually initialise on this host
/// (ADR-0033, step 1).
/// </summary>
/// <remarks>
/// <para>
/// Runs exactly once during <see cref="FrameFlowBootstrapper.Initialize"/> after
/// the FFmpeg load has been confirmed. The probe enumerates types via
/// <c>av_hwdevice_iterate_types</c> and, for each, attempts
/// <c>av_hwdevice_ctx_create</c> with a default device specifier. The temporary
/// context is unref'd immediately — only the success/failure verdict is kept.
/// </para>
/// <para>
/// Cost on a typical machine: 10–50 ms across all compiled-in backends. Costs
/// rise on systems with multiple GPUs or contested device files; consumers can
/// disable probing via <see cref="FrameFlowNativeOptions.SkipHardwareProbe"/>.
/// </para>
/// </remarks>
internal static partial class HardwareDecodeProbe
{
    /// <summary>
    /// Walks the FFmpeg hwdevice types and returns a populated
    /// <see cref="HardwareDecodeCapabilities"/>. Never throws — failures per
    /// backend are captured in the corresponding
    /// <see cref="HardwareDecodeBackend.DiagnosticMessage"/>.
    /// </summary>
    internal static HardwareDecodeCapabilities Run(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var backends = new List<HardwareDecodeBackend>();
        int type = FFAvUtil.av_hwdevice_iterate_types(FFAvUtil.AvHwDeviceTypeNone);

        while (type != FFAvUtil.AvHwDeviceTypeNone)
        {
            var avName = FFAvUtil.AvHwDeviceGetTypeName(type);
            var kind = ClassifyBackend(type);
            var display = DisplayNameFor(kind);

            // Attempt to create a temporary device context. NULL device → use the
            // platform default (default GPU, default render node, etc.). We do not
            // pass any options — a more sophisticated probe could enumerate
            // devices per backend, but that is overkill for v1.
            int rc = FFAvUtil.av_hwdevice_ctx_create(
                out nint ctxRef,
                type,
                device: null,
                opts: nint.Zero,
                flags: 0
            );

            if (rc == 0 && ctxRef != nint.Zero)
            {
                FFAvUtil.av_buffer_unref(ref ctxRef);

                LogHwProbeSuccess(logger, avName, display);

                backends.Add(
                    new HardwareDecodeBackend(
                        Kind: kind,
                        DisplayName: display,
                        AvDeviceTypeName: avName,
                        Initialized: true,
                        DiagnosticMessage: null
                    )
                );
            }
            else
            {
                var diagnostic = $"av_hwdevice_ctx_create returned {rc} for type '{avName}'.";
                LogHwProbeFailure(logger, avName, display, rc);

                backends.Add(
                    new HardwareDecodeBackend(
                        Kind: kind,
                        DisplayName: display,
                        AvDeviceTypeName: avName,
                        Initialized: false,
                        DiagnosticMessage: diagnostic
                    )
                );
            }

            type = FFAvUtil.av_hwdevice_iterate_types(type);
        }

        return new HardwareDecodeCapabilities(backends);
    }

    /// <summary>
    /// Maps an FFmpeg <c>AVHWDeviceType</c> integer onto FrameFlow's
    /// <see cref="HardwareDecodeBackendKind"/>. Unknown values fall through to
    /// <see cref="HardwareDecodeBackendKind.Other"/> so the probe stays
    /// forward-compatible with new FFmpeg releases.
    /// </summary>
    internal static HardwareDecodeBackendKind ClassifyBackend(int avHwDeviceType) =>
        avHwDeviceType switch
        {
            FFAvUtil.AvHwDeviceTypeCuda => HardwareDecodeBackendKind.Cuda,
            FFAvUtil.AvHwDeviceTypeVaApi => HardwareDecodeBackendKind.VaApi,
            FFAvUtil.AvHwDeviceTypeD3D11Va => HardwareDecodeBackendKind.D3D11Va,
            FFAvUtil.AvHwDeviceTypeDxva2 => HardwareDecodeBackendKind.Dxva2,
            FFAvUtil.AvHwDeviceTypeVideoToolbox => HardwareDecodeBackendKind.VideoToolbox,
            FFAvUtil.AvHwDeviceTypeQsv => HardwareDecodeBackendKind.Qsv,
            FFAvUtil.AvHwDeviceTypeMediaCodec => HardwareDecodeBackendKind.MediaCodec,
            FFAvUtil.AvHwDeviceTypeVulkan => HardwareDecodeBackendKind.Vulkan,
            FFAvUtil.AvHwDeviceTypeDrm => HardwareDecodeBackendKind.Drm,
            FFAvUtil.AvHwDeviceTypeVdpau => HardwareDecodeBackendKind.Vdpau,
            FFAvUtil.AvHwDeviceTypeD3D12Va => HardwareDecodeBackendKind.D3D12Va,
            FFAvUtil.AvHwDeviceTypeOpenCl => HardwareDecodeBackendKind.OpenCl,
            _ => HardwareDecodeBackendKind.Other,
        };

    /// <summary>
    /// Returns the inverse mapping: from a FrameFlow
    /// <see cref="HardwareDecodeBackendKind"/> back to the FFmpeg
    /// <c>AVHWDeviceType</c> integer. Returns
    /// <see cref="FFAvUtil.AvHwDeviceTypeNone"/> for
    /// <see cref="HardwareDecodeBackendKind.Other"/> because there is no
    /// well-defined inverse.
    /// </summary>
    internal static int ToAvHwDeviceType(HardwareDecodeBackendKind kind) =>
        kind switch
        {
            HardwareDecodeBackendKind.Cuda => FFAvUtil.AvHwDeviceTypeCuda,
            HardwareDecodeBackendKind.VaApi => FFAvUtil.AvHwDeviceTypeVaApi,
            HardwareDecodeBackendKind.D3D11Va => FFAvUtil.AvHwDeviceTypeD3D11Va,
            HardwareDecodeBackendKind.Dxva2 => FFAvUtil.AvHwDeviceTypeDxva2,
            HardwareDecodeBackendKind.VideoToolbox => FFAvUtil.AvHwDeviceTypeVideoToolbox,
            HardwareDecodeBackendKind.Qsv => FFAvUtil.AvHwDeviceTypeQsv,
            HardwareDecodeBackendKind.MediaCodec => FFAvUtil.AvHwDeviceTypeMediaCodec,
            HardwareDecodeBackendKind.Vulkan => FFAvUtil.AvHwDeviceTypeVulkan,
            HardwareDecodeBackendKind.Drm => FFAvUtil.AvHwDeviceTypeDrm,
            HardwareDecodeBackendKind.Vdpau => FFAvUtil.AvHwDeviceTypeVdpau,
            HardwareDecodeBackendKind.D3D12Va => FFAvUtil.AvHwDeviceTypeD3D12Va,
            HardwareDecodeBackendKind.OpenCl => FFAvUtil.AvHwDeviceTypeOpenCl,
            _ => FFAvUtil.AvHwDeviceTypeNone,
        };

    private static string DisplayNameFor(HardwareDecodeBackendKind kind) =>
        kind switch
        {
            HardwareDecodeBackendKind.Cuda => "NVIDIA CUDA (NVDEC)",
            HardwareDecodeBackendKind.VaApi => "VA-API",
            HardwareDecodeBackendKind.D3D11Va => "Direct3D 11 Video Acceleration",
            HardwareDecodeBackendKind.Dxva2 => "DirectX Video Acceleration 2",
            HardwareDecodeBackendKind.VideoToolbox => "Apple VideoToolbox",
            HardwareDecodeBackendKind.Qsv => "Intel Quick Sync Video",
            HardwareDecodeBackendKind.MediaCodec => "Android MediaCodec",
            HardwareDecodeBackendKind.Vulkan => "Vulkan Video",
            HardwareDecodeBackendKind.Drm => "Linux DRM",
            HardwareDecodeBackendKind.Vdpau => "NVIDIA VDPAU",
            HardwareDecodeBackendKind.D3D12Va => "Direct3D 12 Video Acceleration",
            HardwareDecodeBackendKind.OpenCl => "OpenCL",
            _ => "Other hardware backend",
        };

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Hardware decode probe: {AvName} ({DisplayName}) initialized successfully."
    )]
    private static partial void LogHwProbeSuccess(
        ILogger logger,
        string avName,
        string displayName
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Hardware decode probe: {AvName} ({DisplayName}) failed to initialize (code {ReturnCode})."
    )]
    private static partial void LogHwProbeFailure(
        ILogger logger,
        string avName,
        string displayName,
        int returnCode
    );
}
