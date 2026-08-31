# ADR-0040: Capture Sources and Encoder Terminals (Tier 5, design only)

**Status:** Proposed (design only) — **partially implemented.** The
video-only **H.264 → MP4 encoder terminal** slice has been built (see
**ADR-0053**), in the substrate-adapted shape the FrameFlow.Graph fork
(ADR-0049) requires; ADR-0053 supersedes the literal public signatures
sketched in the "Encoder + muxer terminals" section below. Everything
else here — capture sources, audio encoders, HEVC, MKV/WebM/HLS muxers —
remains design-only, deferred until a concrete consumer demands it.
**Date:** 2026-05-12
**Supersedes:** None. Companions ADR-0036 (decoded media stream),
ADR-0037 (pixel operators), ADR-0038 (memory-domain operators),
ADR-0039 (Whisper operator).
**Related:** `docs/CROSSBAR_SHAPING_ROADMAP.html` (Tier 5),
`docs/IDEAL_AVALONIA_PLAYER.md` (the design target this ADR
unblocks), `docs/OBS_REPLICATION.md` (the long-arc north star),
Periphery's device topology layer (the natural integration point
for capture sources).

## Context

Tiers 1–4 of the Crossbar-shaping roadmap fill out the operator
vocabulary for what FrameFlow can do *with frames it already has* —
pixel format conversion, resize, memory-domain transitions, audio
resampling, transcription. The substrate is Crossbar-shaped
end-to-end.

What FrameFlow doesn't have yet are the *boundaries*:

- **Capture sources.** Decoded media streams come from files
  (`IDecodedMediaStreamFactory` + `IMediaSource`). Live sources —
  cameras, microphones, screen capture, RTSP, HLS — don't exist as
  FrameFlow surfaces yet. Periphery has camera enumeration and
  capture today, but there's no Crossbar-shaped bridge to a
  `FramePipeline<IVideoFrame>`.
- **Encoder + muxer terminals.** Decoded frames go to sinks
  (renderers, audio outputs) or are dropped at terminal `RunAsync`.
  There's no path to *write a file* — no `EncodeTo(h264)`, no
  `MuxInto(mp4, "out.mp4")`. Recording, transcoding, ABR ladder
  generation, and OBS-style streaming all want this.

Both are the *next* substantial subsystems in the roadmap. Both
should be Crossbar-shaped from day one — the lesson of ADRs 0036
through 0039 is that the Crossbar idiom keeps consumer code small
and composable.

This ADR is **design only.** It captures the shape the substrate
should have when the implementation lands. No code in this commit.

## Decision

Define two new substrate surfaces — one for sources, one for
terminals — both Crossbar-shaped, both mirroring the
`IDecodedMediaStream` pattern from ADR-0036 in spirit.

### Capture sources

**Public surface:**

```csharp
namespace FrameFlow.Capture;

public interface ICaptureSource : IAsyncDisposable
{
    /// <summary>Stable identifier for the device backing this source.</summary>
    string DeviceId { get; }

    /// <summary>Human-readable label for diagnostics / UI.</summary>
    string DisplayName { get; }

    /// <summary>Static format information at construction time.</summary>
    CaptureFormat Format { get; }
}

public interface IVideoCaptureSource : ICaptureSource
{
    FramePipeline<IVideoFrame> Frames { get; }
}

public interface IAudioCaptureSource : ICaptureSource
{
    FramePipeline<PcmAudioBuffer> Frames { get; }
}

// Single combined source for devices that produce both (some webcams
// with built-in microphones, screen capture + system audio).
public interface IMultimodalCaptureSource : ICaptureSource
{
    FramePipeline<IVideoFrame> Video { get; }
    FramePipeline<PcmAudioBuffer> Audio { get; }
}
```

`CaptureFormat` is a small record carrying the static format
(width / height / fps for video; sample rate / channels for audio).
Format changes mid-capture are rare but possible (a webcam may
renegotiate); per-packet metadata in the pipeline carries the
current shape.

**Factories**, one per backend, all returning the interfaces above:

