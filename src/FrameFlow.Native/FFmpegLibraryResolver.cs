// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;

namespace FrameFlow.Native;

/// <summary>
/// Encapsulates the platform-specific mapping from FFmpeg library short names
/// (e.g. <c>"avutil"</c>) to their on-disk file names (e.g. <c>avutil-61.dll</c>).
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

    // FFmpeg 9.x version suffixes per library.
    // These correspond to the SONAME / DLL name used in the v7 release series.
    private static readonly IReadOnlyDictionary<string, string> WindowsSuffixes = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["avutil"] = "avutil-61",
        ["swresample"] = "swresample-7",
        ["swscale"] = "swscale-10",
        ["avcodec"] = "avcodec-63",
        ["avformat"] = "avformat-63",
    };

    private static readonly IReadOnlyDictionary<string, string> UnixSonames = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["avutil"] = "libavutil.so.61",
        ["swresample"] = "libswresample.so.7",
        ["swscale"] = "libswscale.so.10",
        ["avcodec"] = "libavcodec.so.63",
        ["avformat"] = "libavformat.so.63",
    };

    private static readonly IReadOnlyDictionary<string, string> MacOsDylibNames = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["avutil"] = "libavutil.61.dylib",
        ["swresample"] = "libswresample.7.dylib",
        ["swscale"] = "libswscale.10.dylib",
        ["avcodec"] = "libavcodec.63.dylib",
        ["avformat"] = "libavformat.63.dylib",
    };

    /// <summary>
    /// Returns the platform-specific file name for the given FFmpeg library short name.
    /// </summary>
    /// <param name="libraryName">Short name, e.g. <c>"avutil"</c>.</param>
    /// <returns>
    /// The on-disk file name including version suffix, e.g. <c>avutil-61.dll</c> on Windows.
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
