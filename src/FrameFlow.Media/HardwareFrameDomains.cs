// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>Where a hardware decode backend's frames are (#566).</summary>
public static class HardwareFrameDomains
{
    /// <summary>The GPU domain <paramref name="backend"/> decodes into.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="backend"/> is not a defined value.</exception>
    public static FrameMemoryDomains Of(HardwareDecodeBackendKind backend) =>
        backend switch
        {
            HardwareDecodeBackendKind.Cuda => FrameMemoryDomains.Cuda,
            HardwareDecodeBackendKind.VaApi => FrameMemoryDomains.VaApi,
            HardwareDecodeBackendKind.D3D11Va => FrameMemoryDomains.D3D11,
            HardwareDecodeBackendKind.Dxva2 => FrameMemoryDomains.Dxva2,
            HardwareDecodeBackendKind.VideoToolbox => FrameMemoryDomains.VideoToolbox,
            HardwareDecodeBackendKind.Qsv => FrameMemoryDomains.Qsv,
            HardwareDecodeBackendKind.MediaCodec => FrameMemoryDomains.MediaCodec,
            HardwareDecodeBackendKind.Vulkan => FrameMemoryDomains.Vulkan,
            HardwareDecodeBackendKind.Drm => FrameMemoryDomains.Drm,
            HardwareDecodeBackendKind.Vdpau => FrameMemoryDomains.Vdpau,
            HardwareDecodeBackendKind.D3D12Va => FrameMemoryDomains.D3D12,
            HardwareDecodeBackendKind.OpenCl => FrameMemoryDomains.OpenCl,
            HardwareDecodeBackendKind.Other => FrameMemoryDomains.OtherGpu,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Not a hardware decode backend."),
        };
}
