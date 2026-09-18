# ADR-0044: Sink Ownership and Disposal

## Status

**Accepted.** Replaces the `Func<IAudioSink>` factory pattern with
direct `IAudioSink` singleton registration; codifies idempotent
`DisposeAsync` as a contract requirement on `IAudioSink` and
`IVideoSink`; makes `PlaybackSession` a borrower of sinks rather
than an owner. This is a breaking change to public DI conventions
and several `FrameFlow.Media` types; per the project stance
recorded as the project stance - FrameFlow is pre-1.0 with no external
consumers, so breaking changes land inline without shims - the change
lands without migration shims.

> **Amended 2026-09-17 (#168).** Two things below stopped being true, and the record kept
> teaching them. The *Decision* and *Implementation* sections are left as they were written, as
> this repository's records are; what they got wrong is corrected here and at each of the two
> sections, and the **Decision summary** carries the current rule.
>
> **1. The DI provider is not the canonical owner. Whoever constructs a sink owns it.** The
> container owns the sinks it registered, and a caller who constructs one owns that. Both are the
> same rule with a different owner. The fluent builder stands up no container at all — the DI
> registration the *Layer 2 PlayerBuilder* section describes was removed with `MediaPlayerCore`
> (#224) — and `IVideoSink` and `IAudioSink` have said "owned by their DI container or by their
> immediate caller" since ADR-0044 shipped, which is the rule the code has followed throughout.
>
> **2. One sink may serve several players, in sequence.** The *Layer 2* section forbids it:
> "Do not reuse the same sink instance across multiple players." ADR-0062's gapless playlist is
> built on doing exactly that, and it is why nothing is rebuilt at an item boundary. The
> constraint that remains is narrower: a sink serves one player at a time. Reuse after a fault is
> untested, which is #29.
>
> A player, a pass and a session are users. They call `ActivateAsync` and `DeactivateAsync`, and
> never `DisposeAsync`. That part of the record held.
>
> **What the drift cost.** Alternative B below predicted it: a factory "creates sinks the caller
> never sees and cannot dispose". `WithOpenAlAudio` was later written to do precisely that,
> constructing a sink inside the player layer and handing it to something that by rule would not
> dispose it, and no caller could reach it either. It leaked an OpenAL device per player until
> #275 retired it. The reasoning here was right; the rule was written down in a place nobody read
> and in terms that had stopped matching the code.

**Date:** 2026-05-13
**Supersedes:** Implicit ownership conventions established by
ADR-0005 (native resource ownership) for the sink case; no other
ADR is wholly superseded.
**Related:**
- ADR-0005 (native resource ownership rules)
- ADR-0024 (playback controller as public API surface)
- ADR-0025 (video sink and frame pool architecture)
- ADR-0036 (decoded media stream decoupled from playback)
- ADR-0043 (consumer-configurable pipeline operators)

## Context

Two patterns have coexisted for sink registration since the
early FrameFlow design:

- **Video sinks** register as DI singletons (`AddSingleton<IVideoSink>(...)`).
  The DI provider disposes them when the container is torn down.
  `PlaybackSession` never disposes the video sink — the constructor
  comment is explicit: *"the video sink is a shared singleton and
  is NOT disposed here."*

- **Audio sinks** register as `Func<IAudioSink>` factories
  (`AddSingleton<Func<IAudioSink>>(sp => () => new OpenAlAudioSink(...))`).
  `ServiceProviderPlaybackSessionFactory` invokes the factory at
  session construction and passes the resulting instance to the
  session. `PlaybackSession.DisposeAsync` explicitly disposes the
  audio sink — the comment is explicit: *"the audio sink is
  created per session by the factory and is session-owned."*

The design intent behind the factory shape was per-session fresh
sinks — each playback session would get its own freshly-constructed
sink, isolated from any others. In practice no consumer creates
multiple sessions per DI container, so the factory pattern is a
degenerate one-shot in every use today.

Layer 2's `PlayerBuilder.BuildAsync` makes the degeneracy
explicit: it holds a single sink instance and wraps it in a
factory closure to satisfy the playback layer's expectation:

```csharp
var captured = _audioSink;
services.AddSingleton<Func<IAudioSink>>(_ => () => captured);
```

The captured sink is never disposed by the DI provider (the
container only knows about the `Func`, not the instance).
Disposal still happens — `PlaybackSession.DisposeAsync` invokes
it — but the path is asymmetric with video and depends on the
session knowing it should dispose what the factory produced.

This pattern works today but exhibits several smells:

1. **Asymmetric ownership.** Audio and video sinks behave
   identically from a consumer's perspective (constructed once,
   used until player teardown, then released), but their
   disposal paths diverge. Reading the code, it is not obvious
   who disposes the audio sink without tracing through three
   layers of indirection.

2. **Factory shape obscures lifecycle.** The `Func<IAudioSink>`
   contract implies "fresh sink per call" semantics. Consumers
   that close over a single instance (every consumer in
   practice) silently violate the implied contract; consumers
   that genuinely create fresh sinks per invocation would leak
   under the asymmetric Layer 2 wrapping.

3. **Layer 1 / Layer 2 inconsistency.** Direct
   `IPlaybackController` consumers (test harnesses, integration
   tests) register `Func<IAudioSink>` and rely on session-side
   disposal. Layer 2 `FrameFlowPlayer` consumers register
   `WithAudioSink(IAudioSink)` and rely on transitive disposal
   via the same session path. Same end behavior, two different
   mental models at the registration site.

4. **No way to safely realize the original factory intent.**
   If a future scenario actually needs per-session-fresh sinks
   (concurrent sessions per app, sink-pool strategies), the
   current `Func<IAudioSink>` registration provides no caller-side
   handle for tracking. Each invocation produces an instance the
   caller never sees. Designing for that future requires
   replacing the factory contract anyway.

5. **Hidden dependency on dispose idempotency.** The asymmetry
   only works because no path double-disposes. Any registration
   change that adds `IAudioSink` to DI alongside `Func<IAudioSink>`
   would expose latent non-idempotency in sink implementations.
   The contract that prevents this from breaking is implicit, not
   stated.

The architectural question is whether to evolve the existing
model (preserve `Func<IAudioSink>`, add explicit ownership
markers, accept the asymmetry) or replace it. With no external
consumers and no migration constraints, the
correct move is to replace.

## Decision

### Sink ownership

> Amended: **whoever constructs a sink owns it** — the container for what it registered, the
> caller for what they built. See the amendment at the top of this record.

**The DI provider is the canonical owner of sink lifecycle.**
Sinks register as singletons (`AddSingleton<IAudioSink>(...)` or
`AddSingleton<IVideoSink>(...)`). When the DI container is
disposed, the sinks are disposed. There is exactly one disposal
path; there is exactly one owner.

**`PlaybackSession` and `PipelineController` are users, not
owners.** They invoke `ActivateAsync` / `DeactivateAsync` to
coordinate sink state across session lifecycle transitions
(start, pause, terminal teardown), but they do not call
`DisposeAsync` on sinks. Activate/Deactivate are state operations
that happen many times in a session's life; Dispose is an
ownership operation that happens exactly once.

### Sink contract: idempotent disposal

`IAudioSink.DisposeAsync` and `IVideoSink.DisposeAsync`
implementations **must support idempotent disposal**. Calling
`DisposeAsync` more than once must be a no-op (no throw, no
side effects, no resource access).

This is already implicit in `IAsyncDisposable` best practice
("Dispose patterns should be safe to call multiple times"); the
ADR codifies it at the sink contract level. Implementations
typically use a `_disposed` flag with an early return.

The idempotency contract removes the "who disposes first"
anxiety that motivated the asymmetric ownership in the first
place. Multiple paths *could* attempt disposal without
correctness consequences; in practice only one path actually
runs.

### Registration shape: direct singleton, no factory

**Drop `Func<IAudioSink>` entirely.** The factory shape carried
water for a multi-session design that nobody uses; with that
gone, direct singleton registration is the right shape.

Consequences for the public surface:

- **`IAudioSinkFactory` interface (FrameFlow.Media)** — deleted.
- **`FrameFlowBuilder.UseAudioSinkFactory(Func<IAudioSink>)`** —
  deleted. Replaced by `UseAudioSink(IAudioSink)`, symmetric with
  the existing `UseVideoSink`.
- **`FrameFlowDecodingOptions.AudioSinkFactory`** — deleted. The
  decoding options no longer expose audio sink configuration;
  registration happens through DI extensions.
- **`AddFrameFlowOpenAlAudio` (DI extension)** — rewritten to
  register `IAudioSink` as a singleton implementation of
  `OpenAlAudioSink`. The DI provider constructs the instance
  lazily on first resolution and disposes it on provider
  teardown.
- **`AddFrameFlowOpenAlAudio` (standalone builder extension)** —
  unchanged in intent, updated to call the new
  `UseAudioSink(IAudioSink)` method.

### PlaybackSession lifecycle

`PlaybackSession.DisposeAsync` no longer disposes the audio
sink. The teardown sequence becomes:

1. Cancel pumps; wait for them to drain.
2. Call `_audioSink?.DeactivateAsync(...)` to release device
   contexts and stop hardware pipelines. (State operation;
   reversible by future `ActivateAsync` calls if the sink were
   to be reused — though in practice the session is single-use.)
3. Dispose the decoded media stream, the owned clock source
   (when the session created its own wallclock), and any other
   session-internal resources.
4. **Do not** dispose the audio sink or the video sink. The DI
   provider that owns them will dispose them when it tears down.

### Layer 2 PlayerBuilder

> Amended: none of this section describes the code. The per-player DI container went with
> `MediaPlayerCore` (#224); `PlayerBuilder` stands one up nowhere. The quoted XML doc was never
> written onto either builder interface, and its two claims are both wrong now — the player does
> not dispose the sink, and reusing one sink across players in sequence is what the playlist
> player is for (ADR-0062). See the amendment at the top of this record.

`PlayerBuilder.BuildAsync` registers caller-provided sinks
directly:

```csharp
if (_audioSink is not null)
    services.AddSingleton<IAudioSink>(_audioSink);
if (_videoSink is not null)
    services.AddSingleton<IVideoSink>(_videoSink);
```

Symmetric, no factory wrapping. The provider tracks both for
disposal; `MediaPlayer.DisposeAsync` (which disposes the
provider) cleans up both.

`WithAudioSink(IAudioSink)` and `WithVideoSink(IVideoSink)`
keep their existing surface. The XML doc now states the
contract explicitly: *"Ownership transfers to the player; the
player will dispose this sink when the player is disposed. Do
not reuse the same sink instance across multiple players."*

### Layer 1 ServiceProviderPlaybackSessionFactory

```csharp
// Before
var audioSinkFactory = _serviceProvider.GetService<Func<IAudioSink>>();
audioSink: audioSinkFactory?.Invoke(),

// After
var audioSink = _serviceProvider.GetService<IAudioSink>();
audioSink: audioSink,
```

Mechanically identical to the video sink resolution; the
asymmetry disappears.

### Test fixtures

Integration test harnesses register sinks as `IAudioSink`
singletons directly. The harness already holds the sink
reference (for post-playback assertions on captured frames),
so no API change is needed at the harness call site — only the
registration shape changes:

```csharp
// Before
services.AddSingleton<Func<IAudioSink>>(() => audioSink);

// After
services.AddSingleton<IAudioSink>(audioSink);
```

Post-playback assertions still work: the harness reads the
captured frames from its retained reference before disposing
the DI provider, after which the sink is disposed.

## Alternatives considered

### A. Explicit ownership marker (Level 2 from the prior conversation)

Keep `Func<IAudioSink>`. Add a `transferOwnership: bool`
parameter to `WithAudioSink`. Track ownership in `PlayerBuilder`
and dispose owned sinks at player teardown. Rejected: the
underlying asymmetry between audio and video remains; the
factory shape continues to carry water for a multi-session
design no one uses; tests still need to know two different
registration patterns. More code, more state, same asymmetry.

### B. Caller-owns everywhere (Level 3 from the prior conversation)

`PlaybackSession` disposes nothing it didn't construct.
Consumers (DI containers, direct callers) are responsible for
all sink lifecycle. Same destination as the divergent answer
*if* `Func<IAudioSink>` is also retired; substantially worse
*if* it's kept (because the factory creates sinks the caller
never sees and cannot dispose). The divergent answer is this
plus the factory removal, which is the right combination.

### C. Reference-counted sinks (`IAddRefDisposable` pattern)

Sinks become `IAddRefDisposable` like Crossbar frames. Every
consumer `AddRef`s; last release disposes. Rejected: real
multi-owner scenarios for sinks don't exist (sinks are
heavyweight, long-lived, bound 1:1 to a player or session).
Refcounting overhead and use-after-free risk are not justified
by the use case. Reconsider if sinks ever genuinely have
multiple owners.