```csharp
namespace FrameFlow.Capture;

public static class Camera
{
    public static Task<IVideoCaptureSource> OpenAsync(
        string deviceId,
        VideoCaptureOptions? options = null,
        CancellationToken ct = default);

    public static IReadOnlyList<CameraDevice> Enumerate();
}

public static class Microphone
{
    public static Task<IAudioCaptureSource> OpenAsync(
        string deviceId,
        AudioCaptureOptions? options = null,
        CancellationToken ct = default);

    public static IReadOnlyList<MicrophoneDevice> Enumerate();
}

public static class ScreenCapture
{
    public static Task<IVideoCaptureSource> OpenAsync(
        ScreenCaptureOptions options,
        CancellationToken ct = default);
}

public static class RtspSource
{
    public static Task<IMultimodalCaptureSource> OpenAsync(
        Uri url,
        RtspOptions? options = null,
        CancellationToken ct = default);
}
```

#### Periphery integration

Camera and microphone enumeration is **Periphery's job**, not
FrameFlow's. Periphery already enumerates devices via udev / setupapi
and manages device proxies (per its ADRs). FrameFlow consumes
Periphery's device handles to produce capture sources:

```csharp
// User code:
var devices = Periphery.Devices.EnumerateCameras();
var cameraDeviceProxy = devices[0].OpenProxy();
await using var source = await Camera.FromPeripheryProxy(cameraDeviceProxy);
await using var stream = source.Frames
    .ConvertPixelFormat(Bgra32)
    .ToSinkAsync(view, ct);
```

The seam between Periphery (device topology) and FrameFlow (frame
pipelines) is concentrated in the `Camera.FromPeripheryProxy` style
factory. Each platform implementation lives in a per-RID package
(`FrameFlow.Capture.MediaFoundation` on Windows,
`FrameFlow.Capture.AVFoundation` on macOS, `FrameFlow.Capture.V4L2`
on Linux) — same shape as `FrameFlow.Audio.OpenAL` today.

`ScreenCapture` and `RtspSource` don't go through Periphery —
they're not device-topology concerns. Screen capture uses the
platform's screen-API; RTSP uses FFmpeg's network protocols.

#### Lifecycle

Capture sources are *live*. They start producing the moment they're
created (mirroring `IDecodedMediaStream` from ADR-0036). Pause and
resume are not part of the interface — consumers can stop pulling
to backpressure the source, and `DisposeAsync` is the terminal
operation. Sources with hardware-side pause (e.g. some cameras
support a still-frame mode) can expose backend-specific methods on
their concrete types; the substrate stays narrow.

#### Backpressure

Each source owns a bounded(1) channel between its native callback
and the pipeline's first downstream operator. When the consumer
falls behind, the channel fills and the native callback either
drops frames (default for live sources, since live-source
backpressure has to drop somewhere) or blocks (configurable for
deterministic batch workflows).

### Encoder + muxer terminals

**Public surface:**

```csharp
namespace FrameFlow.Encoding;

public interface IEncoder<TFrame, TPacket> : IAsyncDisposable
    where TFrame : IDisposable
    where TPacket : IDisposable
{
    EncoderInfo Info { get; }
    FramePipeline<TPacket> Encode(FramePipeline<TFrame> input);
}

public interface IMuxer : IAsyncDisposable
{
    /// <summary>
    /// Adds a stream of encoded packets to the mux. Multiple streams
    /// can be added before the first packet is consumed; each gets
    /// a unique stream index. Returns a consumer that the encoder writes
    /// packets to.
    /// </summary>
    // ADR-0010 (2026-05-15): the substrate type for this is now
    // `FrameConsumer<EncodedPacket>` — `IFrameSink<T>` was deleted.
    FrameConsumer<EncodedPacket> AddStream(EncodedStreamConfig config);

    /// <summary>
    /// Drives the mux to completion. Returns when every added sink's
    /// upstream pipeline has drained.
    /// </summary>
    Task RunAsync(CancellationToken ct);
}
```

Encoded packets flow through pipelines just like decoded frames.
The `EncodedPacket` type wraps a `byte[]` plus DTS / PTS / stream
metadata; `IDisposable` for Crossbar's substrate contract.

**Factories,** mirroring the source side:

```csharp
public static class Encoder
{
    public static IEncoder<IVideoFrame, EncodedPacket> H264(H264EncoderOptions? opts = null);
    public static IEncoder<IVideoFrame, EncodedPacket> Hevc(HevcEncoderOptions? opts = null);
    public static IEncoder<PcmAudioBuffer, EncodedPacket> Aac(AacEncoderOptions? opts = null);
    public static IEncoder<PcmAudioBuffer, EncodedPacket> Opus(OpusEncoderOptions? opts = null);
}

public static class Muxer
{
    public static IMuxer Mp4(string path);
    public static IMuxer Mkv(string path);
    public static IMuxer Webm(string path);
    public static IMuxer Hls(string outputDir, HlsOptions opts);
}
```

