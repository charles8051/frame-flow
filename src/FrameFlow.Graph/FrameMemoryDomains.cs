// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// A set of memory domains: the domains a node accepts, or that a source can hand out (#435).
/// GPU memory is one domain per API that holds it (#566), since a node that reads one API's
/// frames cannot read another's.
/// </summary>
/// <remarks>
/// <see cref="Gpu"/> is every GPU domain named here. A declaration compiled against it takes the
/// domains named when it was compiled, so a domain added later is refused rather than claimed.
/// Test for any GPU domain with <c>(domains &amp; FrameMemoryDomains.Gpu) != 0</c>:
/// <see cref="Enum.HasFlag"/> asks for all of them.
/// </remarks>
[Flags]
public enum FrameMemoryDomains
{
    /// <summary>No domain.</summary>
    None = 0,

    /// <summary>Frames in CPU-accessible system memory.</summary>
    Cpu = 1,

    /// <summary>Direct3D 11 textures, as D3D11VA decodes into.</summary>
    D3D11 = 1 << 1,

    /// <summary>Direct3D 12 resources, as D3D12VA decodes into.</summary>
    D3D12 = 1 << 2,

    /// <summary>CUDA device memory, as NVDEC decodes into.</summary>
    Cuda = 1 << 3,

    /// <summary>Vulkan images, as Vulkan Video decodes into.</summary>
    Vulkan = 1 << 4,

    /// <summary>VA-API surfaces.</summary>
    VaApi = 1 << 5,

    /// <summary>Direct3D 9 surfaces, as DXVA2 decodes into.</summary>
    Dxva2 = 1 << 6,

    /// <summary>Core Video pixel buffers, as VideoToolbox decodes into.</summary>
    VideoToolbox = 1 << 7,

    /// <summary>Intel Quick Sync surfaces.</summary>
    Qsv = 1 << 8,

    /// <summary>Android MediaCodec buffers.</summary>
    MediaCodec = 1 << 9,

    /// <summary>DRM PRIME buffers.</summary>
    Drm = 1 << 10,

    /// <summary>VDPAU surfaces.</summary>
    Vdpau = 1 << 11,

    /// <summary>OpenCL images.</summary>
    OpenCl = 1 << 12,

    /// <summary>GPU memory of a backend FFmpeg reports and FrameFlow does not name.</summary>
    OtherGpu = 1 << 13,

    /// <summary>Every GPU domain.</summary>
    Gpu = D3D11 | D3D12 | Cuda | Vulkan | VaApi | Dxva2 | VideoToolbox | Qsv | MediaCodec | Drm | Vdpau | OpenCl | OtherGpu,

    /// <summary>Every domain.</summary>
    Any = Cpu | Gpu,
}
