// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native.Interop;

/// <summary>
/// Output-direction (muxing) additions to the <c>libavformat</c> P/Invoke
/// surface (ADR-0040): output-context allocation, stream creation, header /
/// trailer writing, interleaved packet writing, AVIO open/close, and context
/// teardown.
/// </summary>
/// <remarks>
/// <para>
/// Partial extension of <see cref="FFAvFormat"/> declared in
/// <c>FFAvFormat.cs</c> (which binds the demux/input direction). Targets
/// FFmpeg 7.x (libavformat-61).
/// </para>
/// <para>
/// All pointer parameters are <see cref="nint"/>; raw pointer values must not
/// escape outside <c>FrameFlow.Native</c> and the encode layer per ADR-0005.
/// </para>
/// </remarks>
internal static partial class FFAvFormat
{
    /// <summary>
    /// Allocates an <c>AVFormatContext</c> for output. On success
    /// <paramref name="ctx"/> receives the allocated <c>AVFormatContext*</c>.
    /// The muxer is selected by <paramref name="formatName"/> (e.g.
    /// <c>"mp4"</c>) when non-null, otherwise inferred from
    /// <paramref name="fileName"/>'s extension.
    /// </summary>
    /// <returns>
    /// A non-negative value on success; a negative AVERROR code on failure
    /// (in which case <paramref name="ctx"/> is <see cref="nint.Zero"/>).
    /// </returns>
    /// <remarks>
    /// Ownership: on success the caller owns the context and must release it
    /// with <see cref="avformat_free_context"/> (after closing any AVIO opened
    /// for it via <see cref="avio_closep"/>).
    /// </remarks>
    [LibraryImport("avformat", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avformat_alloc_output_context2(
        out nint ctx,
        nint oformat,
        string? formatName,
        string? fileName
    );

    /// <summary>
    /// Adds a new stream to an output <c>AVFormatContext</c>. Returns the new
    /// <c>AVStream*</c> on success, or <see cref="nint.Zero"/> on failure. The
    /// stream is owned by the format context and freed by
    /// <see cref="avformat_free_context"/> — do not free it directly.
    /// </summary>
    /// <param name="ctx">The output format context.</param>
    /// <param name="codec">
    /// Optional <c>AVCodec*</c> hint; pass <see cref="nint.Zero"/> when the
    /// stream's parameters are filled separately via
    /// <see cref="avcodec_parameters_from_context"/>.
    /// </param>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint avformat_new_stream(nint ctx, nint codec);

    /// <summary>
    /// Writes the container header to the output. Must be called after all
    /// streams are added and their parameters populated, and before any
    /// packet is written. The muxer may rewrite each stream's
    /// <c>time_base</c> during this call.
    /// </summary>
    /// <returns>
    /// 0 (<c>AVSTREAM_INIT_IN_WRITE_HEADER</c>) or 1
    /// (<c>AVSTREAM_INIT_IN_INIT_OUTPUT</c>) on success; a negative AVERROR
    /// code on failure. Treat any non-negative value as success.
    /// </returns>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avformat_write_header(nint ctx, nint options);

    /// <summary>
    /// Writes a packet to the output, buffering and reordering it by DTS so
    /// streams remain interleaved. The packet's timestamps must already be
    /// expressed in its stream's <c>time_base</c> and its
    /// <c>stream_index</c> set. The function takes ownership of the packet's
    /// data reference; the caller should still <c>av_packet_unref</c> it
    /// afterwards to reset the struct.
    /// </summary>
    /// <returns>0 on success; a negative AVERROR code on error.</returns>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_interleaved_write_frame(nint ctx, nint pkt);

    /// <summary>
    /// Writes the container trailer and flushes any buffered packets. For MP4
    /// this writes the <c>moov</c> atom (the index); the output file is not a
    /// valid, seekable MP4 until this returns. Must be the last write call
    /// before closing AVIO.
    /// </summary>
    /// <returns>0 on success; a negative AVERROR code on error.</returns>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int av_write_trailer(nint ctx);

    /// <summary>
    /// Frees an <c>AVFormatContext</c> (output or input allocated without
    /// <c>avformat_open_input</c>) and all streams and codec parameters it
    /// owns. Does <b>not</b> close an attached AVIO context — call
    /// <see cref="avio_closep"/> on <c>pb</c> first.
    /// </summary>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void avformat_free_context(nint ctx);

    /// <summary>
    /// Opens (creates/truncates) the file at <paramref name="url"/> for the
    /// output context and stores the resulting <c>AVIOContext*</c> in
    /// <paramref name="pb"/>. Required for every muxer that does not set
    /// <c>AVFMT_NOFILE</c> (MP4 always requires it).
    /// </summary>
    /// <param name="pb">
    /// Receives the opened <c>AVIOContext*</c>; assign it to the format
    /// context's <c>pb</c> field.
    /// </param>
    /// <param name="url">Output file path (UTF-8).</param>
    /// <param name="flags">AVIO flags; use <see cref="AvioFlagWrite"/>.</param>
    /// <returns>0 on success; a negative AVERROR code on error.</returns>
    [LibraryImport("avformat", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avio_open(ref nint pb, string url, int flags);

    /// <summary>
    /// Flushes and closes an <c>AVIOContext</c> and sets <paramref name="pb"/>
    /// to <see cref="nint.Zero"/>. Call before <see cref="avformat_free_context"/>.
    /// </summary>
    /// <returns>0 on success; a negative AVERROR code on error.</returns>
    [LibraryImport("avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int avio_closep(ref nint pb);

    /// <summary><c>AVIO_FLAG_WRITE</c> — open the AVIO context for writing.</summary>
    internal const int AvioFlagWrite = 2;

    /// <summary>
    /// <c>AVFMT_GLOBALHEADER</c> — set in <c>AVOutputFormat.flags</c> when the
    /// muxer wants the codec global headers in <c>extradata</c> rather than
    /// in-band. MP4 always sets this.
    /// </summary>
    internal const int AvfmtGlobalHeader = 0x0040;

    /// <summary>
    /// <c>AVFMT_NOFILE</c> — set in <c>AVOutputFormat.flags</c> for muxers that
    /// manage their own I/O (no <see cref="avio_open"/> needed). MP4 never
    /// sets this.
    /// </summary>
    internal const int AvfmtNoFile = 0x0001;
}
