# FrameFlow Architecture

This document is the canonical architectural vision for FrameFlow: a clean,
headless, FFmpeg-based media core with platform presenters and audio backends
at the edges. It was written as a from-first-principles design, and that design
has now largely landed — a working substrate playback core, a pull-shape
controller, a zero-copy Windows presenter, and a fan of runnable examples that
play real media all exist in the tree. Read this document for the *intended
shape and the reasoning behind it*; treat the source, the ADR series
(`docs/adr/`, currently through ADR-0067), and the examples under `examples/`
as the authority on exactly what is built today.

The functionality the architecture is organized around:

- load FFmpeg binaries
- open media
- decode video and audio
- present video in Avalonia (and other presenters)
- output audio cross-platform
- support seeking, pause/resume, and synchronization

## Why this shape

The driving constraint is that no single engine type should own demuxing,
decoding, sync, rendering, threading, and UI at once. Fusing those concerns
makes a media library:

1. hard to learn from, because every concern is interleaved in one place
2. hard to change safely, because one edit can ripple across several subsystems
3. hard to test, because behavior is coupled to native state and background threads

Separating them — the structure this document describes — buys:

- explicit subsystem boundaries
- headless playback logic
- platform adapters at the edges
- software-first correctness
- future GPU acceleration without forcing it into v1 (now realized as the
  zero-copy presenter and the hardware-decode path)

## High-level design goals

### 1. Headless core first

The playback engine should not know about Avalonia controls.

It should expose:

- decoded video frames
- decoded audio blocks
- media state
- commands such as play, pause, seek, and stop

The UI layer should only consume that engine.

### 2. Audio and video are separate pipelines

They share:

- the media source
- the playback state
- the synchronization model

They should not share decode logic or device logic.

### 3. Software pipeline first

Version 1 should target:

- software decode
- CPU video conversion
- CPU-backed presentation
- simple, reliable audio output

Hardware acceleration should be added as an optional adapter later.

### 4. Stable boundaries around native code

FFmpeg-specific pointers and allocation rules should be isolated into a few focused classes.

Most of the system should deal in managed types and explicit lifetimes.

### 5. Observable state machine

Playback should be modeled as a small, explicit state machine:

- `Idle`
- `Opening`
- `Ready`
- `Playing`
- `Paused`
- `Seeking`
- `Stopped`
- `Ended`
- `Faulted`

That will make behavior easier to reason about than scattered booleans.

### 6. Modern .NET design patterns

The new design should feel natural inside a modern .NET application.

That means favoring:

- dependency injection friendly services
- `IOptions<T>` / `IOptionsMonitor<T>` for configuration
- hosted-service friendly startup and shutdown
- async-first APIs where async is materially useful
- immutable option/configuration objects where practical
- small, explicit service contracts instead of broad manager types

The player should be usable in:

- plain library scenarios
- Avalonia applications
- hosted applications built around `HostApplicationBuilder`

### 7. Composition over inheritance

Prefer small collaborating components over deep hierarchies.

Examples:

- a playback session composed from demux, decode, sync, and output services
- an audio pipeline composed from packet reader, decoder, resampler, and sink
- a video pipeline composed from packet reader, decoder, converter, sync strategy, and presenter

Inheritance should be used sparingly, mostly where a framework requires it, such as UI controls.

### 8. Fluent usability where it helps

Fluent APIs are desirable for:

- builder/setup flows
- options registration
- initialization/bootstrap configuration
- pipeline composition

But the core decode path should not become "fluent for its own sake." Runtime processing should remain explicit and debuggable.

Good targets for fluent APIs:

- `AddFrameFlow(...)`
- `AddFrameFlowNative()` / `AddHostedBootstrap()`
- `AddFrameFlowOpenAlAudio()`
- `AddFrameFlowAvaloniaVideoSink()` / `AddFrameFlowSdlVideoSink()`
- pipeline/builder composition during startup

Binary resolution is configured through `FrameFlowNativeOptions`
(`UseBundledBinaries`, `ProbeSystemLibraries`, `CustomFfmpegPath`,
`SkipHardwareProbe`) rather than through fluent `Use…` methods.