#### Composition example

The end-to-end "record this camera feed to MP4" call site:

```csharp
await using var camera = await Camera.OpenAsync(deviceId);
await using var mic = await Microphone.OpenAsync(micId);
await using var mux = Muxer.Mp4("out.mp4");

var videoEncoder = Encoder.H264();
var audioEncoder = Encoder.Aac();

var videoSink = mux.AddStream(new EncodedStreamConfig("h264", camera.Format));
var audioSink = mux.AddStream(new EncodedStreamConfig("aac", mic.Format));

var videoTask = videoEncoder
    .Encode(camera.Frames)
    .ToSinkAsync(videoSink, ct);

var audioTask = audioEncoder
    .Encode(mic.Frames)
    .ToSinkAsync(audioSink, ct);

await Task.WhenAll(videoTask, audioTask, mux.RunAsync(ct));
```

Five concepts, five lines. Each operator is composable; each
source / sink is interchangeable.

#### Why encoders separate from muxers

Symmetry with the decode side: `IDecodedMediaStream` (the
demux+decoders) lives separate from `IPlaybackController` (the state
machine that drives it). For the write direction, the encoder
(transforms decoded frames to encoded packets) lives separate from
the muxer (multiplexes streams of encoded packets into a container).

This also lets advanced consumers compose pieces:

- Transcode (read MP4, decode, re-encode at different bitrate, mux
  to MP4): `stream → decode → encode → mux`.
- Streaming (read camera, encode, send via WebRTC): `camera → encode
  → webrtc-sink` (no mux).
- Recording with branching (capture once, write to MP4 + send to
  HLS): `camera → encode → Broadcast → [mp4-mux, hls-mux]`.

#### Lifecycle

Encoders are stateful — they own codec context, rate-control state,
B-frame buffers. Constructed once per stream; disposed when the
pipeline ends. Same lifetime story as the `IVideoConverter` /
`IAudioResampler` primitives.

Muxers buffer the first packet of each stream until all streams
have produced their initial headers, then write the container
header and stream from there. The `RunAsync` method drives the
buffered-then-streaming machinery.

### Sized scope

This is **months of work**, not days. Approximate sizing:

| Subsystem | Sized | Notes |
|---|---|---|
| Camera (Windows MediaFoundation) | 2-3 weeks | Periphery integration + MF capture session |
| Camera (macOS AVFoundation) | 2-3 weeks | Same shape, different backend |
| Camera (Linux V4L2) | 2-3 weeks | V4L2 + buffer mapping |
| Microphone (all platforms) | 1-2 weeks each | Smaller surface than video |
| Screen capture (Windows) | 2 weeks | DXGI Desktop Duplication API |
| Screen capture (others) | 1-2 weeks each | Platform-specific |
| RTSP source | 1-2 weeks | FFmpeg `avformat_open_input` + network options |
| H264 encoder | 1 week | libavcodec wrapping |
| AAC encoder | 1 week | libavcodec wrapping |
| MP4 muxer | 1 week | libavformat wrapping |
| MKV / WebM muxer | 0.5 week each | libavformat — small variants once MP4 lands |
| HLS muxer | 1-2 weeks | Segment scheduling, manifest, target-duration policy |

Real work, real timeline. This ADR is the **shape**; the
implementation is deferred until concrete consumers (the
captioning demo's recording mode? OBS-replication? a future
inference-record demo?) actually need it.

## Consequences

### Positive

- **The substrate has a name for what's missing.** Future ADRs
  reference "the ADR-0040 capture source seam" instead of
  reinventing the shape.
- **Crossbar-shaped from day one.** When capture lands, it composes
  with every existing operator. A camera feed can be `.Resize`'d
  for inference, `.Broadcast`'d to a renderer + an HLS recorder,
  `.TranscribeWithWhisper`'d if it has audio.
- **Periphery integration is a single seam.** Camera enumeration
  stays where it belongs; the bridge from device proxy to pipeline
  is a small factory method.

### Negative

