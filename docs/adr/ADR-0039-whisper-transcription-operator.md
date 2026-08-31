# ADR-0039: Whisper Transcription as a Crossbar Pipeline Operator (Tier 3, part 1)

**Status:** Accepted (implementing now).
**Date:** 2026-05-12
**Supersedes:** None.
**Related:** ADR-0036 (decode/playback decoupling), ADR-0037 (pixel
operators), ADR-0038 (memory-domain operators),
`docs/CROSSBAR_SHAPING_ROADMAP.html` (Tier 3 audit),
`docs/LIVE_CAPTIONING_DEMO.md` (the demo this serves).

## Context

The live-captioning demo (`FrameFlow.Examples.LiveCaptioning`) wires
Whisper inference by hand:

1. `stream.Audio` → `Resample(16_000, 1)` (Tier 1 vocabulary).
2. `.Observe(packet => normalize S16→float32 + write to chunk Channel)`.
3. A separate `WhisperAsrWorker` task reads from the chunk channel,
   accumulates 5-second windows, runs `WhisperProcessor.ProcessAsync`,
   writes `Caption` records to a caption Channel.
4. A separate console task reads from the caption channel and prints.

That's three coordinated tasks, two channels, a per-window state
machine, and a custom worker class — all to express "audio → text."
The decoded-audio-to-captions plumbing has the same shape as the
pixel operators in ADR-0037: a stateful transform between two
typed streams.

Tier 3 of the Crossbar-shaping roadmap proposes folding this into a
pipeline operator:

```csharp
stream.Audio
    .Resample(16_000, 1)
    .TranscribeWithWhisper(modelPath)   // FramePipeline<Caption>
    .Observe(c => Console.WriteLine($"[{c.From} → {c.To}] {c.Text}"))
    .RunAsync(ct);
```

Everything in this chain is composable with the rest of Crossbar's
vocabulary. The "manage a background worker + thread two channels"
ceremony disappears.

## Decision

Add a new `FrameFlow.Whisper` package, peer to `FrameFlow.Audio` /
`FrameFlow.Video` in shape:

```
FrameFlow.Whisper/
├─ Caption.cs                — public record (From, To, Text). IDisposable.
├─ WhisperPipelineExtensions — public extension method
│     TranscribeWithWhisper(this FramePipeline<PcmAudioBuffer>, string modelPath, WhisperOptions?)
├─ WhisperOptions.cs         — public record (Language, WindowSize, ...)
└─ Internal/
    ├─ WhisperWindowTransform — stateful operator implementation
    └─ PcmFloatConverter       — S16 mono → float32 [-1, 1] inline
```

### Operator surface

```csharp
public static FramePipeline<Caption> TranscribeWithWhisper(
    this FramePipeline<PcmAudioBuffer> audio,
    string modelPath,
    WhisperOptions? options = null);

public sealed record WhisperOptions(
    string Language = "en",
    TimeSpan WindowSize = default,           // default 5 seconds
    int InputSampleRate = 16_000,
    int InputChannels = 1);

public sealed record Caption(
    TimeSpan From,
    TimeSpan To,
    string Text) : IDisposable
{
    public void Dispose() { }   // no-op; value type
}
```

The operator assumes input is at the configured rate / channels (the
caller composes `.Resample(16_000, 1)` first — same shape as every
other operator in the codebase). Mismatched input throws.

### Cardinality

**Many-to-many.** Each upstream `PcmAudioBuffer` contributes samples
to a rolling window; each completed window may produce 0..N
downstream `Caption` packets (Whisper returns multiple segments per
window).

This rules out Crossbar's `Transform` (1-in-1-out). The operator
uses `FramePipeline<Caption>.Create(build)` directly — same pattern
as the seek-aware `GatedBy` design considered (and rejected) in
ADR-0036 Phase 2.

### Window state machine (inside the operator)

