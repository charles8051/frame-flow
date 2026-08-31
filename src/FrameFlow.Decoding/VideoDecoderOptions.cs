// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding;

/// <summary>
/// Configuration options for <see cref="VideoDecoder"/>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="AudioDecoderOptions"/>. Kept deliberately small — the
/// runtime full-queue policy (<see cref="VideoDecoder.DropNewestWhenQueueFull"/>)
/// and hardware-frame yield (<see cref="VideoDecoder.YieldHardwareFrames"/>) stay
/// mutable on the decoder because callers toggle them after construction; only the
/// construction-time packet-queue depth lives here.
/// </remarks>
public sealed class VideoDecoderOptions
{
    /// <summary>
    /// Capacity of the decoder's internal bounded packet queue, in packets.
    /// The demux pump writes cloned packets into this queue; once it is full the
    /// pump either blocks (<see cref="VideoDecoder.DropNewestWhenQueueFull"/> is
    /// <see langword="false"/> — the no-audio/block default) or drops the newest
    /// packet (<see cref="VideoDecoder.DropNewestWhenQueueFull"/> is
    /// <see langword="true"/> — when an audio stream shares the pump). Defaults to
    /// 512. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For video this is a read-ahead bound in frames.</b> The demuxer returns
    /// one packet per coded frame, so the depth is ~1:1 with frames: 512 ≈ 512
    /// frames ≈ ~20 s at 25 fps (~17 s at 30 fps, ~8.5 s at 60 fps) of compressed
    /// read-ahead.
    /// </para>
    /// <para>
    /// <b>Block mode (no audio consumer)</b> paces the pump to the video consumer's
    /// drain rate and never drops a packet, so a smaller value (e.g. 64 ≈ ~2.5 s at
    /// 25 fps) is safe and trims read-ahead latency and memory. <b>Drop mode (audio
    /// present)</b> sheds the newest packet when full; keep this large (≥ 512) so a
    /// post-seek GOP burst cannot fill the queue and trip drop-newest mid-GOP, which
    /// would break P-frame reconstruction and garble the GOP (see
    /// <see cref="VideoDecoder.SendPacketAsync"/> and the reset-path note in
    /// <see cref="VideoDecoder.ResetPacketQueue"/>).
    /// </para>
    /// <para>
    /// Lowering this is otherwise primarily useful for tests that need to observe the
    /// backpressure boundary deterministically without buffering hundreds of frames
    /// first. Production callers rarely need to change it.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Null derives the capacity from the stream's frame rate via
    /// <see cref="ReadAheadCapacity"/>, so the queue holds a comparable amount of <i>time</i>
    /// to the audio queue rather than a comparable number of packets. Set a value only to
    /// pin it; a fixed count reintroduces the frame-rate dependence described there.
    /// </remarks>
    public int? PacketQueueCapacity { get; init; }
}
