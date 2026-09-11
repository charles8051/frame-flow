# ADR-0069: One error model across the playback stack

## Status

Accepted (2026-09-11).

Resolves the split recorded in issue #102. Extends the `Result` type introduced
alongside [ADR-0032](ADR-0032-pull-shape-playback-controller.md) up one layer,
to `IMediaPlayer`. Nothing is superseded — `IPlaybackController` keeps the
contract it already had, and this decision is about the surface above it.

### What shipped

| | |
| --- | --- |
| `IMediaPlayer` transport returns `Task<Result>` | `src/FrameFlow.Player/IMediaPlayer.cs` |
| `IMediaPlayer.ErrorOccurred` | same, forwarded from the controller |
| `MediaPlayerCore` / `PlaylistMediaPlayerCore` pass through | `src/FrameFlow.Player/` |
| `PlayerCommand` — run a transport command from a control, report the outcome | `src/FrameFlow.Avalonia/PlayerCommand.cs` |

## Context

### Two layers of one stack disagreed

`IPlaybackController` returns a result type:

```csharp
Task<Result> LoadAsync(IMediaSource source, CancellationToken cancellationToken = default);
Task<Result> PlayAsync(CancellationToken cancellationToken = default);
Task<Result> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
```

`IMediaPlayer` wrapped that same controller and threw:

```csharp
Task PlayAsync(CancellationToken cancellationToken = default);
Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
```

The conversion sat in a private helper on each of the two implementations:

```csharp
private static void ThrowIfFailed(Result result, string op)
{
    if (result.IsSuccess)
        return;
    var err = result.Error;
    throw new InvalidOperationException(
        $"{op} failed: {err?.Category} — {err?.Message}",
        err?.Inner);
}
```

`ErrorCategory` is structured, and this flattened it into a string. A caller
who wanted to tell "the source cannot seek" from "the player is disposed" had
to parse a message.

`Result`'s own doc comment says to prefer it "for expected failure paths
(invalid state transitions, user-initiated operations on disposed objects,
etc.)". A seek on a non-seekable source is exactly that, and one layer up it
became an exception.

### Nobody was catching

The clinching evidence was on the consuming side. Every `IMediaPlayer` call in
the Avalonia chrome was wrapped in a bare `catch { }` — five in
`FrameFlowTransportBar`, one in `FrameFlowPlayerChrome`, plus two
fire-and-forget seeks from the key handler.

So a refused seek did not reach the user, did not reach a log, and did not
reach telemetry. Neither did any genuine bug in the chrome, which the same
`catch { }` swallowed. The exception model was not merely inconvenient at that
boundary; it was producing silence.

`IPlaybackController` also has `IObservable<PlaybackError> ErrorOccurred` and
`IMediaPlayer` did not expose it, so a consumer holding only the player surface
had no structured error channel at all — not for commands, and not for
failures arising mid-stream.

## Decision

**`IMediaPlayer` returns `Task<Result>` from its four transport commands, and
gains `ErrorOccurred`.**

The two implementations become pass-throughs; `ThrowIfFailed` is deleted from
both.

### What still throws

`Result` is for outcomes the state machine can refuse. It is not a general
replacement for exceptions, and three things stay as they are:

- **Argument validation.** A null source or a NaN volume is a caller bug, not a
  playback outcome. `ArgumentNullException` and friends are correct.
