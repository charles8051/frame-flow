// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;

namespace FrameFlow.SDL.Bootstrap;

/// <summary>
/// Silk.NET <see cref="INativeContext"/> implementation that resolves SDL2 function pointers
/// from a pre-loaded native library handle.
/// </summary>
/// <remarks>
/// By supplying this context when constructing a <see cref="Silk.NET.SDL.Sdl"/> instance,
/// Silk.NET never independently searches for SDL2 — every function pointer is resolved
/// from the handle that <see cref="SdlBootstrapper"/> explicitly loaded. This is the
/// correct integration seam for a third-party Silk.NET assembly (ADR-0019).
/// </remarks>
internal sealed class SdlNativeContext : INativeContext
{
    private readonly nint _handle;

    internal SdlNativeContext(nint handle) => _handle = handle;

    /// <inheritdoc />
    public nint GetProcAddress(string proc, int? slot = null)
    {
        if (NativeLibrary.TryGetExport(_handle, proc, out var addr))
            return addr;

        throw new EntryPointNotFoundException(
            $"SDL2 entry point '{proc}' was not found in the loaded library."
        );
    }

    /// <inheritdoc />
    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null) =>
        NativeLibrary.TryGetExport(_handle, proc, out addr);

    /// <inheritdoc />
    /// <remarks>
    /// SDL2 has no unload concept. The handle is kept for the process lifetime.
    /// </remarks>
    public void Dispose() { }
}
