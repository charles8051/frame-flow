// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Media;

/// <summary>
/// An opaque handle to a decoded video frame with metadata, ref counting,
/// and domain-specific accessors.
/// </summary>
/// <remarks>
/// <para>
/// Frames are ref-counted (ADR-0080). A new frame starts at ref count 1. Each
/// <see cref="AddRef"/> call increments the count and returns the same instance;
/// each <see cref="IDisposable.Dispose"/> call decrements it. When the count
/// reaches zero the frame releases its storage.
/// </para>
/// <para>
/// V1 frames are always CPU-resident (<see cref="FrameMemoryDomain.Cpu"/>).
/// GPU domain accessors will be added when hardware decode backends land.
/// </para>
/// <para>
/// <b>Graph binding.</b> <see cref="IVideoFrame"/> extends
/// <see cref="Graph.IFrame"/> and <see cref="IRefCounted"/>, so a video
/// chain is a <c>GraphChain&lt;IVideoFrame&gt;</c> and the frame itself is
/// the graph item (ADR-0080, decision 6). The runtime requires Width, Height, Timestamp, and IDisposable;
/// FrameFlow's domain-natural name for the timestamp is
/// <see cref="Pts"/>, so <see cref="IFrame.Timestamp"/> is
/// provided here as a default-interface alias for Pts. Existing
/// IVideoFrame implementations remain source-compatible.
/// </para>
/// </remarks>
public interface IVideoFrame : IFrame, IRefCounted
{
    // ── Metadata (always available, zero cost) ────────

    /// <summary>Presentation timestamp of this frame.</summary>
    TimeSpan Pts { get; }

    /// <summary>Display duration of this frame.</summary>
    TimeSpan Duration { get; }

    // Width and Height are inherited from FrameFlow.Graph.IFrame.

    /// <summary>Pixel format of the frame data.</summary>
    PixelFormat Format { get; }

    /// <summary>Memory domain where the frame data resides.</summary>
    FrameMemoryDomain MemoryDomain { get; }

    /// <summary>
    /// The substrate-level timestamp, aliased to <see cref="Pts"/>
    /// via a default interface implementation. Implementations that have
    /// a more precise distinction between presentation timestamp and
    /// substrate timestamp can override this.
    /// </summary>
    TimeSpan IFrame.Timestamp => Pts;

    // ── Ref counting ──────────────────────────────────

    /// <summary>
    /// Increments the reference count and returns the same frame for fluent usage.
    /// </summary>
    /// <returns>This frame instance.</returns>
    new IVideoFrame AddRef();

    /// <summary>The graph's view of <see cref="AddRef"/>: the same call, the same instance.</summary>
    IRefCounted IRefCounted.AddRef() => AddRef();

    // ── Domain access ─────────────────────────────────

    /// <summary>
    /// Returns a <see cref="CpuFrameData"/> view if the frame is CPU-resident;
    /// otherwise <see langword="null"/>.
    /// </summary>
    CpuFrameData? AsCpu() => null;

    /// <summary>
    /// Forces a CPU copy of the frame data. For CPU-resident frames this is
    /// zero-cost; for GPU-resident frames this performs a read-back.
    /// </summary>
    CpuFrameData ToCpu();
}
