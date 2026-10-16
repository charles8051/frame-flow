// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media.Core;

/// <summary>
/// Maps a media failure to an <see cref="ErrorCategory"/> (#755, #757). Pure: values in, a value
/// out. A thrown <see cref="MediaException"/> takes its category from
/// <see cref="Categorize(int, MediaFailureStage)"/>, and a <see cref="PlaybackError"/> built from
/// a caught exception takes it from <see cref="Categorize(Exception?, ErrorCategory)"/>, which
/// reads the same value back. The two paths cannot disagree.
/// </summary>
/// <remarks>
/// <list type="table">
///   <listheader><term>Cause</term><description>Category</description></listheader>
///   <item><term><c>ENOENT</c>, <c>EACCES</c>, <c>EIO</c></term><description><see cref="ErrorCategory.Io"/></description></item>
///   <item><term><c>ENOMEM</c>, <c>AVERROR_BUG</c>, <c>AVERROR_BUG2</c>, a non-negative code</term><description><see cref="ErrorCategory.System"/></description></item>
///   <item><term><c>AVERROR_INVALIDDATA</c> before the first packet</term><description><see cref="ErrorCategory.Source"/></description></item>
///   <item><term><c>AVERROR_INVALIDDATA</c> mid-stream</term><description><see cref="ErrorCategory.Decode"/></description></item>
///   <item><term>no decodable stream, <c>AVERROR_STREAM_NOT_FOUND</c></term><description><see cref="ErrorCategory.Source"/></description></item>
///   <item><term>a <see cref="DeviceLostException"/>, or a device-loss HRESULT, anywhere in the chain</term><description><see cref="ErrorCategory.DeviceLost"/></description></item>
///   <item><term>any other code</term><description><see cref="ErrorCategory.Source"/> while opening or seeking, <see cref="ErrorCategory.Decode"/> while reading or decoding</description></item>
/// </list>
/// <para>
/// The errno values used here are the same on Windows, Linux and macOS. Network errnos are not:
/// <c>ETIMEDOUT</c> and <c>ECONNREFUSED</c> differ between the C runtimes FFmpeg is built
/// against, so no code maps to <see cref="ErrorCategory.Network"/> yet.
/// </para>
/// </remarks>
internal static class MediaErrorClassification
{
    /// <summary><c>AVERROR(ENOENT)</c>: no such file or directory.</summary>
    internal const int NoSuchFile = -2;

    /// <summary><c>AVERROR(EIO)</c>: an I/O error.</summary>
    internal const int IoError = -5;

    /// <summary><c>AVERROR(ENOMEM)</c>: out of memory.</summary>
    internal const int OutOfMemory = -12;

    /// <summary><c>AVERROR(EACCES)</c>: permission denied.</summary>
    internal const int AccessDenied = -13;

    /// <summary><c>AVERROR_INVALIDDATA</c>: invalid data found when processing input.</summary>
    internal const int InvalidData = -('I' | ('N' << 8) | ('D' << 16) | ('A' << 24));

    /// <summary><c>AVERROR_BUG</c>: an internal FFmpeg bug.</summary>
    internal const int Bug = -('B' | ('U' << 8) | ('G' << 16) | ('!' << 24));

    /// <summary><c>AVERROR_BUG2</c>: an internal FFmpeg bug.</summary>
    internal const int Bug2 = -('B' | ('U' << 8) | ('G' << 16) | (' ' << 24));

    /// <summary>
    /// <c>AVERROR_STREAM_NOT_FOUND</c>: the code a <see cref="MediaFailureStage.StreamResolution"/>
    /// failure carries, since no FFmpeg call returned one.
    /// </summary>
    internal const int StreamNotFound = -(0xF8 | ('S' << 8) | ('T' << 16) | ('R' << 24));

    /// <summary>The category of FFmpeg error <paramref name="errorCode"/> reported at <paramref name="stage"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stage"/> is not a defined stage.</exception>
    public static ErrorCategory Categorize(int errorCode, MediaFailureStage stage)
    {
        bool beforeFirstPacket = stage switch
        {
            MediaFailureStage.Open or MediaFailureStage.Probe or MediaFailureStage.StreamResolution => true,
            MediaFailureStage.Seek
            or MediaFailureStage.DemuxRead
            or MediaFailureStage.SendPacket
            or MediaFailureStage.ReceiveFrame => false,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Not a media failure stage."),
        };

        // FFmpeg reports errors as negative codes. Anything else reaching here is a caller that
        // read a result it should not have, which is the player's fault.
        if (errorCode >= 0)
            return ErrorCategory.System;

        return errorCode switch
        {
            NoSuchFile or AccessDenied or IoError => ErrorCategory.Io,
            OutOfMemory or Bug or Bug2 => ErrorCategory.System,
            InvalidData => beforeFirstPacket ? ErrorCategory.Source : ErrorCategory.Decode,
            StreamNotFound => ErrorCategory.Source,
            _ when beforeFirstPacket || stage == MediaFailureStage.Seek => ErrorCategory.Source,
            _ => ErrorCategory.Decode,
        };
    }

    /// <summary>
    /// The category of <paramref name="exception"/>, or <paramref name="fallback"/> when nothing
    /// in it has one.
    /// </summary>
    /// <param name="exception">The failure. Its inner exceptions are searched too.</param>
    /// <param name="fallback">
    /// The category when the failure is none of the media or I/O failures this recognises. A
    /// caller reporting a failure of its own passes <see cref="ErrorCategory.System"/>.
    /// </param>
    public static ErrorCategory Categorize(Exception? exception, ErrorCategory fallback)
    {
        // Device loss wins wherever it is in the chain: a decode failure on a lost device is the
        // loss, not the code FFmpeg returned for it (#759).
        if (IsDeviceLoss(exception))
            return ErrorCategory.DeviceLost;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case MediaException media:
                    return media.Category;
                case IOException or UnauthorizedAccessException:
                    return ErrorCategory.Io;
                case OutOfMemoryException:
                    return ErrorCategory.System;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Whether <paramref name="exception"/> or one of its inner exceptions is a
    /// <see cref="DeviceLostException"/> or carries a device-loss HRESULT, as a
    /// <c>COMException</c> or a Vortice <c>SharpGenException</c> from a removed device does.
    /// </summary>
    public static bool IsDeviceLoss(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DeviceLostException || DeviceLoss.IsDeviceLostCode(current.HResult))
                return true;
        }

        return false;
    }

    /// <summary>The first <see cref="DeviceLostException"/> in <paramref name="exception"/> or its inner exceptions.</summary>
    public static DeviceLostException? FindDeviceLoss(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DeviceLostException lost)
                return lost;
        }

        return null;
    }

    /// <summary>The first <see cref="MediaException"/> in <paramref name="exception"/> or its inner exceptions.</summary>
    public static MediaException? FindMediaFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MediaException media)
                return media;
        }

        return null;
    }
}
