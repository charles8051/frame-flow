# ADR-0035: Split `IMasterClock` out of `IAudioSink`

## Status

Accepted. Core architecture landed in commit
[`f6edb75`](../../) — `IMasterClock` interface, `IAudioSink :
IMasterClock` inheritance, `WallClockMasterClock`, `WallClockAudioSink`
deprecation, and the contract test suite. DI-wiring migration (sync
strategies depending on `IMasterClock` explicitly via constructor
injection rather than inherited interface satisfaction) is tracked as a
follow-up; the current pipeline annotation makes the role explicit at
the call site without a constructor change.

> **Note 2026-05-16.** The `WallClockAudioSink : IAudioSink` sketch
> in §"Context" was deprecated rather than implemented; no concrete
> wall-clock audio sink ships today. If a future use case revives the
> idea, the implementation will follow Crossbar ADR-0010 conventions
> — standalone `IAsyncDisposable` with `FrameConsumer<IAudioBuffer> Consumer { get; }`
> exposed via a constructor-cached `Consumer = PresentAsync;`.

> **Note 2026-08-26.** `IMasterClock` no longer exists. The decision this
> ADR records — extract the clock from `IAudioSink` so pacing couples to
> "a clock" rather than to "an audio sink" — stands and shipped. The type
> carrying it does not match the name, namespace, or shape used below:
>
> - **Name and namespace.** `FrameFlow.Media.IMasterClock` shipped as
>   `FrameFlow.Graph.IClockSource`, in the substrate forked from Crossbar
>   per ADR-0049.
> - **Shape.** The single synchronous pull `TimeSpan GetPlaybackTime()`
>   became a latest-value-cached signal: `TimeSpan Latest` plus
>   `ValueTask WaitUntilAsync(TimeSpan target, CancellationToken)`. This is
>   the pull-to-signal refinement sketched in
>   `docs/decoding-pipeline-proposed.html`, which has since landed.
> - **Inheritance.** `IAudioSink : IMasterClock` did not survive.
>   `IAudioSink : IAsyncDisposable` carries no clock; a sink that owns one
>   implements `IClockSource` directly, as `OpenAlAudioSink` does.
> - **Wall-clock implementation.** `WallClockMasterClock` shipped as
>   `FrameFlow.Playback.WallClockSource`, which also implements
>   `ISeekableClock`.
>
> Read every `IMasterClock` below as `IClockSource` with those differences
> in mind. ADR-0065 §Context already records the rename correctly.

## Context

ADR-0003 establishes audio as the master clock when audio is present, with a
wallclock fallback for video-only playback. ADR-0034 introduced diagnostics
surfaces and raised a related question: are master-clock reads (per-frame, hot
path, single-purpose) and diagnostics snapshots (low-frequency, observability)
really the same role on the same interface? That question turned out to have a
clean answer at the *API* level — two distinct methods on the same type,
sharing the same lock — but it surfaced a deeper conflation that *this* ADR
addresses.

The conflation has been visible in the codebase for a while in the form of
`WallClockAudioSink`:

```csharp
// FrameFlow.Sdl/WallClockAudioSink.cs
public sealed class WallClockAudioSink : IAudioSink
{
    // ...
    public ValueTask WriteAsync(PcmAudioBuffer audioBuffer, …)
    {
        // audio data is consumed and discarded
        return PaceTo(audioBuffer);
    }

    public TimeSpan GetPlaybackTime() => _wallClock.Elapsed;
}
```

`WallClockAudioSink` is an `IAudioSink` by declaration, but it has nothing to
do with audio output — every buffer it receives is discarded. It exists
because the only way to be a *master clock* is to be an *audio sink*, since
`GetPlaybackTime()` lives on `IAudioSink`. The class's docstring acknowledges
this directly: *"This is a timing adapter, not an audio output backend."*

That's a category error encoded in the type system. The cost shows up as:

- Anyone reading `WallClockAudioSink` has to mentally untangle "this is an
  audio sink that doesn't output audio."
- Video-only paths instantiate an `IAudioSink` that wastes work (pacing audio
  buffers, tracking sample counters) just to surface a `Stopwatch.Elapsed`.
- A future non-audio master clock — e.g., a network-timing-based clock for
  live RTSP streams, or a frame-rate clock for replay tests — would need to
  go through the same fiction.
- The sync strategy and pipeline controller couple to `IAudioSink` for clock
  reads when they only need clock reads. `AudioMasterSyncStrategy` is named
  for the *policy* (audio is the master) but its dependency is the
  *interface* (`IAudioSink`), not the role (master clock).

Per ADR-0027's public-surface discipline, this is the kind of inherited
ambiguity worth fixing before it propagates into more consumers.

