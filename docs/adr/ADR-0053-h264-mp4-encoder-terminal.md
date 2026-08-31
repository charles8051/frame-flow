# ADR-0053: H.264 → MP4 Encoder Terminal (first slice of ADR-0040)

**Status:** Accepted — implemented. Delivers the H.264 video encoder + MP4
muxer terminal scoped by ADR-0040, in the shape the current
`FrameFlow.Graph` substrate (ADR-0049) actually takes.
**Date:** 2026-05-28
**Supersedes:** The literal public signatures sketched in ADR-0040's
"Encoder + muxer terminals" section (`IEncoder<TFrame,TPacket>.Encode(FramePipeline<TFrame>)`,
`IMuxer.AddStream(EncodedStreamConfig) → FrameConsumer<EncodedPacket>`,
`IMuxer.RunAsync`). ADR-0040 predates the Crossbar→FrameFlow.Graph fork; those
names referred to substrate types (`FramePipeline<T>`, `FrameConsumer<T>`,
`ToSinkAsync`) that the fork renamed/reshaped. This ADR records what was built.
**Related:**
- **ADR-0040** (capture sources and encoder terminals — design only). This ADR
  implements one slice of it: video-only H.264 → MP4.
- **ADR-0052** (motion-triggered pre-roll clip recorder). The concrete consumer
  that forced this work; its §5 H.264 MP4 output prerequisite is now satisfied.
- **ADR-0049** (FrameFlow.Graph fork). The substrate this terminal composes with.
- **ADR-0044** (sink ownership and disposal), **ADR-0045** (unified pipeline
  termination via `RunAsync`). The ownership/termination model the terminal follows.
- **ADR-0005** (native resource ownership), **ADR-0011** (FFmpeg interop binding
  approach), **ADR-0017** (struct access via generated bindings). The native-layer
  rules the new encode/mux bindings obey.

## Context

ADR-0040 designed capture sources + encoder/muxer terminals as a large,
deliberately-deferred subsystem ("until a concrete consumer demands it").
ADR-0052's clip recorder is that consumer, but only for one narrow path:
writing a stream of decoded BGRA `IVideoFrame`s to an **H.264-in-MP4 file**.

Investigation (ADR-0052, 2026-05-28) confirmed the write direction did not
exist: `FrameFlow.Native` bound only the *decode* loop (`avcodec_send_packet` /
`avcodec_receive_frame`) and *demux* input (`avformat_open_input`,
`av_read_frame`), and those bindings are `internal` with no path to an example.
So the encoder terminal had to be built in the library, not forked as
example-local FFmpeg code.

Two facts shaped the implementation:

1. **ADR-0040's surface predates the substrate fork.** Its `FramePipeline<T>` /
   `FrameConsumer<T>` / `ToSinkAsync` names no longer exist verbatim; the live
   substrate is `Graph` + `SourceNode<T>` / `OperatorNode<T,TOut>` /
   `SinkNode<T>` + ports, with `VideoFrameRef : IRefCounted` flowing through it.
2. **The substrate has no per-operator/per-sink end-of-stream hook.** Node
   bodies run per item; only `SourceNode` has a `Cleanup` callback. But a
   correct encode→mux pipeline has two mandatory EOS operations — *flush the
   encoder* (drain buffered packets) and *write the MP4 trailer* (the `moov`
   index, without which the file is unplayable) — that must happen, in order,
   after the last frame.

## Decision

### Scope delivered

Video-only **H.264 → MP4**. Everything else in ADR-0040 stays deferred:
capture sources (`ICaptureSource`/`Camera`/`Microphone`/`ScreenCapture`/`RtspSource`),
audio encoders (AAC/Opus), HEVC, and the MKV/WebM/HLS muxers.

### Layering: bindings in `FrameFlow.Native`, terminal in `FrameFlow.Encoding`

Mirrors the decode side (bindings in `FrameFlow.Native`, the `VideoDecoder`
impl in `FrameFlow.Decoding`):

- **`FrameFlow.Native`** gains the write-direction P/Invoke:
  `avcodec_find_encoder_by_name` / `avcodec_send_frame` /
  `avcodec_receive_packet` / `avcodec_parameters_from_context` / `av_new_packet`
  (libavcodec); `avformat_alloc_output_context2` / `avformat_new_stream` /
  `avformat_write_header` / `av_interleaved_write_frame` / `av_write_trailer` /
  `avio_open` / `avio_closep` / `avformat_free_context` (libavformat);
  `av_frame_get_buffer` / `av_frame_make_writable` (libavutil). Plus an
  `OutputFormatContextHandle` (SafeHandle that closes AVIO before freeing) and
  write-direction struct accessors that overlay `FFmpeg.AutoGen.Abstractions`
  structs per ADR-0017. `InternalsVisibleTo("FrameFlow.Encoding")` follows the
  `FrameFlow.Decoding`/`FrameFlow.Video` precedent — native pointers never
  escape the encode layer (ADR-0005).
- **`FrameFlow.Encoding`** holds the public contracts and the libav-backed
  implementation together (as `FrameFlow.Decoding` keeps `IVideoDecoder` +
  `VideoDecoder`). A separate `FrameFlow.Encoding.LibAv` package (ADR-0040's
  step 4) was not created: there is only one backend, and the decode side it
  mirrors did not split interface from impl.

