# Could we build our own OBS on top of FrameFlow?

**Last reviewed:** 2026-05-11
**Scope:** What it would take to ship a FrameFlow-based competitor to
OBS Studio — feature, architecture, and effort.
**Audience:** Anyone evaluating whether the playback substrate is also
a streaming/recording substrate, and what investment would close the
gap.

This document answers "is FrameFlow the right tool for this?" against a
product rather than a library. OBS is an *application*, so the comparison
is product scope as much as feature surface, and the section labels
reflect that.

## TL;DR

**Yes, the architecture works.** FrameFlow + Crossbar already does
the hardest pieces (pull-shape pipeline graphs with explicit
backpressure, hardware-accelerated decode, multi-output fan-out,
typed frame contracts, async/await as the threading model, ML
inference seam via `Crossbar.Cuda` + `Crossbar.Onnx`). The pieces
needed to ship an OBS competitor are mostly **additive operators and
sources** — not architectural changes.

**The build is real.** Best case ~9–15 months of focused work for
parity on the everyday-streamer use case. Some classes of OBS
features (plugin ecosystem, scripting, deep Windows hooks for game
capture) are 5+ year ecosystems we are not going to recreate.

**The interesting question is differentiation,** not parity. OBS
exists and is excellent for what it does. The cases where a
FrameFlow-based competitor wins are: ML-native scenes (live
captioning, auto-cropping to the speaker, virtual greenscreen via
segmentation), programmatic automation (scenes-as-code, hotkeys-as-
code, scriptable hot cuts), better recording reliability under fault
(deterministic teardown and ADR-0024-style state machines instead of
"OBS crashed and lost 3 hours"), and being a library/SDK first so
other apps can embed the same engine. **The product should not be
"OBS but in C#"; it should be "what OBS would look like if you
designed it in 2026 with ML and structured logging from day one."**

## Architectural framing

OBS's model: **scenes are layered compositions of sources, with a
single program output that is the result of compositing all visible
sources of the current scene.** Sources can be video (camera, screen,
window, file, browser, image) or audio (mic, desktop, file).
Transitions move between scenes. Filters attach to sources or scenes.
The compositor runs at the program frame rate, sampling each source's
most-recent frame.

FrameFlow's model: **pipelines are typed graphs of pull-shape
operators between frame producers and frame sinks, with explicit
backpressure at every channel boundary.** Multi-output is `Broadcast`.
Format compatibility is type-checked. Threading is async/await over
`FrameChannelOptions`.

The mapping is one-to-one with one important shape change:

