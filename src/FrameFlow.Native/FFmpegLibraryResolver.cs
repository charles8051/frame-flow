// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native;

/// <summary>
/// Encapsulates the platform-specific mapping from FFmpeg library short names
/// (e.g. <c>"avutil"</c>) to their on-disk file names (e.g. <c>avutil-59.dll</c>).
/// </summary>
internal static class FFmpegLibraryResolver
{
    // Required library short names in dependency order.
    // avutil must be loaded first because avformat and avcodec depend on it.
    internal static readonly string[] RequiredLibraries =
    [
        "avutil",
        "swresample",
        "swscale",
        "avcodec",
        "avformat",
    ];

    // FFmpeg 7.x version suffixes per library.
    // These correspond to the SONAME / DLL name used in the v7 release series.
    private static readonly IReadOnlyDictionary<string, string> WindowsSuffixes = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["avutil"] = "avutil-59",
        ["swresample"] = "swresample-5",
        ["swscale"] = "swscale-8",
        ["avcodec"] = "avcodec-61",
        ["avformat"] = "avformat-61",
    };

    private static readonly IReadOnlyDictionary<string, string> UnixSonames = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["avutil"] = "libavutil.so.59",
        ["swresample"] = "libswresample.so.5",
        ["swscale"] = "libswscale.so.8",
        ["avcodec"] = "libavcodec.so.61",
        ["avformat"] = "libavformat.so.61",
    };

    private static readonly IReadOnlyDictionary<string, string> MacOsDylibNames = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["avutil"] = "libavutil.59.dylib",
        ["swresample"] = "libswresample.5.dylib",
        ["swscale"] = "libswscale.8.dylib",
        ["avcodec"] = "libavcodec.61.dylib",
        ["avformat"] = "libavformat.61.dylib",
    };

    /// <summary>
    /// Returns the platform-specific file name for the given FFmpeg library short name.
    /// </summary>
    /// <param name="libraryName">Short name, e.g. <c>"avutil"</c>.</param>
    /// <returns>
    /// The on-disk file name including version suffix, e.g. <c>avutil-59.dll</c> on Windows.
    /// Falls back to the bare library name when the short name is not in the known map.
    /// </returns>
    internal static string PlatformFileName(string libraryName)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsSuffixes.TryGetValue(libraryName, out var win)
                ? win + ".dll"
                : libraryName + ".dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOsDylibNames.TryGetValue(libraryName, out var mac)
                ? mac
                : "lib" + libraryName + ".dylib";
        }

        // Linux / generic Unix
        return UnixSonames.TryGetValue(libraryName, out var so) ? so : "lib" + libraryName + ".so";
    }

    /// <summary>
    /// Builds the set of candidate full paths to try for a given library and search directory.
    /// </summary>
    /// <param name="libraryName">Short library name.</param>
    /// <param name="searchDirectory">
    /// Directory to search in, or <see langword="null"/> to skip directory-based candidates.
    /// </param>
    /// <returns>
    /// An enumerable of candidate paths in priority order. Callers should try each path with
    /// <see cref="NativeLibrary.TryLoad(string, out nint)"/> and stop on the first success.
    /// </returns>
    internal static IEnumerable<string> CandidatePaths(string libraryName, string? searchDirectory)
    {
        var platformName = PlatformFileName(libraryName);

        if (!string.IsNullOrEmpty(searchDirectory))
        {
            // Full path in the configured search directory.
            yield return Path.Combine(searchDirectory, platformName);
        }

        // Bare platform name — allows the OS loader to apply its own search rules.
        yield return platformName;

        // Bare short name — last resort, works when the OS has a shim or compat symlink.
        yield return libraryName;
    }
}
