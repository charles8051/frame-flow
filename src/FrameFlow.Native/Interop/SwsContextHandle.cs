// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// A <see cref="SafeHandle"/> that owns an <c>SwsContext*</c> allocated by
/// <c>sws_getContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ownership rule (ADR-0005): the <see cref="VideoDecoder"/> allocates the sws context
/// for the lifetime of the decoder session and is the sole owner. The handle is disposed
/// when the decoder is disposed.
/// </para>
/// <para>
/// The handle is <see langword="internal"/>; the raw pointer must never be exposed
/// outside <c>FrameFlow.Native</c> or <c>FrameFlow.Decoding</c>.
/// </para>
/// <para>
/// Thread safety: <see cref="SafeHandle"/> uses atomic reference counting internally.
/// <c>sws_scale</c> itself is not thread-safe; callers must serialize access.
/// </para>
/// </remarks>
internal sealed class SwsContextHandle : SafeHandle
{
    /// <summary>
    /// Initializes a new <see cref="SwsContextHandle"/> that takes ownership of the
    /// given swscale context pointer.
    /// </summary>
    /// <param name="handle">
    /// The <c>SwsContext*</c> returned by <c>sws_getContext</c>.
    /// Must not be <see cref="nint.Zero"/>.
    /// </param>
    internal SwsContextHandle(nint handle)
        : base(invalidHandleValue: nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == nint.Zero;

    /// <summary>
    /// Releases the swscale context by calling <c>sws_freeContext</c>.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        FFSwScale.sws_freeContext(handle);
        handle = nint.Zero;
        return true;
    }
}
