# ADR-0027: Public API Surface Cleanup

**Status:** Proposed
**Date:** 2026-04-12
**Supersedes:** Partially supersedes ADR-0021 §1 (options-based loop configuration)
**Related:** ADR-0008 (result types), ADR-0021 (looped playback strategy), ADR-0023 (hierarchical state machine), ADR-0024 (PlaybackController as public API surface)

## Context

With the playback pipeline now stable and all media features functional, the public API surface warrants scrutiny before consumers build against it. Several issues have emerged from examining the contracts, options types, enums, and observables that `IPlaybackController`, `IPlaybackControllerFactory`, and the DI builder expose.

### Dead options

Every property in `FrameFlowPlaybackOptions` and `FrameFlowVideoOptions` is declared, tested for round-trip binding through DI, but never consumed by any production code:

| Option | Default | Read by production code? |
|--------|---------|--------------------------|
| `Playback.AutoPlay` | `false` | No |
| `Playback.UseAudioAsMasterClock` | `true` | No |
| `Playback.Loop` | `false` | No |
| `Playback.MaxLoopCount` | `0` | No |
| `Video.EnableVideo` | `true` | No |
| `Video.EnableFrameDropping` | `true` | No |

A consumer writes `options.Playback.AutoPlay = true`, the code compiles, the option resolves through DI, but nothing happens. This is actively misleading — worse than the option not existing at all.

### Loop/Repeat dualism

ADR-0021 specified options-time loop configuration (`Loop`, `MaxLoopCount`). ADR-0024 introduced `IPlaybackController` with a runtime `RepeatMode` state machine and `SetRepeatModeAsync`. These are two independent mechanisms for the same behavior. The runtime `RepeatMode` FSM is what actually works. The options-based properties were never wired to the controller. Consumers reasonably expect `Loop = true` to make playback loop — it does not.

### Speculative enum values

`RepeatMode.All` is documented as "Loop the entire playlist (future — reserved for playlist support)." It is a fully wired FSM state, but its behavior is identical to `RepeatMode.One` (seek to zero). There is no playlist concept in the codebase. Shipping a value whose behavior is either undefined or a duplicate is confusing for consumers.

### Transient states in PlaybackState

`Initializing`, `Preparing`, and `InitialBuffering` are auto-chained through within a single `LoadAsync` call. They are observable via `PlaybackStateChanged` but no command is valid in these states and consumers can never meaningfully react to them. Every state observer must handle five loading substates that resolve in microseconds.

`Destroyed` has no corresponding public command — it is only reachable internally during `DisposeAsync`. Consumers see it in the enum but cannot transition to it through the public API.

### IsPlaying semantic trap

`IsPlaying` returns `State == Playing && SeekingState == NotSeeking`. This means `State == PlaybackState.Playing` and `IsPlaying` can disagree during a seek. Consumers who check either one in isolation get subtly different answers about whether playback is active. The name `IsPlaying` suggests it matches the `Playing` state, but it is actually a compound predicate over two state machines.

### Observable subscription verbosity

`IPlaybackController` uses raw `IObservable<T>` without System.Reactive. Every consumer must implement `IObserver<T>` and manage `IDisposable` subscriptions manually. The SDL example requires custom `ActionObserver<T>` and `CompositeDisposable` helper classes. This boilerplate will be duplicated by every consumer.

### StateTransition.TriggerName as string

`StateTransition<T>.TriggerName` is a stringified internal enum (`PlaybackTrigger.Play.ToString()`). Consumers reacting to specific triggers must do string comparisons against names they cannot see at compile time. An internal rename silently breaks consumer code.

### Decoder DI registration leaks internal shapes

Consumers register decoder factories as `Func<IDemuxSession, IVideoDecoder?>` and audio sinks as `Func<IAudioSink>` — raw function types that are internal implementation details of `PlaybackSessionFactory`. There is no `AddFrameFlowDecoding()` extension method.

## Decision

### 1. Remove dead options from FrameFlowPlaybackOptions and FrameFlowVideoOptions

Remove all option properties that are not consumed by production code:

- Remove `FrameFlowPlaybackOptions.AutoPlay`
- Remove `FrameFlowPlaybackOptions.UseAudioAsMasterClock`
- Remove `FrameFlowPlaybackOptions.Loop`
- Remove `FrameFlowPlaybackOptions.MaxLoopCount`
- Remove `FrameFlowVideoOptions.EnableVideo`
- Remove `FrameFlowVideoOptions.EnableFrameDropping`

