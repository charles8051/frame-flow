// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Encoding.Internal;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Encoding;

/// <summary>
/// Factory entry points for video encoders (ADR-0040).
/// </summary>
public static class Encoder
{
    /// <summary>
    /// Creates an H.264 video encoder. Defaults to the software
    /// <c>libopenh264</c> codec (deterministic, hardware-independent);
    /// override <see cref="H264EncoderOptions.EncoderName"/> to target a
    /// hardware encoder.
    /// </summary>
    /// <param name="options">Encoder configuration, or <see langword="null"/> for defaults.</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostics (ADR-0010).</param>
    public static IVideoEncoder H264(
        H264EncoderOptions? options = null,
        ILoggerFactory? loggerFactory = null
    ) => new H264VideoEncoder(options ?? new H264EncoderOptions(), loggerFactory);
}