### D. Facade with lifecycle managed elsewhere

A `SinkFacade` type the player references stably; the underlying
sink lifecycle lives in a separate registry. Rejected: adds
indirection without resolving the ownership question. The
"elsewhere" still has to own disposal, and the facade hides the
contract rather than clarifying it. Pattern-mismatch for the
problem.

### E. Status quo with documentation

Update XML docs to make the asymmetric ownership contract
explicit; change no code. Rejected: paper-only fix. The
asymmetry remains in code, the smells remain in the
registration shape, and Layer 1 / Layer 2 mental models stay
divergent. With no migration cost under that stance,
fixing the underlying design is cheaper than documenting around
it.

## Consequences

### Positive

- **Single ownership model across audio and video.** The mental
  model collapses to "register as DI singleton, provider disposes
  on container teardown." Symmetric across sink types, symmetric
  across Layer 1 and Layer 2.
- **Standard .NET disposal semantics.** Constructor-injected
  dependencies are borrowed, not owned. PlaybackSession aligns
  with the conventional reading of constructor-injected lifetimes.
- **Smaller public surface.** `IAudioSinkFactory`,
  `UseAudioSinkFactory`, `AudioSinkFactory` configuration
  property, `Func<IAudioSink>` registration convention — all
  retired. Less to learn, less to maintain, less to document.
