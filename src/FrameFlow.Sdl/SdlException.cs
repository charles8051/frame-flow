// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.SDL;

/// <summary>
/// The exception thrown when an SDL2 operation fails.
/// </summary>
/// <remarks>
/// This exception always carries structured <see cref="Operation"/> and <see cref="SdlError"/>
/// properties. The standard parameterless and message-only constructors are intentionally
/// omitted because every SDL failure has an identifiable operation and error string.
/// </remarks>
public sealed class SdlException : Exception
{
    /// <summary>Gets the name of the SDL operation that failed (e.g., <c>SDL_CreateWindow</c>).</summary>
    public string Operation { get; }

    /// <summary>Gets the SDL error string reported by <c>SDL_GetError()</c>.</summary>
    public string SdlError { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="SdlException"/> with the failing
    /// operation name and the SDL error string.
    /// </summary>
    /// <param name="operation">The name of the SDL function that failed (e.g., <c>SDL_CreateWindow</c>).</param>
    /// <param name="sdlError">The error string returned by <c>SDL_GetError()</c>.</param>
    public SdlException(string operation, string sdlError)
        : base($"{operation} failed: {sdlError}")
    {
        Operation = operation;
        SdlError = sdlError;
    }
}
