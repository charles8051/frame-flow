// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Diagnostics;

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// One hardware decode pool, from the frame that revealed it until nothing uses it: the
/// decoder that created it and every <see cref="GpuVideoFrame"/> built from its surfaces.
/// </summary>
/// <remarks>
/// <para>
/// The pool is an FFmpeg <c>AVHWFramesContext</c>, reference-counted by the codec context and
/// by every frame cloned from it, so it outlives its decoder while frames are held. Its
/// surfaces stay in <see cref="DecodePoolMetrics.Capacity"/> for exactly that long, so the
/// capacity and the outstanding count describe the same live surfaces.
/// </para>
/// <para>
/// A renegotiation in <c>get_format</c> creates a new pool. The decoder then lets go of the old
/// generation, and the old one leaves the capacity when its last frame is released.
/// </para>
/// </remarks>
internal sealed class DecodePoolGeneration
{
    private int _holders = 1; // the decoder that saw the pool first

    /// <param name="framesContext">The <c>AVHWFramesContext*</c>, used only as the pool's identity.</param>
    /// <param name="size">The pool's <c>initial_pool_size</c>.</param>
    public DecodePoolGeneration(nint framesContext, int size)
    {
        FramesContext = framesContext;
        Size = size;
        DecodePoolMetrics.OnPoolCapacityChanged(size);
    }

    /// <summary>The pool's identity. Never dereferenced.</summary>
    public nint FramesContext { get; }

    /// <summary>The number of surfaces in the pool.</summary>
    public int Size { get; }

    /// <summary>A frame built from this pool's surfaces holds the pool too.</summary>
    public void Retain() => Interlocked.Increment(ref _holders);

    /// <summary>
    /// Releases one holder. The last release takes the pool's surfaces out of the capacity.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _holders) == 0)
            DecodePoolMetrics.OnPoolCapacityChanged(-Size);
    }
}