- **Idempotent dispose as a stated contract** removes a class
  of latent bugs. Any future change that adds a second disposal
  path becomes safe by default.
- **Test fixtures simplify.** Registration shape changes from
  `Func<IAudioSink>(() => sink)` to `IAudioSink(sink)`.
  Mechanical search-and-replace.

### Negative

- **Breaking change to several public types.** Acceptable under
  the no-consumers-yet stance, but worth noting that this
  changes the published-API picture for any future first
  release.
- **Per-session-fresh-sink capability is removed.** No
  consumer uses it today, but if a future scenario needs it
  (concurrent multi-session players, pooled sink strategies)
  it will need to come back as a deliberate design — likely a
  pool/registry interface, not a `Func<T>` factory.
- **Implementations must audit dispose idempotency.** The
  existing sinks (`OpenAlAudioSink`, `AvaloniaVideoSink`,
  `SdlVideoSink`, `NullVideoSink`) need a quick check; most
  already have `_disposed` flags. Test doubles
  (`HarnessAudioSink`, `CapturingAudioSink`,
  `CapturingVideoSink`) need the same audit.

### Neutral

- **`ActivateAsync` / `DeactivateAsync` semantics unchanged.**
  These remain session-level state operations. The session
  still calls `DeactivateAsync` during terminal teardown; only
  `DisposeAsync` ownership moves.
