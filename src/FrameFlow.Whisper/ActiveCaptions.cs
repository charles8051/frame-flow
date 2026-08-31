// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Whisper;

/// <summary>
/// The set of captions currently active for one frame's PTS.
/// </summary>
/// <param name="Captions">
/// Captions active at this frame, ordered oldest-first so the renderer
/// can stack them.
/// </param>
public sealed record ActiveCaptions(IReadOnlyList<Caption> Captions);