Less ideal targets:

- low-level decode loops
- timestamp math
- native resource wrappers

### 9. Lifecycle decoupled from processing logic

Initialization, startup, and teardown of native resources should be separated from playback logic itself.

For example:

- bootstrap should configure FFmpeg and validate availability
- factories/builders should create sessions and pipelines
- hosted services may own long-lived environment setup
- playback components should focus on packet/frame/sample processing

This avoids mixing:

- resource acquisition
- environment validation
- runtime processing
- disposal policy

inside the same class.

### 10. Decoupling as a design constraint

All components should be as decoupled as they can practically be.

In practice, that means:

- depend on interfaces at boundaries
- keep native interop types from leaking outward
- isolate platform-specific implementations behind adapters
- separate configuration objects from service implementations
- avoid "god object" coordinators that both own resources and implement policy

Decoupling does not mean maximum abstraction everywhere. It means introducing seams where they make testing, replacement, and understanding materially easier.

## .NET-first application model

The greenfield design should work well inside a hosted DI application model.

### Service registration

The preferred consumer experience should look roughly like this:

```csharp
services
    .AddFrameFlow(options =>
    {
        options.Audio.EnableAudio = true;
        options.Video.HardwareDecode.Mode = HardwareDecodeMode.Auto;
    })
    .AddFrameFlowOpenAlAudio()
    .AddFrameFlowAvaloniaVideoSink();
```

Native binary resolution is a separate option type, `FrameFlowNativeOptions`,
bound through `AddFrameFlowNative()` — it is deliberately not nested inside
`FrameFlowOptions` (issue S-3).

or, for a host-based app:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .Configure<FrameFlowOptions>(builder.Configuration.GetSection("FrameFlow"))
    .Configure<FrameFlowPlayerOptions>(builder.Configuration.GetSection("FrameFlow:Player"))
    .AddFrameFlow()
    .AddFrameFlowOpenAlAudio();
```

The exact API can evolve, but the design target should be:

- easy registration
- sensible defaults
- optional features added compositionally
- no requirement that the UI control manually wires the whole engine

### Options model

Prefer focused option types such as:

- `FrameFlowOptions`
- `FrameFlowPlaybackOptions`
- `FrameFlowVideoOptions`
- `FrameFlowAudioOptions`
- `FrameFlowBufferingOptions`

These should be:

- easy to bind from config
- usable directly in tests
- stable enough to support fluent configuration helpers

### Hosted lifecycle integration

Some resource lifecycles can integrate well with the host:

- FFmpeg bootstrap/probe
- audio backend warm-up if needed
- logging/diagnostics initialization

But a playback session itself should usually remain an explicitly created runtime object, not a singleton hosted service.

That gives a clean split between:

- application environment lifecycle
- per-playback-session lifecycle

## Suggested fluent surfaces

To capture the usability goals without overcomplicating the core, these are the best places for fluent patterns.

### Builder/configuration surface

Use fluent composition for:

- registering services
- selecting adapters
- configuring defaults
- enabling optional capabilities

### Session creation surface

Consider a factory or builder such as:

```csharp
await using var player = await FrameFlowPlayer.Open(path)
    .WithAudioSink(audioSink)
    .WithAvaloniaVideoView(view)
    .BuildAsync(ct);
