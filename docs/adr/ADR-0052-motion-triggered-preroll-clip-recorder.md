# ADR-0052: Motion-Triggered Pre-Roll Clip Recorder Example

**Status:** Accepted — the ADR-0040 prerequisite is **delivered**
(see **ADR-0053**: the H.264 → MP4 encoder terminal), and the recorder is
implemented** as the **MotionClip** tool at
`src/FrameFlow.MotionClip` (promoted from an example to a packaged
`dotnet` tool — `motionclip` — for kiosk deployment and standalone use). The §5
H.264 MP4 output is a
thin call into `FrameFlow.Encoding`'s `Mp4VideoWriter`
(`WriteAsync(VideoFrameRef)` + `CompleteAsync()`, or `AsSinkNode()` for the
graph branch). It implements §§3–6 (pre-roll buffer, frame-delta
motion detection, the `Idle → Recording → Saving` state machine, and clip
output) over a live camera tracked resiliently via Periphery's
`DeviceSessionHost` (start-regardless / connect-on-plug / reconnect), with a
windowed preview and a `--synthetic` fallback for camera-less / CI runs;
the motion + pre-roll taps run inline in a single recorder sink node
(§4). Still **follow-up work**: the live-camera source (§1, the
`Camera.Multicast` Periphery path) and the windowed Avalonia presenter
(§2 windowed mode, the status chip / clip counter) — both noted as seams
in the spike's `README.md` and `SyntheticSceneSource`.
**Date:** 2026-05-28
**Related:**
- **ADR-0040 (capture sources and encoder terminals) — PREREQUISITE.** This
  example consumes the H.264 encoder + MP4 muxer terminal that ADR-0040
  designs but leaves deferred ("design only ... until a concrete consumer
  demands it"). This example *is* that concrete consumer. Investigation
  (2026-05-28) confirmed the encode/mux bindings do not exist in
  `FrameFlow.Native` today (`FFAvCodec` binds only the decode loop;
  `FFAvFormat` binds only demux/input), and the existing native bindings are
  `internal` with no `InternalsVisibleTo` for example projects — so the
  output path cannot be example-local FFmpeg code. ADR-0040 must be
  implemented (encode/mux P/Invoke in `FrameFlow.Native` + a Crossbar-shaped
  encoder terminal) before §5 of this ADR can be built. See §5.
- ADR-0009 (threading model) — pre-roll buffer holds frames across concurrent
  producer/consumer threads
- ADR-0012 (memory management) — frames in the ring are un-pooled copies;
  ref-counting discipline is load-bearing where pooled frames are cloned
- ADR-0050 (shape-aware YOLO detection) — motion detection here is NOT YOLO;
  frame-delta on CPU is chosen to avoid model-download dependency and to be
  semantically correct for "pixels moved"
- ADR-0051 (model acquisition) — informs the decision not to use YOLO

## Context

Every existing example is a *consumer* of media: it plays, transcribes, or
annotates a stream in real time and discards each frame once presented. None
demonstrate **stateful pipeline side effects** — keeping frames alive beyond
presentation, reacting to content-level events, or writing pipeline output
back to the filesystem.

A motion-triggered security camera demo exercises four pipeline capabilities
no existing example covers:

1. **Sliding-window frame retention.** Keep the last N seconds of decoded
   frames alive *outside* the normal pool-rental lifecycle so a triggering
   event can look backward in time. This is architecturally non-trivial
   because the pipeline is unidirectional — frames flow forward only — yet the
   recorder needs frames from the past.

2. **In-pipeline motion detection.** A lightweight tap operator that classifies
   each frame without blocking the main path.

3. **Content-driven state machine over a live stream.** Transition from idle
   buffering → recording → saving in response to motion events while the
   main pipeline continues uninterrupted.

4. **Writing pipeline output to disk.** Close the loop between "frames flowing
   through the pipeline" and "a real H.264 MP4 file on disk."

Together these show that FrameFlow's pipeline substrate supports DVR-style
applications, anomaly capture, and event-triggered-record patterns without new
library primitives — everything composes from existing operators and the
AddRef/Clone frame lifecycle.

### What is not in scope

- *Designing* the encoder/muxer abstraction. That is ADR-0040's job; this
  example is its first consumer and inherits whatever public surface ADR-0040
  ships. The output path (§5) is a thin call into that terminal, not
  example-local FFmpeg interop. (An earlier draft of this ADR assumed an
  example-local `VideoClipWriter` driving FFmpeg directly; that was retracted
  once the binding gap was found — see §5.)
- YOLO/ML-based motion detection. Frame-delta on CPU is the right fit: no
  model download, no hardware dependency, no AGPL concern (ADR-0051), and
  semantically correct for "something moved."
- File-based or RTSP sources. The source is a local camera device.

---

## Decision

### 1. Source: live camera

The example captures from the default (or specified) camera device, matching
the `Camera.Multicast` example's capture path. The pipeline runs on an
infinite source; it never terminates naturally. Shutdown is via Ctrl+C
(SIGINT), which fires the top-level `CancellationToken`. On cancellation the
pipeline drains and any in-progress recording clip is saved before exit.

### 2. Two runtime modes: windowed and headless

`Program.cs` parses `--headless`. In headless mode, Avalonia is never
initialised; the pipeline runs as a plain console app driven by a
`CancellationTokenSource`. In windowed mode (default), Avalonia is started
normally and the pipeline adds an `AvaloniaVideoSink` presenter branch.

The core pipeline code — capture, decode, pre-roll buffer, motion detector,
clip recorder — is identical in both modes. The presenter branch is additive.

### 3. Pre-roll buffer: full display-resolution, 2-second default

Frames inserted into the ring are **un-pooled copies** produced via
`CloneCpu()` or equivalent. At 720p BGRA32/30fps the ring holds at most
~60 frames × ~3.5 MB ≈ 210 MB when full. The `PreRollBuffer` constructor
enforces a maximum capacity (frames, not seconds) and documents the size math.
`pre_roll_seconds` is configurable; the default is 2 s.

Un-pooled copies decouple ring depth from the display pool's semaphore. The
display pool is sized for display use; the ring's memory budget is the only
new concern.

### 4. Motion detection: frame-delta on CPU

A stateful tap. Per frame:

1. Downsample to a fixed small size (320×180) and convert to 8-bit grayscale
   using integer arithmetic on `CpuFrameData` pixel bytes.
2. Compute per-pixel absolute difference against the previous downsampled frame.
3. Count pixels where `|a − b| > pixel_threshold` (default 25).
4. Compute `motion_ratio = changed_pixels / total_pixels`.
5. If `motion_ratio > motion_threshold` (default 0.02), fire `MotionDetected`.
6. Store the current downsampled frame as the next reference.

This is ~30 lines of C#. No SIMD intrinsics in the prototype; if the 320×180
grayscale diff proves too slow on a real camera source a `Vector<byte>` path
can be added. The two threshold constants are configurable at construction.

The detector runs inline (synchronous) in the main fan-out node, not on a
skip-while-busy background worker, because detection must keep pace with camera
delivery rate to avoid accumulating lag. At 320×180 grayscale the per-frame
cost is sub-millisecond; if profiling shows otherwise, move to skip-while-busy.

### 5. Output: H.264 MP4 via ADR-0040's encoder terminal (PREREQUISITE)

The clip is written as H.264 MP4 by the encoder/muxer terminal that **ADR-0040
delivers**. This example does not contain FFmpeg encode/mux code.

**Why this is a prerequisite, not example-local code.** An earlier draft of
this ADR proposed an example-local `VideoClipWriter` driving FFmpeg directly.
Investigation on 2026-05-28 retracted that plan:

- **Encode/mux bindings do not exist.** `FrameFlow.Native.Interop.FFAvCodec`
  binds only the decode loop (`avcodec_send_packet` / `avcodec_receive_frame`);
  `FFAvFormat` binds only demux/input (`avformat_open_input`, `av_read_frame`,
  …). Absent: `avcodec_find_encoder`, `avcodec_send_frame`,
  `avcodec_receive_packet`, `avformat_alloc_output_context2`,
  `avformat_new_stream`, `avformat_write_header`, `av_interleaved_write_frame`,
  `av_write_trailer`, `avio_open`. (swscale and AVFrame/AVPacket alloc *do*
  exist, but only cover the conversion + buffer plumbing, not encode or mux.)
- **The bindings that exist are unreachable from an example.** They are
  `internal` to `FrameFlow.Native`, and `AssemblyInfo.cs` grants
  `InternalsVisibleTo` only to `FrameFlow.Decoding` / `FrameFlow.Audio` /
  `FrameFlow.Video` (+ tests) — no example project. So "example drives FFmpeg
  directly" is structurally impossible against the current native layer.

Both gaps are exactly the subsystem ADR-0040 scopes (encode + mux terminals,
Crossbar-shaped, in `FrameFlow.Native` + a terminal surface). Rather than fork
a second, duplicate, example-grade FFmpeg binding set — which would be a
throwaway parallel to the library's own hand-written P/Invoke and would not
survive contact with the eventual ADR-0040 surface — this example waits on
ADR-0040 and consumes the real terminal.

**Shape the example expects from ADR-0040.** A terminal that accepts a stream
of `VideoFrameRef` (BGRA32 at display resolution), encodes H.264, and muxes to
an MP4 path. The recorder hands the snapshot-plus-post-roll frame sequence to
this terminal and awaits completion. The internal BGRA32 → YUV420P conversion,
`AVCodecContext` setup, send-frame/receive-packet drain loop, and
header/trailer writing all live behind ADR-0040's surface, not here.

The output path is `<output_dir>/<timestamp>_clip.mp4`. `output_dir` defaults
to the working directory and is configurable via `--output-dir`.

**Build sequencing.** The pipeline, pre-roll buffer, motion detector, and
recorder state machine (§§1–4, 6) have no dependency on the encoder and can be
built and validated first against a stub clip writer (e.g. one that counts
frames or dumps PNGs for visual confirmation). The H.264 MP4 output is wired in
once ADR-0040 lands.

### 6. Clip recorder state machine

```
         MotionDetected
              │
     ┌────────▼──────────────────┐
     │                           │
 ┌───▼──┐              ┌─────────▼────────┐           ┌─────────┐
 │ Idle │              │    Recording     │─post-roll─►│  Saving │
 └──────┘              └──────────────────┘  timeout  └────┬────┘
     ▲                         │                           │
     │                  MotionDetected                save complete
     │               (reset post-roll timer)              │
     └────────────────────────────────────────────────────┘
                                                 (new MotionDetected
                                                  while Saving: dropped)
```

- **Idle → Recording:** First `MotionDetected`. Atomically snapshot the
  pre-roll ring (lock, drain into array, clear ring). Start accumulating
  post-roll frames via subscription to the fan-out node.
- **Recording → Saving:** Post-roll timeout elapses (`post_roll_duration`,
  default 3 s) with no new motion. Subsequent `MotionDetected` events during
  recording reset the post-roll timer (extending the clip).
- **Saving → Idle:** Background save task (the ADR-0040 encoder terminal, or
  the stub writer until it lands) completes.
  `MotionDetected` events during saving are dropped (logged at `Warning`).
  No save-task stacking.

The state is a simple `enum RecorderState` + `Interlocked.CompareExchange`.
It is intentionally minimal: the interesting design is in the pipeline topology,
not the recorder internals.

---

## Pipeline Topology

```
  Camera source
       ↓
  ConvertPixelFormat(→ BGRA32)
       ↓
  Resize(→ display resolution)
       ↓
  [Configurator-terminated SinkNode]
       │
       ├── [windowed only] AvaloniaVideoSink presenter branch
       │
       ├── MotionDetector tap (inline, synchronous)
       │      Downsamples → grayscale → frame delta → fires MotionDetected
       │
       └── PreRollBuffer tap
              CloneCpu() → insert into capped Queue<IVideoFrame>
              (oldest evicted+disposed when at capacity)
                    │
                    ▼ (on MotionDetected, async)
              ClipRecorder
                    Snapshot pre-roll ring
                    Accumulate post-roll frames
                    Dispatch frames to ADR-0040 encoder terminal
                    (H.264 MP4) on a background task
```

The display pipeline runs uninterrupted. The motion detector and pre-roll
buffer are side-channel taps that clone or downsample frames without holding
the main path.

---

## Avalonia UI (windowed mode)

- Live video feed occupying most of the window.
- A status chip in a corner: `● Recording` (red pulsing) / `○ Idle` (grey).
- Clip counter: `N clips saved to <output_dir>`.
- The window does not have a seek bar or play/pause control; the camera source
  runs continuously and the window exists only to observe the pipeline.

The window structure mirrors `AvaloniaMulticast`: a single presenter sink
(raw `AvaloniaVideoSink` + `Image` control) wired into the fan-out alongside
the detector and ring taps.

---

## Consequences

**Positive:**
- Demonstrates pipeline topology not covered by any existing example:
  pre-roll buffering, content-aware state machines, filesystem side effects.
  Completes the natural progression: playback → real-time analysis → reaction.
- Motion detection is dependency-free. Runs on any machine with a camera.
- The pre-roll buffer pattern generalises to DVR, highlight-clip, and
  anomaly-capture applications. Building it in an example first validates the
  pattern before any library promotion.
- The headless mode establishes a new example archetype: a FrameFlow pipeline
  that runs as a pure console process with no UI dependency, useful for
  embedded, server-side, or automated scenarios.

**Negative / risks:**
- **Memory budget.** Un-pooled ring copies exist outside pool accounting.
  A `pre_roll_seconds = 60` at 1080p allocates ~14 GB silently.
  `PreRollBuffer` must document the math at the constructor and enforce a
  reasonable maximum capacity.
- **`CloneCpu()` / `ToCpu()` correctness.** The pre-roll buffer is the first
  heavy consumer of the CPU-clone path under sustained camera-rate load. Stride,
  padding, and format assumptions will be stress-tested here.
- **Blocked on ADR-0040 for the full feature.** The headline capability —
  saving a real H.264 MP4 clip — cannot ship until ADR-0040's encoder terminal
  is implemented (new encode/mux P/Invoke in `FrameFlow.Native` + a terminal
  surface). This pulls a deliberately-deferred subsystem onto the critical
  path. Mitigated by the build sequencing in §5: everything except the encoder
  can be built and validated against a stub writer first.
- **This example is now the forcing function for ADR-0040's design.** Encoder
  terminal API decisions (frame ownership at the terminal, sync vs. streaming
  encode, error propagation on a bad clip, codec/container parameterisation)
  will be driven by this consumer. That is the intended outcome of ADR-0040's
  "until a concrete consumer demands it" deferral — but it means the encoder
  surface should be designed with this recorder's needs explicitly in view.
- **`CloneCpu()` / `ToCpu()` correctness.** The pre-roll buffer is the first
  heavy consumer of the CPU-clone path under sustained camera-rate load.
  Stride, padding, and format assumptions will be stress-tested here.

**Neutral:**
- No new `src/` library projects *in this example*. Pre-roll buffer, motion
  detector, and the recorder state machine are example-local types. The
  encoder terminal they call into is ADR-0040's `src/` work, tracked there.
- The example does not require a real-time guarantee on the save path. A slow
  encode delays the recorder returning to `Idle` but does not affect camera
  capture or motion detection.