If `FrameFlowPlaybackOptions` or `FrameFlowVideoOptions` become empty after removal, retain the empty classes as placeholders for future options. The DI sub-option binding in `AddFrameFlow` should be updated to remove the field-copy logic for deleted properties.

**Reinstatement rule:** These options may be re-added when the implementation that reads them is implemented in the same changeset. Options must never be added ahead of behavior.

### 2. Unify loop control under RepeatMode

The runtime `RepeatMode` FSM is the single mechanism for repeat/loop behavior. The options-based `Loop` / `MaxLoopCount` properties from ADR-0021 §1 are superseded.

If configuration-time initial repeat mode is desired, introduce a single option:

```csharp
public sealed class FrameFlowPlaybackOptions
{
    /// <summary>
    /// Initial repeat mode applied when a controller is created.
    /// Can be changed at runtime via
    /// <see cref="IPlaybackController.SetRepeatModeAsync"/>.
    /// </summary>
    public RepeatMode InitialRepeatMode { get; set; } = RepeatMode.Off;
}
```

The controller reads this at construction time and sets its initial state machine state accordingly. This eliminates the dualism — there is one concept (repeat mode) with one runtime control and one optional initial value.

### 3. Remove RepeatMode.All

Remove `RepeatMode.All` from the public enum. The repeat state machine simplifies to two values:

```csharp
public enum RepeatMode
{
    /// <summary>No looping — playback ends at end-of-stream.</summary>
    Off,

    /// <summary>Loop the current media item indefinitely.</summary>
    One,
}
```

`RepeatMode.All` may be re-introduced when playlist support is implemented. At that point it will have distinct, testable behavior.

Remove the corresponding `RepeatTrigger.SelectAll` trigger and `RepeatMode.All` FSM configuration.

### 4. Collapse transient loading states in PlaybackState

Replace `Initializing`, `Preparing`, and `InitialBuffering` with a single `Loading` value:

```csharp
public enum PlaybackState
{
    Idle,
    Loading,       // was: Initializing → Preparing → InitialBuffering
    Paused,
    Playing,
    Rebuffering,
    Ended,
    Stopped,
    Error,
}
```

The controller's internal state machine may still use sub-states for its own dispatch, but the public `PlaybackState` enum and the `PlaybackStateChanged` observable emit only the collapsed values. Consumers see one `Idle → Loading` transition and one `Loading → Paused` transition per load.

Remove `Destroyed` from the public enum. It is an internal disposal detail. `DisposeAsync` transitions to `Stopped` (if not already stopped) before tearing down resources. The `Destroyed` state, if retained internally, is never surfaced through `State` or `PlaybackStateChanged`.

### 5. Rename IsPlaying to IsActivelyPresenting

Rename the compound predicate to clearly communicate its semantics:

```csharp
/// <summary>
/// Returns <see langword="true"/> when media frames are actively being
/// rendered — the primary state is <see cref="PlaybackState.Playing"/>
/// and no seek operation is in progress.
/// </summary>
bool IsActivelyPresenting { get; }
```

This eliminates the confusion between `State == PlaybackState.Playing` (which is true during seeks) and the compound "frames are actually flowing" predicate. Consumers who want the raw state check it directly; consumers who want the rendering-active signal use `IsActivelyPresenting`.

### 6. Remove TriggerName from StateTransition

Change `StateTransition<T>` to:

```csharp
public readonly record struct StateTransition<T>(T Previous, T Current)
    where T : struct, Enum;
```

Consumers should react to state pairs (`Previous → Current`), not to internal trigger names. The trigger name was a debugging convenience that leaked implementation details. Diagnostic trigger information remains available in structured logs (ADR-0010).

### 7. Add observable convenience extensions

Provide a minimal set of subscription helpers in the public API so consumers do not need to implement `IObserver<T>` or manage composite disposables:

```csharp
public static class PlaybackObservableExtensions
{
    /// <summary>Subscribe with a delegate, returning a disposable subscription.</summary>
    public static IDisposable Subscribe<T>(
        this IObservable<T> source, Action<T> onNext);

    /// <summary>Subscribe with separate next and error delegates.</summary>
    public static IDisposable Subscribe<T>(
        this IObservable<T> source, Action<T> onNext, Action<Exception> onError);
}
```

