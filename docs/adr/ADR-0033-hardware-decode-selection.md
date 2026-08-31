# ADR-0033: Hardware decode capability probing and selection

## Status

Accepted

## Context

ADR-0004 deferred hardware decode to "Phase 09 (optional)" and ADR-0015
(superseded by ADR-0030) reserved the seam for GPU-resident frames at the
*producer* side. Neither ADR specifies how FrameFlow should pick a hardware
decoder, how it discovers what's available on the host, or how callers steer
behaviour. The current `VideoDecoder` is software-only:
`avcodec_find_decoder(codecId)` → `avcodec_open2(ctx, codec, null)`. Nothing in
`FrameFlow.Native` enumerates compiled-in hwaccels or probes whether they
initialise on this machine, and nothing in `FrameFlowOptions` exposes a knob
for the consumer.

FFmpeg exposes hwaccel via two intertwined mechanisms:

1. **Hardware decoders** — codec-specific entry points like `h264_cuvid`
   (full decoder implementations, distinct `AVCodec`).
2. **Hardware accelerators** — `AVHWDeviceType` values (`CUDA`, `VAAPI`,
   `D3D11VA`, `DXVA2`, `VIDEOTOOLBOX`, `QSV`, `MEDIACODEC`, …) that attach
   to a *software* decoder via `hw_device_ctx` so it offloads work to the GPU
   while staying the same `AVCodec`.

The (2) path is the one we use: it's simpler (no second `avcodec_find_decoder`
lookup table), it covers the vast majority of platform pairings, and it
matches what FFmpeg's own examples document as the modern path. The `cuvid`-style
explicit decoders remain a future option for codecs where (2) is unavailable.

Three concrete questions need answers:

1. **Local environment detection** — what hwaccels exist in this FFmpeg build
   *and* actually initialise on this host?
2. **Capability introspection** — how do callers find out which backends are
   available (for diagnostics, logging, UI)?
3. **Selection policy** — how does the user steer "try HW, fall back to SW"
   vs. "HW required" vs. "SW only," and which backend wins when several apply?

This ADR scopes the **decoder-side** answer. Zero-copy GPU *delivery* (a
producer-side `GpuVideoFrame` flowing through the pipeline to a GPU-aware
presenter) is the seam ADR-0030 reserves and is explicitly a follow-up
ADR. v1 of hwaccel decodes on the GPU and downloads to CPU
(`av_hwframe_transfer_data`), producing the same `CpuVideoFrame` the
software path produces.

## Decision

### 1. Probe at bootstrap time, cache for the process lifetime

`FrameFlowBootstrapper` gains a second phase after the FFmpeg load succeeds:
walk `av_hwdevice_iterate_types`, then for each compiled-in type call
`av_hwdevice_ctx_create(out ctx, type, NULL, NULL, 0)`. The temporary
context is unref'd immediately — we only want the success/failure verdict.

The result is exposed on `FrameFlowBootstrapResult`:

```csharp
public sealed record FrameFlowBootstrapResult(
    bool IsSuccess,
    string? ResolvedPath,
    FfmpegBinarySource BinarySource,
    string Message,
    HardwareDecodeCapabilities Capabilities  // new
);

public sealed record HardwareDecodeCapabilities(
    IReadOnlyList<HardwareDecodeBackend> Available
);

public sealed record HardwareDecodeBackend(
    HardwareDecodeBackendKind Kind,
    string DisplayName,
    string AvDeviceTypeName,
    bool Initialized,
    string? DiagnosticMessage
);

public enum HardwareDecodeBackendKind
{
    Cuda, VaApi, D3D11Va, Dxva2, VideoToolbox, Qsv, MediaCodec,
    Vulkan, Drm, Vdpau, Other
}
```

A backend with `Initialized = false` is still listed (so consumers can log
"VAAPI compiled in but couldn't open `/dev/dri/renderD128`") — selection
filters to `Initialized = true`.

Probe cost is one `av_hwdevice_ctx_create` + `av_buffer_unref` per compiled-in
type. Measured roughly 10–50 ms total on a system with one or two backends.
Cheap enough to be unconditional; can be disabled via
`FrameFlowNativeOptions.SkipHardwareProbe` for environments that want to keep
bootstrap minimal (constrained containers, smoke tests).

