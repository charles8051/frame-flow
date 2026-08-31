// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Provides helpers for locating .NET single-file bundle extraction directories.
/// </summary>
/// <remarks>
/// When an application is published with <c>PublishSingleFile=true</c> and
/// <c>IncludeNativeLibrariesForSelfExtract=true</c>, the .NET AppHost extracts native
/// libraries to <c>{extractBase}/{appName}/{hash}/</c> before managed code starts.
/// The hash subdirectory is not knowable at compile time, so callers must enumerate
/// candidates and apply their own file or subdirectory predicates.
/// </remarks>
internal static class BundleExtractionHelper
{
    /// <summary>
    /// Enumerates bundle extraction hash directories for the current process,
    /// ordered by last-write time descending (most recent extraction first).
    /// Returns an empty sequence if no extraction directory exists or
    /// <see cref="Environment.ProcessPath"/> is unavailable.
    /// </summary>
    public static IEnumerable<string> EnumerateHashDirectories()
    {
        // DOTNET_BUNDLE_EXTRACT_BASE_DIR is set by the .NET AppHost when running a single-file
        // bundle. If absent, fall back to the platform default used by the runtime.
        var extractBase =
            Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")
            ?? Path.Combine(Path.GetTempPath(), ".net");

        var processPath = Environment.ProcessPath;
        if (processPath is null)
            return [];

        var appName = Path.GetFileNameWithoutExtension(processPath);
        var appExtractDir = Path.Combine(extractBase, appName);

        if (!Directory.Exists(appExtractDir))
            return [];

        return Directory
            .EnumerateDirectories(appExtractDir)
            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d));
    }
}
