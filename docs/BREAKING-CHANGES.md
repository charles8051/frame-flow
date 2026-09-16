# Breaking changes

FrameFlow is pre-1.0. `README.md` says the public surface moves freely between
releases, and it does. This file is what that promise owes you in return: the
list, with the fix for each one.

Entries are grouped by the release they ship in and ordered within it by how
likely you are to hit them. Each says what broke, what to write instead, and —
where it is not obvious — why the change was worth making.

**Read the first entry of any group carefully.** Most breaks here are compile
errors, which announce themselves. A few are not, and those are called out.

## Unreleased

### 1. A configurator without a sink is refused, and it used to run

**This one is not a compile error.** A consumer that called `ConfigureVideo` or
`ConfigureAudio` without registering a sink wired its own terminal inside the
configurator and returned an untouched chain as a placeholder. That mode is gone: the
configurator returns its chain open, and the builder terminates it at the registered
sink. Building a player without one now throws, naming the call that needs a partner.

```csharp
// Before: the configurator terminated, and the returned chain was ignored.
.ConfigureVideo(chain =>
{
    chain.Then(convert).To(myFanOutSink);
    return chain;
})

// After: register the sink, return the chain open.
.WithVideoSink(myFanOutSink)
.ConfigureVideo(chain => chain.Then(convert))
```

A consumer that needs more than one sink wires the extras on `Branch` edges inside the
configurator and returns its trunk open. `Branch` takes either an `EdgeConfig<T>`, for a branch
that clones, or an `EdgeOptions`, for one that takes its own ref; both require the options,
because a defaulted edge would be capacity-1 and blocking and would hold the trunk back frame
for frame. The branch's first edge is configured by `Branch`, so passing options to the `Then`
that follows it throws rather than silently winning or losing. `FrameFlow.Examples.Multicast` and
`FrameFlow.Examples.Multicast.Dml` show the shape: one delegate sink registered with
`WithVideoSink`, fanning out to three panes.

**Why.** The two modes decided different things about pacing. A configurator that
terminated itself ran behind an in-graph `PaceUntil`, which holds a decode-texture lease
across the clock wait; a single-sink graph runs upstream of `ClockSelectVideoSink`, which
does not. Keeping both meant the same lambda had different behaviour depending on whether
a sink happened to be registered. The full reasoning is in
`docs/adr/graph-chain-forks-joins-and-termination.md`.

**If your overlay drew from the configurator**, it is now upstream of the pacer and runs
ahead of the display. Key it off `IFramePresentedSource.FramePresented` on your sink,
which reports the frame that reached the screen. `FrameFlow.Examples.LiveCaptioning` does
this for its caption and detection overlays.

### 2. `ITimeSource` is gone; `PlaybackClock` takes a `TimeProvider`

`ITimeSource` was a one-member interface over `DateTimeOffset.UtcNow`, written
before `System.TimeProvider` existed. Every project here targets `net10.0`, where
`TimeProvider` covers the same ground and is what the rest of FrameFlow already
uses — `WallClockSource`, `HeadlessVideoSink`, `OpenAlAudioSink` and
`RecordingGate` all take one. Keeping both meant two abstractions for one
concept, and a caller had to know which subsystem picked which.

| Member | Before | After |
|---|---|---|
| `PlaybackClock(ITimeSource)` | `ITimeSource` | `System.TimeProvider` |
| `ITimeSource` | public interface | removed |
| `SystemTimeSource` | internal | removed |

```csharp
// Before
public sealed class FrozenClock : ITimeSource
{
    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
}
var clock = new PlaybackClock(new FrozenClock());

// After — no custom type needed
var time = new FakeTimeProvider();            // Microsoft.Extensions.TimeProvider.Testing
var clock = new PlaybackClock(time);
```

`new PlaybackClock()` is unchanged and still reads real wall-clock time; it now
passes `TimeProvider.System` rather than a `SystemTimeSource`.

