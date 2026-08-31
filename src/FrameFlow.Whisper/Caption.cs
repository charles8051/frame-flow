// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Whisper;

/// <summary>
/// A transcribed segment with its time range in media time. Emitted
/// by <c>FrameFlow.Whisper.WhisperOperators.TranscribeWith</c>
/// and consumed by caption timelines / overlay sinks.
/// </summary>
/// <param name="From">Start of the segment in media PTS.</param>
/// <param name="To">End of the segment in media PTS.</param>
/// <param name="Text">The transcribed text, whitespace-trimmed.</param>
public sealed record Caption(TimeSpan From, TimeSpan To, string Text);
