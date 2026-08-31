// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// Expose internals to the decoding test project so test doubles and factories
// can access DemuxSession constructors and BuildMediaInfo without real FFmpeg binaries.
[assembly: InternalsVisibleTo("FrameFlow.Decoding.Tests")]

// Expose internal demux classification seams to playback contract tests that
// lock cross-assembly EOF-vs-fault semantics without real FFmpeg runtime setup.
[assembly: InternalsVisibleTo("FrameFlow.Playback.Tests")]

// ADR-0038: expose GpuVideoFrame's internal AVFrame pointer to the
// FrameFlow.Video pipeline operators so the ToCpu() operator can perform
// av_hwframe_transfer_data + sws_scale without round-tripping through
// public API surface that doesn't fit the native semantics.
[assembly: InternalsVisibleTo("FrameFlow.Video")]
[assembly: InternalsVisibleTo("FrameFlow.Video.Tests")]
