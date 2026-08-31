// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// A <see cref="SafeHandle"/> that owns an <c>AVFrame*</c> allocated by
/// <c>av_frame_alloc</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ownership rule (ADR-0005): the decoder allocates the <c>AVFrame</c>, owns it
/// throughout the decode-and-copy cycle, and frees it via this handle.
/// After pixel data has been copied into the managed output buffer, the handle should
/// be disposed to release native frame memory — never pass the raw handle across a
/// subsystem boundary.
/// </para>
/// <para>
/// Per ADR-0012, pixel data crosses queue boundaries as pooled managed memory
/// (<see cref="System.Buffers.IMemoryOwner{T}"/>), not as a live <c>AVFrame</c> pointer.
/// </para>
/// <para>
/// Thread safety: <see cref="SafeHandle"/> provides atomic reference counting.
/// The underlying <c>AVFrame</c> must be accessed by one thread at a time.
/// </para>
/// </remarks>
internal sealed class FrameHandle : SafeHandle
{
    /// <summary>
    /// Initializes a new <see cref="FrameHandle"/> that takes ownership of the
    /// given native frame pointer.
    /// </summary>
    /// <param name="handle">
    /// The <c>AVFrame*</c> returned by <c>av_frame_alloc</c>.
    /// Must not be <see cref="nint.Zero"/>.
    /// </param>
    internal FrameHandle(nint handle)
        : base(invalidHandleValue: nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == nint.Zero;

    /// <summary>
    /// Releases the native frame by calling <c>av_frame_free</c>, which also unrefs
    /// all reference-counted buffers attached to the frame.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        FFAvUtil.av_frame_free(ref handle);
        return true;
    }
}
