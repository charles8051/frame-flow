// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text;
using FrameFlow.Native.Interop;
using FFmpegAbstractions = FFmpeg.AutoGen.Abstractions;

namespace FrameFlow.Native.Core;

/// <summary>
/// The FFmpeg shared libraries FrameFlow requires, as the ABI check names them.
/// </summary>
internal enum FfmpegLibrary
{
    AvUtil,
    SwResample,
    SwScale,
    AvCodec,
    AvFormat,
}

/// <summary>
/// One library's loaded version, as its <c>*_version()</c> entry point reported it.
/// </summary>
/// <param name="Library">Which library was probed.</param>
/// <param name="Version">The packed version integer that library returned.</param>
internal readonly record struct FfmpegLibraryVersion(FfmpegLibrary Library, uint Version);

/// <summary>
/// Decides whether the FFmpeg that loaded is the one FrameFlow's struct bindings describe.
/// </summary>
/// <remarks>
/// <para>
/// FrameFlow reads FFmpeg structs by overlaying the layouts in
/// <c>FFmpeg.AutoGen.Abstractions</c> onto native pointers (see the accessors in
/// <c>FrameFlow.Native/Interop</c> and ADR-0017). Those layouts are generated from one
/// FFmpeg major version's headers. FFmpeg does not keep struct layouts stable across major
/// versions, so overlaying them on a different major reads the wrong bytes at the wrong
/// offsets. Nothing about that fails loudly: every function this project P/Invokes still
/// exists and still links, so the process runs and returns nonsense.
/// </para>
/// <para>
/// <b>Every required library is checked, not just libavutil.</b> The layouts FrameFlow
/// overlays are owned by different libraries: <c>AVFrame</c> and <c>AVBufferRef</c> by
/// avutil, <c>AVCodecContext</c> and <c>AVPacket</c> by avcodec, <c>AVFormatContext</c> and
/// <c>AVStream</c> by avformat. A search path that resolved them from different FFmpeg
/// generations would pass a check that read only one of them and leave the rest overlaid
/// wrong.
/// </para>
/// <para>
/// The versions compared are the per-library ones, not FFmpeg's own release number, and
/// they do not match each other. FFmpeg 7.1 is libavutil 59, libavcodec 61, libavformat 61,
/// libswscale 8 and libswresample 5.
/// </para>
/// <para>
/// This type is pure. It takes the packed version integers that the <c>*_version()</c>
/// entry points returned and gives back a verdict, with no IO and no state, so every branch
/// is testable without FFmpeg present.
/// </para>
/// </remarks>
internal static class FfmpegAbiCheck
{
    /// <summary>
    /// The <c>libavutil</c> major version FrameFlow's struct bindings were generated for.
    /// </summary>
    /// <remarks>
    /// Read from the binding package rather than written down here, so it moves when the
    /// <c>FFmpeg.AutoGen.Abstractions</c> package reference in <c>FrameFlow.Native.csproj</c>
    /// moves and cannot drift from it. The same holds for every entry in
    /// <see cref="ExpectedMajor"/>.
    /// </remarks>
    internal const int ExpectedAvutilMajor = FFmpegAbstractions.ffmpeg.LIBAVUTIL_VERSION_MAJOR;

    /// <summary>The library set the check covers, in the order the loader loads them.</summary>
    internal static readonly IReadOnlyList<FfmpegLibrary> RequiredLibraries =
    [
        FfmpegLibrary.AvUtil,
        FfmpegLibrary.SwResample,
        FfmpegLibrary.SwScale,
        FfmpegLibrary.AvCodec,
        FfmpegLibrary.AvFormat,
    ];

    /// <summary>The major version the generated bindings were built against, per library.</summary>
    internal static int ExpectedMajor(FfmpegLibrary library) =>
        library switch
        {
            FfmpegLibrary.AvUtil => FFmpegAbstractions.ffmpeg.LIBAVUTIL_VERSION_MAJOR,
            FfmpegLibrary.SwResample => FFmpegAbstractions.ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR,
            FfmpegLibrary.SwScale => FFmpegAbstractions.ffmpeg.LIBSWSCALE_VERSION_MAJOR,
            FfmpegLibrary.AvCodec => FFmpegAbstractions.ffmpeg.LIBAVCODEC_VERSION_MAJOR,
            FfmpegLibrary.AvFormat => FFmpegAbstractions.ffmpeg.LIBAVFORMAT_VERSION_MAJOR,
            _ => throw new ArgumentOutOfRangeException(nameof(library)),
        };

    /// <summary>The library's soname stem, for diagnostics.</summary>
    internal static string Name(FfmpegLibrary library) =>
        library switch
        {
            FfmpegLibrary.AvUtil => "libavutil",
            FfmpegLibrary.SwResample => "libswresample",
            FfmpegLibrary.SwScale => "libswscale",
            FfmpegLibrary.AvCodec => "libavcodec",
            FfmpegLibrary.AvFormat => "libavformat",
            _ => throw new ArgumentOutOfRangeException(nameof(library)),
        };

