# ADR-0062: Gapless multi-source playlist playback without per-item presenter rebuild

> Drafted on branch `feature/gapless-playlist`; assigned ADR-0062 on merge to
> `main` per this repository's "number-at-merge" rule.

## Status

Accepted — core implemented on `feat/gapless-playlist-impl` (see Implementation
below). Preroll (`PlaylistPrerollMode.NextItem`) and the presenter-side
format-change reconfigure remain follow-ups.

**Date:** 2026-06-06
**Related:**
- ADR-0044 (sink ownership and disposal — the session/controller are *users* of sinks, never owners; the structural enabler for this whole design)
- ADR-0003 (audio-master synchronization policy — null audio sink falls back to the wallclock pacer)
- ADR-0028 (internal layering and ownership cleanup — single-owner clock mutation; the controller caches an immutable loaded-media snapshot)
- ADR-0035 / ADR-0057 (master-clock interface split / pull-based master clock — the `IClockSource` read surface this design rebases per item)
- ADR-0058 (shared OpenAL device + context — keeps a long-lived, reused audio sink safe)
- ADR-0059 (discard streams with no consumer — the per-session discard decision that must be re-evaluated per item)
- ADR-0021 (looped playback strategy — `RepeatMode.One` solves the single-file loop; this ADR is the multi-file analog)
- ADR-0016 / ADR-0025 (Avalonia presenter frame delivery / video sink + frame-pool architecture — the presenter whose GPU resources we keep warm)

## Context

### The problem, with measured evidence

A FrameFlow consumer that plays a **rotating, looping multi-file playlist** —
the canonical case is a digital-signage view —
rebuilds the **entire** GPU present pipeline on **every playlist item boundary**.
Per item, such a view constructs a fresh
`CompositionInteropVideoView`, calls `Initialize()` and `EnsureSink()`, builds a
fresh `OpenAlAudioSink`, and calls `MediaPlayer.CreateAsync(...)`, plays to end,
then tears all of it down, and the next render repeats the cycle:

- a new `CompositionInteropVideoView` + `Initialize` + `EnsureSink` per item;
- a new `OpenAlAudioSink` + `MediaPlayer.CreateAsync` per item.
- a teardown call disposes the player and audio sink and removes the video view; the playlist service advances by rebuilding.

That recreation churns, **every single item**: the D3D11 device, the NV12→BGRA
`VideoProcessor`, the 3-buffer keyed-mutex shared-texture ring, the
`ICompositionGpuInterop` import, *and* the FFmpeg demuxer + decoder.

**Impact (measured this session, on a weak Intel HD 620 kiosk).** Each rebuild is
a roughly 2× CPU and GPU spike. In repeated 4-second-interval sampling on one
looping clip: GPU video-decode engine ~3×, video-processing and 3D engines ~2×;
CPU oscillated 47% → 97% and GPU-3D 28% → 58% purely from this churn. The
user-visible symptom is a freeze: the surface holds its last frame for the
rebuild duration (often seconds), briefly plays, then rebuilds and freezes again.

`RepeatMode.One` already solves the **single-file** case
(`PlaybackControllerCore.cs:812-827` loops the one decoder by seeking to zero on
end-of-stream; nothing rebuilds). The open problem is the **multi-file**
rotating/looping playlist: it must switch sources, and today switching == full
rebuild.

### Central hypothesis (validated below)

> Switching the decode **source** is the cheap part; rebuilding the **presenter**
> (the video sink + its GPU resources) is the expensive part. Keep the
> presentation surface + GPU resources warm across items and swap only the decode
> source — ideally gaplessly (preroll the next source before the current ends, and
> wrap at end-of-list for looping).

### What the current architecture already gives us (the load-bearing finding)

FrameFlow's existing layering is *already structured* for warm sinks across
sequential sources. Three independent facts establish this:

1. **The session never owns or disposes the sinks.** `IVideoSink` and `IAudioSink`
   both restate the ADR-0044 rule verbatim: "Sinks are owned by their DI container
   or by their immediate caller; the playback session and pipeline controller are
   *users* of sinks, not owners, and never invoke `DisposeAsync` on a sink"
   (`src/FrameFlow.Media/IVideoSink.cs:38-46`,
   `src/FrameFlow.Media/IAudioSink.cs:39-47`). `IPlaybackSession` repeats it in the
   lifecycle contract: "it does not own the sink object lifetime and must not
   dispose externally supplied sink instances"
   (`src/FrameFlow.Playback/IPlaybackSession.cs:19-21,89-94`). And the
   implementation honors it: `SubstrateSession.DisposeAsync`
   (`src/FrameFlow.Playback/SubstrateSession.cs:582-693`) disposes the pipeline,
   decoders, demux, owned wallclock, and CTS — it only `DeactivateAsync`es the
   audio sink (line 608) and **never disposes either sink**.