| OBS concept | FrameFlow concept | Notes |
|---|---|---|
| Source | `IFrameSource<IVideoFrame>` or `<PcmAudioBuffer>` | Existing playback decoder is already a source; capture sources are missing |
| Scene | `Crossbar.Merge` operator (not yet built) | Multi-input fusion is the missing primitive — see [roadmap](#roadmap) |
| Filter on source | `Transform<IVideoFrame, IVideoFrame>` | Already supported as a Crossbar operator |
| Compositor | Specialised `Merge` over per-source frame pipelines, fixed-rate | Output frame rate becomes the merge's pull cadence |
| Transition | Time-windowed mix operator | Stateful operator; needs `Window` primitive |
| Program output | Terminal sink (encoder → muxer → file/network) | Encoder terminals are [ADR-0040](adr/ADR-0040-capture-sources-and-encoder-terminals.md); H.264/MP4 landed in [ADR-0053](adr/ADR-0053-h264-mp4-encoder-terminal.md) |
| Audio mixer | `Merge<PcmAudioBuffer>` with per-input gain | Same `Merge` primitive |
| Hotkey | C# event handler invoking controller methods | Trivial; the wiring is the UI's problem |
| Scripting | Roslyn scripting / C# expressions | "Scenes-as-code" wins here |
| Plugin | NuGet package | No registry, no DLL ABI, no init hook |

The shape change: OBS scenes are **logical groupings of sources with
a compositor on top**; FrameFlow has the operator algebra but not
the "named composition of sources with z-order, position, scale,
crop" object. That object is a thin record type sitting above the
operator graph. The interesting work is in `Merge` semantics, not in
the scene abstraction itself.

## Quick assessment

**Today, FrameFlow can do** the playback half of OBS (load a media
source, run it through transforms, multi-output it to UI and capture
sinks) and the inference half (live ML on decoded frames with
zero-copy GPU paths). The "stream/record" half — capture sources,
encoders, muxers, network push — is **architecturally ready** but
**not implemented**.

**Today, FrameFlow cannot do** any of: screen capture, window
capture, game capture, webcam input, microphone input, system audio
loopback, RTMP/SRT streaming, scene compositing, audio mixing,
real-time effects. Building those is the work.

**Encoding and muxing are the one partial answer.** Video-only
H.264-in-MP4 recording works today
([ADR-0053](adr/ADR-0053-h264-mp4-encoder-terminal.md)). No audio
track, no other codec, no other container — see the encoding tables
below for what that leaves.

**If you're asking "can we ship a 1.0?"** — the honest answer is
~9–15 months for a usable competitor, with phases below.

## Feature parity — capture sources

OBS sources are the most platform-specific surface. None of these
exist in FrameFlow today; they would live in **Periphery** (the
device-and-OS-services repo) and surface as `IFrameSource<T>` to
FrameFlow.

| Source | OBS plugin | FrameFlow / Periphery status | Notes |
|---|---|---|---|
| Display capture (Windows) | `win-capture` (DDA) | ❌ Not started | Desktop Duplication API; well-documented |
| Display capture (macOS) | `mac-capture` (CGDisplay / SCK) | ❌ Not started | ScreenCaptureKit on macOS 12.3+ |
| Display capture (Linux X11) | `linux-capture` (xshm) | ❌ Not started | PipeWire is the modern path; X11 acceptable bridge |
| Display capture (Linux Wayland) | `linux-pipewire` | ❌ Not started | PipeWire portal |
| Window capture (Windows) | `win-capture` (PrintWindow / WGC) | ❌ Not started | Windows Graphics Capture API |
| Window capture (macOS) | SCK with window filter | ❌ Not started | |
| Window capture (Linux) | `linux-capture` (xcomposite) | ❌ Not started | XComposite, harder under Wayland |
| Game capture (Windows DX/OGL/Vk hooks) | `win-capture` (graphics-hook) | ❌ **Hard.** Process-injection DLL with API hooks | One of OBS's secret-sauce features; 18+ month build |
| Webcam (Windows) | `win-dshow` / Media Foundation | 🟡 Periphery's Windows path started | |
| Webcam (Linux V4L2) | `linux-v4l2` | ❌ Not started | |
| Webcam (macOS AVFoundation) | `mac-avcapture` | ❌ Not started | |
| Microphone (Windows WASAPI) | `win-wasapi` | ❌ Not started | OpenAL has capture; native WASAPI better |
| Microphone (macOS Core Audio) | `mac-capture` | ❌ Not started | |
| Microphone (Linux Pulse / Pipewire) | `linux-pulseaudio` | ❌ Not started | |
| Desktop audio loopback (Windows) | `win-wasapi` (loopback) | ❌ Not started | **Most-asked-for streaming feature** |
| Desktop audio loopback (macOS) | requires kernel extension or BlackHole | ❌ Not started | Apple intentionally restricts; users install BlackHole |
| Desktop audio loopback (Linux) | Pulse `.monitor` source | ❌ Not started | Trivial once Pulse capture works |
| Media source (file) | `media-source` (FFmpeg) | ✅ `MediaSource.FromFile` | Existing decoder is this |
| Browser source (CEF) | `obs-browser` | ❌ **Hard.** 200MB+ runtime | Could embed CefSharp; expensive |
| Image source | static load | ❌ Trivial | Skia / ImageSharp one-liner |
| Image slideshow | composed | ❌ Trivial | Compose Image source + timer |
| Text (GDI / FreeType) | `text-freetype2` / `text-gdi` | ❌ Not started | Skia covers cross-platform |
| Color source | constant | ❌ Trivial | |
| Scene (recursive) | scene-as-source | ❌ Falls out of `Merge` once we have it | |
| NDI | obs-ndi plugin | ❌ Out of scope for v1 | libndi has restrictive license |
| VLC source (any media) | obs-vlc | ✅ Subsumed by `MediaSource` | We are the VLC source |

The **win** here: most of these are mechanical Periphery work, not
research. The **loss**: it's a lot of mechanical work — three
platforms × four capture types is twelve platform-specific shims
before you've shipped anything.

## Feature parity — compositor / scenes

This is where the architectural lift is.

| Feature | OBS | FrameFlow |
|---|---|---|
| Layered compositing | Built-in scene compositor | ❌ Needs `Crossbar.Merge` operator |
| Z-order | Drag in scene editor | ❌ Property of merge inputs |
| Position / rotation / scale | Per-source transform | ❌ Trivial transform operator once Merge exists |
| Crop | Per-source crop | ❌ Trivial transform |
| Opacity / blending | Per-source alpha | ❌ Needs alpha-aware merge |
| Mask (image / luma / chroma) | Filter | ❌ Transform operator |
| Bounding box / fit modes | Stretch / Letterbox / Crop to fit | ❌ Trivial once Transform exists |
| Hide/show toggle | Visibility checkbox | ❌ Conditional merge input |
| Multiple scenes | Scene list | ❌ Multiple parallel pipelines + active-pipeline selector |
| Scene transitions | Cut / Fade / Swipe / Stinger | ❌ Time-windowed mix operator |
| Studio Mode (preview / program) | Two compositors, swap | ❌ Two parallel pipelines with crossfade |
| Nested scenes | Scene-in-scene | ❌ Falls out of recursive `Merge` |
| Scene collections | Saved scene lists | ❌ Serialization layer |

The blocker is **`Crossbar.Merge`** — multi-input fusion is the
missing primitive. Once it lands, scene composition is mostly
transforms layered on top of it.

## Feature parity — encoding + muxing

All of these ride FFmpeg's encoder API. The encoder-terminal contract
landed in [ADR-0040](adr/ADR-0040-capture-sources-and-encoder-terminals.md),
and H.264/MP4 in [ADR-0053](adr/ADR-0053-h264-mp4-encoder-terminal.md);
the rows below track what is actually wired up beyond that.

| Encoder | OBS | FrameFlow |
|---|---|---|
| Software H.264 | ✅ x264 (default) | ✅ `libopenh264` (ADR-0053). `libx264` is GPL and not in the LGPL build |
| x265 (software H.265) | ✅ | ⏳ |
| SVT-AV1 (software AV1) | ✅ recent | ⏳ |
| NVENC (NVIDIA H.264/HEVC/AV1) | ✅ | ⏳ |
| AMF (AMD) | ✅ | ⏳ |
| QSV (Intel Quick Sync) | ✅ | ⏳ |
| Apple VT (macOS) | ✅ | ⏳ |
| AAC (audio) | ✅ FDK / FFmpeg | ⏳ |
| Opus (audio) | ✅ | ⏳ |

| Muxer | OBS | FrameFlow |
|---|---|---|
| MP4 | ✅ | ✅ video-only (ADR-0053); no audio track yet |
| MKV | ✅ recommended for crash safety | ⏳ |
| MOV | ✅ | ⏳ |
| FLV | ✅ (RTMP-friendly) | ⏳ |
| MPEG-TS | ✅ (SRT-friendly) | ⏳ |
| Fragmented MP4 | ✅ | ⏳ |
| HLS segments | ✅ | ⏳ |

**Important OBS-specific behavior we should match:** MKV-as-default
for recording. MKV's frame-by-frame structure means a crash mid-record
loses at most one frame; an MP4 with a missing `moov` atom requires
ffmpeg `untrunc`-style recovery. OBS's "remux to MP4 after recording"
flow is the right default and we should adopt it.

## Feature parity — streaming outputs

| Output | OBS | FrameFlow |
|---|---|---|
| RTMP push (Twitch / YouTube / FB) | ✅ | ⏳ Phase 5 (FFmpeg `avformat` URL) |
| RTMPS (TLS) | ✅ | ⏳ |
| SRT push | ✅ | ⏳ Would be `Crossbar.Net.Srt` peer package |
| HLS push (segment to CDN) | ✅ recent | ⏳ |
| WebRTC push | 🟡 Recent (WHIP) | ❌ Out of scope; peer project |
| Multiple simultaneous outputs | ✅ recent | ✅ Falls out of `Broadcast` |
| Replay buffer (ring buffer + save hotkey) | ✅ | ⏳ Trivial `Window` operator + file sink |
| Recording + streaming simultaneously | ✅ | ✅ Falls out of `Broadcast` (two terminal sinks) |
| Stream delay (latency for moderation) | ✅ | ⏳ Trivial buffered delay operator |
| Reconnect logic | ✅ exponential backoff | ⏳ Source-side retry; need pattern |
| Network optimization / dropped frames tracking | ✅ | ✅ Falls out of diagnostics surface (ADR-0034) |

The "multiple simultaneous outputs" feature is **already an
architectural strength** because `Broadcast` is symmetric: stream and
record from the same pipeline by adding two terminal sinks. OBS got
this in version 30 (2023); we have it for free.

## Feature parity — audio mixer

| Feature | OBS | FrameFlow |
|---|---|---|
| Multi-source mixing | ✅ Built into the audio backend | ❌ Needs `Merge<PcmAudioBuffer>` |
| Per-source gain | ✅ | ❌ Trivial transform |
| Per-source pan (stereo) | ✅ | ❌ Trivial transform |
| Per-source mute | ✅ | ❌ Conditional merge |
| VU meters (RMS/peak) | ✅ | ❌ Tap operator + diagnostics surface |
| Audio monitoring (route to headphones) | ✅ | ❌ Branch to secondary OpenAL sink |
| Multi-track recording (separate streams per source) | ✅ | ❌ Multiple muxer inputs, no audio mix |
| Audio filters (gain / gate / compress / limit / EQ / noise suppression) | ✅ ships ~12 filters | ❌ Transform operators; one per filter |
| Noise suppression (RNNoise / NVIDIA RTX / Speex) | ✅ via plugins | ❌ Could ship `RNNoise` operator; NVIDIA via Crossbar.Cuda |
| VST 2.x plugin host | ✅ | ❌ Not planned |

The audio side is genuinely lacking. None of this is hard
architecturally — once `Merge<PcmAudioBuffer>` exists, every audio
feature falls out of operators on top of it.

## Feature parity — effects / video filters

| Effect | OBS | FrameFlow |
|---|---|---|
| Color correction (gamma / contrast / brightness / saturation / hue / opacity) | ✅ | ❌ One transform |
| Chroma key (greenscreen) | ✅ classic | ❌ Trivial shader / Crossbar.Cuda kernel |
| Color key | ✅ | ❌ |
| Luma key | ✅ | ❌ |
| Image mask / blend | ✅ | ❌ |
| Crop / pad | ✅ | ❌ |
| Scaling / aspect ratio | ✅ | 🟡 Done inside decoder via swscale |
| Scroll | ✅ | ❌ |
| Sharpen | ✅ | ❌ |
| LUT (color grading) | ✅ | ❌ |
| **AI background removal** (segmentation) | 🟡 Via plugins (`StreamFX`, NVIDIA Broadcast) | ✅ **Architectural win** — Crossbar.Onnx + a segmentation model is a few lines |
| **AI noise suppression** | 🟡 NVIDIA Broadcast plugin | ✅ Crossbar.Onnx + RNNoise/NSNet2 |
| **AI auto-framing / speaker detection** | 🟡 Via plugin | ✅ YOLO + crop operator |
| **AI subtitle generation** | ❌ Not native | ✅ Whisper.cpp + overlay operator |

The classical filters are all "one operator each" work. The AI
filters are where **our differentiation lives** — Crossbar.Onnx +
Crossbar.Cuda makes "live subtitle from microphone" a few-line
pipeline, where OBS needs a plugin and NVIDIA Broadcast and a
specific GPU.

## Feature parity — control surfaces / UX

| Feature | OBS | FrameFlow |
|---|---|---|
| Desktop GUI (scene editor, source list, mixer, transport) | ✅ Qt | ❌ Would be an Avalonia app |
| Hotkeys | ✅ Per-action global hotkeys | ❌ Wire to controller methods |
| Multiview (multiple scenes visible simultaneously) | ✅ | ❌ Multiple Avalonia VideoView controls |
| Stats overlay (FPS / bitrate / dropped frames) | ✅ | ✅ Diagnostics surface (ADR-0034) covers everything |
| WebSocket remote control (OBS-Websocket plugin) | ✅ via plugin | ❌ A small ASP.NET Core wrapper |
| Stream Deck integration | ✅ via OBS-Websocket | ❌ Falls out of WebSocket |
| Hotkey shortcuts / scripted scene changes | ✅ Lua / Python scripts | ✅ **C# scripting** via Roslyn — better |
| Audio device picker | ✅ | ❌ Periphery enumeration |
| Auto-record on stream start | ✅ | ❌ One DI registration |
| Replay buffer save hotkey | ✅ | ❌ Hotkey + Window operator + file sink |
| Source visibility tweens | ❌ Plugin (`Move`) | ❌ Animated transform operator |
| Source duplicate / link | ✅ | ❌ Falls out of Broadcast on a source |

## Feature parity — plugin ecosystem

OBS has a vast plugin ecosystem (OBS Project lists 500+; including
unofficial there are thousands). The most-used:

| Plugin | OBS use | FrameFlow equivalent |
|---|---|---|
| OBS-Websocket | Remote control protocol | Roll our own minimal API; ASP.NET Core |
| StreamFX | FFmpeg filters/sources, blur, shaders, encoder presets | Many of these become Crossbar operators; encoder presets become DI options |
| obs-ndi | NDI source/sink | Out of scope for v1 (license) |
| obs-browser (CEF) | Browser source | Would need CefSharp embedding |
| obs-virtual-cam | Output as virtual webcam | Per-platform virtual-cam driver; Periphery work |
| Move plugin | Animated source movement | Animated transform operator |
| Source Record | Per-source record-to-file | Falls out of Broadcast |
| Advanced Scene Switcher | Conditional scene logic | C# script with controller commands |
| StreamElements / Streamlabs widgets | Browser-source widgets | Same as obs-browser path |

**The honest verdict on plugins:** we will never have OBS's plugin
ecosystem. We can have a *different* one: NuGet packages of
operators, scripted by C# instead of Lua. That's a different value
proposition, not a worse one. Users who pick OBS for "the plugin
ecosystem" are not our target users in v1.

## Where a FrameFlow-based OBS competitor would differ on purpose

### 1. Scenes-as-code, not scenes-as-JSON

OBS persists scenes as JSON; users edit them in the scene editor.
A FrameFlow-based competitor's canonical scene definition is a C#
expression:

```csharp
var scene = Scene.Compose(
    PrimaryCamera.Transform(Crop(left: 100)).Scale(0.5),
    DesktopCapture,
    Overlay.Image("logo.png").AtCorner(Corner.TopRight),
    Overlay.Text(() => $"Live · {DateTime.Now:HH:mm}"));
```

A JSON / YAML format can layer on top for the GUI's persistence. The
canonical form is the code, which means scenes are reviewable in PRs,
version-controllable, refactorable, type-checked.

### 2. ML inference is first-class, not a plugin

Auto-framing, live captions, virtual background, noise suppression —
all of these are NVIDIA Broadcast features that OBS users install
separately. In our model:

```csharp
PrimaryCamera
    .Transform(BackgroundRemoval.WithModel("rvm-mobilenetv3"))
    .Transform(AutoFraming.OnSpeaker())
    .Broadcast(...)
```

Crossbar.Onnx and Crossbar.Cuda already exist. Models are a NuGet
package away.

### 3. Recording fault-tolerance is structural, not heroic

OBS's "MKV by default, remux to MP4 later" pattern works because MKV
tolerates a missing trailer. A FrameFlow-based recorder benefits
from the same MKV default, plus the diagnostics surface (ADR-0034)
gives structured observability of pipeline health during recording.
Worker fault → terminal-state transition is ADR-0023's contract; an
abrupt termination ends the recording with the file in a valid
state.

### 4. No global init, no hot-loaded plugins

OBS uses dlopen/LoadLibrary for plugins, with all the ABI version
matching and crash-on-bad-plugin behaviour that implies. We use
NuGet. A plugin is a package reference at build time. Cost: no
in-process hot-add of features; benefit: reproducible builds,
predictable crash surface.

### 5. The library is the product

OBS is a desktop app. A FrameFlow-based "OBS" is a desktop app *and*
an SDK — other apps embed the same engine to record / stream
without pulling in a UI. Game engines, conferencing apps,
classroom-capture systems all become consumers of the same library.

## Where OBS beats us today (and stays beating us)

These are honest assessments, not roadmap items.

- **Brand and trust.** Streamers know OBS works. We will be unknown.
- **Plugin ecosystem.** Thousands of plugins, ten years of
  accretion. We will not match this.
- **Game capture via API hooks.** OBS's process-injection DLL with
  DX/Vk/OGL hooks is a ~5-year subspecialty. We could do "share my
  whole screen" easily; "share *just* my game's framebuffer with
  zero overhead" is a different planet.
- **Browser source.** CEF integration is 200MB of runtime and a
  large maintenance burden. OBS pays the cost; we shouldn't unless
  there's specific demand.
- **macOS desktop audio capture.** Apple intentionally restricts
  this; OBS users install BlackHole or similar. We inherit the same
  limitation.
- **VST 2.x plugin host.** OBS hosts audio VSTs. Building a VST host
  is its own project. We can host RNNoise / NSNet2 ONNX models
  cheaper than VSTs and call that done.

## Roadmap to a 1.0 streamer/recorder app

Sequenced for buildable releases. Each phase is something a user
can pick up and use end-to-end.

| Phase | Scope | Estimate | Useful for |
|---|---|---|---|
| **0** | Existing FrameFlow + Crossbar (today). | Done | Play files, run ML on decoded frames, multi-output to UI/capture sinks. |
| **1** | **Encoders + muxers.** The contract is [ADR-0040](adr/ADR-0040-capture-sources-and-encoder-terminals.md) and H.264/MP4 has landed ([ADR-0053](adr/ADR-0053-h264-mp4-encoder-terminal.md)). Remaining: hardware NVENC + VT + QSV via FFmpeg, MKV / FLV / TS muxers, replay buffer (ring-buffered Window operator → file sink). Note `libx264` is GPL and is not in the pinned LGPL build. | 2–4 weeks | "Record my screen to disk" — assuming screen capture also lands. |
| **2** | **Cross-platform capture sources** in Periphery: screen (Win DDA / mac SCK / Linux PipeWire), microphone, system audio loopback (Windows + Linux). | 1 quarter | A minimum-viable recorder. |
| **3** | **`Crossbar.Merge` operator** — multi-input fusion. Unlocks scenes, audio mixing, multi-camera. | 1 quarter | The architectural primitive. Without this we are not a real OBS competitor. |
| **4** | **Scene model + scene transitions.** Thin record types layered on Merge. Cut/Fade/Swipe transitions as Window operators. | 4–6 weeks | The product surface most users want first. |
| **5** | **RTMP push** via FFmpeg `avformat`. Reconnect logic. Bitrate management. | 2–4 weeks | "Stream to Twitch/YouTube." |
| **6** | **Audio mixer** as Merge<PcmAudioBuffer> + transforms (gain, pan, gate, compress). VU-meter diagnostics. | 4–6 weeks | Professional audio handling. |
| **7** | **Avalonia desktop app** — scene editor, source list, audio mixer, transport, stats overlay. Hotkeys. | 1–2 quarters | The thing users download and run. |
| **8** | **ML differentiators** — virtual background (segmentation), auto-framing (YOLO), live captions (Whisper). | 1 quarter, parallel to others | The "why use this instead of OBS" pitch. |
| **9** | **WebSocket remote control** (ASP.NET Core, OBS-Websocket-compatible enough that Stream Deck plugins work). | 2–4 weeks | Integration with existing streaming ecosystems. |
| **10** | **Polish:** scene collections, profiles, settings UI, auto-update, crash-reporter. | Open-ended | Shipping a product, not a tech demo. |

**Total realistic budget:** 9–15 months of focused dev for a 1.0
that a streamer would actually use, vs OBS. Phases 1–3 are the
critical path; everything after parallelises.

**Critical path is `Crossbar.Merge`.** Until that operator exists,
nothing in the scene compositor section works.

## Anti-goals — things we deliberately won't build

- **Game-capture process-injection hooks.** We will support "share
  this window" via OS-level capture APIs. We will not write a DLL
  that injects into game processes and hooks Present/SwapBuffers.
  That subspecialty is OBS's moat; we are not contesting it.
- **CEF / Chromium browser source.** 200MB+ runtime, large
  maintenance surface, niche use case. Build it as a third-party
  Crossbar operator package if someone wants it.
- **VST plugin host.** Audio VSTs are a niche we are not going to
  enter. ONNX-based audio models are the better seam for us.
- **NDI as a first-party feature.** Library licensing is
  restrictive; NDI's value is interop with broadcast workflows we're
  not yet in.
- **Custom kernel extensions for macOS desktop audio.** Apple
  doesn't want us doing this. Users install BlackHole.
- **A native UI per platform.** Avalonia is the cross-platform
  answer. We're not writing three UIs.

## Why this is a plausible product

The contrarian observation: **OBS is excellent at the streaming-from-
your-bedroom use case, less excellent at everything else.** The
adjacent markets that aren't well-served:

- **Classroom / lecture capture** — needs reliability, automation,
  programmable scene logic ("when the camera detects no speaker for
  60 seconds, switch to the slides scene"). Our scenes-as-code
  pitch.
- **Live-event production** — needs multi-camera, NDI replacement,
  reliable network ingest. Some of our architectural strengths
  (typed pipelines, structured diagnostics) help; some of our gaps
  (NDI, SDI capture) hurt.
- **Conferencing-app embedded recording** — needs a *library*, not
  an app. Our position: we're an SDK, not a desktop app, primarily.
  OBS's plugin model can't embed.
- **ML-augmented streaming** — "auto-frame on speaker," "blur my
  room not me," "subtitle my stream live." These are NVIDIA
  Broadcast features today, locked to NVIDIA hardware. We can do
  them cross-platform via Crossbar.Onnx.
- **Records as code** — broadcasters / podcasters with deterministic
  episodic workflows want "press button, run my preset
  template." Currently scripted via OBS-Websocket + external
  tools. Native in our model.

The product is not "OBS but in C#." It's **"a programmable streaming
SDK with a reference desktop app, ML-native, that happens to also
cover the OBS use case."**

## Maintenance

Conventions:

- Status legend: ✅ shipped · 🟡 partial, or only via a plugin or
  workaround · ⏳ planned, not built · ❌ not started or out of scope.
- Flip rows when you ship the feature.
- Add rows when OBS ships something new that's interesting.
- Update the roadmap when phases change shape.
- The "Where OBS beats us" list should grow if we discover more; the
  "Where we differ on purpose" list should grow if we make
  architectural choices that bake in a difference.

Honesty is the only thing that makes this document useful. Don't
oversell. If a row is ❌ today and might be ❌ forever, mark it ⚪
("explicitly out of scope") rather than ⏳.
