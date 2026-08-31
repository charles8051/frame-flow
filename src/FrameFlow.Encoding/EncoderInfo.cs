// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Encoding;

/// <summary>
/// Static description of a constructed encoder: the codec it uses and the
/// geometry / frame rate it is configured for. Exposed via
/// <see cref="IEncoder{TFrame, TPacket}.Info"/> for diagnostics.
/// </summary>
/// <remarks>
/// For encoders that infer geometry from the first frame (the default H.264
/// path), <see cref="Width"/> and <see cref="Height"/> are 0 until the encoder
/// has opened on its first input frame.
/// </remarks>
/// <param name="CodecName">The underlying codec/encoder name (e.g. <c>"libopenh264"</c>).</param>
/// <param name="Width">Coded width in pixels, or 0 before the encoder has opened.</param>
/// <param name="Height">Coded height in pixels, or 0 before the encoder has opened.</param>
/// <param name="FrameRateNumerator">Configured frame-rate numerator.</param>
/// <param name="FrameRateDenominator">Configured frame-rate denominator.</param>
public readonly record struct EncoderInfo(
    string CodecName,
    int Width,
    int Height,
    int FrameRateNumerator,
    int FrameRateDenominator
);
