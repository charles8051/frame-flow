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

### 1. `ITimeSource` is gone; `PlaybackClock` takes a `TimeProvider`

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

### 2. `IMediaPlayer.Diagnostics` is gone

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

### 3. `null` hardware decode capabilities now probe, as documented

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

### 4. The playlist player reports failed items, and gives up on a run of them

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

### 5. A fault in lateness recovery no longer stops playback

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