### 2. Capability surface is a single read-only object

`HardwareDecodeCapabilities` is attached to `FrameFlowBootstrapResult` and
also registered as a singleton service so DI consumers can inject it without
holding the bootstrap result. The set is computed once during
`FrameFlowBootstrapper.Initialize` and never mutates — there is no
"refresh" API. Driver changes mid-process are out of scope.

### 3. Selection policy lives on `FrameFlowVideoOptions`

```csharp
public sealed class FrameFlowVideoOptions
{
    public HardwareDecodeOptions HardwareDecode { get; set; } = new();
}

public sealed class HardwareDecodeOptions
{
    public HardwareDecodeMode Mode { get; set; } = HardwareDecodeMode.Auto;

    /// <summary>Preferred backends in priority order. Empty = OS default order.</summary>
    public IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends { get; set; } = [];
}

public enum HardwareDecodeMode
{
    /// <summary>Never attach an hwaccel; always use the software decoder.</summary>
    Disabled,

    /// <summary>Try hwaccel; fall back to software if none binds. (Default.)</summary>
    Auto,

    /// <summary>Try hwaccel; fail Load if none binds.</summary>
    Required,
}
```

**Selection algorithm** (executed by `VideoDecoder.Open`):

1. Resolve the codec via `avcodec_find_decoder(codecId)` (unchanged).
2. If `Mode == Disabled`, open software-only. Done.
3. Otherwise walk `avcodec_get_hw_config(codec, i)` until exhausted, building
   a candidate list of `(AVHWDeviceType, hwPixelFormat)` pairs whose config
   advertises `AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX`.
4. Intersect that list with `HardwareDecodeCapabilities.Available` filtered
   to `Initialized = true`.
5. Sort by `PreferredBackends` first, then OS default order:
   - **Windows:** D3D11VA → DXVA2 → CUDA → QSV
   - **Linux:** VAAPI → CUDA → VDPAU → QSV
   - **macOS:** VideoToolbox
6. For each candidate in order:
   a. Create a fresh `AVHWDeviceContext` of that type.
   b. Set `codec_ctx->hw_device_ctx = av_buffer_ref(device_ctx)`.
   c. `avcodec_open2`. On success, capture the chosen backend and break.
   d. On failure, log a structured diagnostic and try the next candidate.
7. If no candidate succeeds:
   - `Mode == Auto` → reset the codec context, open software, log a
     `Warning` that hwaccel was attempted and failed.
   - `Mode == Required` → return `Result.Fail(InvalidOperation, ...)`
     from `LoadAsync` (per ADR-0008), surface the failure to the caller.

The chosen backend (or `null` for software) is exposed on the decoder as
`IVideoDecoder.HardwareBackend` for diagnostics and tests.

### 4. Frame delivery: CPU for v1

When a decoded frame's pixel format matches the hwaccel format (e.g.,
`AV_PIX_FMT_CUDA`), the decoder allocates a second `AVFrame` for the CPU
side, calls `av_hwframe_transfer_data(swFrame, hwFrame, 0)`, then runs the
existing `sws_scale → CpuVideoFrame` path. The pipeline downstream of the
decoder is unchanged.

This intentionally leaves the GPU→CPU download in the hot path. The wins of
v1 hwaccel are:

- decode work moves off the CPU (smaller CPU footprint, lower power)
- some codecs decode faster on dedicated silicon than even highly optimised
  SW (esp. AV1, HEVC, 4K H.264)

The loss is the bandwidth cost of the download. For the YOLO + Whisper
captioning demo this is fine — those models hold the GPU anyway and we want
frames on the CPU to push to ONNX. Zero-copy delivery is for the future
ADR that introduces `GpuVideoFrame` and a Skia/OpenGL-aware Avalonia
presenter.

### 5. Wiring through DI