2. **The session factory captures the sinks once and stamps out a fresh session
   per load.** `SubstrateSessionFactory` "captures the long-lived sinks + options
   … and produces a fresh session per controller load"
   (`src/FrameFlow.Playback/SubstrateSessionFactory.cs:14-18,58-74`). Every session
   it creates is bound to the **same** sink instances.

3. **The expensive GPU resources live in the *view*, not the sink, and are created
   lazily — not tied to player lifetime.** The `CompositionInteropVideoSink` is a
   thin frame-handoff buffer; the D3D11 device, `VideoProcessor`, keyed-mutex ring,
   and compositor import live in `CompositionInteropVideoView` and are created
   lazily on first frame via `_gpuConverter ??= new D3D11Nv12SharedConverter(...)` /
   `_cpuUploader ??= new D3D11BgraUploader(...)`
   (`src/FrameFlow.Avalonia.Windows/CompositionInteropVideoView.cs:274-303`). They
   are *not* rebuilt as long as the view + sink instance survive.

In other words: a second player feeding the **same** sink instance reuses the same
view and therefore the same warm GPU resources. Nothing in the core forces the
per-item rebuild — the consumer is rebuilding the presenter *by choice*, because
FrameFlow offers no ergonomic "play these sources in sequence on warm sinks" API,
so the natural reach is "new sink + new `MediaPlayer` per file."

### So why does switching a source cost a full rebuild *today*?

Five concrete blockers, top to bottom:

1. **The public surface has no playlist concept.** `IMediaPlayer`
   (`src/FrameFlow.Player/IMediaPlayer.cs:15-58`) is a single-source player:
   Play/Pause/Seek/SetRepeatMode + state, no enqueue / next-source / current-item.
   `RepeatMode` has only `Off` and `One` — no playlist loop
   (`src/FrameFlow.Media/RepeatMode.cs:6-13`).

2. **The controller is single-source and disposes the session to change it.** The
   controller holds one `_session` field
   (`src/FrameFlow.Playback/PlaybackControllerCore.cs:70`); `Load` is permitted
   only from `Idle` or `Unloaded` (`…:662-664,874`); and `Unloaded`'s entry
   disposes the session (`…:866-875`, `DisposeSessionAsync` at `…:1109-1120`). The
   only multi-source-ish path is replay-from-`Ended`
   (`TryHandleReplayFromEndedAsync`, `…:515-546`): it `Unload`s (disposing the
   session), `LoadSourceAsync`es the **same** source (a brand-new session), then
   `Play`s. It is serial dispose → reopen, with no preroll.

3. **A serial boundary gap persists even with a warm sink.** Because the only
   switch path is dispose-then-reopen, the surface holds its last frame across
   demux-open + decoder-alloc + warmup before the next item presents — the freeze,
   shortened but not eliminated.

4. **Format/resolution is latched to the first frame.** GPU resources are sized to
   the first frame and never reconfigured:
   `D3D11Nv12SharedConverter`'s `VideoProcessorContentDescription` fixes
   input/output width/height at construction
   (`src/FrameFlow.Avalonia.Windows/D3D11Nv12SharedConverter.cs:76-86`) and its
   keyed-mutex ring textures are sized to `(width,height)` (`…:108-146`). The view's
   `??=` (above) never rebuilds them, and `OnFormatChangedAsync` is a no-op in the
   composition sink (`src/FrameFlow.Avalonia.Windows/CompositionInteropVideoSink.cs`
   `OnFormatChangedAsync` returns `default`). A mid-stream resolution change is
   silently mis-sized; only width/height *layout* updates
   (`CompositionInteropVideoView.cs:359-364`).

5. **The master clock is selected once, per session, and not per item.**
   `SubstrateSession`'s constructor picks the pacing clock once: the audio sink if
   it implements `IClockSource`, else an owned `WallClockSource`
   (`src/FrameFlow.Playback/SubstrateSession.cs:139-148`). The `WallClockSource` is
   per-session-owned and disposed on session dispose (`…:667-677`). For a warm,
   multi-item session the master clock must be re-baseable per item; and the
   audio-vs-wallclock *selection* must follow whether **this** item actually has an
   activated audio stream — not whether an `IClockSource` audio sink happens to be
   attached. (This is also a latent single-item issue: a video-only file played
   with an attached `IClockSource` audio sink paces video against a frozen audio
   counter, because `_clockSource` is the audio sink but `_hasAudio` is false so the
   sink is never activated — see `…:425-441` for the activation gate and
   `OpenAlAudioSink.cs:651` for the on-demand `Latest`.)

### What it costs to open a new source (the cheap half)

Opening a new source is bounded FFmpeg I/O plus decoder allocation, and it is
fully isolated from the presenter:

