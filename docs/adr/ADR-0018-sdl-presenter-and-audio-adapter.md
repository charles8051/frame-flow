# ADR-0018: SDL Presenter and Audio Adapter (FrameFlow.Sdl)

**Status:** Proposed
**Date:** 2026-03-29
**Supersedes:** None
**Related:** ADR-0003 (audio-master sync), ADR-0005 (native resource ownership), ADR-0009 (threading model), ADR-0016 (Avalonia presenter strategy)

## Context

The SDL examples (`SdlPlayer`, `SdlCorpusRunner`) have independently developed identical solutions to the same problem: SDL requires all window, renderer, and event-polling calls to happen on the OS thread that called `SDL_Init`. The FrameFlow playback pipeline calls `IVideoFramePresenter.PresentAsync` from a thread-pool worker, which cannot make SDL calls directly.

Both examples solve this with the same pattern:

1. A dedicated pinned "SDL-Main" thread that never yields to the thread pool.
2. `PresentAsync` stores the frame via `Interlocked.Exchange` — no SDL calls.
3. A `RenderPendingFrame()` method called from the SDL event loop renders the stored frame using SDL calls.

This pattern is duplicated across two projects with slight variations. The audio side has the same problem: `OpenAlAudioSink` is an unimplemented stub, so both examples use an inline `WallClockAudioSink` / `MeteringAudioSink` that paces audio writes to wall-clock time and drives the sync strategy via `GetPlaybackTime()`.

A dedicated `FrameFlow.Sdl` project would consolidate the proven presenter and audio sink into a reusable adapter package, giving any SDL-based application correct threading, frame synchronization, and real-time pacing out of the box.

## Decision

Create a `FrameFlow.Sdl` project under `src/` containing:

### 1. `SdlVideoPresenter` — thread-safe frame presenter

Implements `IVideoFramePresenter`. Splits frame acceptance from rendering:

- **`PresentAsync(frame)`** — called from any thread. Stores the frame via `Interlocked.Exchange(ref _pendingFrame, frame)`. Disposes the previously pending frame if it was not yet rendered (frame drop). Makes zero SDL calls. Returns synchronously.

- **`RenderPendingFrame()`** — called by the consumer from the SDL event-loop thread. Atomically takes the pending frame, updates the SDL texture, and calls `RenderClear` / `RenderCopy` / `RenderPresent`. Disposes the frame after copying pixel data. Also checks the `_destroyRequested` flag and performs deferred SDL resource destruction if set (see disposal contract below).

- **`ClearAsync()`** — disposes any pending frame without rendering. Safe from any thread.

**Frame ownership contract:** ownership transfers to the presenter on `PresentAsync`. The presenter disposes each frame after rendering or when a newer frame replaces it. At most one frame is held at a time. Callers must not access the frame after calling `PresentAsync`.

**Texture lifecycle:** the SDL texture is lazily (re)created when the incoming frame dimensions change. If `SDL_CreateTexture` fails during a dimension change, the error is logged and the frame is discarded — no exception propagates into the consumer's event loop. The texture, renderer, and window are all created and destroyed on the SDL thread.

**Pixel format assumption:** the presenter assumes BGRA32 input (SDL `ARGB8888` on little-endian). This matches the current `VideoDecoder` output format. Any change to the decoder's output pixel format requires a corresponding change to the texture format. This assumption is documented in the type's XML doc comment.

#### Thread field ownership

Fields are partitioned by owning thread to avoid data races:

| Field | Owner | Access pattern |
|-------|-------|----------------|
| `_pendingFrame` | shared | `Interlocked.Exchange` only |
| `_destroyRequested` | shared | `volatile bool` |
| `_window`, `_renderer`, `_texture` | SDL thread | read/write in `RenderPendingFrame`, `Initialize`, deferred destroy |
| `_textureWidth`, `_textureHeight` | SDL thread | read/write in `RenderPendingFrame` only |
| `_frameCount` | SDL thread | incremented in `RenderPendingFrame`, not in `PresentAsync` |

#### Disposal contract (deferred destroy)

`DisposeAsync` may be called from any thread (including via `await using` from a thread-pool context). It must not call SDL functions directly. Instead:

1. `DisposeAsync` sets `volatile bool _destroyRequested = true`.
2. `DisposeAsync` drains `_pendingFrame` via `Interlocked.Exchange` and disposes it.
3. The next call to `RenderPendingFrame` checks `_destroyRequested` at entry. If set, it destroys the SDL texture, renderer, and window, then returns immediately.

This ensures SDL resource destruction always happens on the SDL thread regardless of which thread calls `DisposeAsync`. If `DisposeAsync` is called after the event loop has exited (no more `RenderPendingFrame` calls), the consumer must call a synchronous `DestroyResources()` method from the SDL thread in their `finally` block.

#### Thread-affinity debug assertion

In debug builds, the presenter captures `Environment.CurrentManagedThreadId` at `Initialize` call time and asserts that `RenderPendingFrame` is called from the same thread. This catches wrong-thread violations early during development without runtime cost in release builds.

