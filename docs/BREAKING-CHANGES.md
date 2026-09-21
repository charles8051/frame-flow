# Breaking changes

FrameFlow is pre-1.0. `README.md` says the public surface moves freely between
releases, and it does. This file is what that promise owes you in return: the
list, with the fix for each one.

Entries are grouped by the release they ship in and ordered within it by how
likely you are to hit them. Each says what broke, what to write instead, and —
where it is not obvious — why the change was worth making.

**Read the first entry of any group carefully.** Most breaks here are compile
errors, which announce themselves. A few are not, and those are called out.

## Unreleased — since `v0.10.0`

Nothing breaking yet.

### Not breaking, but worth knowing

- **`MediaSource.FromStill(path, dwell)` opens a still, so the `image2` recipe has one home.**
  Opening a single image as a clip of a known length took three settings a caller had to know:
  name `image2`, pass `framerate` as a rational, and pass `pattern_type=none`. Getting any one
  wrong failed in a different way, and the failures read as decode problems rather than
  configuration ones. The factory does all three and works the rational out from the dwell, in
  exact integer terms, so a fractional dwell needs no invariant formatting: 7.5 seconds is
  `2/15`. `MediaSource.FromFile` and the `with` form still work, and nothing that used them
  changes. It takes no position on the extension — whether a file is one image is a fact about
  the content, and a caller that hands it something animated gets the first frame reported as
  the whole file, as before. #304.

## `v0.10.0` — since `v0.9.0-alpha.1`

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
`docs/adr/ADR-0078-graph-chain-forks-joins-and-termination.md`.

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