- `DemuxSessionFactory.OpenAsync` runs `avformat_open_input` →
  `avformat_find_stream_info` → packet-buffer alloc → managed `MediaInfo`
  (`src/FrameFlow.Decoding/DemuxSessionFactory.cs:66-73,101,125-128`). The
  `AVFormatContext` is wrapped in a `FormatContextHandle` and owned by the
  `DemuxSession` until its `DisposeAsync`.
- Decoders (`AVCodecContext`) are built in `SubstrateSession.InitializeAsync`
  (`src/FrameFlow.Playback/SubstrateSession.cs:228-248`) and torn down in its
  `DisposeAsync`.
- The decoder output is **not** held by the sink: the session wires
  `decoder → PaceUntil → PausableGate → (optional configurator) → sink` as a
  graph each run (`…:754-812`). The sink is the terminal node, supplied from
  outside. Swapping the source means rebuilding that graph + demux + decoders —
  all session-internal, all cheap relative to the GPU stack — while the sink
  terminal stays the same warm instance.

This confirms the hypothesis: **the swappable runtime (demux + decoders + graph) is
already cleanly separated from the warm presenter (sink + GPU resources + clock),
and the core already forbids the session from touching the sink.** The work is to
expose that separation as a first-class capability and to close the boundary gap
with preroll.

## Decision

Add a **first-class FrameFlow playlist capability that keeps the sinks, their GPU
resources, and the position clock warm for the life of the playlist, and swaps
only the per-item decode runtime underneath them — prerolling the next item so the
hand-off at end-of-stream is gapless, and wrapping at end-of-list to loop.**

The capability is built from the seams that already exist, not bolted on beside
them:

### 1. A playlist is an internal session that composes per-item runtimes

Introduce an internal `PlaylistSession : IPlaybackSession`
(`FrameFlow.Playback`) that the **existing** `PlaybackControllerCore` drives
exactly as it drives a `SubstrateSession` today — same lifecycle methods, same
callbacks, same single `_session` field. Internally, `PlaylistSession` owns:

- the ordered **play queue** (and the repeat policy);
- the **current** per-item runtime and a **prerolled-next** per-item runtime;
- a reference to the caller-supplied **warm sinks** + the controller's **clock**.

The per-item runtime *is a `SubstrateSession`* — each constructed with the **same**
sink instances and the **same** `IPlaybackClock`. `SubstrateSession` already is
"one source over caller-supplied sinks it does not own," so reusing it as the
per-item runtime requires no new decode/graph code. `PlaylistSession` is pure
orchestration over a sequence of them.

Critically, the per-item EOS is **intercepted, not bubbled**. `SubstrateSession`
fires end-of-stream through its injected `SessionCallbacks.OnEndOfStream`
(`SubstrateSession.cs:853-855`). `PlaylistSession` supplies *its own* callbacks to
each item runtime whose `OnEndOfStream` means "advance the playlist," not "tell the
controller the media ended." The controller's real `OnEndOfStream` fires only when
the queue is exhausted **and** repeat is `Off`. To the controller, a looping
playlist looks like one session that simply never ends.

### 2. The hand-off keeps the presenter warm and rebases the clock

At an item boundary (natural EOS, loop-wrap, or an explicit skip):

1. **Promote the prerolled-next runtime** (or build it now if preroll was off /
   not finished). The current runtime is disposed — which, per ADR-0044, disposes
   its demux + decoders + graph + owned wallclock but **leaves the sinks
   untouched**. The view's `??=` GPU resources survive because the sink instance
   survives.
2. **Rebase the position clock** to `TimeSpan.Zero` so the new item reports a clean
   `0 → duration` timeline. The controller's `IPlaybackClock` is reused, not
   recreated (`PlaybackClock.Seek/Start`,
   `src/FrameFlow.Playback/PlaybackClock.cs:55-97`).
3. **Select and rebase the master pacing clock per item** (see §3).
4. **Open the new runtime's gates** so its already-decoded first frame presents
   immediately. No GPU resource is rebuilt unless the format changed (§4).
5. **Signal the boundary** so the controller refreshes its cached
   `MediaInfo`/`Duration` and emits a transition event, and so the consumer can
   enqueue the following item.

The only GPU work at a same-format boundary is "present the next frame into the
existing ring" — the device, `VideoProcessor`, ring, and compositor import are
never touched.

### 3. The clock model across items

Two clocks, handled distinctly (this is the part the current single-session design
does not generalize):

- **Position clock** (`IPlaybackClock`, controller-owned): **rebased to zero** at
  each boundary, so each item plays `0 → its own duration`. This is the natural
  signage model and keeps `Duration`/seek scoped to the current item. (A
  playlist-global running position is an explicit non-goal for v1 — see Edge cases.)