`FrameFlow.Graph.IClockSource` is **not** affected and is not going anywhere. It
carries media time — a position on the presentation timeline that seeks, pauses
and is mastered by the audio device — which is a different axis from wall time
and one `TimeProvider` cannot express.

### 3. `IMediaPlayer.Diagnostics` is gone

The `IObservable<PlaybackDiagnosticsSnapshot>` on `IMediaPlayer` never emitted.
Both player implementations returned a subscription that did nothing, so any
code subscribed to it was already receiving nothing.

**This is a binary break as well as a source break.** Code that references
`Diagnostics` stops compiling, which announces itself. An assembly that was
compiled against an earlier `IMediaPlayer` and is not rebuilt does not: it still
calls `IMediaPlayer.Diagnostics.get`, and the method that makes that call throws
`MissingMethodException` when it runs. Rebuild anything that references the
member against this version. Once rebuilt, nothing changes at runtime.

ADR-0034 decided against a pushed snapshot stream: one rate chosen by the
library cannot suit every consumer, and a timed stream is polling moved inside
the player. The stub contradicted that decision.

```csharp
// Before — compiled, and never fired
_player.Diagnostics.Subscribe(render);

// After — poll on a timer you own, at the rate you need
var timer = new DispatcherTimer(
    TimeSpan.FromMilliseconds(500),
    DispatcherPriority.Background,
    (_, _) => render(_player.GetDiagnostics()));
timer.Start();
```

State changes, errors and loop stalls are discrete events that a poll can miss,
and they keep their observables: `StateChanged`, `ErrorOccurred`, `LoopStalled`.

If you **implement** `IMediaPlayer`, delete your `Diagnostics` property. Leaving
it compiles, but nothing reads it.

### 4. `null` hardware decode capabilities now probe, as documented

**Not a compile error.** Nothing you write changes; what runs does.

