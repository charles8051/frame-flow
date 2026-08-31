// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// Per-sample numeric type and channel-interleaving layout for decoded
/// audio buffers. The set mirrors what FFmpeg's <c>AVSampleFormat</c>
/// produces after audio decode, restricted to the variants Crossbar
/// consumers actually exchange.
/// </summary>
/// <remarks>
/// <para>
/// <b>Interleaved vs planar.</b> In an interleaved layout the samples
/// for all channels share one buffer in channel-major order
/// (<c>L0 R0 L1 R1 …</c>). In a planar layout each channel lives in its
/// own contiguous buffer. FFmpeg decoders produce planar by default for
/// many codecs; audio devices typically consume interleaved. Both are
/// first-class — a downstream operator converts between them when the
/// boundary requires it.
/// </para>
/// <para>
/// The encoding is intentionally not <c>[Flags]</c>: a sample is
/// exactly one of these formats. The set is open — additional variants
/// (e.g. fixed-point 24-bit packed, IEC 61937 compressed) may be added
/// without breaking the existing values.
/// </para>
/// </remarks>
public enum AudioSampleFormat
{
    /// <summary>Signed 16-bit little-endian PCM, interleaved.</summary>
    Int16,

    /// <summary>Signed 32-bit little-endian PCM, interleaved.</summary>
    Int32,

    /// <summary>IEEE 754 32-bit float PCM, interleaved.</summary>
    Float32,

    /// <summary>IEEE 754 64-bit float PCM, interleaved.</summary>
    Float64,

    /// <summary>Signed 16-bit little-endian PCM, planar (one buffer per channel).</summary>
    Int16Planar,

    /// <summary>Signed 32-bit little-endian PCM, planar (one buffer per channel).</summary>
    Int32Planar,

    /// <summary>IEEE 754 32-bit float PCM, planar (one buffer per channel).</summary>
    Float32Planar,

    /// <summary>IEEE 754 64-bit float PCM, planar (one buffer per channel).</summary>
    Float64Planar,
}

/// <summary>
/// Extension methods over <see cref="AudioSampleFormat"/> for the
/// metadata operators (per-sample byte size, layout introspection,
/// CLR type resolution) that consumers reach for repeatedly.
/// </summary>
public static class AudioSampleFormatExtensions
{
    /// <summary>
    /// Size in bytes of a single sample (one channel's value at one
    /// frame). Total byte count of an audio buffer is
    /// <c>BytesPerSample × ChannelCount × FrameCount</c>.
    /// </summary>
    public static int BytesPerSample(this AudioSampleFormat format) =>
        format switch
        {
            AudioSampleFormat.Int16 or AudioSampleFormat.Int16Planar => 2,
            AudioSampleFormat.Int32 or AudioSampleFormat.Int32Planar => 4,
            AudioSampleFormat.Float32 or AudioSampleFormat.Float32Planar => 4,
            AudioSampleFormat.Float64 or AudioSampleFormat.Float64Planar => 8,
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "Unrecognized AudioSampleFormat."
            ),
        };

    /// <summary>
    /// <see langword="true"/> when the format is planar (one buffer per
    /// channel); <see langword="false"/> when interleaved.
    /// </summary>
    public static bool IsPlanar(this AudioSampleFormat format) =>
        format switch
        {
            AudioSampleFormat.Int16Planar
            or AudioSampleFormat.Int32Planar
            or AudioSampleFormat.Float32Planar
            or AudioSampleFormat.Float64Planar => true,
            AudioSampleFormat.Int16
            or AudioSampleFormat.Int32
            or AudioSampleFormat.Float32
            or AudioSampleFormat.Float64 => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "Unrecognized AudioSampleFormat."
            ),
        };

    /// <summary>
    /// The CLR element type a typed buffer for this format would use
    /// (e.g. <see cref="short"/> for <see cref="AudioSampleFormat.Int16"/>).
    /// </summary>
    public static Type ClrType(this AudioSampleFormat format) =>
        format switch
        {
            AudioSampleFormat.Int16 or AudioSampleFormat.Int16Planar => typeof(short),
            AudioSampleFormat.Int32 or AudioSampleFormat.Int32Planar => typeof(int),
            AudioSampleFormat.Float32 or AudioSampleFormat.Float32Planar => typeof(float),
            AudioSampleFormat.Float64 or AudioSampleFormat.Float64Planar => typeof(double),
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "Unrecognized AudioSampleFormat."
            ),
        };
}
