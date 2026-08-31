# Live Captioning Demo — Build Plan

**Last reviewed:** 2026-05-11
**Status:** Planning
**Owners:** TBD
**Scope:** A focused demo showing FrameFlow + Crossbar running *two*
ML models on the same playback pipeline — YOLO on video, Whisper on
audio — with results merged onto the rendered frame.

## Why this demo

The demo is the proof-by-example for several architectural claims
the project has been making:

1. **Cross-stream ML is first-class.** The
   [OBS replication doc](OBS_REPLICATION.md) argues FrameFlow's
   differentiator is "ML inference as an operator, not a plugin."
   A working captions-while-detecting demo is the physical evidence.
2. **The pipeline merging philosophy works in practice.** The
   "merging pipelines, plainly" essay (an unpublished working note,
   2026-05-11)
   sketched how separate-pipeline outputs should compose onto a final
   frame. This demo is the canonical instance of that pattern.
3. **Crossbar.Onnx + Crossbar.Cuda + frame-flow integrate cleanly.**
   The YOLO example already proved video + ONNX. Adding audio +
   ASR validates the substrate handles *both* streams without
   architectural strain.
4. **The diagnostics surface (ADR-0034) gives end-to-end
   observability for a non-trivial pipeline.** With two inference
   stages and a merge, "where's the latency?" must be answerable
   from `controller.GetDiagnostics()` + the diagnostics tabs.

## What success looks like

A user runs:

```bash
dotnet run --project examples/FrameFlow.Examples.LiveCaptioning -- video.mp4
```

…and gets an Avalonia window showing:

- The video playing through at normal speed
- YOLO bounding boxes drawn on detected objects with class labels
- Live captions appearing near the bottom of the frame as people
  speak, with a ~1-2 second lag (Whisper's chunk window plus
  inference)
- Captions fading out after a few seconds; recent N captions visible
- A status overlay showing: frames decoded / dropped, Whisper
  chunks processed, YOLO inference time, total dropped frames

**Non-goals (deliberate scope cuts):**

- ❌ Real-time microphone capture (deferred — file source first)
- ❌ Speaker diarization (no "Speaker 1: …" labels)
- ❌ Translation (English-only Whisper for v1)
- ❌ Punctuation / casing polish beyond what Whisper emits
- ❌ Speech-to-text on languages other than English
- ❌ Subtitle file output (`.srt` / `.vtt`) — overlay only
- ❌ Configurable models (one fixed YOLOv8n, one fixed Whisper-base.en)

These are all good follow-ups; none are blockers for the demo's
primary purpose.

## Architecture

```
                ┌────────────────────────┐
                │ IPlaybackController    │
                │ (FrameFlow.Playback)   │
                └─────┬──────────────┬───┘
                      │              │
                      │              │
    VideoFrames pull──┘              └──audio sink (ForwardingAudioSink)
       │                                       │
       │                                       ├─→ OpenAlAudioSink
       │                                       │   (audible output)
       │                                       │
       │                                       └─→ Channel<PcmAudioBuffer>
       ▼                                              │
   ┌──────────────────┐                               ▼
   │ YOLO inference   │                     ┌────────────────────┐
   │ (Crossbar.Onnx)  │                     │ Whisper worker     │
   └────────┬─────────┘                     │ (Whisper.net)      │
            │                               │ Audio buffering    │
            ▼                               │ Chunk → ASR        │
  detections per frame                      └────────┬───────────┘
            │                                        │
            │                                  CaptionStream
            │                                  (timestamped text)
            │                                        │
            ▼                                        │
   ┌────────────────────────────────────────────────┴───┐
   │ Caption + Detection overlay renderer (Avalonia)    │
   │  • Resolve current caption by media PTS            │
   │  • Draw bounding boxes (scaled to display)         │
   │  • Draw caption text at bottom with backdrop       │
   └────────────────────────────────────────────────────┘
```

The key shape:

- **Video side** runs in pull mode (no `IVideoSink` registered). The
  Avalonia preview Control is both the consumer and the renderer,
  following the YOLOv8 example's pattern.
- **Audio side** uses a `ForwardingAudioSink` — a small custom sink
  that wraps `OpenAlAudioSink` for audible playback and *also*
  publishes the same `PcmAudioBuffer` to a `Channel<>` the Whisper
  worker reads from. This is the pragmatic answer to the
  sink/pull mutual-exclusion contract (ADR-0032 §3): the sink is
  registered, audio plays through speakers, *and* Whisper still gets
  the buffers via the tap.
- **Caption stream** is a typed channel of `(TimeSpan from, TimeSpan to,
  string text)` records. The renderer looks up which caption is
  current based on the master clock position.

## Build phases

Each phase is independently runnable — at the end of each, you can
launch something and see progress.

### Phase 0: Prerequisites (today)