- **Clock-source-from-audio-sink behavior unchanged.** When
  the audio sink implements `IClockSource`, the session still
  uses it as the master clock. The clock stops advancing when
  the audio sink is deactivated; disposal happens later (DI
  provider teardown) but no consumer reads from a deactivated
  clock.

## Implementation

The refactor lands in a single commit-set targeting only this
change. No mixing with feature work; no migration shims; no
`[Obsolete]` markers.

1. **Sink contract update.** Add idempotency note to xmldoc on
   `IAudioSink`, `IVideoSink`. Audit existing sink
   implementations for `_disposed` flag; add where missing.
2. **Delete the factory surface.** `IAudioSinkFactory`,
   `UseAudioSinkFactory`, `AudioSinkFactory` property,
   `Func<IAudioSink>` registrations across DI extensions.
3. **Add `UseAudioSink(IAudioSink)` to `FrameFlowBuilder`** —
   symmetric with `UseVideoSink`.
4. **Rewrite `AddFrameFlowOpenAlAudio`** (DI + standalone) to
   register `IAudioSink` as a singleton.
5. **Update `PlayerBuilder.BuildAsync`** — direct `IAudioSink`
   singleton registration.
6. **Update `ServiceProviderPlaybackSessionFactory.CreateSession`** —
   resolve `IAudioSink` directly.
7. **Update `PlaybackSession.DisposeAsync`** — remove the
   `_audioSink.DisposeAsync()` block; keep `DeactivateAsync`.
8. **Update integration harnesses and test fixtures** — change
   registration shape; remove any explicit post-controller
   sink disposal (DI provider now handles it).
9. **Build, run tests, verify.**

## Decision summary

**Current rule, as amended 2026-09-17.** Whoever constructs a sink owns it: the DI container for
what it registered, the caller for what they built. A player, a pass and a session are users —
they call `ActivateAsync` and `DeactivateAsync`, and never `DisposeAsync`. One sink serves one
player at a time and may serve several in sequence, which is what keeps a playlist's presenter
warm across items. `IAudioSink` and `IVideoSink` implementations must support idempotent
`DisposeAsync`.

*As written in 2026-05:* Sinks are DI-singleton resources. The DI provider owns
disposal. `PlaybackSession` uses sinks via Activate/Deactivate
but never disposes them. `IAudioSink` and `IVideoSink`
implementations must support idempotent `DisposeAsync`. The
`Func<IAudioSink>` factory pattern, `IAudioSinkFactory`
interface, and `UseAudioSinkFactory` method are retired without
migration shims. Symmetric ownership, single disposal path,
standard .NET semantics.
