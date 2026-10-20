# Exploration: a Blazor WebAssembly target

**Status:** Exploration. Nothing here is decided. No consumer is asking for a browser target.

**Date:** 2026-10-19

**Issues:** #22 (deferred). This document promotes the gap analysis that issue names but never
wrote down. It restates the position recorded there. It adds no measurements.

## The question

If FrameFlow gains a Blazor WebAssembly target, how does it decode and render video, and which
parts of the substrate carry over unchanged?

## Summary

- **Decode with WebCodecs, not a WASM build of FFmpeg.** `VideoDecoder` and `AudioDecoder` reach the
  browser's hardware decoder. Software decode in WASM is structurally non-competitive with the
  browser's `<video>` element for any codec the browser hardware-decodes.
- **The cost is paid twice.** CPU and battery use are roughly an order of magnitude worse than the
  hardware path. Each frame also crosses WASM heap, JS heap and GPU texture, a copy the native
  compositor path does not make.
- **The substrate ports; the player does not yet.** `FrameFlow.Graph` and `IClockSource` have no
  decoder dependency. The player path builds the FFmpeg decoder directly, so a browser decoder first
  needs the engine seam of #602. See [Analysis against the code](#analysis-against-the-code).
- **Three tiers, in order of preference for the common case.** Tier 0 wraps `<video>`. Tier 1 is a
  WebCodecs sink. Tier 2 is an FFmpeg.wasm sink for codecs the browser does not ship.
- **Do not start with FFmpeg.wasm.** It is the obvious prototype and the wrong one.

## When a sink beats a `<video>` element

A plain `<video>` element is correct for "play this file". A FrameFlow sink earns its place only
when the work needs one of these:

- Codecs the browser will not ship.
- Frame-accurate stepping and scrubbing.
- Several streams synchronised to one master clock.
- In-pipeline effects, such as WebGPU compute for computer vision, colour grading or captioning.
- Control over pixel format and colour space.
- Headless or offscreen rendering for thumbnails or ML inference.

Without one of these, use Tier 0.

## Tiers

### Tier 0: `<video>` wrap

A thin sink that hands the source to a `<video>` element. Correct for the just-play-this-file case.
Smallest change. It gives up frame accuracy, effects and clock control.

### Tier 1: WebCodecs sink

- **Demux and parse** in WASM. This is cheap.
- **Decode** through JS interop into the browser's hardware decoder.
- **Video render** with `VideoFrame.copyTo` into a canvas, or a zero-copy texture upload to WebGL2 or
  WebGPU.
- **Audio** through an `AudioWorklet` fed from decoded `AudioData`.
- **Cost:** about 1.5 to 2 times the CPU of `<video>`, per the figure in #22. That figure is an
  estimate and has not been measured in this repository.
- **Complexity:** substantial.

### Tier 2: FFmpeg.wasm sink

A fallback for codecs the browser does not ship: ProRes, DNxHR, CineForm, raw Bayer and exotic
broadcast formats. It builds on Tier 1 for rendering and audio. It documents a 1080p ceiling and a
battery cost. It is the long tail, not the default.

## What carries over

| Piece | Browser status |
| ----- | -------------- |
| `IClockSource` | Unchanged. The browser supplies the time source. |
| `FrameFlow.Graph` | Unchanged, subject to the threading audit below. |
| Session configurators (`FrameFlow.Playback`) | Need the decoder engine seam first. |
| Native FFmpeg decoding | Does not apply. Replaced by WebCodecs or FFmpeg.wasm. |
| Hardware backends (D3D11VA, V4L2, Vulkan) | Do not apply. The browser owns hardware access. |

## Open questions

- Which demuxers compile to WASM without native dependencies, and what is their size cost?
- How does a `VideoFrame` enter `FramePipeline<T>` without a copy, and who owns its lifetime?
- How does the audio `AudioWorklet` clock map onto `IClockSource` for master-clock sync?
- Can the Blazor WASM runtime and a worker share the pipeline, or does decode need its own worker?
- Which codec list does `VideoDecoder.isConfigSupported` give on the browsers we would target?

These are unanswered. A spike would settle the first three.

## Analysis against the code

Analysis against `origin/main` at `e9802b2`. Everything below was read, not run. No WASM build was
attempted.

### Verdict

The decode position holds: WebCodecs first, FFmpeg.wasm only for codecs the browser lacks. The
porting story is partly wrong. #22 predates the graph fork. One type it names no longer exists, and
the seam that would carry a browser decoder does not exist yet.

### Claims checked

