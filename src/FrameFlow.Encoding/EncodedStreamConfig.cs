// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Encoding;

/// <summary>
/// Describes one encoded stream that a muxer carries — the codec and geometry
/// of an output stream. Surfaced for diagnostics and for the ADR-0040 muxer
/// surface.
/// </summary>
/// <remarks>
/// In the implemented video-only path the muxer wires a stream's actual codec
/// parameters (including the SPS/PPS extradata the MP4 <c>avcC</c> box needs)
/// directly from the encoder via <see cref="IMuxer.AddVideoStream"/>; this
/// record is the human-readable summary of that stream, not the wiring
/// mechanism.
/// </remarks>
/// <param name="CodecName">Codec/encoder name (e.g. <c>"libopenh264"</c>).</param>
/// <param name="Width">Coded width in pixels.</param>
/// <param name="Height">Coded height in pixels.</param>
/// <param name="FrameRateNumerator">Frame-rate numerator.</param>
/// <param name="FrameRateDenominator">Frame-rate denominator.</param>
public readonly record struct EncodedStreamConfig(
    string CodecName,
    int Width,
    int Height,
    int FrameRateNumerator,
    int FrameRateDenominator
);