`MediaPlayer.CreateAsync` and the builder's `BuildPlayerAsync` already passed
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
[The playlist player's queue](adr/ADR-0074-playlist-queue-model.md) has the rules.

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

### 10. `DotGraphSet` is gone

`FrameFlow.Playback.DotGraphSet` held Graphviz renderings of the playback
controller's state machines. The only method that returned one was internal to
`FrameFlow.Playback` and had no callers, so no FrameFlow API ever handed you a
value. Code that names the type stops compiling. Delete the reference; there is
no replacement.

### 11. `IMediaPlayer` gained `LoopRestarted`

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

### 12. `LoopStallSample.RepeatOne` is renamed `ExpectsRepeat`

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

### 13. `MediaPlayer.CreateAsync` returns `Task<IMediaPlaylistPlayer>`

Every player is a queue now, and a single source is a queue of one
(`docs/adr/ADR-0077-one-player-type.md`), so the single-source factory returns the same player
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

### 14. `RepeatMode.All` loops a single source

A player over one source used to play one pass and reach `Ended` under `All`, which
the enum documented as "behaves like `Off`". It now loops, because `All` wraps a queue
and that queue holds one item. `Off` is the mode that ends at the end of the media.

A host that set `All` on a single-source player to mean "play once" sets `Off`.

### 15. A mid-stream fault ends the player instead of failing it

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

### 16. A loop no longer drives the seek state machine

The loop is the session's in-place rewind, taken as one of its inputs, so
`SeekStateChanged` stays `NotSeeking` across a loop and `IsActivelyPresenting` stays
`true`. A host that watched the seek transitions to detect a loop takes
`IMediaPlayer.LoopRestarted`, which also carries the loop's count. A host that gated
UI on `SeekingState` during a loop sees fewer transitions, and none of them false.

### 17. The video chain is built once per load, not once per loop

The loop keeps the graph, so a configurator passed to `configureVideo` runs once per
load rather than once per pass. An operator that carries state across frames now sees
the timeline go back to zero instead of being rebuilt. One that cannot handle that
must reset itself; the reset a graph could hand it is #217.

### 18. `MediaPlaylistPlayer` is gone; `MediaPlayer.CreateAsync` takes a queue

Every player is a queue (ADR-0077), so there is one factory. `MediaPlayer.CreateAsync`
gained an overload taking `IEnumerable<IMediaSource>`, and the `MediaPlaylistPlayer`
class is removed. Entry 13 already made both return `Task<IMediaPlaylistPlayer>`.

```csharp
// Before
await MediaPlaylistPlayer.CreateAsync([first, second], videoSink, audioSink);

// After
await MediaPlayer.CreateAsync([first, second], videoSink, audioSink);
```

Nothing else about the call changes. The parameters, their order and their meaning are
the same, and the player you get back is the same type it was.

One call shape stops compiling. `MediaPlayer.CreateAsync(null)` matched one method and
now matches two, so it is ambiguous (CS0121). Cast the literal, or delete the call: it
threw `ArgumentNullException` the moment it ran.

```csharp
await MediaPlayer.CreateAsync((IMediaSource)null!);
```

### 19. A queue built without a repeat mode plays once

**This one is not a compile error.** `MediaPlaylistPlayer.CreateAsync` defaulted
`initialRepeatMode` to `RepeatMode.All`, and the single-source factory defaulted it to
`RepeatMode.Off`. Folding the two into one method (entry 18) left one name with two
answers to the same omitted argument. Both overloads now default to `RepeatMode.Off`.

A caller who omitted `initialRepeatMode` on a playlist looped forever and now ends after
the last item. Say what you want:

```csharp
await MediaPlayer.CreateAsync([first, second], videoSink, audioSink,
    initialRepeatMode: RepeatMode.All);
```

`SetRepeatModeAsync` still changes it at any time.

### 20. `BuildPlayerAsync` returns `Task<IMediaPlaylistPlayer>`

The builder can now open a queue: `FrameFlowPlayer.Create().WithMedia(IEnumerable<IMediaSource>)`
starts the same chain over an ordered set of sources. It begins narrowed to
`IMediaPlayerBuilder`, because a `MediaPass` plays one source and `BuildAsync` has
no meaning over a queue.

Both `BuildPlayerAsync` terminals widened to match, the same break entry 13 made to
`MediaPlayer.CreateAsync`. `Task<T>` is invariant, so awaiting still compiles and naming
the task does not:

```csharp
// Still compiles — the awaited value is assignable
IMediaPlayer player = await FrameFlowPlayer.Create().WithMedia(path).BuildPlayerAsync();

// Stops compiling — name the new type, or await first
Task<IMediaPlayer> pending = FrameFlowPlayer.Create().WithMedia(path).BuildPlayerAsync();
```

A type outside FrameFlow that implements `IPlayerBuilder` or `IMediaPlayerBuilder`
changes its terminal's return type to match.

### 21. Ended fires a frame later, at the end of the last frame's display

**This one is not a compile error.** Decoded frames carry no display interval today, so
the presenter's end-of-content hold could never engage and `Ended` fired the moment the
last frame was selected rather than when it finished being on screen. Frames now carry
the interval the demuxer gave them, so `Ended` arrives one frame interval later: about
40 ms at 25 fps, and as long as the frame's own interval for a source paced to hold a
picture.

A host that measured how long a clip took to reach `Ended`, or that chained work off it
with a budget tuned to the old arrival, gets one frame more than it used to. A host that
advances a playlist on `Ended` shows the final frame for its full duration first.

`IPlaybackController.Position` is clamped to `Duration` while the state is `Ended`. The
clock runs on for the command hop between the hold completing and the transition freezing
it, and that overshoot used to be hidden inside the frame `Ended` arrived early by. A host
that read `Position` at `Ended` and expected the raw clock now reads `Duration` exactly.
Everywhere but `Ended` it is unchanged.
### 22. `FrameFlowPlayer.Open` is now `FrameFlowPlayer.Create`

`Open` opened nothing. It recorded the source and returned the builder; the demuxer runs
in `BuildAsync` / `BuildPlayerAsync`. The name promised I/O that happens somewhere else,
and it could not be stretched over entry 23's no-source overload.

```csharp
// Before
await FrameFlowPlayer.Open(path).WithVideoSink(sink).BuildPlayerAsync();

// After
await FrameFlowPlayer.Create().WithMedia(path).WithVideoSink(sink).BuildPlayerAsync();
```

The builder is otherwise the same. Where the source goes is entry 25.

### 23. A player can be built with nothing to play

`FrameFlowPlayer.Create()` takes no source, and `MediaPlayer.CreateAsync` accepts an empty
set of them. The player is built with its sinks attached and warm and nothing loaded,
sitting at `PlaybackState.Idle`. The first `PlayAsync` starts whatever `AddAsync` or
`EnqueueAsync` have put in the queue by then.

```csharp
await using var player = await FrameFlowPlayer.Create()
    .WithVideoSink(sink)
    .BuildPlayerAsync();

await player.AddAsync(source);   // arrives later
await player.PlayAsync();        // loads and starts it
```

`PlayAsync` on a player whose queue is still empty is refused with
`ErrorCategory.InvalidOperation` and leaves it at `Idle`, which is what `Play` from `Idle`
has always done.

`MediaPlayer.CreateAsync(sources)` used to throw `ArgumentException` on an empty set, and
`PlaylistCoordinator`'s public constructor did the same. Neither does now. A caller who
was relying on the throw to catch an empty list checks it before the call.

This is for a host that builds its presenter once at startup and receives content
afterwards. Building with a placeholder and replacing it cost a load and a teardown.

### 24. `MediaSource` is built with an object initializer

`MediaSource` was a positional record, so `new MediaSource("clip.mp4")` and
`new MediaSource(name, uri, path, false)` both compiled. It now declares `DisplayName` as a
`required` init property and the rest as ordinary init properties:

```csharp
// Before
var source = new MediaSource("clip.mp4");
var full = new MediaSource("My Video", uri, "/files/test.mp4", false);

// After
var source = new MediaSource { DisplayName = "clip.mp4" };
var full = new MediaSource
{
    DisplayName = "My Video",
    Uri = uri,
    FilePath = "/files/test.mp4",
    IsSeekable = false,
};
```

`MediaSource.FromFile` and `MediaSource.FromUri` are unchanged and remain the way most
callers build one. The generated `Deconstruct` goes with the positional form, so
`var (name, uri, path, seekable) = source;` no longer compiles.

**Why.** The record grew two more optional members, `DemuxerOptions` and `InputFormat`. Three
of the four it already had were optional with defaults, and a fifth and sixth in the
positional tail make an argument list nobody can read at the call site. Naming them is also
what keeps the next member from being a breaking change again.

A type implementing `IMediaSource` directly is unaffected: both new members are default
interface members returning `null`, which is "open this the way it has always been opened".
### 25. `FrameFlowPlayer.Create` names no media; `WithMedia` does

Every player is a queue and a queue can be empty, so the entry no longer takes media. It
is a chained option like the sinks, and it replaces rather than appends, as every other
`With*` on the builder does.

```csharp
// Before
await FrameFlowPlayer.Create(path).WithVideoSink(sink).BuildPlayerAsync();
await FrameFlowPlayer.Create([first, second]).WithVideoSink(sink).BuildPlayerAsync();

// After
await FrameFlowPlayer.Create().WithMedia(path).WithVideoSink(sink).BuildPlayerAsync();
await FrameFlowPlayer.Create().WithMedia([first, second]).WithVideoSink(sink).BuildPlayerAsync();
```

`WithMedia` takes a path, an `IMediaSource`, or an `IEnumerable<IMediaSource>`. The plural
one narrows the chain to `IMediaPlayerBuilder`, which is where `Create(IEnumerable<…>)`
put it before: a `MediaPass` plays one source, so `BuildAsync` is not on offer over a
queue.

**One check moved from compile time to run time.** `Create()` used to be the no-media entry
and returned `IMediaPlayerBuilder`, so `BuildAsync` was unreachable without a source. Now
`WithMedia` comes after the entry, and `Create().BuildAsync()` compiles. It throws
`InvalidOperationException` naming `WithMedia`. `BuildPlayerAsync` with no media is
unchanged and still valid — that is the player with an empty queue.

**If you implement `IPlayerBuilder` or `IMediaPlayerBuilder`**, add the three `WithMedia`
overloads. They are ordinary interface members, not defaulted ones: a default that threw
would turn a compile error into a run-time one, and the narrowing on these interfaces exists
precisely to keep that kind of mismatch at compile time. Entry 20 already changes both
terminals' return type, so an implementer is recompiling against this release either way.

### 26. `BuildAsync` moves to its own entry point: `FrameFlowPass`

`PlayerSession` held no clock. `ClockSelectVideoSink` and `PaceUntil`, the types that hold a
frame until its presentation time, are constructed only on the controller path, so video
through `BuildAsync` was presented as fast as the sink accepted it. Both in-repo users were
audio-only, where the device's backpressure supplies the timing, which is why the gap never
showed. The two terminals read as a difference of transport surface and the difference was
pacing.

The unpaced runtime now has its own entry, so the choice is the first call rather than the
last. `docs/adr/ADR-0079-the-pass-and-the-player.md` is the record.

```csharp
// Before
await using var session = await FrameFlowPlayer.Create()
    .WithMedia(path)
    .WithVideoSink(sink)
    .BuildAsync();
await session.PlayToCompletionAsync(ct);

// After
await using var pass = await FrameFlowPass.Create(path)
    .WithVideoSink(sink)
    .BuildAsync();
await pass.RunToCompletionAsync(ct);
```

| Before | After |
|---|---|
| `FrameFlowPlayer.Create().WithMedia(path)….BuildAsync()` | `FrameFlowPass.Create(path)….BuildAsync()` |
| `PlayerSession` | `MediaPass` |
| `PlayerSession.PlayToCompletionAsync(ct)` | `MediaPass.RunToCompletionAsync(ct)` |
| `IPlayerBuilder.BuildAsync` | gone; the player builds a player |

The source is named at `Create` rather than with `WithMedia`, because a pass with none has
nothing to do. There is no queue: the thing worth reusing across files is the sink, and under
ADR-0044 the caller owns it, so one sink holding a loaded model serves any number of passes.

**A pass with no sink is now refused.** It used to build, and throw from
`PlayToCompletionAsync` once the file was open and the decoders were built. `BuildAsync`
answers first, before any of that. `HeadlessVideoSink` is the terminal for a run that presents
nothing.

`WithRepeatMode`, `WithClock`, `WithHardwareFrames` and `WithAudioActivation` are not on
`IPassBuilder`. They were already unreachable from a chain headed for `BuildAsync`.

### 27. `IMediaPlayerBuilder` is gone; `IPlayerBuilder` has one terminal

The narrowing existed to keep the player-only options off a chain that could still end in
`BuildAsync`. Entry 26 moved that terminal to its own entry, so no such chain exists, and the
two near-identical interfaces fold into one.

```csharp
// Before — the player-only options returned the narrower interface
IMediaPlayerBuilder narrowed = FrameFlowPlayer.Create().WithMedia(path).WithRepeatMode(RepeatMode.All);

// After
IPlayerBuilder builder = FrameFlowPlayer.Create().WithMedia(path).WithRepeatMode(RepeatMode.All);
```

Every chained call site is unchanged: the options return the builder, as they always did, and
`var` never named the difference. What breaks is code that wrote `IMediaPlayerBuilder` down —
a local, a field, a parameter — and any type outside FrameFlow that implemented it. Entries 20
and 25 already broke implementers of these interfaces in this release.

`WithAvaloniaVideoView` keeps two overloads: one on `IPlayerBuilder`, and one that was on
`IMediaPlayerBuilder` and is now on `IPassBuilder`. `WithOpenAlAudio` had the same pair and entry
29 retires it.

### 28. `PlaybackGraph` is gone

Removed in #271. It wired caller-supplied decoders to sinks and ran to EOS, which is
`MediaPass`'s job with the demux session and the decoders handled for you. Its own summary
called it a Phase-3 proof that the full-controller port could build on, and that port landed in
ADR-0077. It had no users outside its own tests.

Use `FrameFlowPass.Create(path)` and let the builder open the file.

### 29. `WithOpenAlAudio` is gone; construct the sink yourself

```csharp
// Before
await using var player = await FrameFlowPlayer.Create()
    .WithMedia(path)
    .WithOpenAlAudio(loggerFactory)
    .BuildPlayerAsync();

// After
await using var audio = new OpenAlAudioSink(loggerFactory?.CreateLogger<OpenAlAudioSink>());
await using var player = await FrameFlowPlayer.Create()
    .WithMedia(path)
    .WithAudioSink(audio)
    .BuildPlayerAsync();
```

`FrameFlowOpenAlBuilderExtensions` is removed, with both overloads.

**Why.** A sink belongs to whoever constructed it. A player and a pass use the sink they are
given and never dispose it, so one sink can serve several players in sequence, which is how the
playlist player keeps a presenter warm across items (ADR-0062). `WithOpenAlAudio` was the one
thing in the library that constructed a sink *inside* that layer and handed it to something
that, by the rule, would not dispose it — and it never handed the sink back, so the caller could
not dispose it either. Every player built that way left an OpenAL device and context open (#275).

Keeping it meant carving an exception into the ownership rule: a builder that disposes what an
extension gave it but not what a caller gave it, distinguished by an API the extension would
need and nothing else would. The shortcut saved one line and had no callers in this repository —
no example, no test — so it goes instead.

`WithAvaloniaVideoView` stays, with both its overloads. It never had the defect: it calls
`view.EnsureSink()` and the view owns the sink, so the extension borrows rather than constructs.

`FrameFlow.Audio.OpenAL` no longer references `FrameFlow.Player`. The extension was the only
thing in it that did, so consuming the audio backend no longer pulls in the player composition
layer.

`AddFrameFlowOpenAlAudio()` for the generic host is unaffected. The container constructs the sink
and the container disposes it, which is the same rule with a different owner.

### 30. `MediaPlayer` is gone; the builder is the way in

```csharp
// Before
await using var player = await MediaPlayer.CreateAsync(
    source,
    videoSink,
    audioSink,
    initialRepeatMode: RepeatMode.All);

// After
await using var player = await FrameFlowPlayer.Create()
    .WithMedia(source)
    .WithVideoSink(videoSink)
    .WithAudioSink(audioSink)
    .WithRepeatMode(RepeatMode.All)
    .BuildPlayerAsync();
```

Every parameter has a chained equivalent, and has since entry 25:

| `CreateAsync` parameter | Builder |
|---|---|
| `source` / `sources` | `WithMedia`, which also takes none |
| `videoSink` / `audioSink` | `WithVideoSink` / `WithAudioSink` |
| `hardwareDecodeMode` | `WithHardwareDecode` |
| `yieldHardwareFrames` | `WithHardwareFrames` |
| `initialRepeatMode` | `WithRepeatMode` |
| `loggerFactory` | `WithLogger` |
| `activateAudioSink` | `WithAudioActivation` |
| `configureVideo` / `configureAudio` | `ConfigureVideo` / `ConfigureAudio` |
| `cancellationToken` | `BuildPlayerAsync(ct)` |

The builder also has `WithClock`, which the factory could not expose.

**Why.** It built the same object through the same body and had no capability the builder
lacked, so what it added was a second name for one thing. Its own documentation said "Prefer the
fluent builder… New code should use the builder", which is debt that never resolves on its own,
and its doc comments have needed editing in every rename this release — the return type
(entry 13), `Open` to `Create` (22), `WithMedia` (25), the terminal split (26). A forwarder with
no behaviour still has prose that drifts.

The construction body it held is now `PlayerFactory.CreateAsync`, internal, reached through
`BuildPlayerAsync`. Nothing about how a player is built has changed.

`FrameFlowPlayer.Create()` and `FrameFlowPass.Create(path)` are the two entry points.
`PlaybackController.Create(...)` still sits below both for a caller who wants the raw state
machine.

### 31. `IPlayerBuilder` gained `WithLatenessRecovery`

**Only a caller that implements `IPlayerBuilder` itself is affected.** A chain that consumes the
builder from `FrameFlowPlayer.Create()` is untouched.

The interface gained a member, so an external implementation stops compiling with CS0535 until it
adds one. Same shape as entry 11, where `IMediaPlayer` gained `LoopRestarted`.

No default implementation is supplied. A builder that silently ignored `WithLatenessRecovery` would
report success and configure nothing, which is the failure this member exists to remove: until now
`PlaybackController.CreatePlaylist` dropped the options on the floor, so every builder-built player
ran with the walk off and no way to say otherwise.

```csharp
public IPlayerBuilder WithLatenessRecovery(LatenessRecoveryOptions options) => this;
```

is enough for an implementation that does not pace, and the compiler names the file to add it to.
### 32. `LoopStallMetrics` is internal; scrape the meter instead

The class and its `RecordLoopStall()` had one caller, inside `FrameFlow.Playback`. Public, it let
a consumer add to `frameflow.playback.loop_stalls` and make the counter disagree with the stalls
the controller actually detected.

Nothing changes for reading the counter, which is what the type was published for. The meter name
and the counter name are the contract, and both are unchanged:

```
dotnet-counters monitor --counters FrameFlow.Playback
```

A `MeterListener` on the `FrameFlow.Playback` meter reaches
`frameflow.playback.loop_stalls` the same way it did before.

Only a caller that invoked `LoopStallMetrics.RecordLoopStall()` breaks, and nothing should have:
a stall the controller did not see is not a stall. To count your own, declare your own meter.

### 33. `PlaylistTransition.Source` is gone; `Item` names the source

**This one is a compile error at every use.** `Source` was a constructor parameter that always
held `Item.Source`, so the record carried the same source twice and a consumer could reach the one
that does not identify which item is playing.

```csharp
// Before
UpdateSelection(transition.Source);

// After
UpdateSelection(transition.Item.Source);
```

Prefer `transition.Item` itself where you were matching. A queue can hold the same source twice,
so matching by source picks the wrong entry; the item is what tells them apart, which is why it
was added.

`Item` is also no longer nullable. It was `PlaylistItem?` only because the four-argument
constructor could leave it unset, and the player never did. The constructor now takes all six
members, so every transition names its item:

```csharp
public sealed record PlaylistTransition(
    PlaylistItem Item, MediaInfo MediaInfo, int Index, bool Wrapped,
    PlaylistItem? Previous, PlaylistTransitionReason Reason);
```

`Previous` stays nullable, because nothing precedes the first item.

`Item` and `MediaInfo` have no `init` accessor, so `with { Item = other }` does not compile. They
are one fact rather than two — the metadata is what the demuxer reported for that item's load, and
nothing can re-derive it to check a pairing — so they are set together or not at all. `with` still
changes `Index`, `Wrapped`, `Previous` and `Reason`; a caller who wants a different item wants a
different transition.

Code that built a transition
with the four-argument form and an object initializer passes the six arguments positionally
instead. A subscriber that null-checked `Item` can drop the check.

### 34. `PlaylistItem` and `PlaylistSnapshot` can be constructed

Not a break. Both constructors were internal, which made `IMediaPlaylistPlayer` an interface you
could implement but not satisfy: `AddAsync`, `EnqueueAsync`, `SetNextAsync`, `ReplaceAsync` and
`GetPlaylist` all return those types, and nothing outside the assembly could produce one. A test
double for a view model, or a decorator over a real player, was out of reach.

```csharp
var item = new PlaylistItem(source);
var snapshot = new PlaylistSnapshot(
    playlist: [item], next: [], queued: [],
    current: item, currentStarted: true, resumeIndex: 0, pendingJump: null, revision: 1);
```

An item built this way belongs to no player. Reference equality is what says so, and
`JumpToAsync` / `RemoveAsync` refuse it with a failed `Result` exactly as they refuse an item
another player owns.

`PlaylistSnapshot` validates rather than trusting its arguments, so a hand-built snapshot cannot
describe a queue no player could be in. It throws when a collection holds a `null`, when
`resumeIndex` falls outside `playlist` (the count itself is in range: it means the pass has
ended), when `pendingJump` is in none of the three collections, or when `revision` is negative.

### 35. `LoopStallEvaluator`, `LoopStallSample` and `LoopStallOutcome` are internal

The loop-stall watchdog's pure core leaves the public surface, the same move entry 32 made for
`LoopStallMetrics`. A consumer reached these only by reimplementing the watchdog, which the
library does not offer: the evaluator is driven by `PlaybackControllerCore`, which is internal,
and `LoopStallSample.NowTicks` has to come from the same clock whose `TimestampFrequency` was
handed to `LoopStallEvaluator.Create`, which nothing public exposes.

The documentation said as much and could not say it to anyone: `LoopStallSample`'s summary
carried a `<see cref="PlaybackControllerCore"/>` pointing at a type no consumer could resolve.

**What a consumer actually watches a stall with is unchanged:**

| Surface | Shape |
| --- | --- |
| `IMediaPlayer.LoopStalled` | `IObservable<LoopStalled>`, edge-triggered |
| `IMediaPlaylistPlayer.ItemStalled` | `IObservable<PlaylistItemStalled>`, naming the item |
| `PlaybackDiagnosticsSnapshot.LoopStalled` / `.LoopOverrun` | level-triggered state |
| `frameflow.playback.loop_stalls` | the counter, on the `FrameFlow.Playback` meter |

28 lines leave `PublicAPI.Unshipped.txt`. `PublicAPI.Shipped.txt` is empty for this assembly, so
nothing supported is withdrawn.

Still public, and deliberately: `PausableGate<T>`, `PaceUntil` and `WallClockSource` are graph
operators a consumer composing their own chain would use, and their documentation addresses one.

### 36. `IPlayerBuilder` gained `WithTimeProvider`

**Only a caller that implements `IPlayerBuilder` itself is affected**, the same shape as entry 31.
An external implementation stops compiling with CS0535 until it adds the member; a chain that
consumes the builder from `FrameFlowPlayer.Create()` is untouched. `PlaybackController.Create`
gained an optional `timeProvider` parameter, which breaks nothing.

The loop-stall watchdog and the position ticker run on wall time, and until now that was
`TimeProvider.System` with no way to say otherwise from outside the assembly. `PlaybackClock`
already took a `TimeProvider`, so a harness on simulated time got a deterministic timeline beside
a real-time watchdog, and the two disagreed by whatever the fake clock's rate was.

```csharp
var fake = new FakeTimeProvider();

await using var player = await FrameFlowPlayer.Create()
    .WithMedia(path)
    .WithVideoSink(sink)
    .WithTimeProvider(fake)
    .BuildPlayerAsync();
```

`WithClock` is unaffected and still wins: a clock passed there is kept as-is. The provider supplies
the clock the player would otherwise have built for itself, so naming one covers both. A caller who
wants their own clock *and* simulated time builds it on the same provider.

These stay two knobs rather than one because they measure different things. An `IPlaybackClock`
carries a position on the presentation timeline, which seeks and pauses. The watchdog needs elapsed
real time, because its job is to notice that the timeline advanced while frames stopped — reading
the clock it supervises would make it measure itself.

### 37. `VideoOperators.ToCpu` exists, and the docs that cited it now name it correctly

Not a break. Four doc comments, a `NotSupportedException` message and an `InternalsVisibleTo`
comment told callers to reach for a `pipeline.ToCpu()` operator in `FrameFlow.Video`. It was never
written, so every one of them named an API that could not be called.

It exists now, as a node rather than the pre-graph shape ADR-0038 specified:

```csharp
graph.Pipeline(source)
    .Then(VideoOperators.ToCpu("readback"))
    .Then(VideoOperators.Resize("resize", 640, 480))
    .To(sink);
```

This is what a hardware-decoding graph was missing. `ConvertPixelFormat`, `Resize` and
`ResizeAndConvert` all route through `SwScaleVideoConverter.Process`, which calls `ToCpu()` on the
incoming frame; on a `GpuVideoFrame` that throws. So a decoder yielding hardware frames could not
reach any CPU operator, and the escape hatch each of them pointed at did not exist.

A frame already on the CPU passes straight through, which is what lets the node sit in a chain
unconditionally: a graph does not know whether the decoder bound a hwaccel backend, and the answer
differs per run and per machine.

The messages that cited the old name now name the new one. One of them also claimed the operator
cached its swscale context across frames; it does not, it calls `ReadbackToCpuBgra32` per frame, and
the claim is gone rather than made true. Reuse across frames is a follow-up, and next to the PCIe
transfer and the full-frame scale it is small.

### 38. The two player interfaces swap names

**This one is a compile error at every use, and a mechanical one.** `IMediaPlaylistPlayer` is now
`IMediaPlayer`, and what `IMediaPlayer` used to name is now `IMediaTransport`.

| Was | Is |
| --- | --- |
| `IMediaPlaylistPlayer` | `IMediaPlayer` |
| `IMediaPlayer` | `IMediaTransport` |

Rename in that order, or the first pass will collide with the second: the name being freed is the
name being taken.

```csharp
// Before
IMediaPlaylistPlayer player = await FrameFlowPlayer.Create()…BuildPlayerAsync();
IMediaPlayer small = player;

// After
IMediaPlayer player = await FrameFlowPlayer.Create()…BuildPlayerAsync();
IMediaTransport transport = player;
```

Nothing about either type changes — same members, same inheritance, same object returned by
`BuildPlayerAsync`. Only the names move.

**Why.** `IMediaPlaylistPlayer` read as a *kind* of player, the one to reach for when there is a
playlist, and ADR-0077 decision 1 is that there is no such kind: every player is a queue and a
single file is a queue of one. `IMediaPlayer` read as the general case while being the narrower
view. A reader meeting both concluded "the plain one for a file, the playlist one for several",
which is wrong in both directions, and type names are the only thing most callers read.

The new name for the small surface is the word the codebase already used for it in prose, and every
consumer of it is a transport widget: the seek bar, the volume control, the state badge, the
transport bar, the position label.

ADR-0077 decision 2's amendment has the reasoning, including why the two interfaces stay separate
rather than folding into one.

### 39. The OpenAL sink's buffer-queue core is internal

`BufferQueueState`, `AlSourceState`, `UploadDecision`, `UnderrunOutcome` and `StartOutcome` leave
the public surface. Entry 35 made the same move for the loop-stall watchdog's core, and entry 32
for `LoopStallMetrics`.

The five types are the pure core of `OpenAlAudioSink`: a `(state, input) -> state'` fold over the
buffer queue, factored out so the queue decisions are unit-testable with no audio device. Nothing
consumes them. The sink threads the state internally, no public member takes or returns one, and
the only references outside the package were its own two test suites.

39 lines leave `PublicAPI.Unshipped.txt`, which drops the assembly's public surface from 61
entries to 22. `PublicAPI.Shipped.txt` is empty for this assembly, so nothing supported is
withdrawn.

**What you use the sink with is unchanged:** construct `OpenAlAudioSink`, hand it to
`WithAudioSink`, or register it with `AddFrameFlowOpenAlAudio`. `Volume`, `Muted`,
`GetPlaybackTime`, `GetDiagnostics`, `BlocksWritten`, `UnderrunCount`, `BackpressureCount` and
`DeviceDisconnected` are all still there.

If you were reading the sink's queue health, `GetDiagnostics()` returns an
`AudioSinkDiagnosticsSnapshot` and is the supported answer. `BufferQueueState` never reflected a
live sink in any case: it is a value the sink folds, not a view onto one.

### 40. `IMediaTransport.MediaInfo` is nullable, and no longer throws

> **This one is a warning, not an error.** `player.MediaInfo.Duration` still compiles. With
> nullable reference types on you get CS8602 at the dereference; with them off you get nothing
> until it returns null. It used to throw at the same moment, so the failure moves rather than
> appears.

```csharp
// Before: throws InvalidOperationException when nothing is loaded.
var info = player.MediaInfo;

// After: null when nothing is loaded.
if (player.MediaInfo is not { } info)
    return;
```

**Why.** Null was always reachable and the type denied it. A player built without `WithMedia`
starts with its sinks warm and an empty queue (ADR-0077's amendment of 2026-09-17), a player
whose queue was cleared has no current item, and a player between items has not finished opening
the next. The property answered all three by throwing `InvalidOperationException`, which is the
exception that means *the caller did something wrong* — and none of those callers had.

The cost showed up in this library's own chrome. `FrameFlowStreamSummary.Refresh` read the
property inside a bare `catch`:

```csharp
// Before, in FrameFlowStreamSummary:
MediaInfo info;
try { info = player.MediaInfo; }
catch { Text = string.Empty; return; }
```

A bare catch to handle an ordinary state, swallowing every other exception on the way past. It is
now an `is not { } info` check. The test double in `FrameFlowVolumeControlTests` had the same
tell from the other side: it returned `default!`, which is null behind a null-forgiving operator,
because there was nothing honest to return.

**This also closes the last silent gap between the two tiers.** `IPlaybackController.MediaInfo`
was already `MediaInfo?`. 13 member names appear on both it and `IMediaTransport`; every other
difference between them either does not compile if confused (`PlaybackStateChanged` versus
`StateChanged` differ in name and payload) or is a different member. `MediaInfo` was the one that
shared a name, shared a type, and disagreed only in whether null was a value or a throw. See
ADR-0024's amendment.

**If you want the old behaviour**, `player.MediaInfo ?? throw new InvalidOperationException(...)`
at your call site says so explicitly.

### 41. `IMediaPlayer` gained `PlaylistChanged`

**Only a caller that implements `IMediaPlayer` itself is affected**, the same shape as entries 31
and 36. An external implementation stops compiling with CS0535 until it adds the member; a caller
that consumes the player from `FrameFlowPlayer.Create()` is untouched and gains a stream.

```csharp
IObservable<PlaylistSnapshot> PlaylistChanged { get; }
```

Nothing observed a queue edit, so a playlist panel or a queue-length readout had to poll
`GetPlaylist()` on a timer it owned. A poll either misses an add-then-remove pair between ticks
or runs faster than the data changes. ADR-0034 leaves *diagnostics* to a caller's own cadence
deliberately, and states the rule that sends this the other way: discrete events that must not be
missed between polls get their own observable.

```csharp
// Before:
_timer = new Timer(_ => Render(player.GetPlaylist()), null, Zero, FromMilliseconds(250));

// After:
_subscription = player.PlaylistChanged.Subscribe(Render);
```

The snapshot is the payload rather than something to fetch afterwards, so a handler renders what
the change produced instead of racing back for a queue that may have moved again.
`PlaylistSnapshot.Revision` orders them.

**It fires for more than the six edit verbs.** A latched jump and each half of a hand-off also
change what a snapshot reports, so they raise it too. The rule is exactly "whenever `Revision`
advances", which is the contract `Revision` already documented. A hand-off is two notifications:
the take moves `Current`, the report flips `CurrentStarted`.

`SourceTransitioned` is unchanged and answers a different question. It fires only on a hand-off
and carries the item's `MediaInfo` and the reason. Subscribe to that one to react to an item
starting, and to this one to redraw a queue.

Raised outside the coordinator's lock, so a handler may call back into the player, on whichever
thread made the change: your thread for an edit, the player's for a hand-off. Delivery is
serialized and in commit order, which is what lets `Revision` order what you receive. The cost is
that a thread changing the queue can wait on another thread's in-flight handler, so keep handlers
short or marshal to your UI thread as you already do for `SourceTransitioned`.

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
  `IMediaPlayer`; `BuildAsync()` still returns `MediaPass`. Additive.
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
