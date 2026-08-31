// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback;

/// <summary>
/// Contains DOT graph strings for each state machine managed by
/// <see cref="PlaybackControllerCore"/>. Each string is a valid DOT language
/// document suitable for rendering with Graphviz or compatible tools.
/// </summary>
/// <param name="Playback">DOT graph for the primary playback state machine.</param>
/// <param name="Seeking">DOT graph for the seeking state machine.</param>
/// <param name="Repeat">DOT graph for the repeat mode state machine.</param>
public sealed record DotGraphSet(string Playback, string Seeking, string Repeat);
