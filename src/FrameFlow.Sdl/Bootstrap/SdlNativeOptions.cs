// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.SDL.Bootstrap;

/// <summary>
/// Options that control how <see cref="SdlBootstrapper"/> resolves the SDL2 native library.
/// </summary>
public sealed class SdlNativeOptions
{
    /// <summary>
    /// Full path to the SDL2 shared library file. When set, this path is used directly
    /// and all other resolution steps are skipped.
    /// </summary>
    /// <example><c>/usr/local/lib/libSDL2.dylib</c> or <c>C:\libs\SDL2.dll</c></example>
    public string? CustomSdlLibraryPath { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default), the bootstrapper searches the NuGet runtime
    /// layout and bundle extraction directories for a bundled SDL2 library.
    /// </summary>
    public bool UseBundledLibrary { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (default), the bootstrapper falls back to the OS loader
    /// if no bundled library is found.
    /// </summary>
    public bool ProbeSystemLibrary { get; set; } = true;
}
