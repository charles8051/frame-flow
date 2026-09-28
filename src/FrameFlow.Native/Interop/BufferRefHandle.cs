// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// A <see cref="SafeHandle"/> that owns one reference to an <c>AVBufferRef*</c>, such as a
/// hardware frames context, and drops it with <c>av_buffer_unref</c>.
/// </summary>
/// <remarks>
/// Frames taken from a frames context hold references of their own, so dropping this one frees
/// the context only once the last of them is released.
/// </remarks>
internal sealed class BufferRefHandle : SafeHandle
{
    /// <summary>Takes ownership of <paramref name="bufferRef"/>, which must not be zero.</summary>
    internal BufferRefHandle(nint bufferRef)
        : base(invalidHandleValue: nint.Zero, ownsHandle: true)
    {
        SetHandle(bufferRef);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == nint.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        nint bufferRef = handle;
        FFAvUtil.av_buffer_unref(ref bufferRef);
        handle = nint.Zero;
        return true;
    }
}
