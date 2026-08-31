// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// A <see cref="SafeHandle"/> that owns an output <c>AVFormatContext*</c>
/// allocated by <c>avformat_alloc_output_context2</c> (ADR-0040 muxer path).
/// </summary>
/// <remarks>
/// <para>
/// Ownership rule (ADR-0005): the muxer that allocates the output context is
/// the sole owner. Ownership transfers to this handle on construction and is
/// released deterministically when the handle is disposed or finalized.
/// </para>
/// <para>
/// Release order matters: an attached AVIO context (the file handle in
/// <c>pb</c>) must be closed with <c>avio_closep</c> before
/// <c>avformat_free_context</c> frees the struct, otherwise the OS file handle
/// leaks and — on Windows — keeps the output file locked. <see cref="ReleaseHandle"/>
/// closes <c>pb</c> defensively even if the muxer already did so on the happy
/// path (<c>avio_closep</c> nulls <c>pb</c>, making the second close a no-op).
/// </para>
/// <para>
/// The handle is <see langword="internal"/>; callers outside
/// <c>FrameFlow.Native</c> and the encode layer must not access the raw
/// pointer value.
/// </para>
/// </remarks>
internal sealed class OutputFormatContextHandle : SafeHandle
{
    /// <summary>
    /// Initializes a new <see cref="OutputFormatContextHandle"/> that takes
    /// ownership of the given output format context pointer.
    /// </summary>
    /// <param name="handle">
    /// The <c>AVFormatContext*</c> returned by
    /// <c>avformat_alloc_output_context2</c>. Must not be <see cref="nint.Zero"/>.
    /// </param>
    internal OutputFormatContextHandle(nint handle)
        : base(invalidHandleValue: nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == nint.Zero;

    /// <summary>
    /// Closes any attached AVIO file context, then frees the output format
    /// context (which also frees its streams and codec parameters).
    /// </summary>
    protected override bool ReleaseHandle()
    {
        // Defensive AVIO close: only when the muxer did not manage its own
        // I/O (MP4 always opens a file). For AVFMT_NOFILE muxers pb belongs
        // to the muxer and must not be closed here.
        var accessor = new AvOutputFormatContextAccessor(handle);
        if ((accessor.OutputFormatFlags & FFAvFormat.AvfmtNoFile) == 0)
        {
            nint pb = accessor.Pb;
            if (pb != nint.Zero)
            {
                FFAvFormat.avio_closep(ref pb);
                accessor.Pb = pb; // pb is now nint.Zero
            }
        }

        FFAvFormat.avformat_free_context(handle);
        handle = nint.Zero;
        return true;
    }
}
