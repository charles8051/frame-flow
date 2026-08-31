// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using FrameFlow.Native.Interop;

namespace FrameFlow.Native.Interop;

/// <summary>
/// A <see cref="SafeHandle"/> that owns an <c>AVFormatContext*</c> allocated by
/// <c>avformat_open_input</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ownership rule (ADR-0005): the component that opens the format context is the sole
/// owner. Ownership is transferred to this handle on construction and released
/// deterministically when the handle is disposed or finalized.
/// </para>
/// <para>
/// Callers outside <c>FrameFlow.Native</c> must not access the raw handle value —
/// it is <see langword="internal"/> by design.
/// </para>
/// <para>
/// Thread safety: <see cref="SafeHandle"/> uses atomic reference counting internally.
/// The underlying <c>AVFormatContext</c> operations are not thread-safe; callers must
/// ensure that only one thread operates on this handle at a time.
/// </para>
/// </remarks>
internal sealed class FormatContextHandle : SafeHandle
{
    /// <summary>
    /// Initializes a new <see cref="FormatContextHandle"/> that takes ownership of the
    /// given native format context pointer.
    /// </summary>
    /// <param name="handle">
    /// The <c>AVFormatContext*</c> value returned by <c>avformat_open_input</c>.
    /// Must not be <see cref="nint.Zero"/>.
    /// </param>
    internal FormatContextHandle(nint handle)
        : base(invalidHandleValue: nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == nint.Zero;

    /// <summary>
    /// Releases the native format context by calling <c>avformat_close_input</c>.
    /// Called deterministically via <see cref="SafeHandle.Dispose()"/> and as a
    /// finalizer fallback.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        // avformat_close_input expects a pointer-to-pointer and sets the pointer to null.
        FFAvFormat.avformat_close_input(ref handle);
        return true;
    }
}
