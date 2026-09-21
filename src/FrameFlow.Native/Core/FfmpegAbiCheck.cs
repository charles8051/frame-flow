// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Native.Interop;
using FFmpegAbstractions = FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Native.Core;

/// <summary>
/// Decides whether the FFmpeg that loaded is the one FrameFlow's struct bindings describe.
/// </summary>
/// <remarks>
/// <para>
/// FrameFlow reads FFmpeg structs by overlaying the layouts in
/// <c>FFmpeg.AutoGen.Abstractions</c> onto native pointers (see the accessors in
/// <c>FrameFlow.Native/Interop</c> and ADR-0017). Those layouts are generated from one
/// FFmpeg major version's headers. FFmpeg does not keep struct layouts stable across
/// major versions, so overlaying them on a different major reads the wrong bytes at the
/// wrong offsets. Nothing about that fails loudly: every function this project P/Invokes
/// still exists and still links, so the process runs and returns nonsense.
/// </para>
/// <para>
/// The version to compare against is <c>libavutil</c>'s, not FFmpeg's own. Those numbers
/// are different: libavutil 59 is FFmpeg 7.x, 60 is 8.x, 61 is 9.x.
/// </para>
/// <para>
/// This type is pure. It takes the packed version integer that
/// <c>avutil_version()</c> returned and gives back a verdict, with no IO and no state, so
/// every branch is testable without FFmpeg present.
/// </para>
/// </remarks>
internal static class FfmpegAbiCheck
{
    /// <summary>
    /// The <c>libavutil</c> major version FrameFlow's struct bindings were generated for.
    /// </summary>
    /// <remarks>
    /// Read from the binding package itself rather than written down here, so it moves when
    /// the <c>FFmpeg.AutoGen.Abstractions</c> package reference in
    /// <c>FrameFlow.Native.csproj</c> moves and cannot drift from it.
    /// </remarks>
    internal const int ExpectedAvutilMajor = FFmpegAbstractions.ffmpeg.LIBAVUTIL_VERSION_MAJOR;

    /// <summary>
    /// Compares a loaded <c>libavutil</c> version against <see cref="ExpectedAvutilMajor"/>.
    /// </summary>
    /// <param name="avutilVersion">
    /// The packed version integer returned by <c>avutil_version()</c>.
    /// </param>
    internal static FfmpegAbiVerdict Check(uint avutilVersion)
    {
        var major = FFAvUtil.AvVersionMajor(avutilVersion);

        if (major == ExpectedAvutilMajor)
            return FfmpegAbiVerdict.Compatible(major);

        var minor = FFAvUtil.AvVersionMinor(avutilVersion);
        var micro = FFAvUtil.AvVersionMicro(avutilVersion);

        var message =
            $"FFmpeg ABI mismatch. The loaded libavutil is {major}.{minor}.{micro}"
            + $"{DescribeRelease(major)}, but FrameFlow's struct bindings are generated for "
            + $"libavutil {ExpectedAvutilMajor}.x{DescribeRelease(ExpectedAvutilMajor)}. "
            + "FFmpeg struct layouts change across major versions, so continuing would read "
            + "frame, packet and stream fields at the wrong offsets and return wrong values "
            + "rather than fail. Install the matching FFmpeg major version, or point "
            + "FrameFlowNativeOptions.CustomFfmpegPath at it.";

        return FfmpegAbiVerdict.Incompatible(major, message);
    }

    /// <summary>
    /// Names the FFmpeg release a <c>libavutil</c> major belongs to, as a parenthesised
    /// aside, or an empty string for a major this table does not know.
    /// </summary>
    /// <remarks>
    /// A convenience for the error message only. Nothing branches on it, and a major
    /// missing from the table costs the reader an aside rather than the diagnosis.
    /// </remarks>
    private static string DescribeRelease(int avutilMajor) =>
        avutilMajor switch
        {
            58 => " (FFmpeg 6.x)",
            59 => " (FFmpeg 7.x)",
            60 => " (FFmpeg 8.x)",
            61 => " (FFmpeg 9.x)",
            _ => string.Empty,
        };
}

/// <summary>
/// The outcome of <see cref="FfmpegAbiCheck.Check"/>.
/// </summary>
/// <param name="IsCompatible">
/// <see langword="true"/> when the loaded <c>libavutil</c> major matches the one the
/// bindings were generated for.
/// </param>
/// <param name="DetectedMajor">The <c>libavutil</c> major that was loaded.</param>
/// <param name="ExpectedMajor">The <c>libavutil</c> major the bindings describe.</param>
/// <param name="Message">
/// A diagnostic naming both versions, or <see langword="null"/> when compatible.
/// </param>
internal readonly record struct FfmpegAbiVerdict(
    bool IsCompatible,
    int DetectedMajor,
    int ExpectedMajor,
    string? Message
)
{
    internal static FfmpegAbiVerdict Compatible(int detectedMajor) =>
        new(
            IsCompatible: true,
            DetectedMajor: detectedMajor,
            ExpectedMajor: FfmpegAbiCheck.ExpectedAvutilMajor,
            Message: null
        );

    internal static FfmpegAbiVerdict Incompatible(int detectedMajor, string message) =>
        new(
            IsCompatible: false,
            DetectedMajor: detectedMajor,
            ExpectedMajor: FfmpegAbiCheck.ExpectedAvutilMajor,
            Message: message
        );
}
