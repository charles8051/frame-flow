// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// Expose internals (the libav-backed H264VideoEncoder / Mp4Muxer and the
// internal encoder↔muxer wiring contract) to the encoding test project so
// round-trip tests can construct and inspect the implementations directly.
[assembly: InternalsVisibleTo("FrameFlow.Encoding.Tests")]
