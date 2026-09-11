// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.CodeAnalysis;

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
/// The error reported by a default-initialised <see cref="Result"/> or
/// <see cref="Result{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both result types are structs, so <c>default</c> is reachable however
/// carefully the factory methods are written: a struct field, an array
/// element, <c>new Result()</c>. <c>default</c> zeroes every field, which
/// makes <c>IsSuccess</c> false and would otherwise leave <c>Error</c> null.
/// That is the one state in which
/// <c>[MemberNotNullWhen(false, nameof(Error))]</c> would be a promise the
/// type cannot keep, and the compiler takes that promise on trust.
/// </para>
/// <para>
/// Substituting this instance closes that state. The annotation then holds
/// everywhere, and a caller who reaches a default result gets a message
/// naming the mistake instead of a NullReferenceException on
/// <c>Error.Message</c>.
/// </para>
/// </remarks>
internal static class DefaultResultError
{
    internal static readonly PlaybackError Instance = new(
        ErrorCategory.InvalidOperation,
        "A default-initialised Result carries no outcome. Produce results with "
            + "Result.Ok() or Result.Fail(...)."
    );
}

/// <summary>
/// A lightweight result type for operations that can fail without throwing.
/// Prefer this over exceptions for expected failure paths (invalid state transitions,
/// user-initiated operations on disposed objects, etc.).
/// </summary>
public readonly record struct Result
{
    private readonly PlaybackError? _error;

    private Result(bool isSuccess, PlaybackError? error)
    {
        IsSuccess = isSuccess;
        _error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    /// <remarks>
    /// When this is <see langword="false"/>, <see cref="Error"/> is guaranteed
    /// non-null, so a failure branch needs no null check to read the category,
    /// message or inner exception.
    /// </remarks>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    /// <summary>Error details when <see cref="IsSuccess"/> is <see langword="false"/>; otherwise <see langword="null"/>.</summary>
    public PlaybackError? Error => IsSuccess ? null : _error ?? DefaultResultError.Instance;

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
    private readonly PlaybackError? _error;

    private Result(bool isSuccess, T? value, PlaybackError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    /// <remarks>
    /// When this is <see langword="false"/>, <see cref="Error"/> is guaranteed
    /// non-null, so a failure branch needs no null check to read the category,
    /// message or inner exception.
    /// </remarks>
    [MemberNotNullWhen(false, nameof(Error))]
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
    public PlaybackError? Error => IsSuccess ? null : _error ?? DefaultResultError.Instance;

    /// <summary>Creates a successful result containing <paramref name="value"/>.</summary>
    public static Result<T> Ok(T value) => new(true, value, null);

    /// <summary>Creates a failed result with the specified error details.</summary>
    public static Result<T> Fail(ErrorCategory category, string message, Exception? inner = null) =>
        new(false, default, new PlaybackError(category, message, inner));

    /// <summary>Creates a failed result from an existing <see cref="PlaybackError"/>.</summary>
    public static Result<T> Fail(PlaybackError error) => new(false, default, error);
}
