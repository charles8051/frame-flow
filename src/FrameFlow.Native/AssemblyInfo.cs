// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.CompilerServices;

// Expose internals to the native test project so tests can inject stub loaders
// without requiring real FFmpeg binaries.
[assembly: InternalsVisibleTo("FrameFlow.Native.Tests")]

// Expose interop declarations (FFAvFormat, FormatContextHandle, etc.) to the
// decoding layer so DemuxSession can call avformat functions directly without
// leaking them to higher layers (ADR-0005, ADR-0011).
[assembly: InternalsVisibleTo("FrameFlow.Decoding")]

// Expose internals to the decoding test project so tests can use fakes and
// inspect internal state without requiring real FFmpeg binaries.
[assembly: InternalsVisibleTo("FrameFlow.Decoding.Tests")]

// Expose interop declarations (FFSwResample, av_opt_set_*, SwrContextHandle) to
// the audio layer so the swr-backed resampler can call swresample functions
// directly without leaking them to higher layers (ADR-0005, ADR-0011).
[assembly: InternalsVisibleTo("FrameFlow.Audio")]
[assembly: InternalsVisibleTo("FrameFlow.Audio.Tests")]

// ADR-0037: expose FFSwScale + SwsContextHandle to the video layer so the
// sws-backed converter can call swscale functions directly. Same rationale
// as FrameFlow.Audio — keep native bindings in one place, operators in
// dedicated packages.
[assembly: InternalsVisibleTo("FrameFlow.Video")]
[assembly: InternalsVisibleTo("FrameFlow.Video.Tests")]

// ADR-0040: expose the encode/mux interop (FFAvCodec encode functions,
// FFAvFormat mux functions, OutputFormatContextHandle, the encode struct
// writers) to the encoding layer so the H.264 encoder + MP4 muxer can call
// avcodec/avformat directly without leaking native pointers to higher layers
// (ADR-0005, ADR-0011). Same rationale as FrameFlow.Decoding / FrameFlow.Video.
[assembly: InternalsVisibleTo("FrameFlow.Encoding")]
[assembly: InternalsVisibleTo("FrameFlow.Encoding.Tests")]