    /// <summary>
    /// Compares one <c>libavutil</c> version against <see cref="ExpectedAvutilMajor"/>.
    /// </summary>
    /// <param name="avutilVersion">
    /// The packed version integer returned by <c>avutil_version()</c>.
    /// </param>
    internal static FfmpegAbiVerdict Check(uint avutilVersion) =>
        Check([new FfmpegLibraryVersion(FfmpegLibrary.AvUtil, avutilVersion)]);

    /// <summary>
    /// Compares each loaded library's major against the one its bindings were generated for.
    /// </summary>
    /// <param name="loaded">
    /// One entry per probed library. A library absent from this list is not checked.
    /// </param>
    internal static FfmpegAbiVerdict Check(IReadOnlyList<FfmpegLibraryVersion> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        List<FfmpegLibraryVersion>? mismatched = null;

        foreach (var entry in loaded)
        {
            if (FFAvUtil.AvVersionMajor(entry.Version) != ExpectedMajor(entry.Library))
                (mismatched ??= []).Add(entry);
        }

        return mismatched is null
            ? FfmpegAbiVerdict.Compatible()
            : FfmpegAbiVerdict.Incompatible(mismatched, Describe(mismatched));
    }

    private static string Describe(IReadOnlyList<FfmpegLibraryVersion> mismatched)
    {
        var sb = new StringBuilder("FFmpeg ABI mismatch. ");

        sb.Append(
            mismatched.Count == 1
                ? "One loaded FFmpeg library is not the major version FrameFlow's struct "
                    + "bindings were generated for: "
                : $"{mismatched.Count} loaded FFmpeg libraries are not the major versions "
                    + "FrameFlow's struct bindings were generated for: "
        );

        for (var i = 0; i < mismatched.Count; i++)
        {
            if (i > 0)
                sb.Append("; ");

            var entry = mismatched[i];
            var major = FFAvUtil.AvVersionMajor(entry.Version);
            var minor = FFAvUtil.AvVersionMinor(entry.Version);
            var micro = FFAvUtil.AvVersionMicro(entry.Version);
            var expected = ExpectedMajor(entry.Library);

            sb.Append(Name(entry.Library))
                .Append(" is ")
                .Append(major)
                .Append('.')
                .Append(minor)
                .Append('.')
                .Append(micro)
                .Append(DescribeRelease(entry.Library, major))
                .Append(", expected ")
                .Append(expected)
                .Append(".x")
                .Append(DescribeRelease(entry.Library, expected));
        }

        sb.Append(
            ". FFmpeg struct layouts change across major versions, so continuing would read "
                + "frame, packet and stream fields at the wrong offsets and return wrong values "
                + "rather than fail. Install a single matching FFmpeg major version, or point "
                + "FrameFlowNativeOptions.CustomFfmpegPath at one."
        );

        return sb.ToString();
    }

    /// <summary>
    /// Names the FFmpeg release a library major belongs to, as a parenthesised aside, or an
    /// empty string for a major this table does not know.
    /// </summary>
    /// <remarks>
    /// A convenience for the error message only. Nothing branches on it, and a major missing
    /// from the table costs the reader an aside rather than the diagnosis.
    /// </remarks>
    private static string DescribeRelease(FfmpegLibrary library, int major) =>
        (library, major) switch
        {
            (FfmpegLibrary.AvUtil, 58) => " (FFmpeg 6.x)",
            (FfmpegLibrary.AvUtil, 59) => " (FFmpeg 7.x)",
            (FfmpegLibrary.AvUtil, 60) => " (FFmpeg 8.x)",
            (FfmpegLibrary.AvUtil, 61) => " (FFmpeg 9.x)",
            (FfmpegLibrary.AvCodec or FfmpegLibrary.AvFormat, 60) => " (FFmpeg 6.x)",
            (FfmpegLibrary.AvCodec or FfmpegLibrary.AvFormat, 61) => " (FFmpeg 7.x)",
            (FfmpegLibrary.AvCodec or FfmpegLibrary.AvFormat, 62) => " (FFmpeg 8.x)",
            (FfmpegLibrary.AvCodec or FfmpegLibrary.AvFormat, 63) => " (FFmpeg 9.x)",
            _ => string.Empty,
        };
}

/// <summary>
/// The outcome of an <see cref="FfmpegAbiCheck"/> comparison.
/// </summary>
/// <param name="IsCompatible">
/// <see langword="true"/> when every probed library's major matches the one its bindings
/// were generated for.
/// </param>
/// <param name="Mismatched">
/// The libraries whose majors did not match. Empty when compatible.
/// </param>
/// <param name="Message">
/// A diagnostic naming each offending library and both versions, or <see langword="null"/>
/// when compatible.
/// </param>
internal readonly record struct FfmpegAbiVerdict(
    bool IsCompatible,
    IReadOnlyList<FfmpegLibraryVersion> Mismatched,
    string? Message
)
{
    internal static FfmpegAbiVerdict Compatible() =>
        new(IsCompatible: true, Mismatched: [], Message: null);

    internal static FfmpegAbiVerdict Incompatible(
        IReadOnlyList<FfmpegLibraryVersion> mismatched,
        string message
    ) => new(IsCompatible: false, Mismatched: mismatched, Message: message);
}
