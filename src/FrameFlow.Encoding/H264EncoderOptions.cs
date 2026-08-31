// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Encoding;

/// <summary>
/// Configuration for the H.264 video encoder (<see cref="Encoder.H264"/>).
/// </summary>
/// <remarks>
/// <para>
/// All properties have working defaults; the common case
/// (<c>Encoder.H264()</c> with no options) produces a 30&#160;fps,
/// constant-quality-ish H.264 stream suitable for MP4. Geometry defaults to
/// "infer from the first encoded frame", so a recorder does not need to know
/// the resolution up front.
/// </para>
/// <para>
/// The default <see cref="EncoderName"/> is <c>libopenh264</c> — the software
/// H.264 encoder statically linked into FrameFlow's LGPL FFmpeg build. It is
/// deterministic and hardware-independent. Override it to target a hardware
/// encoder (e.g. <c>"h264_nvenc"</c>, <c>"h264_qsv"</c>, <c>"h264_mf"</c>) when
/// one is available and desired.
/// </para>
/// </remarks>
public sealed record H264EncoderOptions
{
    /// <summary>
    /// Coded width in pixels, or 0 (default) to infer from the first frame.
    /// Odd values are rounded down to even (H.264 4:2:0 requires even dimensions).
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Coded height in pixels, or 0 (default) to infer from the first frame.
    /// Odd values are rounded down to even.
    /// </summary>
    public int Height { get; init; }

    /// <summary>Frame-rate numerator. Default 30.</summary>
    public int FrameRateNumerator { get; init; } = 30;

    /// <summary>Frame-rate denominator. Default 1.</summary>
    public int FrameRateDenominator { get; init; } = 1;

    /// <summary>Target bitrate in bits per second. Default 4&#160;000&#160;000 (4&#160;Mbps).</summary>
    public long BitRate { get; init; } = 4_000_000;

    /// <summary>
    /// Group-of-pictures size (keyframe interval, in frames). Default 30
    /// (≈ one keyframe per second at 30&#160;fps).
    /// </summary>
    public int GopSize { get; init; } = 30;

    /// <summary>
    /// The FFmpeg encoder to use. Default <c>"libopenh264"</c> (software).
    /// </summary>
    public string EncoderName { get; init; } = "libopenh264";
}
