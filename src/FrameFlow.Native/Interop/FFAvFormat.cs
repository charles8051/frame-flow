// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Source-generated P/Invoke declarations for <c>libavformat</c>.
/// </summary>
/// <remarks>
/// Phase 02 surface: container open, stream info, packet read, seek, and close.
/// Additional declarations will be added in later phases (ADR-0011).
/// All functions target FFmpeg 7.x (libavformat-63).
/// </remarks>
internal static partial class FFAvFormat
{
    /// <summary>
    /// Returns the packed version integer for the loaded <c>libavformat</c> build.
    /// </summary>
    /// <remarks>
    /// Read at bootstrap by <c>FrameFlow.Native.Core.FfmpegAbiCheck</c>. Every library
    /// in the set is checked, not just <c>libavutil</c>: the generated struct layouts
    /// FrameFlow overlays come from these libraries' own headers, and a search path
    /// that resolves them from different FFmpeg generations would otherwise pass a
    /// check that only looked at one of them.
    /// </remarks>
    [LibraryImport("avformat")]
    internal static partial uint avformat_version();

    /// <summary>
    /// Opens an input stream and reads the header. On success <paramref name="ctx"/> is
    /// set to the allocated <c>AVFormatContext*</c>; on failure it is set to
    /// <see cref="nint.Zero"/> and the return value is a negative AVERROR code.
    /// </summary>
    /// <remarks>
    /// Ownership: on success the caller owns the context and must release it with
    /// <see cref="avformat_close_input"/>. On failure no resources are allocated.
    /// </remarks>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avformat_open_input(
        ref nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        nint fmt,
        nint options
    );

    /// <summary>
    /// Overload taking the options dictionary by reference, for a caller that has demuxer
    /// options to pass. On return <paramref name="options"/> holds the entries the demuxer
    /// did not recognise; it is still the caller's to free either way, including on failure.
    /// </summary>
    /// <remarks>
    /// Same entry point as the four-<c>nint</c> form above, which stays for the callers that
    /// pass no options: <c>nint.Zero</c> binds to that one, a <c>ref</c> argument to this.
    /// </remarks>
    [LibraryImport("avformat", EntryPoint = "avformat_open_input")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avformat_open_input(
        ref nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        nint fmt,
        ref nint options
    );

    /// <summary>
    /// Looks up a demuxer by its short name (<c>image2</c>, <c>mp4</c>, ...). Returns the
    /// <c>const AVInputFormat*</c> to hand to <c>avformat_open_input</c> as its format
    /// argument, forcing that demuxer instead of probing, or <see cref="nint.Zero"/> when
    /// no demuxer has that name.
    /// </summary>
    /// <remarks>
    /// The returned pointer is static storage inside libavformat. It is not owned and must
    /// not be freed.
    /// </remarks>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint av_find_input_format(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string short_name
    );

    /// <summary>
    /// Reads packets of a media file to get stream information. Should be called
    /// after <see cref="avformat_open_input"/> and before any packet reading.
    /// </summary>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avformat_find_stream_info(nint ctx, nint options);

    /// <summary>
    /// Closes an opened input <c>AVFormatContext</c> and frees all associated resources.
    /// Sets <paramref name="ctx"/> to <see cref="nint.Zero"/> on return.
    /// </summary>
    /// <remarks>
    /// Ownership: after this call <paramref name="ctx"/> is no longer valid.
    /// This is the required disposal path for every context opened with
    /// <see cref="avformat_open_input"/>.
    /// </remarks>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void avformat_close_input(ref nint ctx);

    /// <summary>
    /// Returns the next frame from the media stream. The packet data is reference-counted
    /// and must be freed by the caller using <c>av_packet_unref</c>.
    /// </summary>
    /// <returns>
    /// 0 on success; AVERROR_EOF at end of stream; negative AVERROR on error.
    /// </returns>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_read_frame(nint ctx, nint pkt);

    /// <summary>
    /// Seeks to a keyframe at or before the given <paramref name="timestamp"/> in the
    /// specified stream. Use <paramref name="flags"/> to control direction and precision
    /// (e.g. <c>AVSEEK_FLAG_BACKWARD</c>).
    /// </summary>
    /// <param name="ctx">Open <c>AVFormatContext*</c> to seek in.</param>
    /// <param name="streamIndex">
    /// Stream index to seek in, or -1 to use a default stream.
    /// </param>
    /// <param name="timestamp">
    /// Target timestamp in the stream's time base when <paramref name="streamIndex"/> is
    /// non-negative, or in <c>AV_TIME_BASE</c> (microseconds) when it is -1.
    /// </param>
    /// <param name="flags">
    /// Seek flags. Use <c>0</c> for default (seek forward to nearest keyframe).
    /// Use <c>AVSEEK_FLAG_BACKWARD = 1</c> to seek to the keyframe before the timestamp.
    /// </param>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_seek_frame(nint ctx, int streamIndex, long timestamp, int flags);

    /// <summary>
    /// Discards all internally buffered data the demuxer has read ahead. Pair
    /// with <see cref="av_seek_frame"/> on demuxers that don't invalidate
    /// pre-fetched per-stream packets when seeking — without this, post-seek
    /// <see cref="av_read_frame"/> calls can return stale packets (with
    /// pre-seek PTS values) before the demuxer catches up to the new file
    /// position.
    /// </summary>
    /// <remarks>
    /// Idempotent and safe to call even when the buffer is already empty
    /// (returns void; no error code). Codec contexts attached to the
    /// streams are independent — call <c>avcodec_flush_buffers</c> on each
    /// open codec separately.
    /// </remarks>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avformat_flush(nint ctx);

    /// <summary>
    /// <c>AV_TIME_BASE</c> — the number of microseconds per second used as the base for
    /// timestamps passed to <see cref="av_seek_frame"/> when <c>stream_index</c> is -1.
    /// </summary>
    internal const int AvTimeBase = 1_000_000;

    /// <summary>
    /// Seek flag: seek to the keyframe before (or at) the requested timestamp rather than
    /// the nearest keyframe in either direction.
    /// </summary>
    internal const int AvseekFlagBackward = 1;
}
