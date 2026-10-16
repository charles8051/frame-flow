// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Core;

namespace FrameFlow.Media;

/// <summary>
/// The step of opening or reading media at which FFmpeg reported a failure.
/// </summary>
public enum MediaFailureStage
{
    /// <summary><c>avformat_open_input</c>: opening the source and reading its header.</summary>
    Open,

    /// <summary><c>avformat_find_stream_info</c>: probing the opened source's streams.</summary>
    Probe,

    /// <summary>
    /// The source opened and was probed, but no stream has codec parameters a decoder can use.
    /// </summary>
    StreamResolution,

    /// <summary><c>avformat_seek_file</c>: repositioning an open source.</summary>
    Seek,

    /// <summary><c>av_read_frame</c>: reading the next packet from an open source.</summary>
    DemuxRead,

    /// <summary><c>avcodec_send_packet</c>: handing a packet to the decoder.</summary>
    SendPacket,

    /// <summary><c>avcodec_receive_frame</c>: taking a decoded frame from the decoder.</summary>
    ReceiveFrame,
}

/// <summary>
/// A failure FFmpeg reported while opening, reading or decoding media. It carries the FFmpeg
/// error code, the stage that failed, and the <see cref="ErrorCategory"/> the code and the
/// stage map to.
/// </summary>
/// <remarks>
/// <para>
/// It derives from <see cref="InvalidOperationException"/>, which these failures were thrown as
/// before, so an existing <c>catch</c> still catches them.
/// </para>
/// <para>
/// <see cref="Category"/> is derived, not chosen by the thrower. A playback error built from this
/// exception reports the same category, so a thrown failure and a returned
/// <see cref="PlaybackError"/> classify alike.
/// </para>
/// </remarks>
public sealed class MediaException : InvalidOperationException
{
    /// <summary>Creates a media failure for an FFmpeg error code reported at a stage.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="errorCode">
    /// The FFmpeg return code, such as <c>AVERROR_INVALIDDATA</c> or <c>AVERROR(ENOENT)</c>.
    /// </param>
    /// <param name="stage">The stage that reported <paramref name="errorCode"/>.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public MediaException(
        string message,
        int errorCode,
        MediaFailureStage stage,
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Stage = stage;
        Category = MediaErrorClassification.Categorize(errorCode, stage);
    }

    /// <summary>
    /// The FFmpeg return code. FFmpeg codes are negative: <c>AVERROR(e)</c> is <c>-e</c> for an
    /// errno value <c>e</c>, and the named codes such as <c>AVERROR_INVALIDDATA</c> are negated
    /// four-character tags.
    /// </summary>
    public int ErrorCode { get; }

    /// <summary>The stage that reported <see cref="ErrorCode"/>.</summary>
    public MediaFailureStage Stage { get; }

    /// <summary>The category <see cref="ErrorCode"/> and <see cref="Stage"/> map to.</summary>
    public ErrorCategory Category { get; }
}
