# ADR-0037: Pixel-Domain Pipeline Operators (Tier 1 of the Crossbar-Shaping Roadmap)

**Status:** Accepted (implementing now).
**Date:** 2026-05-12
**Supersedes:** None. Retires `IFrameConverter` placeholder.
**Related:** ADR-0030 (frame-contract unification with Crossbar), ADR-0036 (decode/playback decoupling), `docs/CROSSBAR_SHAPING_ROADMAP.html` (Tier 1 audit).

## Context

After ADR-0036 the decode pipeline is Crossbar-shaped: `stream.Video`
and `stream.Audio` are `FramePipeline<T>` outputs that compose with
`Transform`, `Observe`, `Broadcast`, `ToSinkAsync`. Audio processing
has a real operator vocabulary — `Resample(rate, channels)` from
`FrameFlow.Audio`. Video processing has none. The moment a consumer
wants pixel format conversion, resize, crop, or overlay, they fall out
of the Crossbar idiom:

- **`IFrameConverter`** is defined in `FrameFlow.Media` but never
  implemented. `PipelineConfig` references it; `PipelineNegotiator`'s
  comment says "Future versions will insert an `IFrameConverter` when
  the format or domain requires it." The placeholder has waited a
  year.
- **Every video sink converts pixel format internally.** The
  Avalonia presenter, the SDL presenter, and the captioning preview
  (when it lands) all bring their own YUV→BGRA conversion. Each
  rebuilds an `SwsContext` the decoder already had to build during
  its internal readback.
- **The inference example wants 640×640 RGB input** for YOLOv8. It
  resizes inside its own preview class because there's no pipeline
  operator for it.
- **Caption overlay** is the headline feature of the captioning
  demo's Phase 2 and there's no way to express it composably today.

The audit document `docs/CROSSBAR_SHAPING_ROADMAP.html` ranks five
tiers of remaining Crossbar-shaping work. **Tier 1 — pixel-domain
operators — is the biggest unlock today**, both because it relieves
concrete pain (the three points above) and because the captioning
demo's Phase 2–5 backlog effectively requires it.

## Decision

Add pixel-domain operators to a new `FrameFlow.Video` package, peer to
`FrameFlow.Audio` in both location and shape:

```
FrameFlow.Audio                     FrameFlow.Video
├─ IAudioResampler.cs    (interface)  ├─ IVideoConverter.cs        (interface)
├─ FfmpegAudioResampler  (swr impl)   ├─ SwScaleVideoConverter     (sws impl)
└─ AudioPipelineExtensions (ops)      └─ VideoPipelineExtensions   (ops)
```

The package depends on `FrameFlow.Media` (for `IVideoFrame`,
`CpuVideoFrame`, `PixelFormat`) and `FrameFlow.Native` (for the
`libswscale` P/Invoke bindings — already present as `FFSwScale`). It
gets `InternalsVisibleTo` to call `sws_*` directly.

### Operators

```csharp
// Convert pixel format, keep dimensions.
public static FramePipeline<IVideoFrame> ConvertPixelFormat(
    this FramePipeline<IVideoFrame> pipeline,
    PixelFormat target);

// Resize, keep format.
public static FramePipeline<IVideoFrame> Resize(
    this FramePipeline<IVideoFrame> pipeline,
    int width,
    int height);

// Both at once, single sws_scale pass.
public static FramePipeline<IVideoFrame> ResizeAndConvert(
    this FramePipeline<IVideoFrame> pipeline,
    int width,
    int height,
    PixelFormat targetFormat);
```

Each operator constructs an `IVideoConverter` captured in the closure
passed to `Transform`. The underlying `SwScaleVideoConverter`
lazy-initializes its `SwsContext` on the first frame, observes the
input dimensions / format, and rebuilds the context if any of (input
dims, input format, output dims, output format) change.

### `IVideoConverter` primitive

```csharp
public interface IVideoConverter : IDisposable
{
    int? TargetWidth { get; }       // null = same as source
    int? TargetHeight { get; }      // null = same as source
    PixelFormat? TargetFormat { get; }  // null = same as source

    CpuVideoFrame Process(IVideoFrame source);
}

public static class VideoConverter
{
    public static IVideoConverter Create(
        int? targetWidth = null,
        int? targetHeight = null,
        PixelFormat? targetFormat = null);
}
```

Caller can use the primitive directly when they need explicit lifetime
control or non-pipeline shapes (e.g., one-shot conversion for a
thumbnail).

### Output format support — initial scope

The first drop supports **packed single-plane output formats** —
`PixelFormat.Bgra32` and `PixelFormat.Rgba32`. These are what sinks
and inference consumers actually want. Multi-plane output (YUV420P,
NV12) is deferred to a follow-up: it requires three-plane buffer
allocation discipline plus a richer `CpuVideoFrame` that exposes
planes properly.

Input format support is unrestricted — anything `swscale` accepts as
input (YUV420P, YUVJ420P, NV12, BGRA, RGBA, and the rest of the
catalog FFmpeg builds with).

### Crop

`Crop(rect)` operator deferred to a follow-up. It doesn't use
`swscale` (a crop is a plane copy with offset, not a resample). Adds
a separate code path inside `SwScaleVideoConverter` or its own
implementation. Low priority — the immediate demo backlog doesn't
need it.

### Overlay