- **Master pacing clock** (`IClockSource`, used by `PaceUntil`,
  `SubstrateSession.cs:759-764`): **per item**, and **selected by whether the item
  has an activated audio stream**, not by whether an `IClockSource` audio sink is
  attached:
  - *Audio-bearing item:* the shared, warm audio sink is the master. It is
    `DeactivateAsync`ed at the end of the outgoing item and `ActivateAsync`ed at the
    start of the incoming one (the existing per-item lifecycle —
    `SubstrateSession.cs:426,608`); its sample counter rebases to ~0 naturally, and
    `IAudioSink` already guarantees volume/mute persist across Activate/Deactivate
    cycles (`src/FrameFlow.Media/IAudioSink.cs:63-79`).
  - *Silent item:* a per-item `WallClockSource` started at zero
    (`src/FrameFlow.Playback/WallClockSource.cs:91-97`).

  Making this selection per-item-audio-aware is the change that lets a single warm
  audio sink span a playlist of mixed audio/silent items **and** removes the latent
  single-item stall noted in Context blocker 5.

- **Preroll and the master clock.** The next item is prerolled with a *light*
  warmup — open the source and decode its first frame — and is **not** started on
  the live master clock until hand-off. The audio sink is a single device resource
  and stays owned by the *current* item until the boundary; preroll never activates
  it. This is why preroll is audio-safe and why only one item ever drives the master
  clock at a time. (`SubstrateSession.WarmUpAsync`,
  `src/FrameFlow.Playback/SubstrateSession.cs:325-399`, already "start the graph
  with gates closed and await the first decoded frame" without activating audio —
  the preroll path reuses exactly this shape.)

### 4. Format/resolution changes are handled in the presenter, once, only when they change

Replace the first-frame `??=` latch in `CompositionInteropVideoView` with
"reconfigure GPU resources when the incoming frame's `(width, height, pixel
format)` differs from the resources' current size," and honor the sink's
`OnFormatChangedAsync` as the signal. On a change, dispose and rebuild **only** the
`D3D11Nv12SharedConverter` / `D3D11BgraUploader` (the `VideoProcessor` + keyed-mutex
ring); the D3D11 device and the compositor interop import stay warm. Same-format
items — the common signage case of a fixed canvas — skip this entirely and pay
nothing.

### 5. Public surface

In `FrameFlow.Media`, `RepeatMode` gains `All` (loop the whole playlist),
orthogonal to the existing `Off`/`One`.

In `FrameFlow.Player`, a small playlist surface that mirrors the warm-sink
`MediaPlayer.CreateAsync` shape (same sinks, hw mode, configurators, logger), built
around a **current + next** model rather than a frozen list — because the canonical
consumer's playlist rotates and changes between items:

```csharp
namespace FrameFlow.Player;

/// <summary>
/// A player that presents an ordered, optionally-looping sequence of sources
/// through ONE warm video sink + ONE warm audio sink. Item boundaries swap only
/// the decode source; the presenter (sink + GPU resources) and the clock stay
/// warm. RepeatMode.All loops the queue; One loops the current item; Off ends
/// at the tail.
/// </summary>
public interface IMediaPlaylistPlayer : IMediaPlayer
{
    /// <summary>The source currently presenting (null before the first item).</summary>
    IMediaSource? CurrentSource { get; }

    /// <summary>Append a source to the tail of the play queue.</summary>
    Task EnqueueAsync(IMediaSource source, CancellationToken ct = default);

    /// <summary>
    /// Replace the "plays immediately after the current item" slot — the preroll
    /// target. Passing null clears it (the queue tail is used instead).
    /// </summary>
    Task SetNextAsync(IMediaSource? source, CancellationToken ct = default);

    /// <summary>End the current item now and hand off to the next (no rebuild).</summary>
    Task SkipToNextAsync(CancellationToken ct = default);

    /// <summary>
    /// Raised on the UI-free dispatch path each time the presenter hands off from
    /// one source to the next (outgoing item disposed, incoming item presenting).
    /// Carries the new CurrentSource + its MediaInfo so the consumer can advance
    /// its own model and enqueue the following item.
    /// </summary>
    IObservable<PlaylistTransition> SourceTransitioned { get; }
}

public sealed record PlaylistTransition(
    IMediaSource Source,
    MediaInfo MediaInfo,
    int Index,
    bool Wrapped);