```

This is a good fit because it configures object graphs and lifetimes before
processing begins. As built, this is `FrameFlowPlayer.Open(...)` in
`FrameFlow.Player`, returning an `IPlayerBuilder`; the richer surface with
seek, pause, and repeat is `MediaPlayer.CreateAsync(...)`.

### Processing pipeline surface

Pipeline-like composition can help at assembly time:

- packet source -> decoder -> converter -> presenter
- packet source -> decoder -> resampler -> sink

But at runtime, the implementation should still be simple loops and explicit queues. That will be easier to debug and tune.

## Proposed solution structure

One reasonable project layout would be the six projects below. That is the
layering this document argues for, and it is still the spine of the tree — but
the solution has since grown to 22 projects under `src/` (21 libraries and the
`motionclip` tool), adding the processing
graph, encoding, capture, presenters, and inference backends. `README.md` has
the full, current list; the sections below explain why the original six exist
and what each one is not allowed to know about.

### `FrameFlow.Native`

Responsibilities:

- FFmpeg bootstrap and dynamic binding setup
- runtime path resolution
- version probing
- native helper functions

This project should know how to find and initialize FFmpeg, but not how to play media.

### `FrameFlow.Media`

Responsibilities:

- media source abstraction
- stream metadata
- packets, timestamps, frame descriptors
- playback state and commands

This is the shared domain layer for the player.

### `FrameFlow.Decoding`

Responsibilities:

- demux session
- video decoder
- audio decoder
- frame/sample conversion

This is where FFmpeg-heavy code lives.

### `FrameFlow.Playback`

Responsibilities:

- playback orchestration
- clocks and synchronization
- packet scheduling
- buffering
- seek/pause/stop behavior

This layer consumes decoders and produces output-ready data.

### `FrameFlow.Audio`

Responsibilities:

- audio sink abstraction
- default audio backend implementation
- device state, buffering, and timing

This should be separate from decoding so it can be swapped independently.

### `FrameFlow.Avalonia`

Responsibilities:

- Avalonia control
- bitmap/texture presentation adapter
- view-model-ish glue
- user input mapping

This should be the thinnest layer.

## Core abstractions

These types should exist early. Several were renamed as they were built; the
as-built name is noted where it differs. The shared presenter and clock
contracts live in `FrameFlow.Media` and `FrameFlow.Graph`.

| Name here | As built |
|---|---|
| `FFmpegBootstrap` | `FrameFlowBootstrapper` (`FrameFlow.Native`) |
| `PlaybackSession` | `IPlaybackSession` / `SubstrateSession` (`FrameFlow.Playback`) |
| `IVideoFramePresenter` | `IVideoSink` (`FrameFlow.Media`) |
| `ISyncStrategy` / `AudioMasterSyncStrategy` | `IClockSource` (`FrameFlow.Graph`) — the audio sink is the clock, see ADR-0003, ADR-0035, ADR-0057 |
| `AvaloniaVideoPresenter` | `AvaloniaVideoSink` (`FrameFlow.Avalonia`) |
| `VideoPlayerControl` | `FrameFlowVideoView` / `FrameFlowPlayerView` (`FrameFlow.Avalonia`) |

`IMediaSource`, `MediaInfo`, `DemuxSession`, `VideoDecoder`, `AudioDecoder`,
`PlaybackClock`, and `IAudioSink` kept their names.

### `IMediaSource`

Represents the thing being opened.

Examples:

- local file
- URI
- stream source later

### `MediaInfo`

Contains container and stream metadata:

- duration
- video stream info
- audio stream info
- codec names
- time bases

### `DemuxSession`

Owns:

- `AVFormatContext`
- stream selection
- packet reading

Responsibilities:

- open input
- enumerate streams
- read packets
- seek by timestamp

This should not know about UI or audio devices.

### `VideoDecoder`

Owns:

- video `AVCodecContext`
- reusable decode frames
- scaler/converter state

Responsibilities:

- accept compressed packets
- emit decoded frames with timestamps
- convert frames to a chosen presentation format

### `AudioDecoder`

Owns:

- audio `AVCodecContext`
- resampler state

Responsibilities:

- accept compressed packets
- emit PCM blocks in a stable output format

### `PlaybackClock`

Responsibilities:

- maintain current presentation time
- expose wall-clock vs media-clock calculations
- support pause/resume/seek resets

This should be a focused class, not scattered timing math.

### `ISyncStrategy`

Responsibilities:

- define whether audio or wall clock is master
- decide whether video should sleep, drop, or catch up

Start with one implementation:

- `AudioMasterSyncStrategy`

### `IAudioSink`

Responsibilities:

- accept PCM blocks
- start, pause, resume, stop
- report playback position if possible

### `IVideoFramePresenter`

Responsibilities:

- accept decoded video frames
- convert or copy them into presentation buffers

For Avalonia, this can be implemented with `WriteableBitmap` first.

## Data flow

The intended flow should look like this:

1. `FFmpegBootstrap` initializes native bindings.
2. `DemuxSession` opens media and exposes stream info.
3. `PlaybackSession` creates:
   - `VideoDecoder`
   - `AudioDecoder`
   - `PlaybackClock`
   - `IAudioSink`
4. A demux loop reads packets.
5. Packets are routed by stream:
   - video packets -> video packet queue
   - audio packets -> audio packet queue
6. Decoder workers turn packets into:
   - decoded video frames
   - decoded PCM audio blocks
7. The sync coordinator decides when to present each video frame.
8. The audio sink consumes PCM continuously.
9. The video presenter pushes frames into the UI layer.

The key point is that demuxing, decoding, sync, audio output, and UI presentation all become separate concerns.

## Threading model

Keep the first version simple.

### Recommended v1 threads

Use three worker loops:

1. **demux loop**
   - reads packets from the container
   - routes them to stream queues

2. **video decode/present loop**
   - decodes video packets
   - converts frames
   - applies sync timing
   - hands frames to the presenter

3. **audio decode/output loop**
   - decodes audio packets
   - resamples
   - queues PCM into the audio sink

This is easy to understand and already separates the main failure domains.

### Backpressure rules

Every queue must have explicit limits.

Examples:

- maximum queued audio packets
- maximum queued video packets
- maximum pending video frames awaiting UI presentation

When limits are hit, the behavior must be explicit:

- block demux temporarily
- drop stale video frames
- never let audio buffer run unbounded

## Time and synchronization

This is the subsystem to design carefully.

### v1 recommendation

Use **audio as the master clock** whenever audio exists.

Why:

- the sound device consumes samples continuously
- users notice audio glitches more than tiny video jitter
- this matches common player design

### If no audio stream exists

Use a wall-clock-driven video clock.

### Sync policy

The sync strategy should:

- compute target presentation time for each video frame
- sleep briefly if video is early
- drop or skip if video is too late
- reset cleanly after seek

Keep this policy in one class so it is easy to tune.

## Video presentation strategy

### v1

Use software decode and CPU presentation:

- decode with FFmpeg
- convert to BGRA
- copy into a managed buffer
- copy into an Avalonia `WriteableBitmap`

This will be slower than a true GPU path, but it is ideal for correctness and learning.

### v2

Add a true hardware/video surface path behind a separate interface.

Possible future presenters:

- `AvaloniaBitmapPresenter`
- `OpenGlTexturePresenter`
- `D3D11VideoPresenter`
- `MetalVideoPresenter`

Do not design v1 around these yet.

## Audio output strategy

Do not let the core player depend directly on OpenTK/OpenAL.

Instead:

- define `IAudioSink` in a neutral project
- implement one default sink in a backend project

Possible backends:

- OpenAL/OpenTK
- NAudio on Windows
- SDL audio
- MiniAudio wrapper

For the first rewrite, keeping one backend is fine. The important part is that it lives behind the interface.

## Error handling model

Native/media code fails often and should fail clearly.

Use:

- explicit result types where practical
- domain-specific exceptions at subsystem boundaries
- rich diagnostics for:
  - missing FFmpeg libraries
  - unsupported codec
  - failed decoder open
  - failed seek
  - audio device unavailable

The UI layer can simplify those into user-facing messages, but the core should preserve detail.

## Resource ownership rules

This part should be strict from day one.

Each native-owning class should own exactly the resources it allocates.

Examples:

- `DemuxSession` owns `AVFormatContext`
- `VideoDecoder` owns video codec context and scaling context
- `AudioDecoder` owns audio codec context and resampler context

Avoid shared native pointers floating across unrelated classes.

The most important rule is:

**the class that allocates a native resource is responsible for freeing it**

That single rule will prevent a lot of future pain.

## What to keep from the current repo

Keep as reference, not as architecture:

- runtime binary loading ideas
- README/install knowledge
- the current behavior surface of the player control
- the example app

Reuse selectively:

- `FFmpegInitializer` concepts
- `FFmpegPathResolver` concepts
- `FrameEventArgs`-style frame handoff ideas

Do not preserve:

- `FFmpegMediaPlayer` as the central design
- current renderer abstractions without review
- UI-driven engine construction

## Implementation plan (largely delivered)

The phased sequence below is the order the build was intended to follow, and
the bulk of it has shipped: native bootstrap, demux/decode, headless playback
orchestration, A/V sync and seeking, the Avalonia presenter, and optional
hardware acceleration all exist in the tree. It is retained here as the
rationale for the layering and the dependency order between subsystems, not as
a to-do list. For current status consult `docs/ROADMAP.md`, the ADRs, and the
examples.

### Phase 0: API and foundation design

Deliverables:

- top-level consumer API shape
- consumer-oriented usage samples
- lifecycle and disposal model
- options and DI registration shape
- state, event, and error model
- empty method skeletons and foundational contracts

Success criteria:

- the API feels clean from the consumer perspective
- architectural boundaries are explicit before deeper implementation begins
- later phases can implement against a stable shape instead of inventing it as they go

### Phase 1: bootstrap and diagnostics

Deliverables:

- `FFmpegBootstrap.Initialize(...)`
- native library probing
- a small console/test harness that prints FFmpeg version and stream metadata

Success criteria:

- you can reliably load FFmpeg on supported platforms
- you can prove the bindings are actually callable

### Phase 2: headless demux

Deliverables:

- `DemuxSession`
- packet reading API
- stream selection and metadata
- seek API

Success criteria:

- you can open a file
- enumerate streams
- read packets and classify them by stream

### Phase 3: video-only decode

Deliverables:

- `VideoDecoder`
- frame conversion to BGRA
- a file-based or bitmap-based frame dump tool

Success criteria:

- decode first frame
- decode sequential frames
- verify timestamps are sane

### Phase 4: audio-only decode

Deliverables:

- `AudioDecoder`
- PCM output blocks
- WAV dump or sample inspection harness

Success criteria:

- decode audio correctly
- convert to stable stereo PCM output

### Phase 5: playback core

Deliverables:

- `PlaybackSession`
- playback state machine
- demux + separate audio/video loops
- pause/resume/stop

Success criteria:

- stable headless playback without UI

### Phase 6: synchronization

Deliverables:

- `PlaybackClock`
- `AudioMasterSyncStrategy`
- frame timing and late-frame policy

Success criteria:

- video stays visually aligned with audio
- seeking resets timing cleanly

### Phase 7: Avalonia adapter

Deliverables:

- `AvaloniaVideoPresenter`
- thin `VideoPlayerControl`
- event wiring for playback state and progress

Success criteria:

- working UI player with minimal engine/UI coupling

### Phase 8: polish

Deliverables:

- diagnostics/logging
- cancellation cleanup
- better errors
- test coverage around clocks and state transitions

### Phase 9: optional acceleration

Deliverables:

- evaluate GPU decode/render path
- add hardware-specific presenters or decoder adapters

This phase should remain optional until the software path is correct and understandable.

## Suggested order for learning

If your real goal is to learn, build in this order:

1. shape the API from the consumer side
2. open media and inspect streams
3. decode one frame
4. decode one audio chunk
5. loop video-only playback
6. add audio output
7. add sync
8. add seek/pause/resume
9. add Avalonia UI

That sequence teaches the system in the same order the problem actually unfolds.

## Rules for the codebase

These invariants keep the codebase healthy and still hold as the standing rules
for any change:

1. no UI types in the playback core
2. no audio backend types in the decoding layer
3. no shared ownership of native pointers
4. no hidden thread creation inside low-level utility classes
5. every queue has a max size and a policy
6. all timing logic lives in dedicated clock/sync classes
7. software path first, acceleration second

## Foundation (delivered)

The foundation this architecture was built on was an API-first design pass —
deliberately ahead of any new control — that:

- defined the consumer-facing entry points
- defined usage examples and DI/options registration shape
- defined lifecycle and error contracts
- created a stable skeleton before native implementation expanded

That skeleton is in place (see `FrameFlow.Player`'s `MediaPlayer.CreateAsync`
and the `services.AddFrameFlow…()` registrations), and the rest of the
architecture has been built on top of it. New work should extend that surface
rather than reopen the foundation.