`Overlay(other, blendMode)` operator deferred to a follow-up. It
composes two pipelines, which is structurally different from a
single-input transform. Crossbar has `Broadcast` for fan-out but no
"merge two streams with latest-value matching" primitive yet — the
captioning case (overlay updates much slower than video; composite
the latest overlay onto every video frame) needs a custom enumerator.
Designing this well is its own ~half-day exercise; it's separate
from the swscale-backed transforms here.

This ADR ships the swscale-backed transforms. A follow-up ADR will
land overlay once the design is fully worked out.

### `IFrameConverter` retirement

`IFrameConverter` in `FrameFlow.Media` becomes `[Obsolete]` with a
pointer to the operators. `PipelineConfig.Converter` and the
`IFrameConverter?` parameter on `PipelineNegotiator` lose their
placeholder usage. The interface stays in the surface for one
release to give external consumers time to migrate, then gets
deleted.

## Consequences

### Positive

- **Video sinks can stop carrying their own pixel-format
  conversion.** Sinks that need BGRA compose `.ConvertPixelFormat(Bgra32)`
  upstream of themselves. Avalonia and SDL presenters simplify.
- **Inference pipelines compose cleanly.** YOLO becomes
  `stream.Video.ResizeAndConvert(640, 640, Rgba32).DetectWith(model)`.
  No bespoke preview class needed.
- **The captioning demo's video preview becomes trivial.** When Phase
  2 lands, the video path is `stream.Video.ConvertPixelFormat(Bgra32).ToSinkAsync(renderer)`.
- **Crossbar surface gains a pixel operator vocabulary** that matches
  the existing audio operator vocabulary in `FrameFlow.Audio`. The
  shapes are symmetric and that consistency makes future operators
  obvious to write.
- **`IFrameConverter` placeholder retires.** One fewer "future hook"
  interface in `FrameFlow.Media`.

### Negative

- **One more package in the solution.** `FrameFlow.Video` is the
  fifth `src/` package. Not a real problem at this scale, and the
  symmetry with `FrameFlow.Audio` makes it self-documenting.
- **`SwsContext` rebuild on dimension change** is observable cost.
  Most streams have stable dimensions; the rebuild cost is once per
  episode in practice.
- **Initial scope omits Crop and Overlay**, both of which appear in
  the roadmap. Documented as deferred follow-ups; the operator
  vocabulary is designed to accommodate them.

### Neutral

- **Existing decoders' internal `sws_scale` path is unchanged.** The
  decoder still does its own YUV→BGRA conversion as part of producing
  `CpuVideoFrame`. A future commit could remove that internal pass
  once all sinks consume the pipeline operator instead — but that's
  out of scope here.

## Alternatives considered

### A. Build pixel operators into `FrameFlow.Media`

`FrameFlow.Media` is the cross-cutting types layer (frame contracts,
pixel format enum, metadata). Adding swscale-backed transforms there
would pull a heavy native dependency into the type layer. Rejected
for the same reason `Resample` lives in `FrameFlow.Audio` and not
`FrameFlow.Media`.

### B. Build pixel operators inline in `FrameFlow.Playback`

Already we have `PacedAgainst` there. Adding pixel transforms would
keep things in one place. Rejected because pixel transforms are
useful to consumers that bypass the playback layer entirely — the
captioning demo, the inference example. Putting them in
`FrameFlow.Playback` would force those consumers to take a playback
dependency for no reason.

### C. One-operator-per-knob inside `IVideoConverter`

Have a `VideoConverter` with `Width`, `Height`, `Format` properties
the operator sets. Rejected — the three operators give clearer call
sites (`Resize(640, 640)` vs `Convert(width: 640, height: 640)`) and
the underlying primitive can still combine knobs in `ResizeAndConvert`
for the no-double-conversion case.

### D. Use `libavfilter` instead of raw `libswscale`

`libavfilter` has a graph-based filter API with built-in operators
for resize, format conversion, crop, overlay, color space, and more.
Tempting because it'd give us Overlay and Crop "for free." Rejected
because:
- `libavfilter` graph construction is verbose and stateful — a poor
  fit for the per-operator-instance pipeline shape.
- Crossbar's pipeline IS the filter graph. Letting `libavfilter`
  duplicate that abstraction inside our operators reintroduces the
  conflation we just fixed in ADR-0036.
- `libswscale` is much smaller dependency surface and well-understood
  per the existing `FFSwScale` bindings.

A future ADR could revisit `libavfilter` for operators that genuinely
need its graph (e.g., a complex multi-input filter chain) but that's
not the case for the Tier 1 transforms.

## Implementation plan

1. Add `InternalsVisibleTo` for `FrameFlow.Video` / `FrameFlow.Video.Tests`
   in `FrameFlow.Native/AssemblyInfo.cs`.
2. Create `FrameFlow.Video` project with `IVideoConverter`,
   `SwScaleVideoConverter`, `VideoConverter` factory.
3. Add `VideoPipelineExtensions` with `ConvertPixelFormat`, `Resize`,
   `ResizeAndConvert` operators.
4. Add `FrameFlow.Video.Tests` with unit tests on the converter
   primitive (input → known-output for a synthetic frame).
5. Integration tests against the corpus: decode → operator → assert
   output dimensions / format on real frames.
6. Mark `IFrameConverter` `[Obsolete]` with redirect comment.
7. Update `docs/CROSSBAR_SHAPING_ROADMAP.html` — Tier 1 in flight.
8. Wire into the solution; verify full test suite.

Crop, Overlay, and `libavfilter`-based operators are explicitly out
of scope for this ADR. Each gets its own follow-up.
