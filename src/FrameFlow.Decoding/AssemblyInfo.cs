// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// Expose internals to the decoding test project so test doubles and factories
// can access DemuxSession constructors and BuildMediaInfo without real FFmpeg binaries.
[assembly: InternalsVisibleTo("FrameFlow.Decoding.Tests")]

// For RequiresHardwareDecodeFactAttribute, which gates on
// VideoDecoder.HasHardwareCandidate so a machine whose backend opens but whose
// H.264 decoder cannot use it skips rather than failing.
[assembly: InternalsVisibleTo("FrameFlow.Integration.Tests")]

// Expose internal demux classification seams to playback contract tests that
// lock cross-assembly EOF-vs-fault semantics without real FFmpeg runtime setup.
[assembly: InternalsVisibleTo("FrameFlow.Playback.Tests")]

// MediaPass forwards DecodingPipeline's park signal so player tests can
// barrier on the pump being blocked on a full decoder queue.
[assembly: InternalsVisibleTo("FrameFlow.Player")]

// The playback session suspends the decoder's pool watchdog while it is paused (#383): holders
// keep their frames on purpose then, and a decoder parked at its budget is not a deadlock.
[assembly: InternalsVisibleTo("FrameFlow.Playback")]

// ADR-0038: expose GpuVideoFrame's internals to the FrameFlow.Video
// operators. VideoOperators.ToCpu(id) reaches GpuVideoFrame from there;
// the readback itself goes through the public ReadbackToCpuBgra32, so
// the grant is wider than that one caller needs (#279).
[assembly: InternalsVisibleTo("FrameFlow.Video")]
[assembly: InternalsVisibleTo("FrameFlow.Video.Tests")]
