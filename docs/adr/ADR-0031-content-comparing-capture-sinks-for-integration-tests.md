# ADR-0031: Content-Comparing Capture Sinks for Playback Integration Tests

**Status:** Proposed
**Date:** 2026-05-11
**Supersedes:** None
**Related:** ADR-0022 (long-lived workers), ADR-0025 (video sink and frame pool architecture), ADR-0029 (`ChannelVideoSink`), ADR-0030 (frame contract unification with Crossbar)

## Context

FrameFlow's playback runtime ships with a set of integration tests
that exercise the full demux → decode → present pipeline against the
real corpus files in `tests/corpus/`. Those tests use a pair of
instrumented sinks — `HarnessAudioSink` and `HarnessVideoSink` — that
live in `tests/FrameFlow.Integration.Tests/Harness/`. The harness
sinks are excellent at what they're designed to do: they count
lifecycle calls (`Activate` / `Pause` / `Resume` / `Deactivate`),
count blocks and frames, track PTS monotonicity, and simulate the
sink-owned `CpuFramePool` backpressure semantics so the pipeline
behaves like it would in production.

They do **not** retain the actual content. `HarnessAudioSink.WriteAsync`
records the block count and updates a sample counter but drops the
`PcmAudioBuffer.Samples` span on the floor.  `HarnessVideoSink` keeps
an `Interlocked.Exchange`-shaped pending slot but never copies the
pixel data out. The two sinks let us assert "the pipeline played
this many frames with monotonic PTS"; they do not let us assert "the
audio the pipeline produced is the same audio that should be
produced for this source file."

### The bug that surfaced the gap

ADR-0030's deferral kept the playback worker push-shaped and the
inference example bridges to a Crossbar pull pipeline through
`ChannelVideoSink`. In that topology, the playback video worker
calls `_audioSink.GetPlaybackTime()` once per decoded frame for AV
sync. The inference path drops frames downstream non-blockingly, so
the video worker decodes flat-out, hammering `GetPlaybackTime` from
its thread. `OpenAlAudioSink` had no internal lock; the audio
worker's `WriteAsync` and the video worker's `GetPlaybackTime` raced
on the `Queue<uint> _freeBuffers` and on `_processedSamplesPerChannel`
(the buffer-recycle accounting). The corruption surfaced as audibly
**looping audio** at the start of every inference-example playback.

The race was not caught by any test in the repo. None of the
integration tests:

1. exercise the topology where the video worker hammers
   `GetPlaybackTime` from a thread independent of the audio worker
   (`HarnessVideoSink` synthesizes a 16 ms pump, far slower than the
   inference video worker's actual rate);
2. retain the audio output as PCM so that it can be cross-correlated
   against itself or against a reference to detect duplicated
   segments;
3. compare any pipeline output against a deterministic ground-truth
   decode of the source.

The fix (see commit `8b370ab`) was a state lock in `OpenAlAudioSink`,
but the *class of bug* — a content-level regression that lifecycle
counters can't see — is exactly the class we should be able to catch
in CI without humans listening to playback.

### What we want from the next layer of tests

Concretely we want to catch:

- **Audio looping / replay** — the bug we just fixed, and the
  general class where audio samples are emitted more than once or
  out of order.
- **Audio drop / skip** — samples missing relative to source.
- **A/V sync drift** — the audio position diverging from the video
  PTS over time.
- **Stuttering / underrun stalls** — silent gaps in audio, frame
  holds in video.
- **Video frame content corruption** — pixel data that doesn't
  match the source decode (a regression in color space conversion,
  scaling, or the frame-copy paths).
- **PTS sequence violations** — non-monotonic, gapped, or duplicated
  timestamps on either stream.
- **Wrong total duration** — playback that terminates early or
  doesn't terminate at all.

Critically, **all of these are deterministic, bit-level signals**.
They don't require semantic understanding of the content. We do
not need a model to know whether audio looped — we need to know
whether the same PCM samples appeared twice.

### What we rejected up front

A reasonable-sounding proposal was to use VLC (or any external
reference player) as ground truth: screen-capture VLC playing a
corpus file, screen-capture our player playing the same file, feed
both into a multimodal model (CLIP, ImageBind, Gemini-class) to
produce an embedding, and compare embeddings via cosine similarity.

This was rejected for three reasons.

1. **Multimodal embeddings abstract away the very property we need.**
   These models are trained to map "Rick Astley singing" to a tight
   cluster regardless of pitch, tempo, looping, or stuttering.
   Looped audio of the right song would embed close to correct
   playback of the same song. The embedding is *built to ignore*
   the temporal-correctness signal we want.

2. **Compounding non-determinism.** Screen capture introduces
   variance from display refresh rate, compositor scaling, HiDPI
   resolution, color management, audio loopback routing, window
   focus state, and background processes. Multimodal inference
   introduces its own variance. Two non-deterministic sources
   stacked means we'd be calibrating thresholds against noise on
   noise — high CI flake risk.

3. **Tooling weight.** VLC + screen recorder + audio loopback +
   multimodal API + embedding store + cosine threshold tuning, all
   for every CI run, when the bit-exact signals we actually care
   about are free to compute in-process.

The multimodal-embedding approach probably has a place in a much
later "does the rendered application look approximately right"
smoke test, but it is not a fit for catching content-level
playback regressions.

## Decision

Add a content-capturing integration test layer to FrameFlow:

### 1. `CapturingAudioSink` and `CapturingVideoSink`

Two new sinks in `tests/FrameFlow.Integration.Tests/Harness/` that
sit alongside the existing `HarnessAudioSink` and `HarnessVideoSink`.
The capturing sinks retain **all output content** as it flows
through:

- `CapturingAudioSink` records each `PcmAudioBuffer` as a
  `(TimeSpan pts, short[] samples, int sampleRate, int channels)`
  tuple. PCM samples are copied out of the pooled block buffer at
  `WriteAsync` time so the capture survives the block's eventual
  disposal.
- `CapturingVideoSink` records each `IVideoFrame` as a
  `(TimeSpan pts, TimeSpan duration, int width, int height, PixelFormat format, byte[] pixels)` tuple. Pixels
  are copied off the pool-owned frame at `PresentAsync` time so the
  capture survives frame return. The sink owns a real
  `CpuFramePool` so frame-pool backpressure stays faithful to
  production.

Both sinks are `internal` to the integration-tests assembly. They
implement the full `IAudioSink` / `IVideoSink` contract and are
DI-registered via `Func<IAudioSink>` / direct singleton so the
playback runtime uses them in place of the production sinks.

### 2. Reference decoder

A small helper, `ReferenceDecoder`, that takes a corpus-file path
and produces the **ground-truth** audio PCM and video frame
sequence for that file by driving FrameFlow's own `AudioDecoder`
and `VideoDecoder` directly (no playback runtime, no clock, no
sync). The output shape is the same as what the capture sinks
collect — `(pts, samples)` for audio, `(pts, pixels)` for video —
so reference and capture compare 1:1.

The choice of "FrameFlow's own decoder as reference" rather than a
separate FFmpeg subprocess is deliberate: same codec configuration,
same resampler settings, same pixel-format conversions. The
reference is what the pipeline would produce **if it ran without
the playback-runtime concurrency, the AV-sync waiting, and the
sink-owned frame pool**. Differences between reference and capture
are therefore attributable to the runtime layer, not codec-version
drift.

### 3. Assertion library

A `PlaybackInvariants` static class with focused, named assertions
that operate on the captures:

- `AudioPcmMatchesReference(capture, reference, samplesPerChannelEpsilon)`
- `NoDuplicateAudioSegments(capture, windowMs, correlationThreshold)`
  — windowed self-cross-correlation; flags any non-trivial offset
  with correlation above threshold. **This is the targeted test
  for the loop bug we just fixed.**
- `PtsStrictlyMonotonic(captureAudio)` / `PtsStrictlyMonotonic(captureVideo)`
- `AvSyncWithinTolerance(captureAudio, captureVideo, maxDriftMs)`
  — for each captured video frame's PTS, check that the captured
  audio at the same wall-clock position is within ±N ms.
- `VideoFramePixelsMatchReference(captureVideo, referenceVideo, ssimThreshold)`
  — per-frame structural similarity (or pHash) check. Exact-byte
  match is too strict because hardware-decode paths may use
  different chroma rounding; SSIM ≥ 0.99 is the right floor.
- `TotalDurationMatches(captureAudio, expectedSeconds, toleranceMs)`

Each invariant is independently callable so individual tests pick
the ones they care about. A test for the audio-loop regression
calls `NoDuplicateAudioSegments` and nothing else; a full-pipeline
test calls all of them.

### 4. `PlaybackHarness`

A test-side helper that wires up the DI container with the
capturing sinks substituted, loads a corpus file, plays it through
to natural EOF, and returns the captures plus the controller for
post-hoc assertions. Mirrors the pattern of the existing
`IntegrationTestHelper` but produces content, not just counts.

### 5. Corpus expansion (incremental, not blocking)

The existing `tests/corpus/` already has the codec coverage we
need. We add one **A/V-sync-sensitive** clip: a few seconds of
audio that contains discrete, easy-to-localize transients (clicks
or beeps at known offsets) over a video with discrete visual
events at the same offsets. This makes A/V sync drift assertions
quantitative — any deviation between the click PTS and the visual-
event PTS is the drift.

This corpus addition is the only test-data change. The existing
files cover the codec-correctness axis already.

### 6. Layering and CI gating

- **Tier 1 (PR gate):** `PlaybackHarness`-driven content tests
  against 2–3 small corpus files (< 5 s each, < 50 MB combined
  in memory at peak). Runs in seconds. Catches the regression
  classes listed above. Same `dotnet test` invocation as the rest
  of the integration suite.
- **Tier 2 (nightly / on-demand):** broader corpus sweep covering
  all codec / pixel-format permutations. Same harness, more files,
  slower.
- **Out of scope for this ADR:** end-to-end tests that exercise
  the rendered Avalonia or SDL surfaces. Those need either an
  Avalonia render-target capture or a windowed test on a tagged
  release pipeline. We may revisit in a separate ADR. The
  capture-sink layer described here catches the bugs that live in
  the playback runtime; the rendering layer is a separate concern.

## Consequences

### Positive

- The audio-loop bug class becomes a targeted regression test
  (`NoDuplicateAudioSegments`). Future races in the audio sink
  surface in CI, not in production playback.
- AV-sync regressions become measurable, not "did the QA reviewer
  notice it sounded off."
- Content corruption regressions (color space, resampler, frame
  copy) become measurable.
- Zero external tooling. Headless. Runs anywhere `dotnet test`
  runs.
- Composes cleanly with the existing harness sinks: tests that
  only need counts continue to use `HarnessAudioSink`; tests that
  need content opt into `CapturingAudioSink`.

### Negative / costs

- **Memory.** A 5-second 1080p clip at 30 fps = 150 frames × 8.3 MB
  = ~1.2 GB of pixel data if we keep all frames. Mitigated by:
  short corpus files for Tier 1, optional pixel downsample-on-
  capture, and the fact that audio capture (5 s × 48 kHz × stereo
  × 2 bytes = 960 KB) is negligible. Pixel capture is the budget
  item; tests that don't need per-frame pixel comparison can
  disable it.
- **Reference-decode drift.** Using FrameFlow's own decoder as the
  reference means the reference moves when the decoder moves. A
  resampler change that affects all consumers identically would
  pass the test even though it changed user-perceptible output.
  Acceptable because (a) decoder-level changes are caught by
  decoder unit tests, and (b) the assertion target here is
  *runtime-introduced* divergence from same-decoder output.
- **One more pair of sinks to maintain.** Real cost. Mitigated by
  the sinks being small (estimated ~150 lines each, see sketch)
  and test-only.
- **Doesn't validate display / audio device output.** Explicit
  non-goal. Anything that requires checking what comes out of the
  speakers or what's on screen needs a separate layer.

### Neutral

- Does not change any production API surface. All new types are
  `internal` to the integration-tests assembly.

## Alternatives considered

### A. Screen capture + multimodal embedding (the rejected option)

Discussed in the Context section. Wrong tool for the failure modes
we want to catch; would have missed the audio loop bug it was
nominally proposed to catch.

### B. Bit-exact PCM comparison without epsilon

Reject. Resampler implementations have small rounding differences
that don't matter audibly but would fail bit-exact comparison.
Epsilon-tolerant comparison (RMS error < 1 LSB) catches real
regressions without flaking on resampler precision.

### C. External `ffprobe` analysis of captured files

Reject. Adds a subprocess dependency, gives us less control over
the exact PCM/pixel comparison, slower than in-process assertions,
and produces text output we'd have to parse.

### D. Just extend `HarnessAudioSink` / `HarnessVideoSink` with
opt-in capture mode

Considered seriously. Final call: separate types. The lifecycle
harness sinks and the content-capture sinks have different default
memory profiles (the harness sinks intentionally don't keep
content; the capture sinks intentionally do), and combining them
muddies the type-level contract that callers depend on. Tests
that want both can register both kinds and tee.

### E. Property-based testing on the playback output

(QuickCheck-style: generate random corpus files, run pipeline,
check invariants.) Out of scope for this ADR. The capturing-sink
infrastructure is a prerequisite for that work and doesn't
foreclose it. A subsequent ADR could layer property-based tests
on top of the harness once it's in place.

## Implementation plan

1. **Sketch** (this ADR + companion sketch — landing together).
2. Land `CapturingAudioSink`, `CapturingVideoSink`,
   `ReferenceDecoder`, `PlaybackInvariants`, `PlaybackHarness` in
   `tests/FrameFlow.Integration.Tests/Harness/`.
3. Port the four most useful assertions (`NoDuplicateAudioSegments`,
   `PtsStrictlyMonotonic`, `AvSyncWithinTolerance`,
   `AudioPcmMatchesReference`) and write one test per assertion
   against an existing corpus file.
4. Add the A/V-sync-transient corpus clip (#5 above).
5. Wire the inference-example specific scenario:
   `InferenceTopology_NoDuplicateAudioSegments` — a test that
   reproduces the cross-thread `GetPlaybackTime` pattern the
   inference example creates, against the capturing audio sink,
   asserting on `NoDuplicateAudioSegments`. This is the locked-in
   regression for the bug we just fixed.
6. Add Tier 2 (codec sweep) as a separate CI lane, marked `[Trait("Tier","2")]`.

Steps 2–5 are one PR. Step 6 is a follow-up.

## Coverage matrix

This table grounds the abstract claim "content-level tests catch more
than counting tests." For each named failure mode, it states whether
the existing frame-counting / lifecycle-counter approach catches it,
and which named invariant in this ADR's harness catches it. **The
"Yes (harness)" answers depend on the assertion being implemented**
— see the implementation plan above; the sketch currently has
`PtsStrictlyMonotonic` and `DurationsMatch` filled in and the others
as `NotImplementedException`-bodied stubs.

| Failure mode | Frame counting / lifecycle | Content-capture harness |
|---|---|---|
| Audio loop — same samples reappear later in stream | No — count is still right | **Yes** — `NoDuplicateAudioSegments` |
| Audio drop — samples missing mid-stream | No — total count slightly off, within tolerance | **Yes** — `AudioPcmMatchesReference` |
| Audio underrun silent gap (no actual data loss) | Sometimes — depends on the wall-clock budget assertion | **Yes** — `AudioPcmMatchesReference` (gap appears as zeros) |
| Channel swap (L↔R) | No | **Yes** — `AudioPcmMatchesReference` |
| Sample-rate confusion (48k → 44.1k regression) | No | **Yes** — `AudioPcmMatchesReference` + `DurationsMatch` |
| Byte-order flip in resampler output | No | **Yes** — `AudioPcmMatchesReference` |
| A/V drift (audio runs ahead/behind > tolerance) | No | **Yes** — `AvSyncWithinTolerance` |
| Frame reordering within correct total count | No | **Yes** — `PtsStrictlyMonotonic` + pixel match |
| Stuck/repeated video frame (PTS advances, pixels don't) | No | **Yes** — `VideoFramePixelsMatchReference` |
| Color space regression (BGR↔RGB, YUV plane swap) | No | **Yes** — `VideoFramePixelsMatchReference` |
| Decoder fallback to a wrong-looking codec path | No | **Yes** — `VideoFramePixelsMatchReference` |
| Wrong PTS values | Partially — only if PTS-monotonic asserted | **Yes** — explicit invariant |
| Wrong total duration | Yes — if duration assertion exists | **Yes** — `DurationsMatch` |
| Pipeline runs to completion without faults | **Yes** | Yes |
| State-machine transitions correct | **Yes** | Yes (orthogonal — both still apply) |
| Wall-clock budget / no stalls | **Yes** | Yes (orthogonal — both still apply) |
| Lifecycle counts (`Activate` / `Pause` / `Resume` / `Deactivate`) | **Yes** | Not the focus — use `HarnessAudioSink` |

The pattern: counting tests cover the *structural* axis (did the
pipeline run, did the right number of things happen, in the right
order at the state-machine level). Content-capture tests cover the
*content* axis (was the data correct). They're complementary; the
new layer doesn't replace the existing one.

## What this layer does not catch

A separate honest accounting — so future readers don't oversell
what landing this buys us.

1. **Race conditions that don't always trigger.** The
   `OpenAlAudioSink` lock-bug (commit `8b370ab`) is the prototype.
   The harness *can* surface that race because the playback runtime
   still calls `GetPlaybackTime` from the video worker against
   `CapturingAudioSink`, but contention is calmer than in the
   inference example (`CapturingVideoSink` has frame-pool
   backpressure throttling the video worker, unlike the
   `ChannelVideoSink` + downstream-drop topology where frames are
   discarded non-blockingly). The race might surface on one run
   in five. **For race conditions, targeted concurrency unit
   tests like `OpenAlAudioSinkTests.ConcurrentLifecycleAndPlaybackTime`
   are the right tool, not this harness.**

2. **Bugs in code paths the corpus doesn't exercise.** The harness
   only catches bugs surfacing in `tests/corpus/files/`. A 10-bit
   HDR video bug, an 8-channel surround audio bug, or a
   variable-frame-rate bug needs a corpus file that exercises that
   path. Coverage is bounded by corpus breadth.

3. **Bugs in topologies the harness doesn't model.** The harness
   substitutes simple capture sinks. The inference-example race
   manifested because of a *specific* downstream topology
   (`ChannelVideoSink` + Crossbar pipeline + non-blocking
   downstream drop). Bugs that only fire when a particular consumer
   is wired up need a test against that consumer's topology —
   apply the same capture-sink pattern at the consumer's seam.

4. **Rendering-layer bugs.** Anything in the Avalonia compositor,
   SDL surface, DispatcherTimer-driven `InvalidateVisual` cadence,
   or HiDPI scaling lives downstream of the sink interface and is
   invisible to this layer. Explicit non-goal (see Decision §6).

5. **Performance / perceptual quality regressions that aren't
   correctness regressions.** Audio that's bit-exactly correct but
   delivered choppily. Video that's correct but stutters during
   render. The wall-clock-budget assertions from the existing
   integration tests catch some of this; bit-exact content
   comparison does not.

## Test-layer position

Where this layer sits relative to the rest of the test surface:

```
┌─────────────────────────────────────────────────┐
│ Rendering (Avalonia/SDL compositor, HiDPI)      │ ← uncovered
├─────────────────────────────────────────────────┤
│ Topology / integration (consumer wiring)        │ ← partial; one-offs per consumer
├─────────────────────────────────────────────────┤
│ Content correctness (PCM, pixels, A/V sync)     │ ← NEW: this ADR
├─────────────────────────────────────────────────┤
│ Pipeline structure (counts, lifecycle, PTS)     │ ← existing `Harness*Sink`
├─────────────────────────────────────────────────┤
│ Component concurrency (locks, races)            │ ← existing unit tests (e.g.
│                                                 │   `OpenAlAudioSinkTests`)
├─────────────────────────────────────────────────┤
│ Unit logic (decoder math, state-machine rules)  │ ← existing unit tests
└─────────────────────────────────────────────────┘
```

The new layer closes the **content correctness** gap. It does not
substitute for the layers below it (unit tests for component
correctness, concurrency tests for race conditions) or above it
(topology- and rendering-layer tests for consumer-specific and
rendered-output bugs). A regression in any layer needs the test
type that lives at that layer; the harness is necessary but not
sufficient for full coverage.