### Public surface

- `EncodedPacket : IRefCounted` — managed copy of an encoded `AVPacket` payload
  plus PTS/DTS/duration (in encoder time base), keyframe flag, stream index.
  Refcounted so packets can flow through `FrameFlow.Graph` edges (future
  encode → broadcast → [mp4, hls]).
- `IEncoder<TFrame,TPacket>` / `IVideoEncoder` — `Info`, `Encode(frame) → 0..N
  packets`, `Flush() → tail packets`. A **stateful per-frame primitive**, not a
  `FramePipeline` transform (see divergence below).
- `IMuxer` — `AddVideoStream(encoder)`, `StartAsync`, `WriteAsync(packet)`,
  `CompleteAsync`.
- `EncoderInfo`, `EncodedStreamConfig`, `H264EncoderOptions`.
- `Encoder.H264(...)`, `Muxer.Mp4(path)` factories (ADR-0040's names).
- `Mp4VideoWriter` — the composition **terminal**: owns an encoder + muxer,
  exposes `WriteAsync(VideoFrameRef)` / `CompleteAsync()` and a static
  `RecordAsync(...)`, plus `AsSinkNode()` for graph composition.

### Where this diverges from ADR-0040's literal design, and why

| ADR-0040 (design only) | Implemented (ADR-0053) | Why |
|---|---|---|
| `Encode(FramePipeline<TFrame>) → FramePipeline<TPacket>` | `Encode(frame)` + `Flush()` stateful primitive | No substrate EOS hook for an operator to flush buffered packets on. Matches `VideoDecoder` (a primitive that adapters wrap). |
| `IMuxer.AddStream(EncodedStreamConfig) → FrameConsumer<EncodedPacket>` + `RunAsync` | `AddVideoStream(IVideoEncoder)` + explicit `Start/Write/Complete` | The MP4 `avcC` box needs the encoder's SPS/PPS extradata; wiring `codecpar` straight from the encoder's codec context (`avcodec_parameters_from_context`) is the correct, lossless path. |
| Pipeline-driven, terminal `RunAsync` drains | `Mp4VideoWriter` terminal + caller-driven `CompleteAsync` | The flush-then-trailer ordering must be owned somewhere with an explicit completion point. Mirrors ADR-0044's "owner finalizes the sink", not the pump. |

The `Mp4VideoWriter.AsSinkNode()` path *does* compose into a `Graph` and run
under `graph.RunAsync` (ADR-0045); the consumer calls `CompleteAsync()` once the
run drains, because the substrate cannot signal per-sink EOS. A future substrate
primitive (a node with an on-complete callback) could let a standalone encode
operator flush itself — flagged, not built.

### Encoder choice: `libopenh264` by default

The bundled LGPL FFmpeg build (BtbN `win64-lgpl`) statically links Cisco's
`libopenh264` into `avcodec-61.dll` — no extra DLL. It is a software encoder, so
the round-trip is deterministic and hardware-independent (CI-safe), unlike
`h264_nvenc` / `h264_amf` / `h264_qsv` / `h264_mf`. `H264EncoderOptions.EncoderName`
overrides it for consumers who want a hardware encoder. `libx264` (GPL) is not in
the LGPL build. libopenh264 has no B-frames (`max_b_frames = 0`), so PTS == DTS
and the flush drain is simple. The MP4 global-header flag
(`AV_CODEC_FLAG_GLOBAL_HEADER`) is set so SPS/PPS land in `extradata` for a valid
`avcC` box.

### Verification

`tests/FrameFlow.Encoding.Tests` round-trips synthetic BGRA frames →
`Mp4VideoWriter` → temp `.mp4`, then verifies validity three ways: reopening
through the demux path (codec `h264`, correct dimensions, duration > 0,
keyframe-first packets), through the graph `SinkNode` composition, and via the
bundled `ffprobe` (exact frame count). Pure-contract tests cover the packet /
options surface without requiring FFmpeg.

## Consequences

### Positive

- ADR-0052's clip recorder is unblocked: the H.264 MP4 output is a thin call
  into `Mp4VideoWriter`.
- The write direction now exists in `FrameFlow.Native` symmetrically with the
  read direction, ready for the rest of ADR-0040 (audio encoders, more muxers)
  to extend rather than invent.
- `EncodedPacket` is substrate-shaped, so packet-level graph composition
  (branching one encode to multiple muxers) is available when needed.

### Negative / deferred

- No standalone graph encode *operator* (the EOS-flush gap). The terminal owns
  the lifecycle instead. Acceptable; revisit if a consumer needs encode in the
  middle of a graph rather than at its edge.
- Single video stream per muxer in this slice. Audio interleaving, multiple
  streams, and the other containers remain ADR-0040 future work.
- The `EncodedStreamConfig` record exists for the ADR-0040 surface and
  diagnostics but is not the muxer's wiring mechanism (the encoder's codec
  context is). If packet-only muxing without an encoder handle is ever needed,
  the muxer will grow a config-driven `AddStream`.

### Neutral

- `FrameFlow.Encoding` and `FrameFlow.Encoding.Tests` are added to
  `FrameFlow.slnx`. No packaging changes; the project is not published.
