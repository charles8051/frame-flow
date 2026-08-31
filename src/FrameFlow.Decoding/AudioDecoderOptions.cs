// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding;

/// <summary>
/// Configuration options for <see cref="AudioDecoder"/>.
/// </summary>
public sealed class AudioDecoderOptions
{
    /// <summary>
    /// Target sample rate in Hz for the decoded PCM output.
    /// The resampler normalises all audio to this rate.
    /// Defaults to 48 000 Hz.
    /// </summary>
    public int TargetSampleRate { get; init; } = 48_000;

    /// <summary>
    /// Capacity of the decoder's internal bounded packet queue, in packets.
    /// The demux pump writes cloned packets into this queue and backpressures
    /// (blocks <see cref="AudioDecoder.SendPacketAsync"/>) once it is full, so
    /// the depth bounds how far the pump may read ahead of the audio consumer.
    /// Defaults to 512 (~10 s of typical AAC at 44.1 kHz). Must be at least 1.
    /// </summary>
    /// <remarks>
    /// Lowering this is primarily useful for tests that need to observe the
    /// backpressure boundary deterministically without buffering seconds of
    /// audio first. Production callers rarely need to change it.
    /// </remarks>
    // 512 packets is ~10.9 s of AAC, ~13.4 s of MP3 — far more read-ahead than playback
    // needs, and it is the pump's throttle, so it set how far ahead of the video chain the
    // pump could run. 128 is ~2.7 s of AAC and ~3.3 s of MP3: still ample above the sink's
    // own ~1.6 s of device buffering, and low enough that video's queue holds more time than
    // this one at any plausible frame rate or packetization. See ReadAheadCapacity for why
    // that ordering is the invariant.
    public int PacketQueueCapacity { get; init; } = 128;
}
