# ADR-0047: Audio Lookahead Buffer for Captioning Sync

**Status:** Proposed
**Date:** 2026-05-16
**Supersedes:** None
**Related:**
- Crossbar ADR-0013 (`LookAhead()` operator — defer to domain) — the upstream coordinated decision this ADR implements — and its companion exploration, `docs/explorations/rate-decoupled-tee-substrate-primitive.md` **in the Crossbar repository**, which holds the broader design space and the rebuttal. Crossbar is the first-party substrate FrameFlow.Graph was forked from (ADR-0049) and is not published; both paths are relative to that repository, not this one, and are named for anyone with access rather than offered as links.
- ADR-0010 (consumer function unification) — operator model this builds on
- ADR-0045 (unified pipeline termination) — sink ownership shape this works around
- docs/DEFERRED_WORK.md → "Operator-based audio effects" — adjacent future work that may reuse this operator's pump shape

## Context

`FrameFlow.Examples.LiveCaptioning` has a structural caption-timing
problem: captions arrive ~3 seconds after the spoken audio because:

- Whisper transcribes in 2.5 s windows (was 5 s; halved in commit
  `2ca3383` to reduce arrival latency, at the cost of more frequent
  inferences and slightly more boundary truncation)
- Whisper inference adds another ~500 ms
- By the time a `Caption` lands in the timeline, the video has
  already played past its `[From, To]` interval

`CaptionOverlayPipelineExtensions.OverlayOnto` works around this with
*arrival-stamped display*: each caption is shown for `DisplayDuration`
seconds starting from its arrival PTS, regardless of the audio PTS it
actually transcribed. This is documented at length in that file's
remarks — the alternative ("interval matching" against
`Caption.[From, To]`) would produce a single-frame flash because the
caption arrives after the matching frame has already rendered.

Recent improvements (commit `2ca3383`) added `SplitOnPunctuation()`
and `AnimatedReveal()` operators that make the arrival-stamped display
*feel* less blocky. But these are visual illusions of liveness, not
latency fixes. The underlying captions are still 3 seconds late.

The only way to make captions land at their actually-spoken PTS is to
**get audio to the captioning pipeline before the video gets to it**.
Then interval matching becomes feasible because the caption exists in
the timeline at the moment the video frame arrives.

Four shapes were considered in design discussion:

- **Option A:** Pre-transcribe the entire file at open time (separate
  audio decode context, full file → Whisper → caption list before
  playback starts). Heavy startup; perfect captions; cachable.
- **Option B:** Separate audio decoder context with rolling lookahead.
  Two decode passes; modest startup; works for seeks.
- **Option C:** Substrate-level rate-decoupled `Tee` in Crossbar. Big
  substrate change; deferred (see crossbar exploration doc rebuttal).
- **Option D:** Domain-local lookahead buffer operator. Single decode
  pass; eager internal pump establishes a fixed audio lead; the
  capture pipeline taps the buffer at the write side.

Option D is the strongest of the four:

| | A | B | C | D |
|---|---|---|---|---|
| Startup latency | Heavy | Modest | None | None |
| Decode passes | 2 | 2 | 1 | **1** |
| Memory cost | Whole-file caption list | Extra audio decoder | Per-branch buffer | ~3 s audio buffer (~190 KB) |
| Code locality | Captioning subsystem | Captioning subsystem | Crossbar substrate | Captioning subsystem |
| Seek behavior | Free | Restart lookahead | Restart lookahead | Flush + refill (~100 ms) |
| Substrate churn | None | None | High | **None** |

A separate question — whether Option D's operator should live in
Crossbar or in FrameFlow — is settled by crossbar's ADR-0013
(domain-local, with explicit trigger conditions to revisit). This
ADR is the frame-flow side of that coordinated decision: build the
operator here.

## Decision

**Build `WithLookaheadBuffer()` as a domain-local extension in
`FrameFlow.Audio` (or `FrameFlow.Whisper` if it turns out to be
caption-coupled enough that Whisper is the natural home).**

Proposed signature:

```csharp
public static FramePipeline<PcmAudioBuffer> WithLookaheadBuffer(
    this FramePipeline<PcmAudioBuffer> audio,
    TimeSpan lookahead,
    out FramePipeline<PcmAudioBuffer> lookaheadTap);
```

### Mechanism

The operator spawns two internal flow paths:

1. **Eager input pump** — a background `Task` that pulls from
   `audio` as fast as upstream can produce, writing each packet into
   an internal bounded channel. Backpressures only when the internal
   channel hits its bound (i.e., when the buffer has reached its
   target lookahead depth). This is what establishes the lead.

2. **Demand-driven output** — when downstream pulls (i.e., the
   `OpenAlAudioSink` consumes), dequeue from the internal channel
   and hand off. Pull rate is set by downstream (OpenAL real-time
   consumption).

Steady state: the input pump keeps the internal channel full at the
target depth. Upstream produces at OpenAL's rate (because the pump
can only pull as fast as the channel has space, and the channel only
has space at OpenAL's drain rate). The N seconds of lead, once
established, are maintained as a fixed offset between "audio that has
arrived in the buffer" and "audio that has been served to OpenAL."

The caption tap reads at the **write side** of the internal channel
— each packet, as it's enqueued, gets forwarded (via `AddRef` + side
write) to a separate channel that the caption pipeline consumes.
Captions see audio with PTS = `(OpenAL's playback PTS) + lookahead`.

When captions are emitted by Whisper, their `Caption.From` /
`Caption.To` correspond to the lookahead-ahead audio PTS. By the time
playback reaches that PTS, the caption has been sitting in the
timeline for `lookahead - inferenceTime` seconds — definitely ready
for interval matching.

### Display-side change

`OverlayCaptionsOptions` gains a `MatchMode`:

```csharp
public enum CaptionMatchMode
{
    Arrival,      // current default — caption shown from its arrival
                  // PTS for DisplayDuration; honors no caption timing
    PtsInterval,  // shown when framePts ∈ [Caption.From, Caption.To];
                  // requires captions to land in timeline before
                  // playback reaches them (i.e., lookahead-fed)
}
```

`CaptionTimeline.GetActive(framePts)` grows a parallel `PtsInterval`
code path. The arrival-stamped path remains the default for backward
compatibility and for live/streaming sources (microphone capture,
network streams) that can't run audio ahead of playback.

### Integration

LiveCaptioning's pipeline becomes:

```csharp
.ConfigureAudioPipeline(audio =>
    audio.WithLookaheadBuffer(
        lookahead: TimeSpan.FromSeconds(4),
        out var captionAudio
    )
)
.WithOpenAlAudio(_loggerFactory)
```

```csharp
var captionPipeline = captionAudio
    .Resample(16_000, 1)
    .TranscribeWithWhisper(_whisperModelPath, new WhisperOptions(
        Language: "en",
        WindowSize: TimeSpan.FromSeconds(2.5)
    ))
    .SplitOnPunctuation();
// AnimatedReveal removed — no longer needed once display is
// PtsInterval. The reveal was a visual illusion of liveness;
// real liveness makes the illusion obsolete.
```

```csharp
captionPipeline.OverlayOnto(
    video.ConvertPixelFormat(PixelFormat.Bgra32),
    new OverlayCaptionsOptions(
        DisplayDuration: TimeSpan.FromSeconds(4),
        MaxStackedLines: 1,
        MatchMode: CaptionMatchMode.PtsInterval
    )
);
```

### Seek behavior

On seek, the buffer's internal channel + the caption pipeline's
in-flight inference state are both stale. The buffer flushes; the
eager pump starts refilling from the new playback position; in-flight
Whisper inference for the old position is abandoned (the bridge's
DropIncoming policy handles the discard naturally).

Refill time = `lookahead seconds of audio / decoder rate`. At
typical decode rates (10–100× real-time for h.264-AAC etc.), 4 s of
audio refills in 40–400 ms. Captions for the new position arrive
within roughly the Whisper window+inference time (~3 s) of the seek
completing — the same latency-to-first-caption as a fresh playback
start, which is acceptable.

## Consequences

### Adopted (recommendation)

- New operator in `FrameFlow.Audio` (probable home;
  `FrameFlow.Whisper` is the alternative). ~100 LOC implementation
  + ~150 LOC tests for the rate-decoupling, EOF, cancellation, and
  seek-flush edge cases.
- `OverlayCaptionsOptions.MatchMode` added (additive, default
  `Arrival` preserves current behavior).
- `CaptionTimeline.GetActive(framePts)` grows a parallel
  PtsInterval code path.
- LiveCaptioning example switches to the new path.
- Documentation note in `CaptionOverlayPipelineExtensions.OverlayOnto`
  remarks: the long-standing rationale block for arrival-stamped
  display now describes the *default* — interval matching is
  available via `MatchMode = PtsInterval` when a lookahead source
  feeds the caption pipeline.