```

A factory matches the existing path:

```csharp
public static class MediaPlaylistPlayer
{
    public static Task<IMediaPlaylistPlayer> CreateAsync(
        IEnumerable<IMediaSource> sources,        // initial queue (>= 1)
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,             // ATTACHED ONCE, warm for the whole playlist
        HardwareDecodeMode hardwareDecodeMode = HardwareDecodeMode.Auto,
        bool yieldHardwareFrames = false,
        RepeatMode initialRepeatMode = RepeatMode.All,
        PlaylistPrerollMode preroll = PlaylistPrerollMode.NextItem,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? configureVideo = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? configureAudio = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Controls how aggressively the next item is opened before the current ends.</summary>
public enum PlaylistPrerollMode
{
    /// <summary>No preroll: open the next item at the boundary (warm presenter, small open gap).</summary>
    None,
    /// <summary>Open + first-frame-decode the next item ahead of the boundary (gapless, brief double decode).</summary>
    NextItem,
}
```

The transport verbs (`PlayAsync`/`PauseAsync`/`SeekAsync`/`SetRepeatModeAsync`,
`State`/`Position`/`Duration`/`MediaInfo`, volume/mute) are inherited from
`IMediaPlayer` and act on the **current item**. `Seek` is within the current item's
timeline; `Duration`/`MediaInfo` reflect the current item and update on
`SourceTransitioned`.

## Consequences

### Positive

- **The per-item presenter rebuild is gone.** The D3D11 device, `VideoProcessor`,
  keyed-mutex ring, and compositor import are created once and reused for the life
  of the playlist. The measured CPU/GPU spike and the rebuild freeze disappear for
  same-format playlists — exactly the kiosk's case.
- **Gapless by construction (with preroll).** The next item's open + first-decode
  cost is paid off the hot path; the boundary is "open the gate on a frame that is
  already decoded," not "open a file."
- **It reuses, rather than duplicates, the decode/graph machinery.**
  `SubstrateSession` is the per-item runtime unchanged in its decode shape; the new
  code is orchestration + the clock/format refinements.
- **ADR-0044, ADR-0058, ADR-0059 keep working unchanged.** Sinks are still never
  disposed by the session. The shared OpenAL device/context (ADR-0058) is what
  makes a single long-lived audio sink across the playlist clean. ADR-0059's
  per-session no-consumer discard is re-evaluated correctly per item because each
  item re-opens its own demux — a silent item with a muted sink still drains its
  audio to keep the pump flowing; a sink-less item discards at the demuxer.
- **`RepeatMode.One` is untouched** — it still loops the current item in place via
  the existing seek-to-zero (`PlaybackControllerCore.cs:812-827`).

### Negative / trade-offs

- **Preroll briefly doubles decode resources.** Between "next item opened" and
  "current item ended," two demuxers + two decoder sets are co-resident. With
  hardware decode that is two D3D11VA codec sessions at once — non-trivial on a weak
  HD 620. Mitigations: `PlaylistPrerollMode.NextItem` opens the next item only
  within a short window before EOS (not at item start), and
  `PlaylistPrerollMode.None` keeps the warm-presenter win while accepting a small
  open-gap at the boundary. The mode is a knob, not a fixed policy.
- **New public surface.** `IMediaPlaylistPlayer` + factory + `PlaylistTransition`
  + `RepeatMode.All` + `PlaylistPrerollMode`. Justified: there is no playlist
  vocabulary today and the single-source `IMediaPlayer` cannot express it.
- **The controller's "immutable loaded snapshot" assumption is relaxed.** ADR-0028
  §6 caches `MediaInfo`/`Duration` as immutable post-load
  (`PlaybackControllerCore.cs:73-79,686-687`). A playlist mutates the current item,
  so a boundary callback must refresh that snapshot. This is a deliberate, narrow
  loosening of that invariant for the playlist session, not a removal — single-source
  playback keeps the immutable-snapshot behavior.
- **A small amount of `SubstrateSession` surgery.** The master-clock selection must
  move from "audio sink is `IClockSource`?" (constructor,
  `SubstrateSession.cs:139-148`) to "does this item have an activated audio stream?"
  (resolved after the stream probe in `InitializeAsync`). This is load-bearing for
  mixed-audio playlists and fixes a latent single-item case, but it touches a
  careful file.
- **Format-change reconfigure is new presenter code.** Replacing the `??=` latch
  with a size-compare + rebuild path in `CompositionInteropVideoView` is localized
  but must get keyed-mutex teardown ordering right (dispose the ring + processor,
  keep the device + interop import).

### Neutral

- **A/V sync policy (ADR-0003) is unchanged** — audio-bearing items master on the
  audio clock, silent items on the wallclock. Only *when* that selection is made
  (per item vs once per session) changes.
- **No change to the decode protocol or the seek discipline** (ADR-0048/0055/0056):
  per-item seek delegates to the current `SubstrateSession.SeekAsync`
  (`SubstrateSession.cs:488-580`) unchanged.
- **CPU presenter** (`FrameFlowVideoView` / `AvaloniaVideoSink`) already recreates
  its `WriteableBitmap` lazily on size mismatch
  (`src/FrameFlow.Avalonia/AvaloniaVideoSink.cs` `OnFormatChangedAsync` remarks), so
  the mixed-resolution change is GPU-presenter-specific.

## Alternatives considered

### A. First-class playlist session + preroll (accepted)

Chosen — see Decision. Solves the problem at the layer that owns the presenter/source
separation, keeps the controller and decode machinery, and closes the boundary gap
with preroll.

### B. Consumer-side reuse only — no FrameFlow change

Keep one `CompositionInteropVideoView` + sink + audio sink alive for the whole
signage session and pass the same sink to each new `MediaPlayer.CreateAsync`; only
the controller + session churn. This is *partially supported today* (the sink is
not owned by the session, so re-feeding works for same-format items) and is the
cheapest possible change.

Rejected as the primary answer because it is "less bad," not "right":
- The serial dispose → reopen → warmup **gap persists** — the freeze shortens but
  does not go away, and the open cost stays on the hot path (no preroll).
- It **breaks on a format change** between items (the `??=` latch, blocker 4) with
  no central place to fix it.
- It pushes fragile lifecycle sequencing (warm-sink reuse, idempotent-dispose
  ordering, the `??=`-latched view) onto **every** consumer, who must rediscover it.
- It leaves FrameFlow with no playlist vocabulary, so the next consumer rebuilds
  per item again.

It remains a valid *interim* mitigation a consumer can apply before this ADR lands,
and its feasibility is the proof that the warm-sink core is sound.

### C. Preroll / double-buffer as a standalone mechanism

Preroll is necessary but not sufficient on its own; it is a *property of A's
implementation* (§3), not a separate option. One sink suffices because the hand-off
is atomic at a frame boundary — the incoming first frame replaces the outgoing last
frame. A second surface would only be needed for a **crossfade** between items,
which is an explicit non-goal for v1 (it would require two live presenters and a
compositor blend, a much larger change).

### D. Extend the controller with a source queue (no new session type)

Teach `PlaybackControllerCore` itself to hold a queue and swap `_session`
internally. Rejected: it would thread playlist concerns through the 522-line,
three-state-machine controller (`PlaybackControllerCore.cs`), entangling queue/preroll
policy with seek-drain, repeat, and error orchestration. Modeling the playlist as an
`IPlaybackSession` keeps that machine intact and confines playlist logic to one new
type behind a contract the controller already speaks.

### E. Orchestrate a sequence of *controllers* above the controller

A `PlaylistPlayer` that owns warm sinks and drives one full `PlaybackController` per
item, disposing/promoting controllers at boundaries. Rejected: it churns the
controller (its dispatch loop, three state machines, position ticker, observable
subjects) per item, briefly runs two controllers, and makes preroll awkward —
all to avoid a new session type that is the cleaner seam. The sinks would stay warm,
but everything above them would not.

### F. Extract a `SourceRuntime` from `SubstrateSession`

Instead of reusing whole `SubstrateSession` instances as per-item runtimes, factor
the demux + decoders + graph + pacing out of `SubstrateSession` into a reusable
`SourceRuntime` that `PlaylistSession` composes directly. Cleaner separation
long-term (the playlist would not carry per-item gate/EOF/clock-selection
machinery it does not need), but a larger refactor of a load-bearing file. Deferred:
start by reusing `SubstrateSession` (option A); graduate to a `SourceRuntime`
extraction if the per-item-session overhead or the clock-selection coupling proves
awkward in implementation.

## Consumer impact: a signage playlist view

> **Provenance.** The per-item rebuild below is an **observation** of a
> downstream application outside this repository, made when this ADR was
> written. That consumer's source cannot be cited here, and nothing in this
> repository verifies that it still behaves this way — treat it as recorded
> history, not as a checkable claim. What *is* checkable: the cost of the
> shape is measured in §Context above, and the replacement this ADR shipped
> is in-tree — `src/FrameFlow.Player/MediaPlaylistPlayer.cs` and
> `PlaylistMediaPlayerCore.cs`, pinned by
> `tests/FrameFlow.Playback.Tests/PlaylistIntegrationTests.cs` and consumed by
> `examples/FrameFlow.Examples.AvaloniaPlayer`. Those references show what
> replaced the shape, not that any consumer still exhibits it.

Today, per item: build a new `CompositionInteropVideoView` + sink, a new
`OpenAlAudioSink`, and a new `MediaPlayer`; play; on `Ended` raise an
item-completed event; a teardown call disposes everything; the playlist
service advances by rebuilding.

After adopting this ADR, the heavy objects move **out of the per-item loop**:

- Build the `CompositionInteropVideoView` + video sink **once**, build the audio
  sink **once**, and create **one** `IMediaPlaylistPlayer` via
  `MediaPlaylistPlayer.CreateAsync(initialQueue, videoSink, audioSink, …,
  initialRepeatMode: RepeatMode.All)`.
- Replace the `Ended`→item-completed→teardown→rebuild cycle with a
  `SourceTransitioned` subscription: on transition, update the UI/playlist model and
  `EnqueueAsync` the next rotation item (or `SetNextAsync` to steer the preroll
  target when the rotation changes). No view/sink/player teardown per item.
- The three existing audio modes map directly and are decided **once**, not per
  item: `None` → pass `audioSink: null` (silent items, wallclock pacer);
  `Muted`/`Audible` → attach one muted/unmuted `OpenAlAudioSink` for the whole
  playlist. The consumer logic that today reasons about per-item audio-sink
  construction collapses to a single up-front choice.
- The consumer's "advance on completion → render → teardown → rebuild" cycle
  becomes "on `SourceTransitioned`, enqueue next." The rotating/looping behavior is
  preserved; the rebuild is what's removed.

The kiosk keeps its current safety properties: each item still re-opens its own
demux, so ADR-0059's discard/drain still applies per item, and ADR-0058's shared
OpenAL device makes the one long-lived signage audio sink (alongside the attract
sink) safe.

*(No consumer changes are made as part of this ADR; this section records the
intended adoption.)*

## Validation (proposed)

When implemented, the design should be validated with the existing testability
seams (ADR-0007) rather than UI-driven tests:

- **Playlist session, deterministic** — a fake/controllable video sink + audio sink
  (the existing capturing sinks) and a small two-/three-item fixture playlist:
  assert that across N item boundaries the **sink instance is identical** (never
  re-created) and the sink's underlying GPU-resource handle count does not grow;
  assert the controller's `OnEndOfStream` fires **only** at the tail with
  `RepeatMode.Off`, and **never** with `RepeatMode.All` over K wraps.
- **Clock rebature** — assert the position clock returns to ~zero at each boundary
  and the per-item master clock is the audio sink for audio-bearing items and a
  wallclock for silent items in the same playlist (mixed-audio fixture).
- **Preroll** — assert the next item's first frame is available at the boundary with
  `PlaylistPrerollMode.NextItem` (no open on the hot path), and that
  `PlaylistPrerollMode.None` still reuses the warm sink (just with an open-gap).
- **Format change** — a mixed-resolution fixture: assert the GPU converter/ring are
  rebuilt exactly once at the changing boundary and the device + interop import are
  not.
- **ADR-0059 regression** — a playlist mixing an audio-bearing item and a sink-less
  silent item plays to completion without the demux-pump-starvation freeze.

## Implementation

The core warm-swap playlist (option A, without preroll) landed on
`feat/gapless-playlist-impl`:

- **`FrameFlow.Media/RepeatMode.cs`** — added `All` (loop the whole playlist).
- **`FrameFlow.Playback`:**
  - `RepeatTrigger.SelectAll` + `PlaybackControllerCore` repeat machine wired for
    `All`; the end-of-stream guard now ends a single-source player unless repeat
    is `One` (so `All` on a single source behaves like `Off`).
  - `SubstrateSession` master-clock selection moved from the constructor into
    `InitializeAsync`, so it follows whether *this* item has an activated audio
    stream (audio sink when it does, a wallclock otherwise) — the per-item
    selection that lets one warm audio sink span a mixed audio/silent playlist,
    and which also removes the latent video-only-with-audio-sink stall.
  - `PlaylistCoordinator` (internal) — the shared queue / loop / current-item /
    transition bridge between the player surface and the session.
  - `PlaylistSession` (internal `IPlaybackSession`) — composes per-item
    `SubstrateSession` runtimes over the same warm sinks + clock; intercepts
    per-item end-of-stream to advance, rebases the position clock per item, skips
    faulting items with a consecutive-failure spin guard, and bubbles the real
    end-of-stream only when the queue is exhausted and not looping.
  - `PlaylistSessionFactory` + `PlaybackController.CreatePlaylist(...)` — the
    controller entry point that drives a `PlaylistSession`.
  - `PlaylistTransition` (public) — the hand-off notification.
- **`FrameFlow.Player`:** `IMediaPlaylistPlayer` (public) + `MediaPlaylistPlayer.CreateAsync`
  + `PlaylistMediaPlayerCore`, mirroring the warm-sink `MediaPlayer.CreateAsync`
  shape and reading per-item `MediaInfo`/`Duration` from the coordinator.

Validated by `PlaylistCoordinatorTests` (12 deterministic unit tests of the
queue/loop/wrap/skip logic) and `PlaylistIntegrationTests` (end-to-end over real
corpus: three skip-driven source swaps prove the same video sink instance is
reused across every boundary and is never disposed — the warm-presenter property
— with a `RepeatMode.All` wrap and `Playing` maintained throughout).

**Not yet implemented (follow-ups):** `PlaylistPrerollMode.NextItem` (open +
first-frame-decode the next item before the boundary, for a fully gapless
hand-off) and the `CompositionInteropVideoView` format-change reconfigure (rebuild
the `VideoProcessor` + keyed-mutex ring when a same-warm-device item changes
resolution). Both are isolated additions on top of the landed core; same-format
playlists already pay no per-item presenter rebuild.

### Update (2026-06-21): single-clip same-source reuse — the gapless single-clip loop

The single most common consumer shape — **one** clip on `RepeatMode.All` (a
signage attract/panel loop) — is now
gapless without preroll. The boundary at a single-clip loop wrap (and at any
`RepeatMode.One` boundary, and any back-to-back duplicate) lands on the *same
source object*; `PlaylistCoordinator.DecideNext` detects that by reference identity
and returns `NextKind.Replay`, and `PlaylistSession.AdvanceLockedAsync` services it
by **reusing the live `SubstrateSession` in place via its cheap rewind**
(`RewindToStartAsync` → the retained graph re-runs on the *same decode device*)
rather than disposing and rebuilding it. Nothing is torn down, the decode device
never changes, and — composing with ADR-0064 (the converter owns its own device and
keys its decode-bridge on decode-device identity) — **the presenter never rebinds**.

Measured on the GPU presenter (`examples/FrameFlow.Examples.AvaloniaPlayer
--presenter gpu --no-audio`, single-file folder, NVIDIA dev box): ~24 natural-EOS
loops produced **zero** `device_change_rebind`s and a loop period equal to the clip
duration (no boundary freeze). The multi-item rebuild path, by contrast, rebinds
the converter once per boundary (one decode device per item). Guarded by
`PlaylistCoordinatorTests` (same-source wrap → `Replay`, reference-identity gating)
and a `PlaylistIntegrationTests` natural-EOS cadence/no-freeze assertion.

**Multi-item gapless is still deferred — and is now understood to need more than
preroll.** A *different-source* boundary builds a fresh decode runtime on a fresh
decode device, so the converter rebinds (ADR-0064) on every multi-item hand-off.
Preroll alone moves the open + first-decode off the hot path but does **not** move
that rebind off it (the converter can only bind one decode device at a time, and
the current item is still presenting on the old one until the instant of hand-off).
Truly gapless multi-item therefore requires keeping the **decode device stable
across passes** — which re-opens ADR-0064's deliberately-rejected sub-option (b)
(share/inject one decode device into every per-item FFmpeg runtime). That is a
real architectural decision (it inverts ADR-0064's "FFmpeg owns the decode device"
stance for the playlist path) and its only user-visible payoff is on weak iGPUs
like the kiosk's Intel HD 620 — on a fast GPU the rebuild boundary is already
sub-frame. It is **deferred pending that device-ownership decision (warranting its
own ADR) and on-kiosk HD-620 validation**, neither of which is reproducible on the
NVIDIA dev box. Tracked in `docs/DEFERRED_WORK.md`.

## References

- `src/FrameFlow.Player/MediaPlayer.cs` — `CreateAsync`, the warm-sink construction shape this design mirrors.
- `src/FrameFlow.Player/IMediaPlayer.cs` / `MediaPlayerCore.cs` — the single-source surface the playlist surface extends.
- `src/FrameFlow.Playback/PlaybackControllerCore.cs` — the state machine, single `_session`, `Load`-from-`Idle`/`Unloaded`, replay-from-`Ended`, `RepeatMode.One` loop, cached media snapshot.
- `src/FrameFlow.Playback/SubstrateSession.cs` — the per-item runtime: demux/decoder/graph wiring, `WarmUpAsync` (preroll shape), per-session clock selection, and the `DisposeAsync` that never disposes sinks.
- `src/FrameFlow.Playback/SubstrateSessionFactory.cs` — captures sinks once, fresh session per load.
- `src/FrameFlow.Playback/WallClockSource.cs` / `PlaybackClock.cs` / `src/FrameFlow.Graph/IClockSource.cs` — the two clocks and their rebase primitives.
- `src/FrameFlow.Media/IVideoSink.cs` / `IAudioSink.cs` / `RepeatMode.cs` — sink-ownership contract, audio lifecycle, repeat modes.
- `src/FrameFlow.Avalonia.Windows/CompositionInteropVideoView.cs` / `CompositionInteropVideoSink.cs` / `D3D11Nv12SharedConverter.cs` / `D3D11BgraUploader.cs` — the warm GPU resources and the first-frame format latch.
- ADR-0044, ADR-0058, ADR-0059, ADR-0003, ADR-0028, ADR-0035, ADR-0057, ADR-0021 — the decisions this design composes with.
