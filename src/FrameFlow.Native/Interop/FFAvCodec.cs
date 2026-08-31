// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Source-generated P/Invoke declarations for <c>libavcodec</c>.
/// </summary>
/// <remarks>
/// Phase 02+ surface: codec name query (Phase 02), codec discovery, context allocation,
/// decoder open, and the send-packet / receive-frame decode loop (Phase 03).
/// All pointer parameters are <see cref="nint"/>; raw pointer values must not escape
/// outside <c>FrameFlow.Native</c> and <c>FrameFlow.Decoding</c> (ADR-0005).
/// Targets FFmpeg 7.x (libavcodec-61).
/// </remarks>
internal static partial class FFAvCodec
{
    /// <summary>
    /// Returns a string describing the codec with the given <paramref name="codecId"/>,
    /// or the literal string <c>"unknown"</c> when the ID is not registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 02: used by <c>DemuxSession.BuildMediaInfo</c> to populate
    /// <see cref="FrameFlow.Media.VideoStreamInfo.CodecName"/> and
    /// <see cref="FrameFlow.Media.AudioStreamInfo.CodecName"/> without requiring the
    /// caller to open a decoder.
    /// </para>
    /// <para>
    /// The native function returns a pointer to a <b>statically allocated</b> C string
    /// owned by FFmpeg. We must NOT use <c>UnmanagedType.LPUTF8Str</c> here because that
    /// marshaling attribute causes the runtime to call <c>CoTaskMemFree</c> on the returned
    /// pointer, which corrupts the FFmpeg heap and causes a native process crash.
    /// Instead we return the raw pointer as <see cref="nint"/> and convert via
    /// <see cref="avcodec_get_name"/> which calls
    /// <see cref="System.Runtime.InteropServices.Marshal.PtrToStringUTF8"/>.
    /// </para>
    /// </remarks>
    internal static string avcodec_get_name(int codecId)
    {
        nint ptr = avcodec_get_name_native(codecId);
        return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr) ?? "unknown";
    }

    [LibraryImport("avcodec", EntryPoint = "avcodec_get_name")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial nint avcodec_get_name_native(int codecId);

    /// <summary>
    /// Finds a registered decoder with the given codec ID.
    /// Returns the <c>AVCodec*</c> on success, or <see cref="nint.Zero"/> if not found.
    /// The returned pointer is owned by FFmpeg and must not be freed.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint avcodec_find_decoder(int id);

    /// <summary>
    /// Allocates and partially initialises a new codec context for the given codec.
    /// Returns the <c>AVCodecContext*</c> on success, or <see cref="nint.Zero"/> on OOM.
    /// Caller must free with <see cref="avcodec_free_context"/>.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint avcodec_alloc_context3(nint codec);

    /// <summary>
    /// Copies codec parameters from a stream's <c>AVCodecParameters</c> into a codec context.
    /// Must be called before <see cref="avcodec_open2"/>.
    /// Returns 0 on success or a negative AVERROR code.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avcodec_parameters_to_context(nint ctx, nint par);

    /// <summary>
    /// Initialises the codec context to use the given codec.
    /// Must be called after <see cref="avcodec_parameters_to_context"/>.
    /// Returns 0 on success or a negative AVERROR code.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avcodec_open2(nint ctx, nint codec, nint options);

    /// <summary>
    /// Supplies a raw compressed <c>AVPacket</c> to the decoder.
    /// </summary>
    /// <returns>
    /// 0 on success; AVERROR(EAGAIN) if the decoder needs output read before accepting
    /// more input; AVERROR_EOF if the decoder has been flushed; other negative codes on error.
    /// </returns>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avcodec_send_packet(nint ctx, nint pkt);

    /// <summary>
    /// Retrieves a decoded frame from the decoder into <paramref name="frame"/>.
    /// </summary>
    /// <returns>
    /// 0 on success; AVERROR(EAGAIN) if more input is needed before output is available;
    /// AVERROR_EOF when decoding is complete; other negative codes on error.
    /// </returns>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avcodec_receive_frame(nint ctx, nint frame);

    /// <summary>
    /// Frees the codec context and sets the pointer to <see langword="null"/>.
    /// This is the required disposal path for contexts allocated with
    /// <see cref="avcodec_alloc_context3"/>.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void avcodec_free_context(ref nint ctx);

    /// <summary>
    /// Flushes the decoder buffers. Must be called when seeking so that the next
    /// <see cref="avcodec_send_packet"/> starts from a clean state.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void avcodec_flush_buffers(nint ctx);

    /// <summary>AVERROR(EAGAIN) — decoder needs more input or output must be drained first.</summary>
    internal const int EAgain = -11;

    /// <summary>AVERROR_EOF — end of stream reached.</summary>
    internal const int AvErrorEof = unchecked((int)0xAFAFAFAF); // defined as FFERRTAG in FFmpeg — use platform value via FFAvUtil.AvErrorEof instead

    // -------------------------------------------------------------------------
    // AVPacket lifecycle
    //
    // In FFmpeg 7.x, AVPacket lives in libavcodec (libavcodec/packet.h).
    // Do NOT declare these in FFAvUtil / "avutil" — they will not be found.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Allocates an <c>AVPacket</c> and initialises its fields to default values.
    /// Returns the packet pointer on success, or <see cref="nint.Zero"/> on OOM.
    /// Caller must free with <see cref="av_packet_free"/>.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint av_packet_alloc();

    /// <summary>
    /// Frees the packet and all reference-counted data it holds.
    /// Sets the pointer to <see langword="null"/>.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void av_packet_free(ref nint pkt);

    /// <summary>
    /// Unreferences the packet's reference-counted data and resets its fields to defaults
    /// without freeing the <c>AVPacket</c> struct itself. Use this to recycle a reusable
    /// packet between <c>av_read_frame</c> / <c>avcodec_send_packet</c> calls.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void av_packet_unref(nint pkt);

    /// <summary>
    /// Creates a new reference to the data described by <paramref name="src"/>,
    /// copying into <paramref name="dst"/>. The caller owns the new ref and must
    /// call <see cref="av_packet_unref"/> on <paramref name="dst"/> when done.
    /// </summary>
    [LibraryImport("avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_packet_ref(nint dst, nint src);
}
