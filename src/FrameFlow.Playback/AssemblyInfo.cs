// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FrameFlow.Playback.Tests")]
[assembly: InternalsVisibleTo("FrameFlow.Player")]
// PlaylistCoordinator is internal here and composed by FrameFlow.Player's
// PlaylistMediaPlayerCore, so the tests that pin how those two interact need
// to construct one (ADR-0069: the coordinator must not adopt a repeat mode
// the controller refused).
[assembly: InternalsVisibleTo("FrameFlow.Player.Tests")]