`FrameFlowDecodingServiceCollectionExtensions.AddFrameFlowDecoding` is
updated so the default `Func<IDemuxSession, IVideoDecoder?>` registration is
service-aware: it resolves `IOptions<FrameFlowOptions>` and
`HardwareDecodeCapabilities` at factory time, and calls a new
`DecoderFactories.CreateVideo(options, capabilities, loggerFactory)` that
returns a closure-bound factory. The existing
`DecoderFactories.Video` static property remains for callers who want the
software-only legacy behaviour.

The `Func<IDemuxSession, IVideoDecoder?>` shape on the public surface is
unchanged — only its internal construction depends on DI.

## Consequences

### Positive

- Single, cheap probe surfaces capability info up front; no per-load probing
- Default behaviour (`Auto`) "just works" on every supported platform —
  hwaccel is used when present, software when not, no opt-in needed
- `Required` mode gives perf-sensitive pipelines a hard guarantee
- Capability surface is observable (logs, tests, UI) so silent regressions
  caught
- Pipeline downstream of the decoder is unchanged in v1 — `CpuVideoFrame`
  in, `CpuVideoFrame` out. Pull-mode harness, sinks, presenters need no
  changes
- Per-codec selection (the codec lookup walks
  `avcodec_get_hw_config(codec, i)`) means each stream picks the right
  backend without a hard-coded matrix in our code

### Negative

- Bootstrap is no longer purely "load FFmpeg" — it allocates and disposes a
  device context per backend. Containers without GPU access may produce
  noisy "no device" messages on first run (mitigated by the diagnostic
  field; suppressed at `Information` level)
- Auto fallback hides an hwaccel-bind failure unless the consumer reads
  `HardwareBackend` on the decoder. Documented; logged at `Warning`
- v1 still has the download cost; the user reading "hardware decode" may
  expect zero-copy
- The struct field write to `codec_ctx->hw_device_ctx` extends our use of
  `Unsafe.AsRef<AVCodecContext>` from FFmpeg.AutoGen.Abstractions —
  consistent with the existing `AvFrameAccessor` pattern but increases the
  blast radius if AutoGen field offsets shift in a future FFmpeg major

### Neutral

- The `hw_device_ctx`-on-context path means we don't need a `get_format`
  callback for v1. If a future codec/backend combination requires
  `get_format` to disambiguate, that's an additive change

## Alternatives considered

### Use cuvid-style explicit hardware decoders

Rejected for v1: requires a per-codec lookup table mapping
`(codecId, backend) → "h264_cuvid"`-style names, doubles the surface for
testing, and is largely redundant with the `hw_device_ctx` path which the
FFmpeg project itself documents as the modern approach. Could be added
later as a `HardwareDecodeMode.PreferExplicitDecoder` opt-in if a
codec/platform combination genuinely needs it.

### Probe lazily on first `LoadAsync`

Rejected because bootstrap is *the* documented place to surface native
environment failures (ADR-0002). Pushing hwaccel probing to load time
moves it into the worker thread that's trying to play a file —
diagnostics get harder, and we'd repeat the work on every `LoadAsync`
unless we built a parallel cache.

### Per-controller `HardwareDecodeOptions`

Considered. Rejected for v1 to keep the surface small —
`services.Configure<FrameFlowOptions>` is sufficient for "this app uses
hwaccel" / "this app doesn't." If a multi-controller scenario emerges
where one controller needs hwaccel and another doesn't, we can add a
per-controller override later without breaking the global default.

### Throw on `Required` failure

Rejected. ADR-0008 is unambiguous: load-time failures return
`Result.Fail`. Throwing would let the failure propagate out of an `await`
in user code, which the lifecycle ADR has explicitly designed against.

### Auto-detect "this codec is too cheap to bother accelerating"

Rejected as premature. The cost-benefit of hwaccel depends on the
consumer's CPU pressure, battery posture, and other pipelines that may
also want the GPU. Let the user pick the mode; don't second-guess.

## References

- ADR-0002: FFmpeg bootstrap strategy
- ADR-0004: V1 platform and backend matrix (Phase 09 — superseded by this)
- ADR-0008: Result types and exception boundaries
- ADR-0010: Logging and diagnostics strategy
- ADR-0015: GPU-resident frame pipeline extensibility (superseded; retained
  for archaeology — see also ADR-0030)
- ADR-0030: Unify frame contracts with Crossbar
