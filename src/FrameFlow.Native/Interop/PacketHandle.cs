// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// A <see cref="SafeHandle"/> that owns an <c>AVPacket*</c> allocated by
/// <c>av_packet_alloc</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ownership rule (ADR-0005): the component that allocates the packet is the sole
/// owner. Ownership transfers to this handle on construction and is released
/// deterministically when the handle is disposed or finalized.
/// </para>
/// <para>
/// The handle is <see langword="internal"/>; callers outside <c>FrameFlow.Native</c>
/// and <c>FrameFlow.Decoding</c> must not access the raw pointer value.
/// </para>
/// <para>
/// Thread safety: <see cref="SafeHandle"/> uses atomic reference counting internally.
/// The underlying <c>AVPacket</c> operations are not thread-safe; callers must
/// ensure only one thread operates on this handle at a time.
/// </para>
/// </remarks>
internal sealed class PacketHandle : SafeHandle
{
    /// <summary>
    /// Initializes a new <see cref="PacketHandle"/> that takes ownership of the
    /// given native packet pointer.
    /// </summary>
    /// <param name="handle">
    /// The <c>AVPacket*</c> returned by <c>av_packet_alloc</c>.
    /// Must not be <see cref="nint.Zero"/>.
    /// </param>
    internal PacketHandle(nint handle)
        : base(invalidHandleValue: nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == nint.Zero;

    /// <summary>
    /// Releases the native packet by calling <c>av_packet_free</c>, which also unrefs
    /// all reference-counted data buffers attached to the packet.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        // av_packet_free is in libavcodec in FFmpeg 7.x, not libavutil.
        FFAvCodec.av_packet_free(ref handle);
        return true;
    }
}
