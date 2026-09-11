# One Builder, Two Terminals

## Status

**Draft, pending number assignment.** `IPlayerBuilder` gains
`BuildPlayerAsync`, returning `IMediaPlayer`, alongside the existing
`BuildAsync`, returning `PlayerSession`. The four options that only a player can honour —
repeat mode, injected clock, hardware-frame yield, audio activation —
return a narrower `IMediaPlayerBuilder` whose only terminal is
`BuildPlayerAsync`.

`MediaPlayer.CreateAsync` stays as the positional factory with an
unchanged signature.

**Date:** 2026-09-11
**Related:**
- ADR-0024 (playback controller as public API surface)
- ADR-0043 (consumer-configurable pipeline operators)
- ADR-0044 (sink ownership and disposal)
- Issue #99

## Context

Three construction surfaces existed, and the pleasant one returned the
weakest object.

| Scenario | Entry point | Returns |
|---|---|---|
| App or host playback | `MediaPlayer.CreateAsync(...)` | `IMediaPlayer` |
| Open a file, play to end | `FrameFlowPlayer.Open(...).BuildAsync()` | `PlayerSession` |
| Drive the state machine | `PlaybackController.Create(...)` | `IPlaybackController` |

`PlayerSession` is single-shot. It has no pause, resume, seek, repeat,
`Position`, `StateChanged` or diagnostics. `MediaPlayer.CreateAsync` has
all of it and is eleven positional parameters with no builder at all. Its
own doc comment steered readers away from itself, and the README needed a
three-row table to arbitrate.

A consumer reaching for the fluent API got the crippled object, and
discovering the limit meant rewriting the construction site against a
positional static call.

## Decision

One builder. Two terminals.

```csharp
await using var player = await FrameFlowPlayer.Open(path)
    .WithAudioSink(audio)
    .WithVideoSink(video)
    .WithRepeatMode(RepeatMode.All)
    .BuildPlayerAsync(ct);          // IMediaPlayer
```

`BuildAsync` still returns `PlayerSession` for the play-to-EOS case.
Everything before the terminal is the same chain.

### The narrowing, and why it is not a runtime check

Repeat mode, an injected clock, hardware-frame yield and audio activation
are properties of the `IMediaPlayer` pipeline. They mean nothing to a
`PlayerSession`, which opens a source and runs it to end of stream.

Three ways to handle an option that one terminal cannot honour:

1. Accept it on the shared builder and ignore it on `BuildAsync`.
2. Accept it and throw from `BuildAsync` when it was set.
3. Return an interface from which `BuildAsync` is not reachable.

(1) is the failure this ADR exists to prevent. A consumer sets
`WithRepeatMode(RepeatMode.All)`, calls `BuildAsync`, gets a session that
plays once, and has nothing to read that explains why.

(2) reports the same mistake, but at run time, on a code path the
consumer has to actually execute. It also leaves the invalid chain
expressible, so the API still reads as though the combination were
meaningful.

(3) makes it a compile error. The chain narrows to `IMediaPlayerBuilder`,
where `BuildPlayerAsync` is the only terminal. The mistake cannot be
written down.

### What the narrowing costs

`IMediaPlayerBuilder` repeats the shared options — sinks, configurators,
hardware-decode policy, logging — so a chain keeps flowing after the
narrowing step in either order. `PlayerBuilder` implements the narrow
interface explicitly over the same mutable state; the signatures differ
from `IPlayerBuilder`'s only by return type, so one of the two must be
explicit.

The real price is on extension methods. `WithOpenAlAudio` and
`WithAvaloniaVideoView` each need an `IMediaPlayerBuilder` overload, and
so would any downstream extension written against `IPlayerBuilder`. Two
overloads of a two-line method is the cost of turning a class of silent
misconfiguration into a compile error. Accepted.

A self-typed generic base (`IPlayerBuilderBase<TSelf>`) would remove the
duplication and let extensions be written once. It was rejected as more
clever than clear: generic inference on extension methods over a
self-typed interface is awkward to write and worse to read in an IDE's
completion list.

### `CreateAsync` keeps its signature

`PlaybackController.Create` accepts an `IPlaybackClock`;
`MediaPlayer.CreateAsync` hardcoded `clock: null`, so `WithClock` had
nothing to forward to.

The clock does not become a twelfth parameter on `CreateAsync`. That
signature is published in the 0.7 and 0.8 alphas: an inserted optional
parameter breaks existing positional calls at source and existing
compiled callers at load, and appending it after `cancellationToken` to
avoid that trips CA1068.

Instead `CreateAsync`'s body moved to an internal `CreateCoreAsync` that
takes the clock. The public factory forwards with `clock: null`; the
builder calls the core directly. `WithClock` is the one builder option
the positional factory cannot express.

## Consequences

**Breaking, deliberately.** `IPlayerBuilder` gains five abstract members,
so any external implementation of it stops compiling. Callers are
unaffected at source and at load — adding members to an interface changes
no existing call site or method token.

The interface has exactly one implementation, `internal sealed class
PlayerBuilder`, reachable only through `FrameFlowPlayer.Open`. There is
no registration point, no DI seam and no factory hook through which a
consumer's own implementation could be returned by this library, so an
external implementer would be writing a type nothing can consume.

Default interface implementations were the alternative and are worse
here. `BuildPlayerAsync` cannot be implemented generically — it needs the
builder's own state — so a DIM would have to throw
`NotSupportedException`, trading a compile error for a runtime one.

Under 0.x SemVer the breaking position is MINOR, so this lands in v0.9.0.

`WithLogger` now takes `ILoggerFactory?` and treats null as a no-op. It
threw before, which broke the chain for a conditional step;
`FrameFlow.Examples.AudioOnlyPlayer` had reassigned the builder to a
local to work around it.

Not addressed here: the examples still call `CreateAsync` positionally,
and `LatenessRecoveryOptions` remains reachable only through
`PlaybackController.Create`.