## Decision

### Extract `IMasterClock` as a focused interface

```csharp
namespace FrameFlow.Media;

/// <summary>
/// Provides the reference timeline used by A/V sync strategies (ADR-0003,
/// ADR-0035). One <see cref="IMasterClock"/> is active per playback session;
/// the implementation depends on what the session is doing — an audio sink
/// when audio is present, a wallclock when not, conceivably other sources
/// (frame-rate clock, network timestamp, jamming clock for live streams) in
/// the future.
/// </summary>
public interface IMasterClock
{
    /// <summary>
    /// Returns the master timeline's current position. Called on the video
    /// worker's hot path (~per decoded frame); implementations must be cheap
    /// and thread-safe. The contract permits a non-monotonic answer across
    /// seek/pause/resume — sync strategies are responsible for reasoning
    /// about discontinuities — but it must be monotonic during steady-state
    /// playback within a single play episode.
    /// </summary>
    TimeSpan GetPlaybackTime();
}
```

### `IAudioSink` composes `IMasterClock`

Audio sinks naturally serve both roles — they have the sample counter that
gives the best continuous time source per ADR-0003:

```csharp
public interface IAudioSink : IMasterClock, IAsyncDisposable
{
    // existing surface — GetPlaybackTime is inherited from IMasterClock
}
```

No new method on `IAudioSink`. Existing implementations continue to satisfy
both interfaces with one method. The hot-path call site that previously read
`_audioSink.GetPlaybackTime()` can either keep doing that (`IAudioSink` is-a
`IMasterClock`) or take `IMasterClock` as a narrower parameter.

### `WallClockAudioSink` becomes `WallClockMasterClock`

The class moves out from under `IAudioSink` and stops pretending to receive
audio. The renamed type:

- Implements `IMasterClock` only
- Has no `WriteAsync`, no `Capabilities`, no audio buffer handling
- Owns only its `Stopwatch` and the lifecycle hooks needed to start, pause,
  resume, and reset the wallclock relative to playback events
- Lives in a location that's not audio-specific — likely
  `FrameFlow.Playback.WallClockMasterClock`, since it's a playback-level
  primitive, not a backend adapter

The old name remains as a `[Obsolete]` shim for one release pointing at the
new type, with a note that the audio-sink role was a category mistake fixed
by ADR-0035.

### Pipeline composes the right `IMasterClock` per session

`PipelineController` (and the DI registration that builds it) take an
`IMasterClock` instead of pulling one off `IAudioSink`. Composition lives
where the load decision is made:

```
session has audio  ─▶ register the audio sink, expose it as IMasterClock
session is video-only ─▶ register WallClockMasterClock as IMasterClock
```

`AudioMasterSyncStrategy` keeps its name (the *policy* is still
"audio-master-when-present") but couples to `IMasterClock` for reads:

```csharp
public sealed class AudioMasterSyncStrategy : ISyncStrategy
{
    private readonly IMasterClock _clock;
    public AudioMasterSyncStrategy(IMasterClock clock) { _clock = clock; }

    public TimeSpan GetVideoDelay(TimeSpan framePts) =>
        TimeSpan.FromTicks(Math.Max(0, (framePts - _clock.GetPlaybackTime()).Ticks));
}
```

The `GetVideoDelay(framePts, referenceTime)` overload that takes a caller-
supplied reference becomes unnecessary — strategies pull from the clock
they're composed with. That's a knock-on cleanup, not a precondition.

### `ControllableClockSyncStrategy` becomes the canonical test seam

The existing `ControllableClockSyncStrategy` already takes an injectable
`IPlaybackClock` and reads `_clock.Position` for sync decisions. It's a peer
of `IMasterClock` in spirit. After this ADR, the natural shape is for
`IPlaybackClock` (controllable position) to also implement `IMasterClock`
(read-only position) — possibly via a thin adapter. Tests inject a
`FakeMasterClock` directly; production injects the audio sink or
`WallClockMasterClock`.

## Consequences

### Positive

- **`WallClockAudioSink` stops lying about its role.** The class name matches
  what it does. Reviewers reading video-only test setups stop being confused
  by "why are we registering an audio sink for a video-only stream."
- **Sync code couples to the narrower contract.** `AudioMasterSyncStrategy`
  depends on `IMasterClock`, not the whole `IAudioSink` surface (lifecycle
  methods, capabilities, write path, diagnostics). Easier to reason about,
  easier to test in isolation.
- **Future master clocks land cleanly.** A network-timing master for RTSP, a
  frame-rate master for video-only files where wallclock pacing isn't ideal,
  or a jam-clock for live streams — none of those need to pretend to be
  audio sinks.
