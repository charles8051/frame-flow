// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Native.Core;

/// <summary>
/// Words the error a caller sees when an FFmpeg library cannot be loaded at all.
/// </summary>
/// <remarks>
/// <para>
/// The usual cause is not a missing FFmpeg but the wrong one. The resolver asks for the
/// versioned file names of the FFmpeg major the struct bindings were generated for
/// (<c>libavcodec.so.63</c>, <c>avcodec-63.dll</c>), so an FFmpeg of any other major is
/// invisible to it, however it was installed. A Linux distribution's FFmpeg is typically an
/// older major, which is why the message does not tell the reader to install FFmpeg: it
/// names the release and the per-library majors, and the two things that supply them.
/// </para>
/// <para>
/// Pure: no IO, no state. The loader passes in what it searched.
/// </para>
/// </remarks>
internal static class FfmpegLoadFailure
{
    /// <summary>
    /// Builds the failure message for a library that no candidate path could load.
    /// </summary>
    /// <param name="library">The library's short name, such as <c>"avcodec"</c>.</param>
    /// <param name="searched">The candidate paths tried, in order.</param>
    internal static string Describe(string library, IEnumerable<string> searched)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(searched);

        return $"Failed to load FFmpeg library '{library}'. "
            + $"Searched: [{string.Join(", ", searched)}]. "
            + $"FrameFlow requires {FfmpegAbiCheck.DescribeRequiredFfmpeg()}; "
            + "an FFmpeg of another major version, such as a Linux distribution's, "
            + "does not provide these libraries. "
            + Remedy;
    }

    /// <summary>
    /// The two things that supply a matching FFmpeg, as one sentence. Shared with the ABI
    /// check's message, which a loadable FFmpeg of the wrong major reaches instead.
    /// </summary>
    internal const string Remedy =
        "Reference the FrameFlow.Native.Runtime package, which carries the matching "
        + "FFmpeg for win-x64, win-arm64, linux-x64, linux-arm64 and osx-arm64, "
        + "or set FrameFlowNativeOptions.CustomFfmpegPath to a directory holding a "
        + "matching FFmpeg build.";
}
