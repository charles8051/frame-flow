// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// A managed, immutable representation of a demuxed media packet.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DemuxPacket"/> is the sole form in which packet data crosses the demux
/// boundary into higher layers. It contains no native pointers and carries no FFmpeg
/// types — all data has been copied into managed memory before this type is constructed
/// (ADR-0005).
/// </para>
/// <para>
/// Ownership: the caller that receives a <see cref="DemuxPacket"/> from
/// <c>IDemuxSession.ReadPacketAsync</c> owns the payload. The payload byte array is
/// allocated fresh for each packet. There is no pooling at this layer; callers that need
/// high-throughput packet processing should operate at a lower level.
/// </para>
/// <para>
/// Timestamp semantics: <see cref="Pts"/> is expressed in
/// <see cref="TimeSpan"/> normalized from the stream's native time base.
/// A value of <see cref="TimeSpan.Zero"/> indicates either a genuine zero-time packet or
/// a missing/unknown PTS (see <see cref="HasPts"/>). Always check <see cref="HasPts"/>
/// before using <see cref="Pts"/> for timing decisions.
/// </para>
/// </remarks>
public sealed class DemuxPacket
{
    /// <summary>
    /// Initializes a new <see cref="DemuxPacket"/>.
    /// </summary>
    /// <param name="streamIndex">Index of the stream this packet belongs to.</param>
    /// <param name="pts">
    /// Presentation timestamp normalized to <see cref="TimeSpan"/>, or
    /// <see cref="TimeSpan.Zero"/> when the packet has no PTS.
    /// </param>
    /// <param name="hasPts">
    /// <see langword="true"/> when the source stream provided a valid PTS;
    /// <see langword="false"/> when the PTS was absent or marked as unknown by FFmpeg.
    /// </param>
    /// <param name="dts">
    /// Decode timestamp normalized to <see cref="TimeSpan"/>, or
    /// <see cref="TimeSpan.Zero"/> when absent.
    /// </param>
    /// <param name="hasDts">
    /// <see langword="true"/> when the source stream provided a valid DTS.
    /// </param>
    /// <param name="duration">Nominal duration of the packet in stream time.</param>
    /// <param name="data">
    /// A copy of the encoded packet payload. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="isKeyFrame"><see langword="true"/> when the packet is a key frame.</param>
    public DemuxPacket(
        int streamIndex,
        TimeSpan pts,
        bool hasPts,
        TimeSpan dts,
        bool hasDts,
        TimeSpan duration,
        byte[] data,
        bool isKeyFrame
    )
    {
        ArgumentNullException.ThrowIfNull(data);
        StreamIndex = streamIndex;
        Pts = pts;
        HasPts = hasPts;
        Dts = dts;
        HasDts = hasDts;
        Duration = duration;
        Data = data;
        IsKeyFrame = isKeyFrame;
    }

    /// <summary>
    /// Index of the stream this packet belongs to, matching a stream index in
    /// <see cref="MediaInfo.VideoStreams"/> or <see cref="MediaInfo.AudioStreams"/>.
    /// </summary>
    public int StreamIndex { get; }

    /// <summary>
    /// Presentation timestamp normalized to <see cref="TimeSpan"/>.
    /// Valid only when <see cref="HasPts"/> is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Time base: the value is derived from the stream's <c>AVStream.time_base</c>
    /// via <c>av_rescale_q</c> and scaled to .NET <see cref="TimeSpan"/> ticks
    /// (1 tick = 100 ns = 10^-7 s).
    /// </remarks>
    public TimeSpan Pts { get; }

    /// <summary>
    /// <see langword="true"/> when the original packet contained a valid PTS;
    /// <see langword="false"/> when FFmpeg reported the PTS as <c>AV_NOPTS_VALUE</c>.
    /// </summary>
    public bool HasPts { get; }

    /// <summary>
    /// Decode timestamp normalized to <see cref="TimeSpan"/>.
    /// Valid only when <see cref="HasDts"/> is <see langword="true"/>.
    /// </summary>
    public TimeSpan Dts { get; }

    /// <summary>
    /// <see langword="true"/> when the original packet contained a valid DTS.
    /// </summary>
    public bool HasDts { get; }

    /// <summary>
    /// Nominal duration of this packet in stream time, or <see cref="TimeSpan.Zero"/>
    /// when the duration is unknown.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// A copy of the compressed packet payload.
    /// </summary>
    /// <remarks>
    /// The decoder layer consumes this data to produce decoded frames.
    /// </remarks>
    public byte[] Data { get; }

    /// <summary>
    /// <see langword="true"/> when this packet is a key frame (I-frame for video).
    /// </summary>
    public bool IsKeyFrame { get; }
}