### Architectural costs (the "hurts" the operator brings)

Recorded so they're visible to future maintainers:

1. **Asymmetric operator shape.** Most pipeline operators have one
   input, one output, and run pull-driven. This one has one input,
   two outputs (main path + caption tap), and runs with an internal
   pump that's independent of downstream demand. The asymmetry is
   contained to the operator's implementation but is real.

2. **Pull-based abstraction leak (in-domain).** Every other
   FrameFlow operator runs because someone downstream pulled. This
   one runs because *it pulls itself*, on a Task it spawned. First
   time you debug a deadlock involving this operator, the
   difference from the regular operators will surface. Mitigation:
   call this out in the operator's XML docs and reference this ADR.

3. **Two-consumer backpressure semantics.** If the caption tap is
   slower than OpenAL drain rate, what happens? Tap uses
   DropIncoming (like the current `pcmBridge`), so captions miss
   audio but OpenAL stays smooth. Documented in the operator.

4. **Seek coordination grows one wrinkle.** The buffer + the
   caption pipeline's in-flight Whisper inference + the caption
   timeline all need to flush coherently. Inference is naturally
   abandoned via cancellation; the buffer flush is explicit; the
   timeline drops stale captions on next query.

5. **Master clock semantics gain one extra step to explain.**
   OpenAL is still the master clock; its `_baseSourceTime +
   samples/sampleRate` reads in playback-time PTS, which is what
   video pacing wants. But now the audio decoder is running at
   `(playback PTS + lookahead)`, so if any diagnostic ever asks
   "where is the audio decoder?" it gets a different answer from
   "where is playback?". Probably never matters but worth knowing.

6. **The eager pump is one more thread to manage on lifecycle.**
   Start on first downstream pull, stop on detach / cancellation,
   drain on EOF. Standard concerns; standard means "you have to
   remember them all every time."

None are show-stoppers; they're the price for keeping the substrate
clean while solving the problem in domain.

### Promotion path to substrate

If a second concrete in-substrate consumer materializes that needs
the same asymmetric-tap shape AND cannot be served by the
parallel-pipeline alternative (the higher bar from the crossbar
exploration doc's rebuttal), `crossbar` ADR-0013 spells out the
trigger to promote `LookAhead()` into Crossbar proper. At that
point this operator deprecates in favor of the substrate version
and frame-flow's audio pipeline rewires to use it.

The first second-consumer candidates are the audio effect operators
filed in docs/DEFERRED_WORK.md → "Operator-based audio effects": a look-ahead
limiter needs essentially this same pattern, though scoped to a much
smaller buffer (~10 ms vs ~4 s for captioning). Whether that scope
difference makes the substrate primitive a clean unified shape or a
strained one will be the design question when that work begins.

### Doesn't preclude Option A as a fallback

If Option D turns out to be wrong (the asymmetric operator causes
unanticipated problems, or the lookahead window can't be set high
enough on slow-inference hardware), Option A (pre-transcribe whole
file at open time) is the documented alternative. Both share the
same display-side `MatchMode.PtsInterval` infrastructure, so the
fallback only changes the *source* of the caption track, not the
display.

### Live/streaming sources still work

Sources that can't run audio ahead of playback (microphone capture,
live network streams) keep the existing arrival-stamped display by
omitting `WithLookaheadBuffer` and keeping `MatchMode = Arrival`.
LiveCaptioning is a file-only example today; if it grows a
microphone-input mode later, it'll branch at the audio-pipeline
configurator on source type.

## Notes for the implementation

- Lookahead default of **4 seconds** suggested: covers the 2.5 s
  Whisper window + ~500 ms inference + ~1 s safety margin against
  inference time variance. Configurable per consumer.
- Bound the buffer by **time, not packet count**, in the public API.
  The operator can compute packet count internally from sample rate.
  Time is the intuitive unit for the captioning consumer; future
  consumers (audio effects) likely also want time.
- The eager pump's failure modes need diagnostics — buffer fill
  level, drops on the tap, time-since-last-pull from upstream. The
  rate decoupling is silent when working and silently wrong when
  broken; without observability we'll never debug the "captions
  stopped" case.
- Composes with the existing `SplitOnPunctuation` (still useful).
  Does not compose meaningfully with `AnimatedReveal` (the reveal
  exists to disguise arrival-stamped lag; with PtsInterval matching
  the lag is gone and the reveal becomes a redundant animation that
  visibly fights the actual word timing).