### 2. `SdlAudioSink` — wall-clock paced audio sink

Implements `IAudioSink`. Provides the master clock reference for `AudioMasterSyncStrategy`:

- **`StartAsync()`** — starts an internal `Stopwatch` and resets the cumulative sample counter to zero.
- **`WriteAsync(block)`** — accumulates decoded sample count, then delays (`Task.Delay`) if the cumulative decoded position runs ahead of wall-clock elapsed time. This paces audio-only playback and prevents the audio loop from racing ahead of real time.
- **`GetPlaybackTime()`** — returns `Stopwatch.Elapsed`. The sync strategy uses this as the reference time for computing video frame delays.
- **`PauseAsync()` / `ResumeAsync()`** — stops / restarts the stopwatch.
- **`StopAsync()`** — stops the stopwatch and resets the cumulative sample counter to zero, so that a subsequent `StartAsync` (e.g., after seek) begins pacing from position zero rather than stalling for the duration of previously-decoded audio.

This sink does not produce audible output. It is a timing-only adapter. When a real SDL audio backend is added (SDL_AudioStream or SDL_QueueAudio), it will replace the `Task.Delay` pacing with actual buffer backpressure from the audio device, and `GetPlaybackTime()` will query the device's playback position.

#### `Task.Delay` precision and sync jitter

`Task.Delay` has a minimum resolution of ~15ms on Windows (the default system timer interrupt interval) or ~1ms with `timeBeginPeriod(1)`. This means per-block pacing jitter of up to 15ms. For 24fps content (41.7ms/frame), this is acceptable. For 60fps content (16.7ms/frame), the jitter is a significant fraction of a frame period. The `GetPlaybackTime()` return value (wall-clock elapsed) is not affected by this jitter — it remains smooth — so the video sync delay computation is accurate. The jitter only affects audio-side pacing granularity.

~~Consumers targeting high-frame-rate content on Windows should call `timeBeginPeriod(1)` at application startup to reduce timer resolution, or accept the ~15ms jitter until a real audio device provides hardware-paced backpressure.~~

**Superseded by [ADR-0067](ADR-0067-high-resolution-pacing-timers.md).** Consumers should call nothing. FrameFlow's pacing clocks now sleep on their own high-resolution waitable timers, which are per-timer rather than process-wide, so the quantization described above does not reach them. The guidance above stood for four years and cost three investigations (#128, #145, #152) — it is invisible when unfollowed, since playback simply runs at two thirds rate.

#### `PcmAudioBuffer.SampleCount` semantics

`PcmAudioBuffer.SampleCount` is the total number of interleaved samples across all channels. Per-channel sample count is `SampleCount / Channels`. The audio sink uses this per-channel count to compute decoded duration as `totalSamplesPerChannel / sampleRate`.

### 3. Threading contract

The consumer is responsible for creating the SDL thread and running the event loop. `FrameFlow.Sdl` does not create threads or run event loops — it provides the adapters that plug into a consumer-owned loop. A typical consumer loop:

```
SDL_Init on dedicated thread
create SdlVideoPresenter (on SDL thread)
create PlaybackSession with presenter + SdlAudioSink
session.Play()

while session is playing:
    presenter.RenderPendingFrame()   // on SDL thread
    SDL_PollEvent(...)               // on SDL thread
    Thread.Sleep(4-8ms)              // stay pinned
```

This keeps the threading policy in the consumer's hands. The library provides thread-safe adapters, not a threading framework.

### 4. Frame synchronization model

The frame synchronization chain has three stages:

1. **Decode → channel:** The video decode loop pushes decoded frames into a bounded `Channel<IDecodedVideoFrame>`. Backpressure is applied here when the channel is full (`BoundedChannelFullMode.Wait`).

2. **Channel → sync delay → presenter:** The video present loop reads frames from the channel, calls `_syncStrategy.GetVideoDelay(frame.PTS, audioSink.GetPlaybackTime())`, awaits the computed delay via `Task.Delay`, then calls `presenter.PresentAsync(frame)`. This is where real-time pacing happens.

3. **Presenter → SDL render:** `PresentAsync` stores the frame. The SDL event loop calls `RenderPendingFrame()` at its own cadence (~120Hz with 8ms sleep). If the event loop is slower than the frame rate, frames are dropped (newest-wins). If faster, `RenderPendingFrame()` returns immediately with nothing to render.

Frame drops between stages 2 and 3 are intentional. The sync strategy ensures the frame was released at the correct wall-clock moment. If the SDL render loop is temporarily stalled (e.g., window drag freezes the message pump), the stale frame is dropped and the next one is rendered when the loop resumes. This prevents unbounded frame accumulation and keeps presentation latency low.

**Display latency:** the gap between `PresentAsync` storing a frame and `RenderPendingFrame` displaying it is bounded by the render loop period — up to 8ms in the default configuration. For 24fps content (41.7ms/frame), this is 19% of a frame period. For 60fps content (16.7ms/frame), this is 48%. Consumers targeting high frame rates should reduce the loop sleep to 2-4ms or use an adaptive sleep that targets the next expected frame PTS.

