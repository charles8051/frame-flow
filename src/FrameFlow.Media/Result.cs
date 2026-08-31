// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Categories of errors that can occur during playback operations.
/// </summary>
public enum ErrorCategory
{
    /// <summary>The operation is not valid in the current state.</summary>
    InvalidOperation,

    /// <summary>An error originating from the media source.</summary>
    Source,

    /// <summary>A network-related error (timeout, DNS, connection reset).</summary>
    Network,

    /// <summary>A decoding error (corrupt frame, unsupported codec).</summary>
    Decode,

    /// <summary>An I/O error (file not found, permission denied).</summary>
    Io,

    /// <summary>A system-level error (out of memory, thread pool exhaustion).</summary>
    System,
}

/// <summary>
/// Structured error information attached to a failed <see cref="Result"/> or <see cref="Result{T}"/>.
/// </summary>
/// <param name="Category">The broad error classification.</param>
/// <param name="Message">A human-readable description of the failure.</param>
/// <param name="Inner">An optional inner exception that caused the failure.</param>
public sealed record PlaybackError(ErrorCategory Category, string Message, Exception? Inner = null);

/// <summary>
/// A lightweight result type for operations that can fail without throwing.
/// Prefer this over exceptions for expected failure paths (invalid state transitions,
/// user-initiated operations on disposed objects, etc.).
/// </summary>
public readonly record struct Result
{
    private Result(bool isSuccess, PlaybackError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Error details when <see cref="IsSuccess"/> is <see langword="false"/>; otherwise <see langword="null"/>.</summary>
    public PlaybackError? Error { get; }

    /// <summary>Creates a successful result.</summary>
    public static Result Ok() => new(true, null);

    /// <summary>Creates a failed result with the specified error details.</summary>
    public static Result Fail(ErrorCategory category, string message, Exception? inner = null) =>
        new(false, new PlaybackError(category, message, inner));

    /// <summary>Creates a failed result from an existing <see cref="PlaybackError"/>.</summary>
    public static Result Fail(PlaybackError error) => new(false, error);
}

/// <summary>
/// A lightweight result type for operations that return a value or fail without throwing.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public readonly record struct Result<T>
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, PlaybackError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// The success value. Throws <see cref="InvalidOperationException"/> if <see cref="IsSuccess"/> is <see langword="false"/>.
    /// </summary>
    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException(
                "Cannot access Value on a failed Result. Check IsSuccess first."
            );

    /// <summary>Error details when <see cref="IsSuccess"/> is <see langword="false"/>; otherwise <see langword="null"/>.</summary>
    public PlaybackError? Error { get; }

    /// <summary>Creates a successful result containing <paramref name="value"/>.</summary>
    public static Result<T> Ok(T value) => new(true, value, null);

    /// <summary>Creates a failed result with the specified error details.</summary>
    public static Result<T> Fail(ErrorCategory category, string message, Exception? inner = null) =>
        new(false, default, new PlaybackError(category, message, inner));

    /// <summary>Creates a failed result from an existing <see cref="PlaybackError"/>.</summary>
    public static Result<T> Fail(PlaybackError error) => new(false, default, error);
}