| Claim | Verdict | Evidence |
| ----- | ------- | -------- |
| `FramePipeline<T>` ports unchanged | Refuted | No such type on `main`. It survives only in doc comments as legacy (`src/FrameFlow.Playback/PaceUntil.cs:22-24`, `src/FrameFlow.Video/VideoOperators.cs:23`). The substrate is now `FrameFlow.Graph`. |
| `IClockSource` ports unchanged | Confirmed | `src/FrameFlow.Graph/IClockSource.cs:46`. `FrameFlow.Graph` has no package or project references. |
| Configurator-owned termination ports unchanged | Partly true | Graph termination is decoder-free (`src/FrameFlow.Graph/Graph.cs:19-26`). The configurators that own it live in `FrameFlow.Playback` (`SubstrateSession.cs`, `SubstrateSessionFactory.cs`), which references `FrameFlow.Decoding` and through it FFmpeg. |
| Demux and parse in WASM are cheap | Unverified | FrameFlow demuxes through FFmpeg's `avformat`. `FrameFlow.Native` packs win-x64 natives only. No managed demuxer exists in the repo. |
| A WebCodecs frame can enter the pipeline | Unverified, needs new surface | `FrameMemoryDomain` is `Cpu` or `Gpu` (`src/FrameFlow.Graph/FrameMemoryDomain.cs:31-44`). `FrameMemoryDomains` names `Cpu`, `D3D11`, `D3D12` (`src/FrameFlow.Graph/FrameMemoryDomains.cs:18-30`). A JS-owned `VideoFrame` fits neither without a new domain. |
| Audio through `AudioWorklet` slaves the clock | Partly true | The audio clock logic is already pure (`src/FrameFlow.Media/AudioClockState.cs`, `AudioClockInterpolation.cs`, `AudioClockRateLimit.cs`). Its wiring sits in `SubstrateSession` and `OpenAlAudioSink`. |
| 10x CPU for WASM decode, 1.5-2x for WebCodecs | Unverified | No measurement in the repo or in #22. |

### Assumptions challenged

- **"The substrate ports unchanged."** True for `FrameFlow.Graph` and the pure cores in
  `FrameFlow.Media`. Not true for the player. `SubstrateSession` and `PassBuilder` build the FFmpeg
  decoder directly, and the registered `VideoDecoderFactory` is never resolved (#602, also noted in
  [stateless-decode-core.md](stateless-decode-core.md)). A browser decoder needs the same engine seam
  the stateless-decoder work needs. That is the larger prerequisite, and #22 does not name it.
- **"Demux in WASM is cheap."** Cheap in CPU, but FrameFlow has nothing to run there. The choice is
  FFmpeg.wasm for demux only, a managed MP4 and WebM demuxer, or a JS demuxer such as mp4box.js
  across interop. Tier 1 is not free of FFmpeg.wasm unless one of the last two is picked.
- **Threading.** Browser WASM is single-threaded unless the app ships the multithreaded runtime with
  cross-origin isolation. `NodePumps` uses `Task.Run` (`src/FrameFlow.Graph/NodePumps.cs:316`).
  That runs, but without parallelism. Blocking waits are the hazard: the `SpinWait` path described
  in `src/FrameFlow.Media/LinkedCancellation.cs:15-30` and any sync-over-async would stall the only
  thread. Not audited.
- **Tier 0 as a FrameFlow sink.** A `<video>` element owns its own decode and clock. Wrapping it
  bypasses the graph entirely. It may belong outside FrameFlow as plain Blazor markup.

### Architecture fit

- **Functional core.** The pieces that would carry over are already cores or substrate:
  `FrameFlow.Graph`, the audio clock states, and the decode sequencing in `Decoding.Core`. The
  WebCodecs and `AudioWorklet` interop are shell. That split matches the workspace rule.
- **Public surface.** A browser frame needs a new `FrameMemoryDomains` member and likely a new
  `IVideoFrame` implementation. That is additive but visible.
- **Consistency.** The engine seam under `VideoDecoder` would serve FFmpeg, stateless V4L2 and
  WebCodecs. Designing it for two of the three risks a third redesign.

### Risks

1. **Engine seam absent** (blocks Tier 1 and Tier 2). Trigger: any non-FFmpeg decoder. Effect: the
   browser work rewrites the player path instead of adding a sink. Likely.
2. **Blocking waits on the browser main thread** (blocks). Trigger: a sync wait inside a pump or
   dispose. Effect: a hung tab. Plausible; not audited.
3. **No demuxer for WASM** (blocks Tier 1). Trigger: Tier 1 without FFmpeg.wasm. Effect: Tier 1
   quietly depends on Tier 2's build. Certain unless a demuxer is chosen.
4. **`VideoFrame` lifetime across interop** (track). Trigger: a frame held past `close()` or never
   closed. Effect: decoder stalls when its pool is exhausted, the same shape as the fixed-pool hang
   in hardware decode. Likely without explicit ownership rules.
5. **Unmeasured cost figures** (track). Effect: a tier choice made on numbers nobody ran.

## Recommendation

The decode tiering in #22 stands. Before any browser work, correct #22 to drop `FramePipeline<T>`
and name the engine seam (#602) as a prerequisite shared with the stateless-decoder work. Split out
two follow-ups for when the work is picked up: a demuxer choice for WASM, and an audit of blocking
waits in `FrameFlow.Graph` and `FrameFlow.Media` against a single-threaded runtime.

Keep #22 deferred until a consumer asks for a browser target. When one does, start with Tier 0 if
the need is playback only. Start with Tier 1 if the need is on the list above. Reach for Tier 2 only
when a specific codec forces it.
