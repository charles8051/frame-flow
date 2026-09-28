// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// A set of <see cref="FrameMemoryDomain"/>s: the domains a node accepts, or that a source can
/// hand out (#435).
/// </summary>
[Flags]
public enum FrameMemoryDomains
{
    /// <summary>No domain.</summary>
    None = 0,

    /// <summary>Frames in CPU-accessible system memory.</summary>
    Cpu = 1,

    /// <summary>Frames in GPU device memory.</summary>
    Gpu = 2,

    /// <summary>Either.</summary>
    Any = Cpu | Gpu,
}
