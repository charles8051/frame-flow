// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Native;

/// <summary>
/// Abstracts the act of locating and loading FFmpeg shared libraries.
/// </summary>
/// <remarks>
/// This seam exists to keep <see cref="FrameFlowBootstrapper"/> testable without requiring
/// actual FFmpeg binaries to be present. Production code uses
/// <see cref="FfmpegNativeLibraryLoader"/>; tests inject a stub.
/// </remarks>
internal interface IFfmpegLibraryLoader
{
    /// <summary>
    /// Attempts to load the required FFmpeg libraries from the supplied <paramref name="searchPath"/>
    /// (or the system loader if <paramref name="searchPath"/> is <see langword="null"/>), and
    /// probes the environment by calling at least one FFmpeg function.
    /// </summary>
    /// <param name="searchPath">
    /// The directory to search first, or <see langword="null"/> to rely entirely on the OS loader.
    /// </param>
    /// <param name="source">
    /// The <see cref="FfmpegBinarySource"/> that produced <paramref name="searchPath"/>.
    /// Used only for diagnostic message construction.
    /// </param>
    /// <returns>
    /// A <see cref="FfmpegLoadResult"/> describing whether loading succeeded and,
    /// if so, the detected <c>avutil</c> version.
    /// </returns>
    FfmpegLoadResult TryLoad(string? searchPath, FfmpegBinarySource source);
}
