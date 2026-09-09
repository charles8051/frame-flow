// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Decoding;

/// <summary>
/// How much of the decode a <see cref="IVideoDecoder"/> is allowed to skip when
/// the pipeline is behind. Ordered by damage: each level discards everything the
/// one before it does.
/// </summary>
/// <remarks>
/// <para>
/// These map onto FFmpeg's <c>AVCodecContext.skip_frame</c>. The mapping lives in
/// the decoder so callers never hold an FFmpeg type; the playback layer owns the
/// policy (ADR-0003) and the decoder only obeys.
/// </para>
/// <para>
/// <b>What these cost depends on the content, and it is not a small dependence.</b>
/// On a stream with B-frames, measured at 2160p60, <see cref="NonReference"/> cut
/// lateness from 13.9 s to 4.2 s and <see cref="Bidirectional"/> recovered
/// completely. On a stream without them the same two levels discard nothing at
/// all, because the categories they name are empty, and the first level that does
/// anything is <see cref="KeyframesOnly"/>.
/// </para>
/// </remarks>
public enum DecodeDiscardLevel
{
    /// <summary>Decode everything. The normal state.</summary>
    None = 0,

    /// <summary>
    /// Skip frames nothing else is built from. Safe by construction — no
    /// reference chain can break — which is why it is the first of these tried.
    /// </summary>
    NonReference = 1,

    /// <summary>Skip bidirectionally-predicted frames.</summary>
    Bidirectional = 2,

    /// <summary>
    /// Decode keyframes only. Recovers fastest and stutters to the keyframe
    /// interval, which on a long GOP is a frozen picture between them.
    /// </summary>
    KeyframesOnly = 3,
}