`PlaybackController.Create`, `PlaybackController.CreatePlaylist` and
`VideoDecoder.Open` documented a `null` `hardwareDecodeCapabilities` as
"re-probes". It did not: the decoder replaced `null` with
`HardwareDecodeCapabilities.Empty`, which is the set that forces software decode.
A caller passing `HardwareDecodeMode.Auto` or `Required` with no capabilities
decoded in software, or failed under `Required`, on hardware that could decode
(#181).

`null` now resolves to this process's hardware decode probe, the same result a
bootstrap reports, whenever the mode asks for hardware. `Disabled` never probes.

| Call | Before | After |
|---|---|---|
| `Create(hardwareDecodeMode: Auto)`, capabilities omitted | software decode | hardware where a backend binds |
| `Create(hardwareDecodeMode: Required)`, capabilities omitted | `HardwareDecodeUnavailableException` | hardware where a backend binds |
| any mode, `HardwareDecodeCapabilities.Empty` | software decode, or the exception under `Required` | unchanged |

`MediaPlayer.CreateAsync` and `MediaPlaylistPlayer.CreateAsync` already passed
their bootstrap's probed capabilities and are not affected.

If you relied on the old behaviour to keep decoding in software, say so
explicitly:

```csharp
// Before — null happened to mean software
var controller = PlaybackController.Create(videoSink: sink, hardwareDecodeMode: HardwareDecodeMode.Auto);

// After — ask for software directly
var controller = PlaybackController.Create(videoSink: sink, hardwareDecodeMode: HardwareDecodeMode.Disabled);
```

`DecoderFactories.CreateVideo` now accepts `null` capabilities with the same
meaning. That widens the parameter and breaks no existing call.

### 5. The playlist player reports failed items, and gives up on a run of them

**Not a compile error.** Nothing you write changes; what runs does.

`IMediaPlaylistPlayer` skipped an item that faulted while it played, or that
could not be started, and reported nothing. A faulted last item ended the
playlist as if it had played through. An item that faulted on every pass under
`RepeatMode.All` or `RepeatMode.One` was rebuilt and faulted again, forever
(#180).

| Case | Before | After |
|---|---|---|
| An item faults while playing, or an item after the first cannot be started | skipped, nothing raised | skipped, `ErrorOccurred` raised, state unchanged |
| The last item faults while playing under `Off` | `Ended` | `ErrorOccurred`, then `Ended` |
| More than eight items fail in a row | `Error` only if they failed to start; faults looped forever | `Error`, with an `ErrorOccurred` for each failure and one for giving up |

A first item that fails before anything has played is treated as a single
source's: one that cannot be opened still fails the load, and one that faults
before the first `PlayAsync` puts the player in `Error`. An item that
ends or is skipped without failing breaks a run, and so does a fault after an
item has played for five seconds, or for half its length if that is shorter. A
bad item in a rotation with items that play is reported on every pass and never
given up on.

**`ErrorOccurred` no longer means the player stopped.** On a single-source
player it still does. On a playlist player, check `State` before you treat an
error as terminal:

```csharp
// Before — every error was taken to be the end
player.ErrorOccurred.Subscribe(error => DisposePlayer());

// After — a playlist error may be an item it skipped
player.ErrorOccurred.Subscribe(error =>
{
    Log(error);
    if (player.State == PlaybackState.Error)
        DisposePlayer();
});
```

The state is `Error` by the time the give-up error is raised.

### 6. A fault in lateness recovery no longer stops playback

**Not a compile error.** Nothing you write changes; what runs does.

With `PlaybackController.Create(latenessRecovery: ...)` enabled, a fault inside
the recovery walk put the controller in `Error` and tore down the session. The
walk is an optimisation, so playback now carries on without it.

| Case | Before | After |
|---|---|---|
| The recovery walk faults | `ErrorOccurred`, `Error` | `ErrorOccurred`, state unchanged, playback continues at the rung the walk had reached |

A controller subscriber that treats every error as terminal should check `State`,
as in entry 4. Players built by `MediaPlayer` and `MediaPlaylistPlayer` do not
enable lateness recovery and are not affected.

### 7. A playlist skip follows the player's state

**Not a compile error.** Nothing you write changes; what runs does.

`IMediaPlaylistPlayer.SkipToNextAsync` started the next item playing whatever the
player's state said, and on the last item while paused it lost the end of the
playlist (#182). A skip while paused presented the next item while `State` stayed
`Paused`. A skip at `Ended` with an item enqueued
presented that item while `State` stayed `Ended`, and took it from the queue, so
a following `PlayAsync` found nothing to play and put the player in `Error`.

| State when you skip | Before | After |
|---|---|---|
| `Playing` | next item plays | unchanged |
| `Paused` | next item plays; `State` stays `Paused` | next item becomes current and stays paused until `PlayAsync` |
| `Paused`, last item, `RepeatMode.Off` | nothing plays; `State` stays `Paused`, and `PlayAsync` then reports `Playing` with nothing playing | `Ended` |
| `Ended`, with an item enqueued | enqueued item plays; `State` stays `Ended` | nothing; `PlayAsync` plays the enqueued item |
| Loaded, never played | next item plays; `State` stays `Paused` | takes effect on the first `PlayAsync` |

To resume a playlist at `Ended` after enqueueing, call `PlayAsync` rather than
`SkipToNextAsync`:

```csharp
// Before — played the item while State said Ended
await player.EnqueueAsync(next);
await player.SkipToNextAsync();

// After
await player.EnqueueAsync(next);
await player.PlayAsync();
```

The controller underneath gained one transition to make the paused case end: an
end-of-stream that reaches `Paused` now moves to `Ended` unless the repeat mode
is `One`. It used to be dropped. On a single-source player an end-of-stream can
reach `Paused` when it races a pause.

### 8. A playlist at `Ended` keeps its last item, and Play from there no longer faults

**Not a compile error.** Nothing you write changes; what runs does.

When `IMediaPlaylistPlayer` ran out of items under `RepeatMode.Off`, it disposed the
last item before it reported the end. `SeekAsync` from `Ended` then succeeded with
nothing to seek, and a following `PlayAsync` reported `Playing` while nothing
played. `PlayAsync` from `Ended` with nothing queued failed to load and put the
player in `Error` (#170). Entry 8 decides what that Play does now.

| Case | Before | After |
|---|---|---|
| `SeekAsync` from `Ended`, then `PlayAsync` | `Paused`, then `Playing` with nothing playing | the last item plays from the position sought |
| `PlayAsync` from `Ended`, nothing queued | failed `Result` with `ErrorCategory.System`; `Error` | the playlist starts again from its first item (entry 8) |
| `PlayAsync` from `Ended`, the player holds no items | failed `Result` with `ErrorCategory.System`; `Error` | failed `Result` with `ErrorCategory.InvalidOperation`; stays `Ended` |
| `SeekAsync` from `Ended` after the last item failed | succeeds; `PlayAsync` then reports `Playing` with nothing playing | failed `Result` with `ErrorCategory.InvalidOperation`; stays `Ended` |
| `GetDiagnostics()` at `Ended` | empty pipeline counters | the last item's counters |
| `PlayAsync` from `Ended`, an item queued | the queued item plays | unchanged |

The last item keeps its resources until the next item starts, the player is
unloaded, or it is disposed. That is its demuxer, decoders and graph, an active
audio sink, and a hardware decode device when one is in use. A single-source
player already holds these at `Ended`.

### 9. The playlist player keeps its playlist, and enqueued items play once

**Mostly not a compile error.** The behaviour changes below compile unchanged. Two
return types and six new interface members are compile-visible, to the code
described at the end.

`IMediaPlaylistPlayer` kept an upcoming queue and, under `RepeatMode.All`, a copy
of what had played. The two disagreed: a jump with `SetNextAsync` added a
permanent copy of its source to the loop (#171), a switch to `All` looped only
what played after it, and a skip under `RepeatMode.One` restarted the item.

The player now keeps a playlist with a cursor. Its sources are the playlist, and
`AddAsync` adds to it. `EnqueueAsync` and `SetNextAsync` add items that play once
and never join it. `JumpToAsync`, `RemoveAsync`, `ClearAsync`, `ReplaceAsync` and
`GetPlaylist` are new. The draft ADR
[The playlist player's queue](adr/playlist-queue-model.md) has the rules.

| Case | Before | After |
|---|---|---|
| `EnqueueAsync` or `SetNextAsync` under `All` | the source joins the rotation for good | it plays once; `AddAsync` adds to the rotation |
| `SetNextAsync` then `SkipToNextAsync` under `All` | the source plays, and each jump adds another copy to the rotation | it plays once, then the playlist resumes |
| Enqueue on every transition under `All`, then stop | the player loops over every item it enqueued | the player wraps to its playlist |
| `SkipToNextAsync` under `One` | restarts the current item | the next item plays and repeats |
| Switch to `All` after items played under `Off` | only items taken after the switch loop | the whole playlist loops |
| `PlayAsync` from `Ended`, nothing queued | fails and puts the player in `Error` (entry 7) | the playlist starts again from its first item |
| The factory's sources under `Off` | discarded as they play | kept for the life of the player |

The documented rotation pattern, enqueueing the next item from a
`SourceTransitioned` handler, plays in the same order as before and holds nothing
after each item plays.

To move within the playlist, jump to its item instead of setting it next:

```csharp
// Before — under All, every jump added a copy to the rotation
await player.SetNextAsync(source);
await player.SkipToNextAsync();

// After
var item = player.GetPlaylist().Playlist.First(i => ReferenceEquals(i.Source, source));
await player.JumpToAsync(item);
```

To replace what plays, call `ReplaceAsync` rather than disposing the player.

**Binary break.** `EnqueueAsync` now returns `Task<PlaylistItem>`, and
`SetNextAsync` returns `Task<PlaylistItem?>`. `await player.EnqueueAsync(source);`
still compiles. An assembly compiled against the old signatures and not rebuilt
throws `MissingMethodException`, as entry 2 describes. Rebuild it.

**Implementers.** `IMediaPlaylistPlayer` gains `AddAsync`, `GetPlaylist`,
`JumpToAsync`, `RemoveAsync`, `ClearAsync` and `ReplaceAsync`, with no default
implementations. A type outside FrameFlow that implements it, such as a test
double or a decorator, stops compiling until it adds them. This is a binary break
as well: an implementing assembly compiled against the old interface and not
rebuilt fails to load with `TypeLoadException`, naming the first member it lacks,
when the application uses the type. Rebuild it against this version.

### 9. `DotGraphSet` is gone

`FrameFlow.Playback.DotGraphSet` held Graphviz renderings of the playback
controller's state machines. The only method that returned one was internal to
`FrameFlow.Playback` and had no callers, so no FrameFlow API ever handed you a
value. Code that names the type stops compiling. Delete the reference; there is
no replacement.

### 10. `IMediaPlayer` gained `LoopRestarted`

A loop was visible only on `IPlaybackController`, so a caller of
`MediaPlayer.CreateAsync`, `MediaPlaylistPlayer.CreateAsync` or the builder had
no loop event, and a playlist raised none at all. Both players now expose the
controller's `LoopRestarted`. A playlist raises it when its current item is back
at its start after it played to its end: under `RepeatMode.One`, and for its only
item under `RepeatMode.All`. It does not fire for a skip, a jump or a rebuild after
a failure. `LoopCount` counts consecutive loops of the current item and starts
again at 1 after any other start.

A type outside FrameFlow that implements `IMediaPlayer`, such as a test double,
stops compiling until it adds the member:

```csharp
public IObservable<LoopRestarted> LoopRestarted => _controller.LoopRestarted;
```

This is a binary break as well: an implementing assembly compiled against the old
interface and not rebuilt fails to load with `TypeLoadException` when the
application uses the type. Rebuild it against this version.

### 11. `LoopStallSample.RepeatOne` is renamed `ExpectsRepeat`

The loop-stall watchdog used to watch only while the mode was `RepeatMode.One`, so
a playlist of one under `RepeatMode.All`, which loops through the same rewind, was
never watched. Its input now says whether the player expects the current item to
repeat, and a playlist answers for itself. Code that builds a `LoopStallSample`
stops compiling until it renames the argument:

```csharp
// Before
new LoopStallSample(now, position, duration, RepeatOne: true, Playing: true, loopCount);

// After
new LoopStallSample(now, position, duration, ExpectsRepeat: true, Playing: true, loopCount);
```

A host subscribed to `LoopStalled` can now see a stall from a playlist of one under
`RepeatMode.All`, where it saw none before.

### 12. `MediaPlayer.CreateAsync` returns `Task<IMediaPlaylistPlayer>`

Every player is a queue now, and a single source is a queue of one
(`docs/adr/one-player-type.md`), so the single-source factory returns the same player
the playlist factory does. `IMediaPlaylistPlayer` derives from `IMediaPlayer`, so the
awaited value still satisfies the smaller surface:

```csharp
// Both still compile.
var player = await MediaPlayer.CreateAsync(source);
IMediaPlayer small = await MediaPlayer.CreateAsync(source);
```

`Task<T>` is invariant, so naming the task does not:

```csharp
// Before
Task<IMediaPlayer> pending = MediaPlayer.CreateAsync(source);

// After
Task<IMediaPlaylistPlayer> pending = MediaPlayer.CreateAsync(source);
```

Passing the call where a `Task<IMediaPlayer>` or a `Func<…, Task<IMediaPlayer>>` is
expected needs the same edit. Every caller rebuilds: the return type is part of the
signature, so a compiled caller does not bind to the new method.

`IMediaPlayerBuilder.BuildPlayerAsync` still returns `Task<IMediaPlayer>`.

### 13. `RepeatMode.All` loops a single source

A player over one source used to play one pass and reach `Ended` under `All`, which
the enum documented as "behaves like `Off`". It now loops, because `All` wraps a queue
and that queue holds one item. `Off` is the mode that ends at the end of the media.

A host that set `All` on a single-source player to mean "play once" sets `Off`.

### 14. A mid-stream fault ends the player instead of failing it

A fault raised while a single source played used to put the player in `Error`, which
is terminal. The failure is now reported on `ErrorOccurred` with the item's exception,
and under `RepeatMode.Off` the player reaches `Ended`, where `PlayAsync` starts it
again. Under `One` and `All` the source is rebuilt and reported on every pass, and
nine failures in a row without progress still end in `Error`.

A host that watched `State` alone for failure sees `Ended` where it used to see
`Error`. Subscribe to `ErrorOccurred`, which fires in both cases:

```csharp
player.ErrorOccurred.Subscribe(new ErrorObserver(error => Alert(error)));
```

### 15. A loop no longer drives the seek state machine

The loop is the session's in-place rewind, taken as one of its inputs, so
`SeekStateChanged` stays `NotSeeking` across a loop and `IsActivelyPresenting` stays
`true`. A host that watched the seek transitions to detect a loop takes
`IMediaPlayer.LoopRestarted`, which also carries the loop's count. A host that gated
UI on `SeekingState` during a loop sees fewer transitions, and none of them false.

### 16. The video chain is built once per load, not once per loop

The loop keeps the graph, so a configurator passed to `configureVideo` runs once per
load rather than once per pass. An operator that carries state across frames now sees
the timeline go back to zero instead of being rebuilt. One that cannot handle that
must reset itself; the reset a graph could hand it is #217.

## `v0.9.0-alpha.1` — since `v0.8.0-alpha.1`

### 1. `IMediaPlayer` transport commands return `Result`

> **This one does not announce itself.** `await player.PlayAsync();` still
> compiles, and now discards the outcome instead of throwing. If you relied on
> the exception to reach your error path, that path is now dead and nothing
> tells you.

`IPlaybackController` has always answered in `Result`. `IMediaPlayer` wrapped
that same controller and threw, flattening `ErrorCategory` into an
`InvalidOperationException` message. The two layers now agree.

| Member | Before | After |
|---|---|---|
| `PlayAsync` | `Task` | `Task<Result>` |
| `PauseAsync` | `Task` | `Task<Result>` |
| `SeekAsync` | `Task` | `Task<Result>` |
| `SetRepeatModeAsync` | `Task` | `Task<Result>` |

```csharp
// Before
try { await player.SeekAsync(t); }
catch (InvalidOperationException ex) { Log(ex.Message); }

// After
var seeked = await player.SeekAsync(t);
if (!seeked.IsSuccess)
    Log(seeked.Error.Category, seeked.Error.Message);
```

`Error` is non-null on any failed `Result` — `IsSuccess` carries
`[MemberNotNullWhen(false, nameof(Error))]` — so a failure branch needs no
null check.

Construction still throws. `MediaPlayer.CreateAsync` and the builder terminals
have no player to hand back when they fail. Argument validation still throws,
and so does anything escaping a sink or the decode stack.

Reasoning, and the alternatives rejected, in
[ADR-0069](adr/ADR-0069-one-error-model-across-the-playback-stack.md).

### 2. `IMediaPlayer` gained `ErrorOccurred`

```csharp
IObservable<PlaybackError> ErrorOccurred { get; }
```

Forwarded from `IPlaybackController.ErrorOccurred`. It carries failures that
arise mid-playback rather than in answer to a command; a command's own failure
comes back on its `Result` and is not repeated here.

Only breaking if you **implement** `IMediaPlayer` — a test fake, an adapter, a
decorator. Add the member. It is not a default interface implementation on
purpose: a silent empty observable would hide the channel from exactly the
implementation that forgot it, and the four signature changes above already
mean an external implementor is recompiling.

### 3. Two `IMediaPlayer` members renamed

| Before | After | Why this direction |
|---|---|---|
| `PositionChanged` | `PositionTick` | It is a 250 ms `PeriodicTimer` pushing the clock position unconditionally, bound to `Playing`. Values repeat and the stream is silent while paused. `PositionChanged` promised change semantics the stream does not have. |
| `PollDiagnostics()` | `GetDiagnostics()` | `GetDiagnostics()` is the ADR-0034 convention across 16 declarations — sinks, decoders, the demux session, the controller. `PollDiagnostics` was the only outlier. |

`IPlaybackController` is unchanged; it already used both of these names.

`IMediaPlayer.StateChanged` and `IPlaybackController.PlaybackStateChanged`
deliberately keep different names, because they carry different types —
`PlaybackState` and `StateTransition<PlaybackState>`. One name for both would
say they were interchangeable.

### 4. Nine types moved out of the root `FrameFlow` namespace

They now live in `FrameFlow.Media`, with the rest of that assembly. Three files
declared the bare `FrameFlow` namespace; every other file in the project
declared `FrameFlow.Media`.

| Type | Was | Now |
|---|---|---|
| `FrameFlowOptions` | `FrameFlow` | `FrameFlow.Media` |
| `FrameFlowPlaybackOptions` | `FrameFlow` | `FrameFlow.Media` |
| `FrameFlowVideoOptions` | `FrameFlow` | `FrameFlow.Media` |
| `FrameFlowAudioOptions` | `FrameFlow` | `FrameFlow.Media` |
| `FrameFlowBufferingOptions` | `FrameFlow` | `FrameFlow.Media` |
| `HardwareDecodeOptions` | `FrameFlow` | `FrameFlow.Media` |
| `HardwareDecodeMode` | `FrameFlow` | `FrameFlow.Media` |
| `IFrameFlowBuilder` | `FrameFlow` | `FrameFlow.Media` |
| `FrameFlowServiceCollectionExtensions` | `FrameFlow` | `FrameFlow.Media` |

```diff
-using FrameFlow;                 // HardwareDecodeMode lived here
 using FrameFlow.Media;
```

If you already had `using FrameFlow.Media;`, deleting `using FrameFlow;` is
the whole fix. `HardwareDecodeMode` is the one most consumers hit: it is a
required argument to `MediaPlayer.CreateAsync` and was the only type in that
call living outside `FrameFlow.Media` or `FrameFlow.Player`.

### 5. `FrameFlow.SDL` is now `FrameFlow.Sdl`

The assembly, the package and every type in it (`SdlBootstrapper`,
`SdlException`, `SdlVideoSink`) already used the Pascal form. The namespace was
the only all-caps spelling in the repository.

```diff
-using FrameFlow.SDL;
-using FrameFlow.SDL.Bootstrap;
+using FrameFlow.Sdl;
+using FrameFlow.Sdl.Bootstrap;
```

Two things this does **not** change:

- **The OpenTelemetry meter name stays `FrameFlow.SDL.Sink`.** A meter name is
  a runtime identity that exporters, views and dashboards filter on, and
  renaming it stops them collecting with no error. Your dashboards keep
  working. The instrument prefix `frameflow.sdl.sink` was never touched.
- **Your use of `Silk.NET.SDL.Sdl`.** Inside the FrameFlow.Sdl package the
  namespace now shadows that type's unqualified name, which is why the package
  aliases it to `SdlApi`. That is internal to the package; your own code is
  unaffected, because a `using FrameFlow.Sdl;` imports the namespace's types
  and not the namespace itself.

### 6. SDL's DI extensions extend `IFrameFlowBuilder`, not `IServiceCollection`

`AddFrameFlowSdl`, `AddHostedSdlBootstrap` and both `AddFrameFlowSdlVideoSink`
overloads now take an `IFrameFlowBuilder`, matching the other five adapters.
The only way to obtain one is from `AddFrameFlow()`, which is the check that
was missing.

```diff
 builder.Services
     .AddFrameFlow()
     .AddFrameFlowPlayback()
-    .Services
     .AddFrameFlowSdl()
     .AddFrameFlowSdlVideoSink(sdl, "Player", 1280, 720, out var videoSink);
```

No `[Obsolete]` forwarders: keeping four would double the SDL DI surface to
soften a compile error whose fix is deleting one line.

### 7. `FrameFlow.Avalonia` no longer declares `Subscribe<T>`

`FrameFlow.Playback` and `FrameFlow.Avalonia` both declared

```csharp
public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext)
```

so any file importing both got CS0121 on a fluent `.Subscribe` call — and every
Avalonia consumer imports both. The Avalonia copy is gone; the surviving one is
`FrameFlow.Playback.PlaybackObservableExtensions.Subscribe`, and the two bodies
were behaviourally identical.

```diff
 using FrameFlow.Avalonia;
+using FrameFlow.Playback;
```

`ObserveOnUiThread` stays in `FrameFlow.Avalonia`.

### 8. `default(Result).Error` is no longer `null`

Only reachable if you produce a `Result` without the factory methods — a struct
field, an array element, `new Result()`. It reports
`ErrorCategory.InvalidOperation` with a message naming the mistake, instead of
being a failed result with no reason. This is what lets
`[MemberNotNullWhen(false, nameof(Error))]` hold in every state rather than
almost all of them.

`Result.Ok()` and `Result.Fail(...)` are unchanged.

## Not breaking, but worth knowing

- **A repeat keeps the playlist's current item started.** When a playlist of one
  under `RepeatMode.All` wraps to its item, `PlaylistSnapshot.CurrentStarted` stays
  `true` while the item repeats. It used to read `false` until the repeat
  completed, as it still does for a hand-off to a different item.
- **The fluent builder gained a second terminal.** `BuildPlayerAsync()` returns
  `IMediaPlayer`; `BuildAsync()` still returns `PlayerSession`. Additive.
  `WithRepeatMode`, `WithClock`, `WithHardwareFrames` and `WithAudioActivation`
  narrow the chain to `IMediaPlayerBuilder`, whose only terminal is
  `BuildPlayerAsync`, so setting one and then calling `BuildAsync` is a compile
  error rather than an ignored setting. `WithLogger` now accepts `null` as a
  no-op, so an optional step no longer breaks the chain. Breaking only if you
  implement `IPlayerBuilder` yourself.
- **Packages now ship symbols and SourceLink.** A `.snupkg` accompanies each
  `.nupkg`, so stack traces through FrameFlow carry file and line, and a
  debugger can step into the source. `FrameFlow.Native.Runtime` is the
  exception: it contains FFmpeg's shared libraries and no managed assembly, so
  there is no PDB to ship.
- **Packages now carry `PackageTags`.** They are findable on nuget.org by
  subject rather than only by name.
- **The audio master clock stops at the pause position.** It used to keep
  reading the device while paused, and credit whatever the device let go of. A
  device that drops its queue — a Remote Desktop session ending takes its audio
  endpoint with it — reports the whole queue processed at once, so a long pause
  could resume more than a second further on than it stopped, with every decoded
  frame then due at once. `Position` read across a pause now returns where the
  pause stopped, and resume re-anchors onto it. Nothing to change; the number
  just stops being wrong.
- **The audio master clock is capped at real time.** A playing device consumes
  one second of audio per second, so a reading that advances further than
  elapsed playing time is audio the device dropped rather than played, and the
  clock publishes what real time allows instead of following it. Only reachable
  when the device is already misbehaving. `OpenAlAudioSink.DeviceDisconnected`
  reports that case, and the start log now names the endpoint it opened.
- **The OpenAL start log gained a field.** It reads `OpenAL audio sink started
  on {Device}. BufferPoolSize=...` where it used to begin `OpenAL audio sink
  started. BufferPoolSize=...`. Only breaking if you parse that line.