These are intentionally minimal — not a replacement for System.Reactive. Consumers who want full Rx can reference it directly and use its native extension methods.

### 8. Add AddFrameFlowDecoding builder extension

Provide a DI extension method that registers the decoding layer's internal factory shapes:

```csharp
public static IFrameFlowBuilder AddFrameFlowDecoding(this IFrameFlowBuilder builder)
{
    // Registers IDemuxSessionFactory, video/audio decoder factories
    // with sensible defaults.
}
```

The raw `Func<IDemuxSession, IVideoDecoder?>` registrations become internal details of this method. Consumers call `services.AddFrameFlow().AddFrameFlowDecoding().AddFrameFlowPlayback()` without needing to know about factory function shapes.

## Pushback

### "Removing options is a breaking change."

FrameFlow is pre-release. The options exist in code and tests but have zero runtime effect. Removing them is not a behavior change — it is a documentation correction. Consumers who set these options today are already experiencing silent no-ops.

### "Collapsing PlaybackState loses diagnostic granularity."

The auto-chained substates (`Initializing → Preparing → InitialBuffering`) transition within a single `await LoadAsync()` call. No consumer can observe or react to them individually in practice. If fine-grained loading progress is needed in the future, it should be exposed through a dedicated `LoadingProgress` observable rather than multiplexed onto the primary state machine.

### "Removing RepeatMode.All blocks future playlist support."

Adding an enum value is a non-breaking change. Removing it now and re-adding it with real behavior later is preferable to shipping a value that either duplicates `One` or has undefined semantics. Consumers who encounter `All` today will reasonably ask what it does — and the answer is "the same thing as One, but for a playlist feature that doesn't exist."

### "IsActivelyPresenting is verbose."

It is. But `IsPlaying` is misleading, and misleading is worse than verbose. The compound semantics (Playing AND not seeking) deserve a name that signals the compound nature. Alternatives considered: `IsRendering`, `IsPresentingFrames`, `IsStreaming`. `IsActivelyPresenting` was chosen because it aligns with the sink contract terminology (`PresentAsync`) used throughout the codebase.

## Consequences

### Positive

- Options that consumers set will actually affect behavior. No more silent no-ops.
- One mechanism for repeat/loop eliminates confusion about which one "works."
- Consumers handle fewer states in observers and switch statements.
- The `IsActivelyPresenting` name prevents a class of subtle bugs.
- Observable subscription becomes a one-liner instead of a custom class.
- Decoder DI registration is guided rather than raw.

### Negative

- Removing options, enum values, and renaming properties are source-breaking changes. This is acceptable for a pre-release library.
- Removing `TriggerName` from `StateTransition` removes a debugging aid from the public surface. Mitigated by structured logging.
- The `AddFrameFlowDecoding` extension method adds a new public type to the API surface.

### Neutral

- Internal state machine configuration is unchanged — the controller can still use fine-grained sub-states internally.
- ADR-0008's `Result` type and `ErrorCategory` enum are unchanged.
- ADR-0003's sync strategy is unchanged.
- `IPlaybackController`'s command methods (`LoadAsync`, `PlayAsync`, etc.) are unchanged.
- `IPlaybackControllerFactory` is unchanged.

## Compliance Checklist

When implementing this ADR, verify:

- [ ] All removed option properties have no production-code readers (confirmed by reference search)
- [ ] `FrameFlowPlaybackOptions.InitialRepeatMode` is read by `PlaybackController` at construction time
- [ ] `RepeatMode.All` and `RepeatTrigger.SelectAll` are removed from source and tests
- [ ] `PlaybackState` public enum has exactly: `Idle`, `Loading`, `Paused`, `Playing`, `Rebuffering`, `Ended`, `Stopped`, `Error`
- [ ] `PlaybackStateChanged` never emits `Initializing`, `Preparing`, `InitialBuffering`, or `Destroyed`
- [ ] `IsActivelyPresenting` replaces `IsPlaying` on `IPlaybackController`
- [ ] `StateTransition<T>` no longer carries `TriggerName`
- [ ] `PlaybackObservableExtensions.Subscribe` overloads are public and tested
- [ ] `AddFrameFlowDecoding` registers decoder factories without exposing `Func<>` shapes to consumers
- [ ] All examples compile and function correctly after the changes
- [ ] DI sub-option binding in `AddFrameFlow` no longer copies deleted option fields