- **ADR-0003 is reinforced, not changed.** The audio-master policy is
  unchanged; only the type that expresses "master clock" changes.

### Negative

- **One more interface in `FrameFlow.Media`.** Each new interface is a small
  tax on the public surface (ADR-0027 cares about this). Mitigated: the new
  interface is one method; it's strictly an extraction of an existing role,
  not a new concept.
- **`WallClockAudioSink` rename is a breaking change for anyone depending on
  the type directly** (rare — it's an SDL-example wiring detail). The
  `[Obsolete]` shim absorbs the immediate breakage.
- **DI wiring changes shape.** Today's wiring registers an `IAudioSink` and
  the pipeline pulls master-clock reads off it. Tomorrow the wiring
  registers an `IMasterClock` explicitly (the audio sink can satisfy both
  registrations). One-time refactor.

### Neutral

- **No perf change.** Same method, same call site, same lock. The interface
  is purely a typing improvement.
- **Diagnostics surface (ADR-0034) unaffected.** `GetDiagnostics()` stays on
  `IAudioSink` — it's an audio-sink concern, not a master-clock concern. The
  diagnostics snapshot's `PresentationTime` field continues to mirror what
  `IMasterClock.GetPlaybackTime()` returns for audio sinks, because they
  share an implementation under the same lock.

## Alternatives considered

### Keep the status quo (master clock on `IAudioSink`)

Rejected because `WallClockAudioSink` is the existing evidence that the
conflation actively misleads readers. The cost grows as more master-clock
variants get added.

### Move `GetPlaybackTime()` onto `ISyncStrategy`

Rejected because strategy and clock answer different questions:

- **Clock:** "What time is it on the master timeline right now?"
- **Strategy:** "Given a frame PTS and the clock's answer, how long should I
  wait?"

Strategy is policy; clock is a reading. Coupling them means every strategy
needs to know how to *generate* the reference time (which is platform-
specific — sample counters, wallclock, RTSP NPT — and not strategy-specific).

### Put master clock on `IPlaybackController`

Rejected because the controller doesn't *have* a master clock — it composes
one. Putting `GetMasterTime()` on the controller would force the controller
to be the thing that knows about audio vs. wallclock vs. future variants,
inverting the dependency. The controller should depend on `IMasterClock`,
not be one.

### Make `IMasterClock` implement `IPlaybackClock` directly

`IPlaybackClock` already exists for the controllable test clock. They are
conceptually close (both report a position) but differ in mutability —
`IPlaybackClock` exposes `Start`/`Resume`/`SetPosition`, which a master clock
should not. Keeping them separate honors that asymmetry; an adapter can
bridge them where useful.

## Implementation

Sequenced bottom-up so each step is independently testable.

1. **Add `IMasterClock`** in `FrameFlow.Media`. One method, full docstring.
2. **Make `IAudioSink` inherit `IMasterClock`.** No method moves; the
   existing `GetPlaybackTime()` is now satisfying the inherited member. All
   current implementations continue to compile unchanged.
3. **Add `WallClockMasterClock`** in `FrameFlow.Playback` (or
   `FrameFlow.Media` if a layered home makes more sense — call out in PR).
   Extracts the wallclock state from `WallClockAudioSink`, drops the audio
   write/pace/sample-counter code.
4. **`[Obsolete]` `WallClockAudioSink`** with a pointer to the new type.
   Keep functional behavior for one release so consumers that wired it as
   `IAudioSink` don't break instantly.
5. **Switch sync strategies to take `IMasterClock`.** `AudioMasterSyncStrategy`
   gains a constructor parameter; the existing two-arg `GetVideoDelay` falls
   back to the injected clock.
6. **Update DI wiring.** Register `IMasterClock` explicitly:
   - When `IAudioSink` is registered: register the same instance as
     `IMasterClock`.
   - Otherwise: register `WallClockMasterClock` as `IMasterClock`.
7. **`PipelineController` reads master time from `IMasterClock`** instead of
   reaching into `IAudioSink`. The audio-sink reference stays for lifecycle
   (`ActivateAsync` / `PauseAsync` / etc.).
8. **Integration tests cover the video-only path** using
   `WallClockMasterClock` directly, not through `WallClockAudioSink`.
9. **Remove `WallClockAudioSink`** in a follow-up release once consumers have
   migrated.

## References

- ADR-0003: Audio-master sync policy (the policy this ADR refines without
  changing)
- ADR-0027: Public API surface cleanup
- ADR-0028: Internal layering and ownership cleanup
- ADR-0034: Diagnostics surfaces — the question that surfaced this one
- `FrameFlow.Sdl/WallClockAudioSink.cs` — the smoking gun