#### Why single-slot exchange, not double-buffering (contrast with ADR-0016)

ADR-0016 introduced double-buffering for the Avalonia presenter because Avalonia's Skia compositor reads the front buffer concurrently from a background render thread while the decode thread writes to the back buffer. That concurrent read/write requires two buffers to prevent tearing.

SDL's render path is strictly sequential on a single thread: `SDL_UpdateTexture` copies pixel data, then `SDL_RenderPresent` displays it. There is no concurrent compositor reading the texture while it is being updated. The SDL thread is the sole reader and writer. A single-slot `Interlocked.Exchange` is sufficient because `RenderPendingFrame` atomically takes the frame before accessing its pixel data — no concurrent reader can observe a half-written state.

### 5. Dependencies

`FrameFlow.Sdl` depends on:
- `FrameFlow.Media` (for `IVideoFramePresenter`, `IAudioSink`, frame contracts)
- `Silk.NET.SDL` (SDL2 bindings)

It does **not** depend on `FrameFlow.Playback`, `FrameFlow.Decoding`, or `FrameFlow.Native`. The adapter layer only speaks managed contracts.

### 6. Unsafe code

`SdlVideoPresenter` requires `AllowUnsafeBlocks` for SDL interop (pointer-based texture updates, window/renderer handles). This is unavoidable with Silk.NET's SDL2 bindings. Unsafe code is confined to the presenter's render path.

## Consequences

**Positive:**
- Eliminates duplicated SDL presenter code across example projects.
- Establishes a tested, correct threading model that consumers can rely on.
- Example projects become thin wiring: bootstrap, create adapters, run event loop.
- The wall-clock audio sink provides pacing within system timer resolution for any SDL application until a real audio backend is added.
- Clean adapter boundary: no SDL types leak into the core, no core types leak into SDL.

**Negative:**
- Adds a new project to the solution.
- The `SdlAudioSink` is a timing-only stub — no audible output until a real SDL audio backend is implemented.
- Frame drops between presenter acceptance and SDL rendering are invisible to the playback session. If the SDL loop stalls for extended periods (window drag on Windows), the session keeps running and frames accumulate then get dropped. This is acceptable but consumers should be aware.
- `Task.Delay`-based pacing introduces up to ~15ms of per-block jitter on Windows. This is a known limitation of the timing stub, not of the architecture.
- `SdlVideoPresenter.DisposeAsync` uses deferred destruction — SDL resources are not freed until the next `RenderPendingFrame` call (or an explicit `DestroyResources` call from the SDL thread). Consumers must ensure one of these paths executes after disposal.

## Alternatives Considered

### Keep SDL code in example projects

Rejected. The pattern is already duplicated and proven. Leaving it in examples means every new SDL application must rediscover the threading constraints and reimplement the same solution.

### Have the presenter create and own the SDL thread

Rejected. This would couple the adapter to a specific threading policy. Different consumers may want different event loop structures (game loop, timer-based, integrated with another framework). The adapter should be a passive component that plugs into whatever loop the consumer provides.

### Use SDL_PushEvent to marshal frames to the SDL thread

Rejected. Adds complexity and latency. The `Interlocked.Exchange` pattern is simpler, zero-allocation, and the event loop already polls at high frequency. SDL's event queue has size limits and would require custom event type registration.

### Combine presenter and audio sink into a single "SdlBackend" class

Rejected. They have different responsibilities and lifecycles. A consumer might want the presenter without the audio sink (e.g., using a real audio backend), or the audio sink without the presenter (audio-only playback). Keeping them separate follows the existing `IVideoFramePresenter` / `IAudioSink` contract split.

## Architecture Hawk Review Notes

This ADR was reviewed by the Architecture Hawk agent, which identified 10 findings. The following were incorporated into this revision:

1. **Thread field ownership table** — fields partitioned by owning thread; `_frameCount` moved to SDL-thread-only; `_disposed` replaced with `volatile _destroyRequested`.
2. **Deferred destroy pattern** — `DisposeAsync` sets a flag; SDL resource destruction happens in `RenderPendingFrame` on the SDL thread.
3. **Thread-affinity debug assertion** — captures thread ID at `Initialize`, asserts in `RenderPendingFrame`.
4. **`SDL_CreateTexture` failure handling** — errors logged and frame discarded, no exception propagation into event loop.
5. **`StopAsync` resets sample counter** — prevents multi-minute stall after seek.
6. **`Task.Delay` jitter documented** — up to 15ms on Windows, with mitigation guidance.
7. **`PcmAudioBuffer.SampleCount` semantics pinned** — total interleaved samples, per-channel = `SampleCount / Channels`.
8. **Single-slot vs. double-buffer justification** — explicit comparison with ADR-0016 Avalonia case.
9. **Display latency characterized** — bounded by render loop period, with guidance for high-frame-rate content.
10. **Pixel format assumption documented** — BGRA32 input required, must match decoder output.
