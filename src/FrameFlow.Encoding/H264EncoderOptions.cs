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
/// <see cref="EncoderName"/> is unset by default, and the encoder is resolved
/// against what the loaded FFmpeg actually has: <c>libopenh264</c> first, then
/// <c>h264_videotoolbox</c>. Set it to pin one explicitly — a hardware encoder
/// (<c>"h264_nvenc"</c>, <c>"h264_qsv"</c>, <c>"h264_mf"</c>) or anything else the
/// build carries.
/// </para>
/// <para>
/// <b>Why resolution rather than a fixed default.</b> <c>libopenh264</c> is
/// statically linked into the FFmpeg that FrameFlow ships for Windows and Linux,
/// and on those platforms it is what resolves — deterministic, hardware-independent,
/// and the same encoder on both. FrameFlow ships no FFmpeg for macOS: the
/// bootstrapper resolves a Homebrew <c>ffmpeg@7</c> keg instead, and that build has
/// no <c>libopenh264</c>. A fixed default therefore threw on every Mac, while
/// <c>h264_videotoolbox</c> was sitting there unused.
/// </para>
/// <para>
/// <b>On <c>libx264</c>.</b> It is not in the resolution order, and that is a
/// deliberate omission rather than a licence guarantee. Homebrew's <c>ffmpeg@7</c>
/// is configured <c>--enable-gpl --enable-libx264</c>, so a macOS consumer on the
/// bring-your-own path is already running a GPL FFmpeg whatever encoder is chosen;
/// FrameFlow's LGPL statement covers the builds it ships, not the one it finds.
/// The reason to prefer VideoToolbox there is practical: it is present on every
/// Mac, it is hardware-accelerated, and it does not depend on which optional
/// formulae the consumer's FFmpeg happened to be built against. Pin
/// <c>"libx264"</c> explicitly if it suits you.
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
    /// Pins the FFmpeg encoder by name. Leave <see langword="null"/> (the default) to
    /// resolve against the loaded build, in the order given by
    /// <see cref="DefaultEncoderPreference"/>.
    /// </summary>
    /// <remarks>
    /// A name set here is used as given: if the build does not carry it, opening fails
    /// rather than falling back. Pinning is a statement that this encoder is the one
    /// that matters, and silently substituting another would defeat the point.
    /// </remarks>
    public string? EncoderName { get; init; }

    /// <summary>
    /// The encoders tried in order when <see cref="EncoderName"/> is unset. The first
    /// the loaded FFmpeg carries wins.
    /// </summary>
    /// <remarks>
    /// <c>libopenh264</c> is what FrameFlow's own Windows and Linux builds carry.
    /// <c>h264_videotoolbox</c> covers macOS, where FrameFlow ships no FFmpeg and the
    /// Homebrew build has no openh264. A name absent from a build simply does not match,
    /// so the list needs no platform conditionals.
    /// </remarks>
    public static IReadOnlyList<string> DefaultEncoderPreference { get; } =
        ["libopenh264", "h264_videotoolbox"];
}
