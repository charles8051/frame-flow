// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// Identifies the memory domain where a tensor or audio-buffer's data
/// resides. Used by <see cref="ITensor.MemoryDomain"/> and
/// <see cref="IAudioBuffer.MemoryDomain"/> for diagnostics and for
/// consumers that need to dispatch on domain explicitly.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0012, the substrate does not negotiate domain compatibility
/// at the sink boundary. Conversions are explicit pipeline operators
/// (typically <c>Transform</c>) written by the consumer at the call
/// site where the conversion happens; the enum is for inspection and
/// branching, not for runtime advertisement.
/// </para>
/// <para>
/// The capability-handle layer that ADR-0001 §4 anticipated (a parallel
/// <c>IFrameMemoryDomain</c> with device identity, queue handles, sync
/// primitives) is now expected to grow as operators with parameters
/// rather than as fields on an interface — per ADR-0012's "explicit
/// conversions over implicit negotiation" stance.
/// </para>
/// </remarks>
public enum FrameMemoryDomain
{
    /// <summary>Frame or tensor data is in CPU-accessible system memory.</summary>
    Cpu,

    /// <summary>
    /// Frame or tensor data is in GPU device memory. Used by
    /// <c>Crossbar.Cuda</c> tensors and by future D3D/Vulkan/Metal
    /// implementations. The enum doesn't carry device identity — when
    /// a sink needs to know <em>which</em> GPU, the capability-handle
    /// layer (future) is what answers that.
    /// </summary>
    Gpu,
}
