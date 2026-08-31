// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Phase 03 additions to the <c>libavutil</c> P/Invoke surface: frame
/// allocation/deallocation, timestamp rescaling, and error code constants.
/// </summary>
/// <remarks>
/// <para>
/// This is a partial class that extends <see cref="FFAvUtil"/> declared in
/// <c>FFAvUtil.cs</c>. Splitting by phase keeps the surface auditable.
/// Targets FFmpeg 7.x (libavutil-59).
/// </para>
/// <para>
/// Note: AVPacket lifecycle functions (<c>av_packet_alloc</c>, <c>av_packet_free</c>,
/// <c>av_packet_unref</c>) are declared in <see cref="FFAvCodec"/> because in FFmpeg 7.x
/// the AVPacket type lives in <c>libavcodec</c>, not <c>libavutil</c>.
/// </para>
/// </remarks>
internal static partial class FFAvUtil
{
    // -------------------------------------------------------------------------
    // AVFrame lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Allocates an <c>AVFrame</c> and sets its fields to default values.
    /// Returns the frame pointer on success, or <see cref="nint.Zero"/> on OOM.
    /// Caller must free with <see cref="av_frame_free"/>.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint av_frame_alloc();

    /// <summary>
    /// Frees the frame and all buffers referenced by it. Sets the pointer to
    /// <see langword="null"/>. Safe to call on a zero-initialised pointer.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void av_frame_free(ref nint frame);

    /// <summary>
    /// Unreferences all buffers held by the frame and resets its fields to defaults,
    /// without freeing the <c>AVFrame</c> struct itself. Use this to recycle a reusable
    /// frame between decoder calls instead of freeing and reallocating.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void av_frame_unref(nint frame);

    /// <summary>
    /// Allocates a new <c>AVFrame</c> and copies the source frame's properties
    /// plus a reference to each of its buffers. The returned frame shares the
    /// pixel data via reference counting — both source and clone must be
    /// freed independently with <see cref="av_frame_free"/>, and the
    /// underlying buffer is freed when the last reference drops.
    /// Used by the ADR-0038 GPU yield path: clone the decoder's per-call
    /// <c>AVFrame</c> so the consumer owns a stable reference to the
    /// device-side buffer.
    /// Returns the new frame pointer on success, or <see cref="nint.Zero"/>
    /// on OOM / invalid source.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint av_frame_clone(nint src);

    // -------------------------------------------------------------------------
    // Timestamp rescaling
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rescales a timestamp <paramref name="a"/> from time base <paramref name="bq"/> to
    /// <paramref name="cq"/> using integer arithmetic with rounding toward nearest.
    /// Returns <see cref="AvNoPtsValue"/> if either time base has a zero denominator.
    /// </summary>
    /// <param name="a">The timestamp value to rescale.</param>
    /// <param name="bq">Source time base (e.g. <c>new AvRational(1, 30)</c> for 30 fps).</param>
    /// <param name="cq">Destination time base.</param>
    /// <remarks>
    /// Both <see cref="AvRational"/> parameters are passed by value as 8-byte blittable
    /// structs, matching FFmpeg's <c>AVRational</c> ABI. The earlier broken version of this
    /// binding declared four loose <c>int</c> parameters, which spread the two struct
    /// arguments across four argument slots and caused the callee to read a garbage
    /// denominator on x64.
    /// </remarks>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial long av_rescale_q(long a, AvRational bq, AvRational cq);

    // -------------------------------------------------------------------------
    // av_opt_set_* — option setting for SwrContext (Phase 04)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sets a named integer option on an <c>AVClass</c>-headed object (e.g. <c>SwrContext</c>).
    /// </summary>
    /// <param name="obj">Pointer to the <c>AVClass*</c>-headed object.</param>
    /// <param name="name">Option name as a UTF-8 string.</param>
    /// <param name="val">Integer value to set.</param>
    /// <param name="searchFlags">Search flags; pass 0 for a direct object search.</param>
    /// <returns>0 on success; negative AVERROR on failure.</returns>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_opt_set_int(
        nint obj,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        long val,
        int searchFlags
    );

    /// <summary>
    /// Sets a named string option on an <c>AVClass</c>-headed object.
    /// Used for setting channel layout via <c>"in_chlayout"</c> / <c>"out_chlayout"</c>
    /// in FFmpeg 7.x where the legacy mask API is deprecated.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_opt_set(
        nint obj,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string val,
        int searchFlags
    );

    /// <summary>
    /// Sets a named channel-layout option using the new <c>AVChannelLayout</c> API
    /// (FFmpeg 5.1+). Pass the address of an <c>AVChannelLayout</c> struct.
    /// For standard stereo use <see cref="AvChLayoutStereo"/> via a pinned copy.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_opt_set_chlayout(
        nint obj,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nint layout,
        int searchFlags
    );

    /// <summary>
    /// Fills <paramref name="errbuf"/> with a human-readable description of the
    /// AVERROR code in <paramref name="errnum"/>. Useful for diagnostic messages.
    /// </summary>
    [LibraryImport("avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_strerror(int errnum, nint errbuf, nuint errbuf_size);

    // -------------------------------------------------------------------------
    // Error code constants
    // -------------------------------------------------------------------------

    /// <summary>
    /// AVERROR(EAGAIN) — returned by <c>avcodec_receive_frame</c> when more input
    /// is needed before a decoded frame can be produced.
    /// FFmpeg defines AVERROR(e) as -(e), so the value equals -EAGAIN for the
    /// platform FFmpeg was compiled for. Linux and Windows use EAGAIN=11 (-11);
    /// macOS/BSD use EAGAIN=35 (-35).
    /// </summary>
    internal static readonly int AvErrorEagain = OperatingSystem.IsMacOS() ? -35 : -11;

    /// <summary>
    /// AVERROR_EOF — returned when the stream or decoder has reached end of file.
    /// Defined as <c>FFERRTAG('E','O','F',' ')</c> in FFmpeg headers.
    /// </summary>
    internal const int AvErrorEof = unchecked((int)0xDFB9B0BB); // FFERRTAG('E','O','F',' ') = -(('E')|('O'<<8)|('F'<<16)|(' '<<24))

    /// <summary>AV_NOPTS_VALUE — sentinel used by FFmpeg when a timestamp is not known.</summary>
    internal const long AvNoPtsValue = unchecked((long)0x8000000000000000);

    /// <summary>AVMEDIA_TYPE_VIDEO — integer constant for the video media type.</summary>
    internal const int AvMediaTypeVideo = 0;

    /// <summary>AVMEDIA_TYPE_AUDIO — integer constant for the audio media type.</summary>
    internal const int AvMediaTypeAudio = 1;

    /// <summary>AV_TIME_BASE — microseconds per second, used as the global time base.</summary>
    internal const int AvTimeBase = 1_000_000;

    // -------------------------------------------------------------------------
    // Audio sample format constants (Phase 04)
    // -------------------------------------------------------------------------

    /// <summary>
    /// <c>AV_SAMPLE_FMT_S16</c> — signed 16-bit interleaved (packed) PCM.
    /// This is the normalised output format produced by the audio resampler.
    /// </summary>
    internal const int AvSampleFmtS16 = 1;

    /// <summary>
    /// <c>AV_CHANNEL_LAYOUT_STEREO</c> — stereo channel layout order value used
    /// when the new AVChannelLayout API is not available; fallback for legacy
    /// <c>av_opt_set_int</c> on <c>"out_channel_layout"</c>.
    /// <c>AV_CH_LAYOUT_STEREO</c> = 0x3 (front-left + front-right).
    /// </summary>
    internal const long AvChLayoutStereo = 0x3L;
}
