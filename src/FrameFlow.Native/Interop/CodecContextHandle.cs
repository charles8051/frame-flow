// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// A <see cref="SafeHandle"/> that owns an <c>AVCodecContext*</c> allocated by
/// <c>avcodec_alloc_context3</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ownership rule (ADR-0005): the component that allocates the codec context is the
/// sole owner. Ownership transfers to this handle on construction and is released
/// deterministically when the handle is disposed or finalized.
/// </para>
/// <para>
/// The handle is <see langword="internal"/>; callers outside <c>FrameFlow.Native</c>
/// must not access the raw pointer value.
/// </para>
/// <para>
/// Thread safety: <see cref="SafeHandle"/> uses atomic reference counting internally.
/// The underlying <c>AVCodecContext</c> operations are not thread-safe; callers must
/// ensure only one thread operates on this handle at a time.
/// </para>
/// </remarks>
internal sealed class CodecContextHandle : SafeHandle
{
    /// <summary>
    /// Initializes a new <see cref="CodecContextHandle"/> that takes ownership of the
    /// given native codec context pointer.
    /// </summary>
    /// <param name="handle">
    /// The <c>AVCodecContext*</c> returned by <c>avcodec_alloc_context3</c>.
    /// Must not be <see cref="nint.Zero"/>.
    /// </param>
    internal CodecContextHandle(nint handle)
        : base(invalidHandleValue: nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == nint.Zero;

    /// <summary>
    /// Releases the native codec context by calling <c>avcodec_free_context</c>.
    /// Called deterministically via <see cref="SafeHandle.Dispose()"/> and as a
    /// finalizer fallback.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        FFAvCodec.avcodec_free_context(ref handle);
        return true;
    }
}
