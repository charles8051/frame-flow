// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;

namespace FrameFlow.Decoding;

/// <summary>
/// Thrown by <c>VideoDecoder.Open</c> when
/// <see cref="HardwareDecodeMode.Required"/> is configured but no candidate
/// hardware-decode backend could be bound to the codec (ADR-0033).
/// </summary>
/// <remarks>
/// <para>
/// The playback controller catches this during <c>LoadAsync</c> and surfaces it
/// as <c>Result.Fail(ErrorCategory.System, "Session initialization failed", ex)</c>
/// — the inner exception carries the structured detail (which codec, which
/// backends were tried, and why each failed).
/// </para>
/// <para>
/// In <see cref="HardwareDecodeMode.Auto"/>, the same situation falls through
/// to software decode without raising — no exception is thrown.
/// </para>
/// </remarks>
public sealed class HardwareDecodeUnavailableException : Exception
{
    /// <summary>
    /// The FFmpeg codec ID (<c>AVCodecID</c>) of the stream that failed to bind.
    /// </summary>
    public int CodecId { get; }

    /// <summary>
    /// Human-readable codec name (e.g. <c>"h264"</c>).
    /// </summary>
    public string CodecName { get; }

    /// <summary>
    /// One entry per backend that was attempted, describing why it could not
    /// bind. Use for log/diagnostic output.
    /// </summary>
    public IReadOnlyList<HardwareDecodeAttempt> Attempts { get; }

    public HardwareDecodeUnavailableException(
        int codecId,
        string codecName,
        IReadOnlyList<HardwareDecodeAttempt> attempts
    )
        : base(BuildMessage(codecName, attempts))
    {
        CodecId = codecId;
        CodecName = codecName;
        Attempts = attempts;
    }

    private static string BuildMessage(
        string codecName,
        IReadOnlyList<HardwareDecodeAttempt> attempts
    )
    {
        if (attempts.Count == 0)
        {
            return $"HardwareDecodeMode.Required: no hardware-decode backend "
                + $"is available for codec '{codecName}' on this host.";
        }

        var details = string.Join("; ", attempts.Select(a => $"{a.Backend} -> {a.Reason}"));
        return $"HardwareDecodeMode.Required: no hardware-decode backend could be bound "
            + $"to codec '{codecName}'. Attempts: {details}.";
    }
}

/// <summary>
/// Records the outcome of a single attempt to bind a hardware-decode backend
/// to a codec context. Reported on
/// <see cref="HardwareDecodeUnavailableException.Attempts"/>.
/// </summary>
/// <param name="Backend">The backend that was tried.</param>
/// <param name="Reason">A short description of why the bind did not succeed.</param>
public sealed record HardwareDecodeAttempt(HardwareDecodeBackendKind Backend, string Reason);