What we have:

- ✅ `Crossbar.Onnx` for ONNX inference
- ✅ `Crossbar.Cuda` for GPU tensor lifecycle
- ✅ YOLOv8 example (`FrameFlow.Examples.OnnxInference`) — copy
  patterns from this verbatim
- ✅ `IPlaybackController.AudioBuffers` (ADR-0032) — pull-mode audio
  is the technical foundation
- ✅ `controller.GetDiagnostics()` (ADR-0034) — observability is in
  place
- ✅ Hardware decode (ADR-0033) — reduces CPU pressure when both
  models compete for cycles

What's *not* yet in the tree and must be built:

- ❌ Whisper.net or equivalent ASR integration (NuGet package
  choice — see [risks](#risks--open-questions))
- ❌ `ForwardingAudioSink` — small custom sink that forwards to
  OpenAL + a capture queue
- ❌ Audio chunking / windowing logic for Whisper input
- ❌ Caption overlay drawing
- ❌ The example project itself

### Phase 1: Plain captioning to console (~3-5 days)

**Goal:** Audio file → Whisper → captions printed to console with
timestamps. No video, no overlay. Prove the ASR path works end-to-end.

Steps:

1. Create `examples/FrameFlow.Examples.LiveCaptioning` skeleton
   (csproj + Program.cs, mirror `OnnxInference`'s structure).
2. Add NuGet reference to `Whisper.net` (~10 MB native libs across
   platforms). Bundle / auto-download a Whisper model (use
   `ggml-base.en.bin`, ~150 MB).
3. Write `WhisperAsrWorker` — takes a `Channel<PcmAudioBuffer>` from
   one side and emits `(TimeSpan from, TimeSpan to, string text)`
   records to a `Channel<Caption>` on the other side.
4. Audio chunking: collect PCM into 5-second windows with 0.5s
   overlap. Resample to 16kHz mono float32 inside the worker
   (Whisper.net's input format). PcmAudioBuffer is interleaved S16
   at decoder's output rate (default 48kHz stereo) — a simple
   downsample + mono-mix is sufficient for the demo.
5. Pull audio from `controller.AudioBuffers` (no sink registered in
   this phase — the file plays inaudibly).
6. Print each emitted caption to console with its time range.

**Validation:** Run on a known-content video file (TED talk, news
clip). Compare captions to ground truth informally. Captions should
be readable, ~1-3 seconds delayed from the audio.

### Phase 2: Caption overlay on plain video (~3-5 days)

**Goal:** Same as Phase 1 but with the video playing visibly and
captions overlaid at the bottom. No YOLO yet.

Steps:

1. Create an Avalonia preview Control like `Yolov8InferencePreview`,
   but stripped of detection logic. Pull frames from
   `controller.VideoFrames`; copy pixels into a WriteableBitmap;
   invalidate visual on a DispatcherTimer.
2. The Control owns a `CaptionRingBuffer` — keeps the most recent N
   captions (N=3 visible, rest fade out).
3. The Whisper worker enqueues captions to the ring buffer.
4. The renderer overrides `Render(DrawingContext)` and, after
   drawing the video bitmap, draws captions:
   - Black backdrop rounded rectangle at the bottom
   - White text on top, 24pt, centred
   - Only show captions whose `to` timestamp is within the last 6
     seconds of the current playback position
5. **Time resolution:** the renderer reads `controller.Position` for
   "what is the playback head?" — the master clock; this matches
   captions to the frame currently on screen.

**Validation:** Captions appear at roughly the right time relative
to the audio (within ~1 second). Manual A/B with the video's audio
track confirms correctness.

### Phase 3: Wire up audio playback alongside captioning (~2-3 days)

**Goal:** Audio plays through speakers concurrently with captioning.

Steps:

> **Updated 2026-08-26.** The member names below were refreshed against
> the current contracts. `IAudioSink` no longer carries a clock by
> inheritance, `GetPlaybackTime()` no longer exists, and
> `AudioSinkCapabilities` was replaced by `IVolumeControl` in #106.

1. Write `ForwardingAudioSink : IAudioSink, IClockSource`. It owns
   an inner `OpenAlAudioSink` for audible output and exposes a
   `Channel<PcmAudioBufferRef>` for ASR consumers via a property.
2. `PresentAsync(IAudioBuffer, ct)` forwards to the inner sink AND
   clones the buffer into the channel (via PCM copy —
   `ReadOnlyMemory<short>` doesn't transfer ownership). Channel is
   bounded(8) with `DropOldest` so ASR can fall behind without
   backpressuring the audible path. Ownership of the presented buffer
   transfers to the sink, so the inner sink disposes it.
3. Pass through the rest of the sink surface: `ActivateAsync`,
   `PauseAsync`, `ResumeAsync`, `DeactivateAsync`, and
   `GetDiagnostics()`. If the demo needs volume, implement
   `IVolumeControl` and delegate to the inner sink.
4. Register `ForwardingAudioSink` as `IAudioSink` in the example's
   DI; the Whisper worker reads from its capture channel.
5. **Master-clock implication:** delegate both `IClockSource` members
   to the inner sink — `Latest => _inner.Latest` and
   `WaitUntilAsync(target, ct) => _inner.WaitUntilAsync(target, ct)`.
   The OpenAL sink remains the clock producer; ASR is a passive
   observer. Delegating rather than reimplementing matters for
   `WaitUntilAsync`: a pending wait must observe the inner sink's own
   seek and pause discontinuities.

**Validation:** You can hear the video's audio AND see captions for
it on the rendered frame.

### Phase 4: Add YOLO detection alongside captions (~2-3 days)

**Goal:** The full demo — video plays audibly, YOLO bounding boxes
+ captions both overlaid.

Steps:

1. Port the YOLO inference path from
   `FrameFlow.Examples.OnnxInference` directly. The
   `Yolov8Detector` class + `Yolov8ModelDownloader` can be copied
   verbatim (refactor candidate: extract a `FrameFlow.Examples.Yolo`
   shared project once we have multiple examples needing it).
2. The preview Control runs YOLO inference per frame (background
   task; drop frames that arrive during inference).
3. Renderer composes both overlays in `Render(DrawingContext)`:
   bounding boxes from latest detection results, then caption strip
   at the bottom.
4. Status overlay shows: video FPS, YOLO inferences/sec, dropped
   inference frames, Whisper chunks processed, A/V sync drift.

**Validation:** Full demo runs to natural EOF, all overlays visible,
audio audible, captions readable, bounding boxes track moving
objects.

### Phase 5: Polish (~1 week)

Once the demo works end-to-end:

1. **VAD-based chunking** — replace fixed-window audio chunking
   with a VAD operator (Silero VAD as a small ONNX model, ~2 MB).
   Better caption boundaries; no captions during long silences.
2. **Confidence filtering** — Whisper.net exposes per-segment
   probabilities; drop low-confidence segments instead of showing
   gibberish.
3. **Caption styling** — multi-line wrapping for long sentences,
   word-by-word reveal animation, configurable font/colour.
4. **Performance** — profile the hot path. If YOLO + Whisper +
   decode contend for the GPU, consider:
   - Run YOLO on CPU (smaller batches) and Whisper on GPU, or vice
     versa
   - Reduce YOLO input size to 320 instead of 640
   - Use a smaller Whisper model (`tiny.en`, ~75 MB)
5. **Diagnostics tab integration** — wire the demo's stats panel
   to the existing AvaloniaPlayer's diagnostics renderer
   (`DiagnosticsReport.cs`) — share the discipline.

## Risks & open questions

### 1. Whisper.net vs ONNX Whisper

**Decision needed:** Which ASR backend?

| Option | Pro | Con |
|---|---|---|
| **Whisper.net** (P/Invoke to whisper.cpp) | Battle-tested, fast on CPU, NuGet-installable, handles audio resampling + chunking | Native deps per-platform; not architecturally aligned with our "everything's ONNX via Crossbar.Onnx" pitch |
| **ONNX Whisper** (via Crossbar.Onnx) | Aligns with our stack story; same path as YOLO | Whisper as ONNX is fiddlier — separate encoder/decoder models, autoregressive token generation, mel-spectrogram preprocessing not handled by ONNX itself |

**Recommendation:** **Whisper.net for v1**, ONNX migration as a
follow-up. The doc story is "we *can* go all-ONNX," not "we *only*
do ONNX." A working demo in 2 weeks beats a stalled demo in 8.

### 2. Audio sink fork pattern (`ForwardingAudioSink`)

This is the first place where the playback substrate has needed
"play audio AND tap it for another consumer." Two paths:

- **Demo-local custom sink** (`ForwardingAudioSink` in the example
  project) — pragmatic, scope-contained.
- **Built-in tap operator** — promote the pattern to
  `FrameFlow.Playback` or Crossbar so it's reusable. Would resemble
  Crossbar's existing `Broadcast` operator, but applied to sinks
  rather than pipelines.

**Recommendation:** Start with the demo-local custom sink. If a
second consumer wants the same pattern (it likely will — recording
demos, transcript export, etc.), promote in a follow-up commit.

### 3. Audio backpressure and ASR lag

If Whisper inference falls behind the audio stream (e.g., on
CPU-only machines), the capture channel fills. The `DropOldest`
policy keeps audio playback uninterrupted but skips ASR windows.

**Open question:** is dropping audio chunks for ASR acceptable for
a demo? For most demo sources (TED talks, podcasts), the answer is
yes — captions fall further behind temporarily but recover. For a
production captioning system, dropping is unacceptable; you'd need
to buffer or downsample the workload.

**Decision:** Accept dropping for v1. Document it in the demo's
README.

### 4. Caption timestamp accuracy

Whisper emits segment timestamps relative to its input audio chunk.
We need to translate those to media-time (the master clock). The
chain:

- `PcmAudioBuffer.PresentationTime` gives us the media-PTS of the
  first sample of the buffer.
- The Whisper chunk is N concatenated buffers — we know the PTS of
  the first one.
- Whisper outputs `(t_from, t_to)` *within the chunk*.
- Caption media PTS = chunk_start_pts + t_from.

This is straightforward arithmetic but has off-by-one risk. A
sanity test: caption "hello" should appear when the word "hello" is
heard, not 1-2 seconds off.

### 5. GPU contention

If both YOLO and Whisper are GPU-resident on a single GPU, they
queue. On laptops with integrated graphics (no CUDA), both fall back
to CPU and compete with FFmpeg's decoder.

**Validation requirement:** demo must run on CPU-only machines
(e.g., GitHub Actions runners). If it doesn't, the demo's reach is
limited. The smaller Whisper model + smaller YOLO input size from
Phase 5 polish is the mitigation.

### 6. Cross-platform ASR model bundling

Whisper.net needs `ggml-*.bin` model files. Options:

- Bundle in the NuGet package (large)
- Auto-download on first run (matches YOLO example's
  `Yolov8ModelDownloader` pattern)

**Recommendation:** auto-download on first run with a clear progress
indicator. Same pattern as YOLO. ~150 MB for `base.en`.

## Effort estimate

| Phase | Estimate | Cumulative |
|---|---|---|
| 0: prereqs in place | already done | — |
| 1: ASR-to-console | 3–5 days | ~1 week |
| 2: caption overlay on video | 3–5 days | ~2 weeks |
| 3: forwarding audio sink | 2–3 days | ~2.5 weeks |
| 4: YOLO + captions | 2–3 days | ~3 weeks |
| 5: polish | ~1 week | ~4 weeks |

**Realistic budget: 3-4 weeks** of focused work to ship a demo
ready for inclusion in the OBS replication doc as "see, this is what
we mean by ML-native pipelines."

## Anti-goals

- **Production-grade captioning system.** Real ones need
  VAD-based segmentation, multi-speaker handling, punctuation
  restoration, post-processing for filler words, custom vocabulary,
  language detection. None of those are in scope.
- **A standalone captioning library.** The demo is a demo. If we
  later want a reusable captioning operator, extract it then.
- **Multiple Whisper model size support.** One model, fixed. UI
  configurability is post-demo.
- **Audio-only captioning** (no video stream). The demo's value
  comes from showing two streams + two models + merged overlay; a
  command-line ASR is a one-off.
- **Streaming captions to RTMP / file outputs.** The output is the
  Avalonia window. Other targets come as the encoder terminals grow
  beyond H.264/MP4 ([ADR-0053](adr/ADR-0053-h264-mp4-encoder-terminal.md)).

## Dependencies to add

| Package | Why | Size |
|---|---|---|
| `Whisper.net` | ASR | ~10 MB managed, ~50-200 MB native per RID |
| `Whisper.net.Runtime` | Native libs (per RID) | Already covered above |
| Whisper model (`ggml-base.en.bin`) | The model | ~150 MB, downloaded on first run |
| Avalonia (already referenced) | UI | — |
| Crossbar.Onnx (already in tree) | YOLO inference | — |

No new architectural primitives needed in FrameFlow or Crossbar to
ship this demo. That's deliberate — the demo proves the substrate is
sufficient.

## Open call

Bits I'd like a second pair of eyes on before the build starts:

1. **Whisper.net vs ONNX** — call (see [risks](#risks--open-questions)).
   Default to Whisper.net unless someone has a strong opinion.
2. **`ForwardingAudioSink` placement** — demo-local or promoted?
   Default demo-local; promote if a second consumer asks.
3. **Caption styling** — full-frame width caption strip vs centred
   chunk vs floating bubbles? Default to a YouTube-style centred
   bottom strip with a translucent black backdrop.
4. **Status overlay** — same panel as YOLO example, extended with
   ASR stats? Default yes.
5. **Should the demo be added to the solution as a default-build
   example?** YOLO is. Yes for parity.

## Maintenance

Once the demo ships:

- Add a row to `OBS_REPLICATION.md`'s "ML inference" section marking
  ASR as ✅.
- Promote any architectural primitives that emerged (e.g.
  `ForwardingAudioSink` if it gets reused) out of the demo and into
  a proper home.
- File follow-up issues for the deferred items: VAD-based chunking,
  ONNX Whisper migration, real-time microphone input, recording the
  captioned output to a video file.
