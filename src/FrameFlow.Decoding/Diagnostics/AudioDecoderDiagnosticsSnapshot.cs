// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding.Diagnostics;

/// <summary>
/// Immutable snapshot of an <see cref="IAudioDecoder"/>'s observable state at
/// a single point in time (ADR-0034).
/// </summary>
/// <param name="BuffersDecoded">
/// Cumulative count of <c>PcmAudioBuffer</c> instances yielded by the
/// decoder. One buffer corresponds to one resampled output block, not one
/// input packet — codecs may emit multiple frames per packet (e.g. AAC) or
/// require multiple packets to produce a single output frame.
/// </param>
/// <param name="DecodeErrors">
/// Number of non-fatal decode errors that resulted in a dropped buffer but
/// did not terminate the decode loop.
/// </param>
/// <param name="UsedSyntheticPts">
/// <see langword="true"/> when the decoder has at least once had to
/// synthesise a presentation timestamp because the input frame carried
/// <c>AV_NOPTS_VALUE</c>. Indicates the source has missing PTS data; not by
/// itself a defect.
/// </param>
public sealed record AudioDecoderDiagnosticsSnapshot(
    long BuffersDecoded,
    long DecodeErrors,
    bool UsedSyntheticPts
)
{
    /// <summary>
    /// Zero-valued snapshot used as the seed value for rollups when no
    /// audio decoder is active.
    /// </summary>
    public static AudioDecoderDiagnosticsSnapshot Empty { get; } =
        new(BuffersDecoded: 0, DecodeErrors: 0, UsedSyntheticPts: false);
}
