// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

// Silk.NET.SDL.Sdl is the underlying SDL2 wrapper. The FrameFlow.SDL namespace
// uses the ALL-CAPS form per .NET acronym conventions so it does not collide.
using SdlApi = Silk.NET.SDL.Sdl;

namespace FrameFlow.SDL.Bootstrap;

/// <summary>
/// Resolves and loads the SDL2 native library and produces <see cref="Sdl"/> API instances
/// backed by the resolved handle.
/// </summary>
/// <remarks>
/// Implementations must be safe to register as singletons. <see cref="Initialize"/> is
/// idempotent: repeated calls return a cached result after the first call completes.
/// </remarks>
public interface ISdlBootstrapper
{
    /// <summary>
    /// <see langword="true"/> after a successful <see cref="Initialize"/> call.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Resolves and loads the SDL2 native library.
    /// Must be called before <see cref="CreateSdlApi"/>.
    /// </summary>
    /// <remarks>
    /// Does not throw; all failure information is contained in the returned
    /// <see cref="SdlBootstrapResult"/>. Thread-safe; executes initialization exactly once.
    /// </remarks>
    SdlBootstrapResult Initialize();

    /// <summary>
    /// Creates a <see cref="SdlApi"/> API instance backed by the resolved SDL2 library handle.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="Initialize"/> has not been called or returned a failure result.
    /// </exception>
    SdlApi CreateSdlApi();
}