```
upstream packet
  ├─ if SampleCount == 0 → skip
  └─ append (converted to float32) to _windowBuffer
       ├─ remember _windowStartPts on first sample
       └─ while _windowBuffer.Count >= _windowSamples:
            ├─ slice off _windowSamples
            ├─ await processor.ProcessAsync(slice)
            │     for each segment:
            │         yield new Caption(windowStartPts + s.Start,
            │                           windowStartPts + s.End,
            │                           s.Text.Trim())
            └─ windowStartPts += windowSize

(at end of input)
  └─ if _windowBuffer not empty:
       └─ run one final inference on the partial window
```

This is the same logic as today's `WhisperAsrWorker.RunAsync`,
restructured to emit through `yield return` instead of writing to a
caption channel.

### Resources and lifetime

`WhisperFactory` + `WhisperProcessor` are allocated lazily on first
pull (matching `Resample` and `ConvertPixelFormat`'s lazy-init).
Both are disposed when the pipeline's enumeration ends (whether
naturally at EOF or via cancellation).

### Why a separate package

`FrameFlow.Whisper` instead of `FrameFlow.Audio.Whisper` or putting
the operator in the example folder:

- **Whisper is one ASR backend among several** (Vosk, Azure
  Speech-to-Text, OpenAI Whisper API, Distil-Whisper, …). Future
  packages can live alongside: `FrameFlow.Vosk`, `FrameFlow.AzureSpeech`.
  Each has its own dependencies and licensing concerns; co-tenanting
  them in one package would pull in everything.
- **`Whisper.net` is a heavy dependency** (~150 MB model file even
  for `ggml-base.en.bin`). Consumers who only want pixel operators
  shouldn't be forced to pull it.
- **Mirrors the `FrameFlow.Audio.OpenAL` pattern.** Audio sinks split
  by backend (`FrameFlow.Audio.OpenAL`, future `FrameFlow.Audio.WaveOut`);
  ASR operators split the same way.

### Caption is `IDisposable` because Crossbar requires it

`FramePacket<TFrame>` constrains `TFrame : IDisposable`. `Caption`
holds no unmanaged resources, so `Dispose` is a no-op. The contract
is met; the cost is one extra method on the record.

This is the same shape `PcmAudioBuffer` uses (its `Dispose` returns
the sample buffer to a pool) — Crossbar's substrate doesn't care
what disposal *means*, only that it's deterministic.

### Migration of the captioning example

After the operator lands, the Program.cs shrinks dramatically:

```csharp
// Before: ~250 lines — ASR worker class, two channels, three tasks.
// After: ~80 lines — stream factory, one pipeline, one observer.

await using var stream = await streamFactory.CreateAsync(MediaSource.FromFile(filePath));
var videoDrainTask = stream.Info.VideoStreams.Count > 0
    ? stream.Video.RunAsync(cts.Token)
    : Task.CompletedTask;

await stream.Audio
    .Resample(16_000, 1)
    .TranscribeWithWhisper(modelPath, new WhisperOptions(Language: "en"))
    .Observe((packet, _) =>
    {
        var c = packet.Frame;
        Console.WriteLine($"[{Format(c.From)} → {Format(c.To)}] {c.Text}");
        return ValueTask.CompletedTask;
    })
    .RunAsync(cts.Token);

await videoDrainTask;
```

`WhisperAsrWorker`, `WhisperFormatConverter`, `AudioChunk`,
`WhisperModelDownloader` (well, that one stays — model acquisition is
orthogonal) all retire from the example. The operator carries them.

## Consequences

### Positive

- **The captioning example becomes a one-liner.** All the ceremony
  (windowing, S16→float32, channel bridging, worker lifecycle)
  hides behind a single operator.
- **The Crossbar-shaping vocabulary grows.** Pixel operators (Tier 1),
  memory-domain operators (Tier 2 Phase A), and now user-domain
  operators (Tier 3) share the same shape. Future operators —
  `DetectWith(yoloModel)`, `MeasureLoudness()`, `EncodeTo(h264)` —
  follow the same template.
