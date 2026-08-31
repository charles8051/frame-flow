// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Native;

/// <summary>
/// Describes the outcome of a single attempt to load the FFmpeg native libraries.
/// </summary>
/// <param name="IsSuccess">
/// <see langword="true"/> when all required libraries were loaded and the version probe succeeded.
/// </param>
/// <param name="AvutilVersion">
/// The packed <c>avutil</c> version integer returned by <c>avutil_version()</c>, or
/// <c>0</c> when loading failed.
/// </param>
/// <param name="ErrorMessage">
/// A human-readable description of the failure, or <see langword="null"/> on success.
/// </param>
internal readonly record struct FfmpegLoadResult(
    bool IsSuccess,
    uint AvutilVersion = 0,
    string? ErrorMessage = null
)
{
    /// <summary>Returns a successful result carrying the detected avutil version.</summary>
    public static FfmpegLoadResult Success(uint avutilVersion) =>
        new(IsSuccess: true, AvutilVersion: avutilVersion);

    /// <summary>Returns a failure result with the supplied diagnostic message.</summary>
    public static FfmpegLoadResult Failure(string errorMessage) =>
        new(IsSuccess: false, ErrorMessage: errorMessage);
}