- **It's a paper ADR.** Until something is built, this is a
  promise the design can keep. The shape might evolve during
  implementation.
- **Some choices defer harder questions.** Format change
  negotiation, multi-resolution output for ABR, GPU-resident
  encode (zero-copy pixel handoff from decoder to encoder),
  drop-frame timecode for capture — all under the surface here.
  Future ADRs refine.

### Neutral

- **The implementation will need its own per-platform packages.**
  Same shape as `FrameFlow.Audio.OpenAL` — backend-specific
  packages depending on the substrate.

## Alternatives considered

### A. Use FFmpeg's `libavdevice` directly for capture

FFmpeg has built-in capture support for cameras (DirectShow / V4L2 /
AVFoundation). Wrapping `libavdevice` would be the fastest path to
"can FrameFlow capture a webcam."

Rejected because:
- Periphery already owns device topology and capture; the API
  shouldn't have *two* enumeration surfaces.
- `libavdevice` has its own threading and lifetime quirks that
  duplicate the work `IDecodedMediaStream` already does for file
  sources. Per-platform native APIs give cleaner control over
  buffer ownership and zero-copy paths.

A future revision could expose `libavdevice` as one of the
per-platform backends behind the same `Camera` factory.

### B. Have one `IMediaSource` type for both files and cameras

`IMediaSource` already exists as a descriptor (file path, URL).
Could extend it to cover live sources.

Rejected because `IMediaSource` is a *handle* — it doesn't have a
producing surface. Capture sources need
`FramePipeline<IVideoFrame>` as their primary affordance, which
doesn't fit the descriptor shape. Keeping them separate keeps each
type focused.

### C. Build encoder + muxer as one type

Most encoders implicitly contain a muxer (FFmpeg's "muxer" string
in its `avformat_alloc_output_context2` API blurs the line).

Rejected because:
- Composing branches (Broadcast to MP4 + HLS from one encoded
  stream) needs them separate.
- Encoders without muxers (raw H.264 over WebRTC, raw AAC for
  voice chat) want exactly the encode operator.
- The decode side keeps these separate
  (`IDemuxSession` + `IVideoDecoder`); the write side should
  mirror.

### D. Make capture sources part of `IDecodedMediaStream`

File sources go through `IDecodedMediaStreamFactory`. Capture
sources could too — same factory, same `Video` / `Audio`
properties.

Tempting because consumer code wouldn't know the difference. But
capture and file decode have different lifecycles (live sources
don't have `Duration`, don't support seek, drop frames on
backpressure rather than block), and the interface surface would
become a lowest-common-denominator. Better to keep them as
distinct types and offer a `FrameFlowPlayer.Open(...)` style facade (the
Layer-2 builder from `docs/IDEAL_AVALONIA_PLAYER.md`) that hides
the choice.

## Implementation plan

This ADR ships **no code**. When implementation begins, it lands in
this order:

1. **`FrameFlow.Capture` package** — the interfaces and shared
   types (`CaptureFormat`, options records, the per-modality
   interfaces).
2. **`FrameFlow.Capture.MediaFoundation`** — Windows camera +
   microphone via MF, bridged from Periphery's device topology.
3. **`FrameFlow.Encoding` package** — `IEncoder<,>`, `IMuxer`,
   `EncodedPacket`.
4. **`FrameFlow.Encoding.LibAv`** — FFmpeg-backed encoders
   (H264, AAC) + muxers (MP4, MKV, WebM).
5. **A new example app** that exercises the full chain — likely
   `FrameFlow.Examples.WebcamRecord` (camera + mic to MP4) as the
   minimum viable consumer.
6. **`FrameFlow.Capture.AVFoundation`** / **`V4L2`** —
   per-platform follow-ups once Windows is solid.

Each step is its own ADR or commit batch. The shape is locked
here; the substance accumulates.

## References

- ADR-0036 — decode/playback decoupling (the source-side template
  this mirrors).
- ADR-0037 / ADR-0038 / ADR-0039 — the operator vocabulary capture
  and encode will compose with.
- `docs/OBS_REPLICATION.md` — the long-arc north star that needs
  this substrate.
- `docs/IDEAL_AVALONIA_PLAYER.md` — the Layer-2 builder that
  ultimately hides the choice between file source and capture
  source from simple consumers.
- Periphery's device topology ADRs — the side this ADR's
  `Camera.FromPeripheryProxy` integration consumes.
