// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Native.Interop;

namespace FrameFlow.Encoding.Internal;

/// <summary>
/// Internal seam that lets the libav muxer wire a stream's codec parameters
/// directly from the libav encoder's open <c>AVCodecContext</c> (via
/// <c>avcodec_parameters_from_context</c>) without exposing native handles on
/// the public <see cref="IVideoEncoder"/> surface.
/// </summary>
/// <remarks>
/// Both <c>H264VideoEncoder</c> and <c>Mp4Muxer</c> live in this assembly, so
/// this contract stays internal — the public API never sees a
/// <see cref="CodecContextHandle"/>.
/// </remarks>
internal interface INativeVideoEncoder
{
    /// <summary>The open codec context. Valid only when <see cref="IVideoEncoder.IsOpen"/> is true.</summary>
    CodecContextHandle CodecContext { get; }

    /// <summary>Numerator of the encoder time base (the unit of packet timestamps).</summary>
    int TimeBaseNumerator { get; }

    /// <summary>Denominator of the encoder time base.</summary>
    int TimeBaseDenominator { get; }
}