- **Construction.** `MediaPlayer.CreateAsync` throws when it cannot build a
  player. A factory that fails has no object to hand back, and a
  `Result<IMediaPlayer>` would put every consumer's `await using` behind a
  branch. Whether the fluent builder should differ is
  [#99](https://github.com/charles8051/frame-flow/issues/99)'s question, not
  this one.
- **Whatever escapes a sink or the decode stack.** Those are not outcomes the
  controller chose to report.

### What `IMediaPlaylistPlayer` does

`EnqueueAsync`, `SetNextAsync` and `SkipToNextAsync` keep returning `Task`.
They write to the coordinator and complete synchronously; they have no refusal
to report, and a `Result` that is always `Ok` is a value the caller has to
handle for nothing. `Result` marks the commands that can be refused, and
uniformity that dilutes that signal costs more than it buys.

### What the chrome does with a refusal

An Avalonia control has no injected `ILogger` — the XAML loader constructs it.
It logs through `Avalonia.Logging.Logger`, which is what such a control has and
which every host in this repository already enables with `.LogToTrace()`.
`PlayerCommand` is the one place that knows this:

- a refused `Result` logs at `Warning`
- an exception that escapes logs at `Error`

The `try`/`catch` does not disappear, because these run from `async void`
handlers where an escaping exception takes the process down rather than
reaching a caller. What changes is that it now distinguishes the two cases and
records both, instead of discarding them together.

One call site reports through a latch rather than on every command: the seek
bar's scrub dispatcher. A scrub emits seeks continuously, so reporting each
refusal turns one gesture into a burst of identical warnings, while reporting
none leaves a source that has a duration but refuses seeking
indistinguishable from one that works. The latch gives the first refusal and
clears on the next success, which re-arms it.

A control whose visual state runs ahead of the command needs one thing more.
The loop toggle flips on click, and repeat mode has no observable on this
surface to resynchronise from, so a refusal would leave it advertising a mode
the player never adopted. `PlayerCommand.FireAndForget` takes an optional
`onRefused` for exactly that. Play, pause and stop need nothing: their buttons
follow `StateChanged`, and a refused command produces no transition.

## Alternatives considered

### `IMediaPlayer` keeps throwing, gains a typed `PlaybackException`

Issue #102's Option A, and the smaller change: throw a `PlaybackException`
carrying the `PlaybackError` instead of an `InvalidOperationException` with a
formatted string, and add `ErrorOccurred` so the category survives.

Rejected because it does not fix what was actually broken. The chrome's six
`catch { }` blocks catch a typed exception exactly as thoroughly as an untyped
one. A consumer has to know to catch, and the type system does not tell them —
the same property that makes exceptions right for unexpected failures makes
them wrong for expected ones. `Result` in the signature is the thing a reader
cannot miss.

It also leaves the two layers reading differently for no remaining reason.

### Add `ErrorOccurred` only

The minimum: give the player surface the structured channel it lacks and change
no signature.

Rejected on its own, and folded into this decision instead. An observable is
the right home for a failure with no caller waiting on it; it is the wrong home
for a command's own answer, because it arrives detached from the call that
caused it and correlating the two is the caller's problem.

### Push `Result` further down, into the sinks

Out of scope. `IVideoSink.PresentAsync` and `IAudioSink.PresentAsync` are
hot-path calls on a pacing chain, and a struct return per frame is a different
trade from a struct return per user gesture.
[ADR-0066](ADR-0066-the-sink-contract-shape.md) owns that contract.

## Consequences

### Good

- One error model from `IPlaybackController` through `IMediaPlayer`. The layers
  read the same way, and the wrapper stops discarding `ErrorCategory`.
- A refused command is visible. The chrome logs it; the examples log it and
  show it in their status text.
- Genuine bugs in the chrome now surface. They used to share a `catch { }` with
  every refused seek.
- `IMediaPlayer` has a structured error channel for mid-stream failures for the
  first time.

### Bad

- Breaking change for anyone calling the four transport methods. Pre-1.0, and
  `README.md` states the surface moves freely, but the failure mode is quiet:
  `await player.PlayAsync();` still compiles and now discards the outcome
  instead of throwing. A caller relying on the throw loses their error path
  without a compiler error.

  The public API baseline in
  [#106](https://github.com/charles8051/frame-flow/issues/106) is what would
  make this kind of change loud in review. It is not in place yet.

- `Result` is a `readonly record struct`, so a caller who ignores it pays
  nothing and learns nothing. `[MustUseReturnValue]`-style enforcement is not
  in the BCL; an analyzer would be a separate piece of work.

### Neutral

- `IMediaPlaylistPlayer` now has a mixed surface: `Task<Result>` for transport,
  `Task` for queue management. Deliberate, per *What `IMediaPlaylistPlayer`
  does* above.

## Not settled here

- Whether the chrome should surface a refusal to the **user** rather than only
  to the log. The transport bar has no error affordance, and inventing one
  belongs with the chrome design debt in
  [#19](https://github.com/charles8051/frame-flow/issues/19)–[#21](https://github.com/charles8051/frame-flow/issues/21).
- Whether `Result<T>` should gain `TryGetValue` / `Deconstruct` / implicit
  conversions. Tracked in
  [#101](https://github.com/charles8051/frame-flow/issues/101). `Result<T>` has
  no usages in the repository yet, so there is nothing to shape them against.
- Whether the fluent builder's terminal should return `Result<IMediaPlayer>`.
  That is [#99](https://github.com/charles8051/frame-flow/issues/99)'s
  question.
