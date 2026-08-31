// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.SDL.Bootstrap;

/// <summary>
/// Describes the outcome of an <see cref="ISdlBootstrapper.Initialize"/> call.
/// </summary>
/// <param name="IsSuccess">
/// <see langword="true"/> if SDL2 was successfully resolved and loaded.
/// </param>
/// <param name="ResolvedLibraryPath">
/// The full path to the SDL2 library that was loaded, or <see langword="null"/> if
/// the OS loader was used (system fallback) or initialization failed.
/// </param>
/// <param name="Message">
/// A human-readable summary of the bootstrap outcome, suitable for logging.
/// </param>
public sealed record SdlBootstrapResult(
    bool IsSuccess,
    string? ResolvedLibraryPath,
    string Message
);