- **Per-backend packages stay isolated.** Whisper's 150-MB-model
  dependency lives in `FrameFlow.Whisper`; consumers who want pixel
  operators don't pay for it.
- **Composability is real.** `stream.Audio.TranscribeWithWhisper(...)`
  composes with `.Broadcast`, `.Observe`, `.ToSinkAsync` — all the
  Crossbar idioms. The captioning demo's Phase 2 (overlay captions
  on video) becomes a `Broadcast(audio→Whisper, audio→OpenAlSink)`
  + an Overlay operator on video.

### Negative

- **Many-to-many cardinality requires the raw `FramePipeline.Create`
  factory** instead of `Transform`. More code, more potential
  bugs — but the same shape we already use for source operators in
  `FrameFlow.Decoding`.
- **Whisper.net's process model.** `WhisperProcessor.ProcessAsync` is
  a streaming async-enumerable; we already handle that correctly in
  today's worker. Lifting it into an operator preserves the shape.

### Neutral

- **Caption is a value record with no-op Dispose.** Slightly awkward
  but follows Crossbar's universal constraint.
- **The operator is sync-rate by construction.** Whisper inference
  takes ~1× real-time on CPU for `base.en`; if upstream audio arrives
  faster than inference can chew through, the bounded(1) channel in
  the stream backpressures the demux pump. Same backpressure story
  as every other operator.

## Alternatives considered

### A. Keep the Whisper plumbing in the captioning example

The shape works. But every advanced demo (live RTSP captioning,
multi-source transcription, captioned recording, dub-track generation)
would copy the same boilerplate.

Rejected because consolidating the pattern into an operator
collapses 250 example lines to 80 across every consumer.

### B. Put `TranscribeWithWhisper` in `FrameFlow.Audio`

`FrameFlow.Audio` already has audio-side primitives (`Resample`).
Adding ASR there keeps things "audio-related" in one place.

Rejected — `Whisper.net` is a heavy and opinionated dependency
unrelated to PCM audio resampling. Co-tenancy would pull it in for
every audio consumer.

### C. Express Whisper as an `IAudioSink` consumer

The audio sink contract takes `PcmAudioBuffer` and produces side
effects. A Whisper sink could implement `IAudioSink`, emit captions
through an `IObservable<Caption>` member, and compose via
`pipeline.ToSinkAsync(whisperSink)`.

Rejected because:
- Whisper's output is a *stream of frames*, not a side effect.
  Expressing it as a sink loses the downstream composability — you
  can't `.Broadcast` the captions or pipe them into yet another
  transform.
- The sink lifecycle (`Activate`/`Pause`/`Resume`/`Deactivate`)
  doesn't fit ASR. Whisper has no notion of "pause and resume the
  device."

### D. Use Crossbar's `Transform` and emit multiple Captions per upstream

Not actually possible — `Transform` is 1-in-1-out. We'd have to
return a `Caption` (or wrapper containing multiple) per input
packet, which doesn't match the windowing model where windows
straddle input packets.

`FramePipeline.Create` is the right primitive for
many-to-many.

## Implementation plan

1. Create `FrameFlow.Whisper` project with the surface described above.
2. Move `Caption` and the windowing state machine logic from the
   captioning example into the package.
3. Add tests:
   - Unit tests on the windowing math (chunk accumulation, window
     boundary, EOF flush) without invoking real Whisper.
   - Integration test against a corpus audio file with Whisper loaded
     (gated on model availability).
4. Migrate the captioning example to use the operator.
5. Delete `WhisperAsrWorker.cs`, `WhisperFormatConverter.cs`,
   `AudioChunk` record from the example.
6. Update the `CROSSBAR_SHAPING_ROADMAP.html` Tier 3 section.

Future operators in this family (`DetectWith` for YOLO,
`MeasureLoudness`, etc.) follow the same template. Each lands as its
own package + ADR when there's a consumer pulling for it.
