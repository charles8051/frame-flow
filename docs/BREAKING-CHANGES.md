# Breaking changes

FrameFlow is pre-1.0. `README.md` says the public surface moves freely between
releases, and it does. This file is what that promise owes you in return: the
list, with the fix for each one.

Entries are grouped by the release they ship in and ordered within it by how
likely you are to hit them. Each says what broke, what to write instead, and —
where it is not obvious — why the change was worth making.

**Read the first entry of any group carefully.** Most breaks here are compile
errors, which announce themselves. A few are not, and those are called out.

## Unreleased — since `v0.15.1`

### 1. A pass deactivates its audio sink, and the end of playback waits for the audio to play out

**A change in behaviour, not a compile error.**

`FrameFlowPass.RunToCompletionAsync` used to return as soon as the last buffer had been handed to
the audio sink, and it left the sink active (#609). Now, on a natural end, it awaits the sink's new
`IAudioSink.DrainAsync` and then deactivates the sink. It deactivates on every way out: success,
cancellation and fault.

The player waits for the same drain before it reports the end of any item with audio. That moves
`Ended`, and also `LoopRestarted` under `RepeatMode.One` and `SourceTransitioned` between playlist
items. With `OpenAlAudioSink`, an audio-only item ends up to about a second later than before. Each
loop of it now plays the whole clip, so the loop period grows by the same amount. For an item with
video, the wait is the audio's tail, normally tens of milliseconds.

The device now plays out the buffers it had queued and the partial block it held back, which used to
be dropped. A stream shorter than the sink's pre-buffer depth used to be accepted and never started;
it now plays.

`IAudioSink` gains `DrainAsync`, with a default implementation that completes at once, so an
existing implementation compiles and behaves as before.

At process exit the shared OpenAL device is now closed whatever its lease count (#633). An
`OpenAlAudioSink` still undisposed at that point gives up its device and is inert afterwards, as
when no device could be opened.

**Who hits this.**
- Code that deactivates the sink after `RunToCompletionAsync`. The pass already has, so the call is
  redundant and harmless.
- Code that reads the sink's clock or state after a pass, expecting it still active. It reads a
  deactivated sink.
- A host that times something from `RunToCompletionAsync` returning, or from `Ended`,
  `LoopRestarted` or `SourceTransitioned`. Each now arrives after the item's audio has been heard.
- A custom `IAudioSink` that queues audio ahead of a device. It keeps losing the end of every
  stream that ends naturally until it overrides `DrainAsync`. A pass that is cancelled or faults
  deactivates the sink without draining it, so audio still queued then is dropped either way.
- A sink that wraps another. The default `DrainAsync` completes at once without asking the inner
  sink, so the wrapper must forward it.

**What to write instead.**
- Drop the `DeactivateAsync` after `RunToCompletionAsync`. A sink used by the next pass is
  activated again by that pass.
- In a sink that buffers ahead of its device, implement `DrainAsync`. It should hand the device
  whatever is held back, and complete once the device has played what it was given. It should give
  up if the device stops making progress, and observe the token.
- In a wrapping sink, forward `DrainAsync` to the inner sink.
- To wait for a player to finish, write `await player.WaitForEndAsync(ct)` instead of bridging
  `StateChanged` and `ErrorOccurred` yourself. Subscribe to `ErrorOccurred` or `ItemFailed` too if a
  failed item must count as a failure, because a last item that faults mid-play still ends the queue
  in `Ended`.

### 2. `IMediaTransport` gained `RepeatMode` and `RepeatModeChanged`, and the Loop toggle shows the mode in force

**A compile error for an implementer of `IMediaTransport` or `IMediaPlayer`, and a change in
behaviour for the transport bar's Loop toggle and `LoopByDefault`.**

```csharp
RepeatMode RepeatMode { get; }
IObservable<StateTransition<RepeatMode>> RepeatModeChanged { get; }
```

`IMediaTransport` could set the repeat mode but not read it (#631), so the transport bar's Loop
toggle showed `LoopByDefault` and the last click rather than the mode in force. A player looping a
folder under `RepeatMode.All` showed Loop unchecked, a click on it set `One`, and `All` could not be
chosen from the chrome at all. The two members are the controller's `RepeatMode` and
`RepeatModeChanged`, passed through. The stream keeps the controller's name because it keeps the
controller's `StateTransition<RepeatMode>` payload; `StateChanged` has a name of its own because it
drops the previous state.

The Loop toggle now shows the bound transport's mode and follows `RepeatModeChanged`. A click steps
Off, One, All and back to Off. One and All both show checked, labelled "Loop one" and "Loop all".
`LoopByDefault` sets `One` only on a transport that is not repeating; it used to set `One` on every
bind, whatever the mode. A refused click leaves the toggle on the mode still in force, so the toggle
no longer rolls itself back.

**Who hits this.** A type that implements `IMediaTransport` or `IMediaPlayer` itself, such as a test
fake, an adapter or a decorator, fails with CS0535 until it adds both members, and one compiled
against `v0.15.1` and not rebuilt fails to load with `TypeLoadException` when the application uses
the type. Neither member is a default interface member: the getter could default to
`GetDiagnostics().RepeatMode`, but no default can supply the change stream, and one that never fires
would leave a bound Loop toggle silently stale, which is the defect this fixes. An app that set
`LoopByDefault` and also built or set its player in `All` used to get `One` from the bind; it now
keeps `All`. An app or UI test that clicked the toggle twice to get from Off back to Off now lands
on All, and needs a third click.

**What to write instead.** Forward the members from whatever you wrap:

```csharp
public RepeatMode RepeatMode => _inner.RepeatMode;
public IObservable<StateTransition<RepeatMode>> RepeatModeChanged => _inner.RepeatModeChanged;
```

A fake whose mode never changes returns the mode it holds and an observable that never fires. To
loop one item whatever mode the player was built with, call `SetRepeatModeAsync(RepeatMode.One)`
after binding instead of setting `LoopByDefault`. Code that read the mode from
`GetDiagnostics().RepeatMode` can read `RepeatMode` instead.

### 3. `CreateAsync` opens and warms the model on the thread pool

**A change in behaviour, not a compile error.**

`Yolov8Detector.CreateAsync` and `BlazeFaceDetector.CreateAsync` used to open the session, validate
the model and run the warmup on the caller's thread whenever nothing before them yielded: for YOLO
with a cached model or an override path, and for BlazeFace always (#626). They now run that work in
`Task.Run` and return to an awaiting caller at once. The session factory
(`Func<string, IInferenceSession>`, or `IInferenceSessionFactory.Open`) and the `progress` reporter
are called on a thread-pool thread. `Yolov8Detector.CreateAsync` also checks its token before the
open: a token cancelled after the model is on disk now throws `OperationCanceledException` and
opens nothing, where it used to build the detector.

**Who hits this.** A session factory or progress reporter that must run on the thread that called
`CreateAsync`, such as one that touches a UI control directly rather than through `Progress<T>`. A
caller that passes a token it may already have cancelled and expected a detector anyway.

**What to write instead.** Report through a `Progress<T>` created on the UI thread, which posts back
to it, or marshal in the reporter. To open a session on a thread of your choosing, open it there and
pass it to `Create`, which still runs on the calling thread. A `Task.Run` around `CreateAsync` is no
longer needed.

### 4. A Whisper node starts a new window when its audio jumps

**A change in results, not a compile error.**

`TranscribeWithWhisper` used to take its window's start time from the first buffer and count
forward from there, whatever the later buffers said (#620). A node fed across a seek, a loop or
the next item joined up to one window of earlier audio to the new audio, and stamped every later
caption on the old clock. It now compares each buffer's `PresentationTime` with where the held
audio ends. Off by more than 50 ms either way, the partial window is dropped and a new one starts
at that buffer's time.

Multi-channel input is now mixed to mono before Whisper reads it. With two channels the operator
used to hand Whisper the interleaved samples as if they were one channel, half a window of audio
stamped as a whole one.

**Who hits this.** A source whose buffers do not carry the time they play at: constant or
restarting timestamps over audio meant to run on, such as clips concatenated with each starting
at zero. Every jump now drops the partial window. So does a gap of more than 50 ms in a source
that drops buffers when its consumer falls behind.

**What to write instead.** Stamp each buffer with its position on one running timeline. The
decoder and the resampler already do.

### 5. `CaptionTimeline` starts again when the time goes back

**A change in results, not a compile error.**

`CaptionTimeline` kept the largest PTS `GetActive` had been given, evicted against it, and never
lowered it (#619). After a seek back, a loop or the next item, a caption added at the new position
was evicted as soon as it sat more than `displayDuration` behind the old position, and captions
stamped near the old position showed again when playback reached their stamps. `Add` and `GetActive`
now share one clock: the latest PTS either has been given. A PTS below it starts a new timeline with
nothing on it. The new `Reset()` does the same for a jump the PTS cannot show, such as a short seek
forward.

**Who hits this.** A caller that gives the timeline a PTS below one it gave before, without meaning
a jump. Each such call now empties the timeline. A caller that stamps `Add` with the caption's own
`Caption.From`, which sits behind the presented frame, empties it on every `Add`, so stack mode
never stacks. A caller that drives `GetActive` from a position polled on a timer or read from an
audio clock empties it whenever that position steps back slightly. So does one timeline queried by
two views at different positions, or captions added with a stamp ahead of the frame being presented.

**What to write instead.** Use one timeline per presented stream, and give both `Add` and
`GetActive` the PTS of the frame just presented. Code that built a new timeline at a seek or the
next item can call `Reset()` instead, or rely on the new rule for a jump back.

### 6. `CompositionInteropVideoView.EnsureSink` readies the view to present

**A change in behaviour, not a compile error.**

`EnsureSink` on the zero-copy view used to create its sink and nothing else. The compositor
surface and the GPU interop waited for `Initialize(ILoggerFactory)`, so
`WithVideoSink(view.EnsureSink())`, the `FrameFlowVideoView` pattern, built and played a black
window and logged nothing (#613). `EnsureSink` now runs `Initialize`, without a logger, when it
has not run: once the view is attached, it sets up the compositor surface and starts the stall
watchdog (entry 12). A later `Initialize` call moves the view's logging, and its own sink's, to the
factory it is given. After `DisposeAsync` both throw `ObjectDisposedException` (entry 12).

**Who hits this.** A caller that calls `EnsureSink` off the UI thread on a view already in the
visual tree: it now touches the compositor and must run on the UI thread, as `Initialize` always
had to. Also a caller that took a sink from `EnsureSink` and relied on the view not presenting it,
or not raising `PresenterStalled`, until it called `Initialize`.

**What to write instead.** Wire the view in one call, on the UI thread:

```csharp
// Before
.WithVideoSink(((IVideoSurface)view).AttachSink(loggerFactory))

// After
.WithVideoSurface(view, loggerFactory)
```

`WithVideoSurface(IVideoSurface, ILoggerFactory)` is new on `IPlayerBuilder` and `IPassBuilder`
and takes a `FrameFlowVideoView` too. Code that calls `Initialize` and then `EnsureSink` works
unchanged.

### 7. `Infer`'s chain form takes `onError`

**A binary break, not a compile error.** Source that calls it compiles unchanged.

`InferenceOperators.Infer(chain, id, model, onResult, deviceStage)` takes
`FailureResponse onError = FailureResponse.Propagate` last (#618) and passes it to its node. The
default keeps what the chain form did: a model that throws faults the graph, and a player fails the
item it is playing. `FailureResponse.Discard` loses that frame's result instead, and
`Graph.Discards` counts it under the node's id, as does the `frameflow.graph.discards` metric. A
parameter with a default still changes the method's signature. An assembly compiled against
`v0.15.1` that calls the chain form throws `MissingMethodException` against this release until it is
rebuilt.

**Who hits this.** A library built against `v0.15.1` that calls `chain.Infer(...)` and is loaded
with this release without being rebuilt. An application that builds from source does not.

**What to write instead.** Rebuild. A branch the picture can do without, such as an overlay, can
pass `onError: FailureResponse.Discard`.

### 8. A CUDA provider that cannot load its libraries is ruled out

**A change in behaviour, not a compile error.**

ONNX Runtime's "Failed to load shared library", which the CUDA provider raises when a library it
depends on, such as cuBLAS, is missing, now counts as an unavailable provider (#635). The factory
caches the provider that opens after it, so `ActiveProvider` names that provider where it was
`null`, and later models open there without trying CUDA again. This is what the factory already did
for a missing DLL (`v0.13.0`, entry 14).

**Who hits this.** Code that read a `null` `ActiveProvider` after a CUDA fallback as "fell back",
and a process that expects a later model to try CUDA again once the libraries are on the path.

**What to write instead.** Read `ActiveProvider` for the provider that is running. To probe CUDA
again, build a new factory.

### 9. A bare `null` third argument to `SdlEventLoop.Run` is ambiguous

**A compile error (CS0121).**

`SdlEventLoop.Run` has an overload that takes an `SdlCommandQueue` in the position `onEvent` has
(#605). A call that passes a bare `null` or `default` there, with nothing after it but more `null`s
or named arguments, such as `SdlEventLoop.Run(sdl, sink, null)` or
`SdlEventLoop.Run(sdl, sink, default, ct: token)`, now matches both. A call that passes a handler,
or names the argument, is unaffected, and so is the original overload's behaviour.

**Who hits this.** Code that spells out "no event handler" positionally instead of leaving the
argument out.

**What to write instead.** Drop the argument, or name it:
`SdlEventLoop.Run(sdl, sink, onEvent: null)`.

### 10. `ErrorOccurred` documents that a command's failure into `Error` is raised there too

**Not a change in behaviour.** The documentation now says what the player has always done.

The summary of `IMediaTransport.ErrorOccurred`, and entry 2 of the `v0.9.0-alpha.1` group below,
said a command's own failure comes back on its `Result` and is not repeated on `ErrorOccurred`
(#606). That was never true of a failure that puts the player into `Error`. A `PlayAsync` whose item
cannot be loaded, from `Idle` or `Ended`, returns a failed `Result` and raises `ErrorOccurred`
before it completes, with the same `PlaybackError` instance as `Result.Error`. The behaviour stays:
the `Result` reaches only whoever called, and `ErrorOccurred` is how everyone else hears the player
broke, including an app whose Play button belongs to a chrome control. A command refused with the
state unchanged, such as a play with nothing to play or a seek with nothing loaded, still reports on
its `Result` only.

The same remarks gave "a play on a disposed player" and "a seek on a non-seekable source" as
refusals that come back on the `Result`. Neither is: a transport command issued once the player's
disposal has begun throws `ObjectDisposedException`, as it always has, and the controller has no
seekability check. The docs now say so; #644 asks whether a disposed player should refuse instead.

**Who hits this.** A consumer that took the old summary at its word and handles both channels
reports a failed load twice. One that treated `ErrorOccurred` as "playback broke while nobody was
asking" reacts to a failure its own command already reported. One that relied on a `Result` from a
transport command racing a disposal gets an exception, as it already did.

**What to write instead.** Let `ErrorOccurred` report whatever put the player into `Error`, and the
`Result` report the refusals:

```csharp
player.ErrorOccurred.ObserveOnUiThread().Subscribe(Show);

var played = await player.PlayAsync();
if (!played.IsSuccess && player.State != PlaybackState.Error)
    Show(played.Error); // a refusal; a failure into Error arrives on ErrorOccurred
```

A consumer that sees both copies can also tell them apart by reference: `ErrorOccurred` carries the
instance the `Result` does, and fires before the command completes. Do not issue transport commands
after `DisposeAsync`, or catch `ObjectDisposedException` where a command can race it.

### 11. `ReplaceAsync([])` no longer compiles

**A compile error, on a call that always threw.**

`IMediaPlayer.ReplaceAsync` gained an overload that takes media clips (#656). An empty collection
expression converts to both overloads, so `player.ReplaceAsync([])` is now ambiguous. The call
threw `ArgumentException` before, because a replacement needs at least one item, and it still does.

**What to write instead.** Name the element type, `player.ReplaceAsync(Array.Empty<IMediaSource>())`,
if a test exercises the refusal. Nothing else needs to change.

### 12. `CompositionInteropVideoView` presents again after it rejoins the visual tree

**A change in behaviour, not a compile error.**

**Superseded in part by entries 19 and 20 (ADR-0085).** The view no longer disposes its own sink on
a detach, so the first bullet below no longer holds, and the sink of your own under "What to write
instead" is not needed to keep a player across a trip out of the tree. The rest stands.

The zero-copy view treated its first detach from the visual tree as a dispose (#650). A view that
rejoined the tree, as in a `TabControl` that unloads its inactive tabs, a docking layout, or a
`FrameFlowPlayerView` whose `VideoSurface` was swapped away and back, set no surface up and showed a
black rectangle, and a second detach released nothing. A detach now tears the compositor side down
and leaves the view reusable, as `FrameFlowVideoView` is: rejoining the tree sets the surface up
again and restarts the stall watchdog. Only `DisposeAsync` is final.

- The view's own sink is still disposed on each detach. `EnsureSink` after a detach returns a new
  one, which the view presents once it is back in the tree and disposes on the next detach or
  `DisposeAsync`. Before, that sink was never presented and never disposed. The disposed sink's
  `GetDiagnostics` reads its final counts until the new one exists, and
  `VideoSinkDiagnosticsSnapshot.Empty` from then on, as a sink replaced through `Sink` does.
- A sink assigned through `Sink` stays assigned across a detach, and the view presents it again
  when it rejoins the tree. `Sink` used to read `null` after a detach; it now does after
  `DisposeAsync`.
- The stall watchdog runs only while the view is in the tree. It used to start on `Initialize`
  whether or not the view was attached, so `PresenterStalled` could be raised for a view that was
  not on screen.
- `DisposeAsync` after a detach releases a sink made since, without a second teardown.
- After `DisposeAsync`, `EnsureSink`, `Initialize`, `IVideoSurface.AttachSink` and assigning a sink
  through `Sink` throw `ObjectDisposedException` (#665). They used to make or bind a sink that the
  view never presented and nothing disposed, along with the last frame it was given.

**Who hits this.** Code that read `Sink` after a detach and took `null` to mean the view was done
with a sink it had assigned, code that counted on the stall watchdog before the view joined the
tree, and code that wires a view it has already disposed. Code that worked around the bug by
building a new view for each attach keeps working.

**What to write instead.** Nothing, to reuse a view. The player keeps the sink it was built with,
and the view's own sink is disposed on a detach. Where one player outlives the view's trips out of
the tree, give the view a sink it does not own, which it keeps across them:

```csharp
var sink = new CompositionInteropVideoSink();
view.Sink = sink;
view.Initialize(loggerFactory);
// ... .WithVideoSink(sink)
```

Call `DisposeAsync` to end the view for good.

### 13. `IMediaPlayer` and `IPlayerBuilder` gained members for media clips

**A compile error for a type that implements either interface.** Code that calls them compiles
unchanged.

`IMediaPlayer` gained `EnqueueAsync`, `SetNextAsync`, `AddAsync` and `ReplaceAsync` overloads that
take a `MediaClip`, and `PrepareAsync`. `IPlayerBuilder` gained a `WithMedia` overload that takes a
`MediaClip`, and `WithNextItemPrepared` (#656). A type outside FrameFlow that implements either, such
as a test double or a decorator, stops compiling until it adds them. One compiled against `v0.15.1`
and not rebuilt fails to load with `TypeLoadException` when the application uses the type.

**Who hits this.** An implementer of either interface. FrameFlow's own come from
`FrameFlowPlayer.Create`.

**What to write instead.** Implement them, or forward them to the instance you wrap:

```csharp
public Task<Result> PrepareAsync(PlaylistItem item, CancellationToken cancellationToken = default) =>
    _inner.PrepareAsync(item, cancellationToken);
```

### 14. Frames and buffers carry the timeline they belong to

**Source and binary compatible for almost all code. A new name can clash, a doc reference can
become ambiguous, and a custom node that builds frames or buffers has a new duty.**

Each frame and audio buffer now says which timeline it belongs to: one continuous run of
presentation times, which a load, a seek, a loop or the next item ends (ADR-0084, #677). The player
gives every run a new `TimelineId`, the decoders stamp it on everything they decode, and every
in-repo node that builds a new frame, buffer, inference result or caption from an input copies the
input's. Frames from a camera, and from a synthetic source, are on `None` for now.

- `IFrame` and `IAudioBuffer` gained `Timeline`, a default interface member that returns
  `TimelineId.None`. An existing implementation compiles and runs unchanged and reports `None`.
- `CpuVideoFrame`, `GpuVideoFrame` and `PcmAudioBuffer` store a `Timeline`.
  `CpuVideoFrame.Create`, `PcmAudioBuffer.Create` and `TensorToImage.ToFrame` each gained an
  overload that takes the timeline, after the duration (or the presentation time, for audio). The
  existing signatures stay and produce `None`, so a binary built against `v0.15.1` keeps running.
- `FramePresentedInfo` gained `Timeline`, the presented frame's, as an init-only property rather
  than a third positional member. The two-member constructor and deconstruction are unchanged.
  `AvaloniaVideoSink` and `CompositionInteropVideoSink` fill it in. Two `FramePresentedInfo` values
  now compare equal only when their timelines do as well.
- `InferenceResult<T>` gained an init-only `Timeline`, which the inference node sets from the frame.
  `DetectedVideoFrameRef` and `DetectedFaceFrameRef` report their frame's.
- `CaptionRef` gained `Timeline` and a constructor that takes it; the existing constructor stays and
  produces `None`. The transcription node gives each caption the timeline of the audio its window
  started on, and `SplitOnPunctuation` and `AnimatedReveal` copy it. The `Caption` record is
  unchanged.
- `FrameFlow.Graph` gained `TimelineId`, and `FrameFlow.Graph.Core` gained `TimelineFollower`,
  `PresentedTimeline` and `TimelineRelation`, the two pure followers a stateful consumer uses to
  tell a new timeline from the current one. Entries 15 and 16, and the PRESENT GAP note under
  "Not breaking", list the consumers in the library that now follow them.

**Who hits this.**
- A doc comment that names `CpuVideoFrame.Create{TState}`, `PcmAudioBuffer.Create{TState}` or
  `TensorToImage.ToFrame` without parameters. The reference is now ambiguous, which is a warning
  (CS0419), and an error where warnings are.
- A file that imports `FrameFlow.Graph` and another namespace with a type of its own named
  `TimelineId`: the name is now ambiguous (CS0104).
- A node outside FrameFlow that builds a new frame, buffer or caption from an input, such as a
  filter or a converter. It compiles and runs as before, but what it hands on is on `None`, so a
  consumer that follows timelines treats it as continuous across every seek and loop, as it did
  before.
- A type that implements `IFrame` or `IAudioBuffer` and already has a member named `Timeline` of
  another type. It does not implement the new member, so a caller through the interface reads
  `None`.

**What to write instead.**
- Name an overload in the cref, or write the method name as `<c>` text.
- Qualify the type, or alias one of the two.
- In a node that builds an item from an input, pass the input's timeline to the overload, as the
  in-repo converters do with pixel shape and rotation:

```csharp
return CpuVideoFrame.Create(
    PixelFormat.Bgra32, input.Width, input.Height, input.Pts, input.Duration,
    input.Timeline,
    state, fill,
    sampleAspectRatio: input.SampleAspectRatio, rotation: input.Rotation);
```

- In a frame or buffer type of your own, store the id it was made on and return it from `Timeline`.

### 15. Captions follow the timeline they belong to

**A change in results for a Whisper node fed audio that names its timeline, and a doc-reference
break for `CaptionTimeline`. Code whose frames and audio name no timeline behaves as before.**

`TranscribeWithWhisper` now follows its input's timeline (ADR-0084, #677). A buffer of a timeline
other than the held audio's drops the partial window and starts a new one at its own time, however
close that time is to where the held audio ends, so a seek of a few milliseconds no longer joins
audio of two places. A buffer of the same timeline continues the window wherever its time sits.
The 50 ms rule of entry 4 now applies only to buffers on `TimelineId.None`. Audio the player
decodes names its timeline, and the in-repo resampler keeps it, so this is the path a node fed
from a player takes.

`CaptionTimeline` gained `GetActive(TimeSpan, TimelineId)`, given the presented frame's timeline
(`FramePresentedInfo.Timeline`), and `Add(Caption, TimeSpan, TimelineId)`, given the caption's own
(`CaptionRef.Timeline`). With them:

- A frame on a timeline other than the one on screen starts a new one with nothing from the one it
  left, whichever way its time moved.
- A caption of a timeline the screen has left is dropped, which fixes captions in flight at a seek
  or a loop (#641).
- A caption of a timeline not yet presented is held, even one added before the first frame, and
  shows from that timeline's first frame (#687).
- On one timeline, a time below the clock no longer empties it.

The existing overloads are the same calls on `None` and keep entry 5's behaviour. `Reset()` now
also drops held captions, and it keeps which timelines have been presented.

**Who hits this.**
- A Whisper node on player audio behind a queue that drops buffers, such as a bounded channel that
  drops the oldest when transcription falls behind. A gap on one timeline no longer starts a new
  window: the audio on both sides of it is joined, and the times of the captions after it run
  early by the length of the gap (#682). On `None`, a gap still starts a new window.
- A doc comment that names `CaptionTimeline.Add` or `CaptionTimeline.GetActive` without
  parameters. The reference is now ambiguous (CS0419), which is an error where warnings are.
- A frame or buffer type of your own that keeps one timeline id across a jump in its times. Within
  one id the window no longer looks at the time.

**What to write instead.**
- In the frame-presented handler, pass both timelines:
  `captions.Add(caption, frame.PresentationTime, captionTimeline)` for each caption that has
  arrived, where `captionTimeline` is the `CaptionRef.Timeline` it came with, then
  `captions.GetActive(frame.PresentationTime, frame.Timeline)`. A run counter, a check that the
  time went back, or a `Reset()` at a seek that only told stale captions from current ones can go.
  `FrameFlow.Examples.LiveCaptioning` shows the handler.
- Name an overload in the cref, or write the name as `<c>` text.
- Size a dropping queue in front of the node so that transcription keeps up, or accept that a gap
  joins.
- Give a new id at every jump in a source's own frames or buffers.

### 16. `PresentedResults` and `SyncJoinNode` follow the timeline

**A change in behaviour, not a compile error.**

`PresentedResults<T>` showed, for each presented frame, the latest result at or before its
timestamp, and knew of a seek or a loop only from a timestamp that went back. A new item whose
timestamps did not go back showed the old item's result on its first frames (#621). It now follows
the timeline on screen, through `FramePresentedInfo.Timeline` and `InferenceResult<T>.Timeline`
(ADR-0084):

- A frame shows only a result of its own timeline, or one on `None`.
- When a new timeline reaches the screen, the results of the one it left are dropped, the one
  showing included. `Cleared` is raised unless the new frame has a result of its own.
- A result of a timeline the screen has left is dropped when it is posted.
- A result of a timeline not yet on screen waits for that timeline's first frame. It no longer
  shows on the frames still presenting from the timeline before, and posting it no longer drops the
  results that timeline is still showing.

`SyncJoinNode` follows the timeline of each of its inputs, and never compares one input's timeline
with the other's:

- A primary on a new timeline releases every secondary the window holds, whether its time went
  forward or back.
- A primary that stays on one timeline is no longer judged by its time: a step back of more than
  `Window` does not start a new timeline in the window.
- A secondary on a new timeline releases what the window kept from the one before.
- `SyncJoinKeys` gained `PrimaryTimeline` and `SecondaryTimeline`, optional selectors. Without them
  the join reads `IFrame.Timeline` or `IAudioBuffer.Timeline`, and any other item is on `None`.

An item on `TimelineId.None` keeps the old rules, and each side of a join is judged on its own.
That covers results built without a timeline, a presenter that does not fill
`FramePresentedInfo.Timeline` (every result then keeps the timestamp rules, whatever it carries),
and a join input that is neither a frame nor a buffer and has no selector.

A join inside a player's own graph sees no change at a seek, a loop or the next item. Each of those
starts a new run of the graph, and the join starts every run with an empty window. The join changes
reach a graph that outlives a break, such as one fed from the player through a tap or a channel.

**Who hits this.**
- A `Cleared` handler that took it to mean the picture went back. It is now also raised when a new
  item, a seek forward or a loop reaches the screen while the old item's result is showing.
- A join whose secondary leads its primary, as `SyncMatch.Within` expects, in a graph that outlives
  a break. The primary's break releases the new timeline's secondaries that arrived before it, so
  the new timeline's first primaries match nothing until the next secondary arrives. When the
  secondary's break arrives first, it releases what the old timeline's last primaries would have
  matched. Before, a break forward kept both.
- A join whose primary is a frame or a buffer and whose secondary is not, such as a `CaptionRef` or
  a `RefBox<T>`. Its primary side changes at once: a new timeline releases the window, and a step
  back on one timeline is no longer a break. Only the release on the secondary's own break waits
  for a `SecondaryTimeline` selector.
- A join whose primary steps back in time within one timeline, which a player's frames do not do.
  The window no longer starts again for it.
- Code that compares two `SyncJoinKeys` values. As a record, two keys are now equal only when their
  timeline selectors are too.

**What to write instead.**
- To clear an overlay at a break, handle `Cleared`. There is no need to watch transport events for
  it, or to make a new `PresentedResults<T>` for each graph the player builds.
- For a caption secondary, name the caption's timeline:

```csharp
var keys = new SyncJoinKeys<IVideoFrame, CaptionRef>(
    f => f.Timestamp,
    c => (c.Value.From, c.Value.To))
{
    SecondaryTimeline = c => c.Timeline,
};
```

- To keep a join on the old rules, for example one whose secondary leads across breaks, name no
  timeline on either side:

```csharp
var keys = new SyncJoinKeys<IVideoFrame, CaptionRef>(
    f => f.Timestamp,
    c => (c.Value.From, c.Value.To))
{
    PrimaryTimeline = _ => TimelineId.None,
    SecondaryTimeline = _ => TimelineId.None,
};
```

- In a presenter of your own, set the presented frame's timeline on what it raises:
  `new FramePresentedInfo(pts, presentedAt) { Timeline = frame.Timeline }`.

### 17. A disposed video sink disposes the frames it is given

**A change in behaviour, not a compile error.**

`CompositionInteropVideoSink` went on taking frames after `DisposeAsync`. It counted each one in
`FramesAccepted` and kept the newest in its slot, where nothing would take or dispose it (#664). A
player still presenting into it, such as one built against a view that had since left the visual
tree, pinned a decoder frame there for as long as the sink object lived. It now disposes a frame it
is given after its disposal, does not count it and keeps nothing, as the other in-box sinks do
(ADR-0085, decision 4).

`AvaloniaVideoSink`, `SdlVideoSink` and `CompositionInteropVideoSink` keep their newest frame in a
`LatestWinsFrameSlot`, and a present that raced the sink's dispose could install its frame after the
dispose had emptied the slot. The dispose now closes the slot in the same step, and a frame given to
a closed slot is disposed. A second `AvaloniaVideoSink.DisposeAsync` does nothing; it used to log
its disposal line again.

`LatestWinsFrameSlot` gained `Close`, `Install` and `Installed`. `Install` returns a
`FrameSlotInstall` that says whether the frame was installed, superseded a pending one, or was
refused by a closed slot. `Installed` counts the frames installed, and `CompositionInteropVideoSink`
reports it as `FramesAccepted`. `TrySet` into a closed slot disposes the frame and returns `false`.
A slot that is never closed works as before.

**Who hits this.** Code that read `FramesAccepted` from a disposed `CompositionInteropVideoSink` to
see whether a player still fed it: it now stays where the dispose left it. Also a log check that
counted two disposal lines from one `AvaloniaVideoSink` disposed twice.

**What to write instead.** Read `FramesAccepted` from the sink the view presents now, and ask the
player whether it is still playing. A sink of your own built on `LatestWinsFrameSlot` gets the same
behaviour by calling `Close` from its dispose, and by counting nothing for a frame `Install` returns
`Refused` for.

**Why.** A kept frame can be a decoder surface from a fixed hardware pool. A disposed sink that keeps
one takes that surface out of the pool for as long as the sink object lives.

### 18. A second player or pass built against a held in-box sink fails

**A change in behaviour, not a compile error.**

`AvaloniaVideoSink`, `CompositionInteropVideoSink`, `SdlVideoSink`, `HeadlessVideoSink` and
`OpenAlAudioSink` implement the new `IClaimableSink` (ADR-0085, decision 6; #286).
`BuildPlayerAsync`, the pass builder's `BuildAsync` and `PlaybackController.Create` claim each such
sink they are given, video and audio, before they build anything else. A build against a sink that
another player, controller or pass holds throws `InvalidOperationException`, naming the sink and its
holder. Nothing stopped this before: two players on one view's sink interleaved their frames on one
picture, and two sessions on one `OpenAlAudioSink` raced its activation and both mastered its clock.

The claim is released once the holder's `DisposeAsync` has completed, which is after the last frame
it sends has reached the sink. A player that is paused, has ended or is in `Error` still holds it,
and so does a pass whose run has ended: a pass holds its claim until it is disposed, not until its
run ends. A pass disposed while its run is still going cancels the run, and releases the claim once
the run has ended (entry 24). A build that fails or is cancelled releases its claims before it
throws. A build against an in-box sink that has been disposed throws `ObjectDisposedException`,
where it used to build a player whose every frame the sink discarded.

The items of one player's queue, including one it prepares ahead, share that player's claim. A graph
built by hand (`SinkAdapters.AsSinkNode`, a `Branch` edge inside a configurator, `RecorderPipeline`'s
preview) takes no claim, and neither does a sink without the interface, such as
`DelegatingVideoSink`, so the views behind one are not claimed either; `FanOutVideoSink` passes the
claim on (entry 21). Those keep ADR-0044's rule, which nothing enforces; #694 asks whether a graph
should claim its sinks for its run.

**Who hits this.**
- Code that builds the next player or pass against a sink before the one already built against it
  has been disposed: one that pauses or replaces the old player instead, disposes it without
  awaiting, or builds the new one first and disposes the old one after.
- Code that runs several passes over one sink while each stays in scope, for example a series of
  `await using var pass = ...` declarations in one method, which are disposed only when the method
  returns.
- Code that builds against an in-box sink that has been disposed, such as a view's own sink after
  the view's `DisposeAsync`. A view's own sink used to be disposed by a detach too, so a chain
  written before the view left the visual tree and built after it hit this. That case is gone with
  entries 19 and 20: a view keeps its sink until it is disposed.

**What to write instead.** Await the disposal of the player or pass already built against the sink,
then build the next one:

```csharp
await player.DisposeAsync(); // pausing it is not enough, and neither is a dispose not awaited
player = await FrameFlowPlayer.Create()
    .WithMedia(next)
    .WithVideoSink(sink)
    .BuildPlayerAsync();
```

To run passes over one sink in turn, end each one's scope before the next is built. The dispose is
enough even while a run is still going, because it cancels the run and waits for it (entry 24):

```csharp
foreach (var file in files)
{
    await using var pass = await FrameFlowPass.Create(file).WithVideoSink(sink).BuildAsync();
    await pass.RunToCompletionAsync();
} // disposed here, before the next iteration builds against the sink
```

To change what one player plays, keep the player and call `ReplaceAsync`. `IClaimableSink.Claimant`
names who holds a sink, for a message of your own. A sink of your own takes part by implementing
`IClaimableSink`: compose a `SinkClaimGate`, forward the three members to it, and call its `Close`
from `DisposeAsync`.

**Why.** ADR-0044 says one sink serves one player at a time, and nothing enforced it. ADR-0085 keeps
a view's sink for the view's whole life, so two players built against one view would otherwise feed
it for as long as both live. A claim taken at build fails before anything is decoded, and names the
cause.

### 19. `CompositionInteropVideoView` keeps its sink until it is disposed

**A change in behaviour, not a compile error.**

The zero-copy view disposed its own sink on every detach from the visual tree and made a new one on
the next `EnsureSink`. A player built with the first sink went on feeding a disposed sink, and the
view showed nothing until the app built the player again. The view now keeps one sink for its whole
life, its own or one assigned through `Sink`, and a detach only stops it presenting: the compositor
side and the stall watchdog go, as before, and a reattach presents the same sink again. A player
built before the trip keeps working (ADR-0085, decision 2; #691).

- The view's own sink is no longer disposed on a detach; only `DisposeAsync` disposes it. `Sink`
  no longer reads `null` after a detach, and `EnsureSink` and `IVideoSurface.AttachSink` return the
  same sink after a reattach. This replaces what entry 12 says about the view's own sink.
- While the view does not present, before its first attach as after a detach, its sink keeps only
  its newest frame, and only while a player holds its claim (entry 18). The view hands back the
  frame it presented last as it leaves the tree, so that frame is the newest unless the player
  sends another, and the reattach presents it first: a paused, ended or single-frame picture is
  back at once. With no claim, as when a graph built by hand feeds it, the sink disposes each frame
  it is given and does not count it in `FramesAccepted`, and releasing the claim disposes the frame
  it kept. Nothing is presented and `FramePresented` is not raised until the reattach, which raises
  it again for a frame presented again. The new `CompositionInteropVideoSink.FramesDiscardedOffScreen`
  counts the frames let go of off screen; `FramesSuperseded` and the diagnostics snapshot's
  `FramesDropped` do not.
- While the view presents, it holds the frame it presented last until it has presented the next
  one, where it used to release each frame once presented. On a paused player that is one decoder
  frame held for as long as the view shows it. `MaxHeldFrames` already counted that frame and does
  not change. `DisposeAsync`, assigning or clearing `Sink`, and the release of the claim on the
  view's sink once its player is disposed release it; the picture stays up.
- A caller's sink the view lets go of, by `Sink` or `DisposeAsync`, stays as no view presents it.
  With no claim it disposes every frame it is given and `FramesAccepted` stops counting, until a
  view takes it up again.
- `FramePresented` is no longer raised for a hand-off that completes after a detach or after
  `Sink` is assigned. The view's `FramesCommitted` still counts it.
- `PresenterStalled` is no longer raised while the view is not presenting.
- Assigning `Sink` first claims the outgoing sink. While a player or pass holds it, the assignment
  throws `InvalidOperationException` naming the holder, and nothing changes. Otherwise the outgoing
  sink is disposed, if the view made it, or let go of, as before.
- Assigning `null` to `Sink` while the view is in the visual tree takes the compositor side down and
  stops the stall watchdog, as a detach does. The last frame used to stay on screen.
- `DisposeAsync` on a view that never joined the visual tree runs no compositor teardown, and logs
  none.
- A reattach after the view presented D3D12VA frames presents nothing until the previous D3D12
  converter has been disposed, which releases the decoder frames it still held.

**Who hits this.** Code that relied on a detach to dispose the view's own sink, or read `Sink` after
a detach and took `null` to mean the view was done with it. Code that took a sink from `EnsureSink`
after a reattach and built a new player with it while the old player was still alive: the sink is
the old one, and the build fails at its claim. Code that assigns `Sink` while a player built against
the outgoing sink is alive, or clears `Sink` to blank the view and expects the last frame to stay.

**What to write instead.** Nothing, to keep a player across a tab switch, a panel swap or a docking
move: build it once against the view. To replace the sink, dispose the player first:

```csharp
await player.DisposeAsync(); // the sink stays claimed until this completes
view.Sink = replacement;     // disposes the view's own sink, or lets a caller's go
```

A sink of your own that you gave the view only to outlive a detach, as entry 12 suggested, keeps
working, and is no longer needed for that. Dispose the view to release its own sink.

**Why.** A player depends on its sink, not on where the view sits in the visual tree, and a sink
disposed on every detach stranded the player built with it on every tab switch.

### 20. `FrameFlowVideoView` keeps its sink until it is disposed

**A change in behaviour, not a compile error.** The view also gains `IAsyncDisposable`.

The CPU view disposed its own sink on every detach from the visual tree and made a new one on the
next attach. A player built with the first sink went on feeding a disposed sink, which disposed every
frame it was given, and the picture froze until the app built the player again. A sink assigned
through `Sink` stayed assigned across the detach but was never bound again, so it froze the same way
(#663). The view now keeps one sink for its whole life, its own or one assigned through `Sink`, and a
detach only stops it presenting: a reattach binds the same sink again and presents it. A player
built before the trip keeps working (ADR-0085, decision 2; #691).

- The view's own sink is no longer disposed on a detach; only `DisposeAsync` disposes it. `Sink` no
  longer reads `null` after a detach, and `EnsureSink` and `IVideoSurface.AttachSink` return the same
  sink after a reattach.
- The view no longer copies, presents or counts a frame before its first attach, and no longer
  raises `FramePresented` for one (decision 3). Until then, as after a detach, its sink keeps only its
  newest frame, and only while a player holds its claim (entry 18), and the attach presents that
  frame: a paused, ended or single-frame picture is on screen at once. With no claim, as when a graph
  built by hand feeds it, the sink disposes each frame it is given, and releasing the claim disposes
  the frame it kept.
- The picture the view drew last stays in its bitmaps across a detach and is drawn again on the
  reattach. `Clear` and `DisposeAsync` release them.
- The new `AvaloniaVideoSink.FramesDiscardedOffScreen` counts the frames let go of while no view
  presented the sink, and a copy the view had made and not yet drawn when it stopped presenting the
  sink. `DroppedFrameCount` and the diagnostics snapshot's `FramesDropped` leave them out. A copy
  stranded by a detach or by assigning `Sink` used to be counted as a drop.
- A caller's sink the view lets go of, by `Sink` or `DisposeAsync`, stays as no view presents it.
  With no claim it disposes every frame it is given, until a view takes it up again.
- `DisposeAsync` disposes the view's own sink, lets go of a caller's, and releases the bitmaps.
  Afterwards `EnsureSink`, `IVideoSurface.AttachSink` and assigning a sink through `Sink` throw
  `ObjectDisposedException`, and attaching the view does nothing.
- Assigning `Sink` first claims the outgoing sink. While a player or pass holds it, the assignment
  throws `InvalidOperationException` naming the holder, and nothing changes. Otherwise the outgoing
  sink is disposed, if the view made it, or let go of, as before.

**Who hits this.** Code that relied on a detach to dispose the view's own sink, or read `Sink` after
a detach and took `null` to mean the view was done with it. Code that took a sink from `EnsureSink`
after a reattach and built a new player with it while the old player was still alive: the sink is
the old one, and the build fails at its claim. Code that counted on frames being copied, or
`FramePresented` raised, before the view joined the visual tree. Code that assigns `Sink` while a
player built against the outgoing sink is alive.

**What to write instead.** Nothing, to keep a player across a tab switch, a panel swap or a docking
move: build it once against the view. To replace the sink, dispose the player first:

```csharp
await player.DisposeAsync(); // the sink stays claimed until this completes
view.Sink = replacement;     // disposes the view's own sink, or lets a caller's go
```

Avalonia never disposes a control. To release the view's own sink and its bitmaps, dispose the view
after the player built against it:

```csharp
await player.DisposeAsync();
await view.DisposeAsync();
```

**Why.** A player depends on its sink, not on where the view sits in the visual tree, and the CPU
view now follows the same lifecycle as the zero-copy view (entry 19).

### 21. `FanOutVideoSink` and `DelegatingVideoSink` are in `FrameFlow.Media`

**A compile error if you kept a copy of the examples' `DelegatingVideoSink`; new types otherwise.**

`FrameFlow.Media` gains two video sinks (ADR-0085, decision 8; #611).

- `FanOutVideoSink` presents each frame it is given to several sinks. Registered as a player's one
  video sink, it sits behind the player's pacer, so its targets show each frame at the same moment,
  which a `Branch` to each of them does not (#636). A target can come with a transform that makes
  its frame, such as a filtered copy; the transform keeps the frame's dimensions, pixel format,
  pixel shape and rotation, and a frame that does not is disposed and fails that target's call.
  `MaxHeldFrames`, `AcceptedDomains` and `PreferredBackends` are derived from the targets. A claim
  on it claims every claimable sink beneath it, through nested fan-outs, so a player built against
  it holds the views it presents to (entry 18), and a build against a view another player holds
  fails naming that view. Disposing it disposes none of its targets.
- `DelegatingVideoSink` hands each frame to a delegate, for a sink that ends in your own code. You
  declare its `MaxHeldFrames`, `AcceptedDomains` and `PreferredBackends`. It claims nothing.

The examples' `DelegatingVideoSink` is gone, and the multicast examples present through a
`FanOutVideoSink`.

**Who hits this.**
- An app with its own copy of the examples' `DelegatingVideoSink` in a namespace it imports beside
  `FrameFlow.Media`: the name is now ambiguous (CS0104).
- An app that presents to several sinks from inside one delegate sink, as the examples did. It
  still works, but a player built against it claims none of those sinks.

**What to write instead.** Delete the copy and use the library type. The delegate stays in the
constructor; the rest are init properties:

```csharp
// Before: the examples' copy
var sink = new DelegatingVideoSink(WriteAsync, maxHeldFrames: 0);

// After
var sink = new DelegatingVideoSink(WriteAsync) { MaxHeldFrames = 0 };
```

To show each frame in several views, present through a fan-out rather than from a delegate. A
fan-out over plain sinks takes them directly, `new FanOutVideoSink(first, second)`; name each
target with `FanOutTarget` when one of them has a transform:

```csharp
var panes = new FanOutVideoSink(
    FanOutTarget.Of(direct),
    FanOutTarget.Of(detection),
    FanOutTarget.Transform(filtered, frame => Filter(frame)));
```

**Why.** The only delegate sink lived in the examples, which are not packed, so the v0.10.0 note
that pointed at it could not be followed (#611). A fan-out in the library passes a player's claim
on to the views it feeds, which a delegate cannot do, because it cannot see what its delegate
calls.

### 22. A video sink owns the frame it is given even when its present throws

**A change in behaviour, not a compile error.**

A player hands each frame to its video sink through its pacer. When the sink's `PresentAsync` threw,
or was cancelled because the player stopped, the pacer disposed the frame again. `HeadlessVideoSink`
disposes a frame and then throws on a cancelled token, and `FanOutVideoSink` (entry 21) disposes its
reference and then throws when a target fails, after each target has taken its own, so behind a
player either one's frame was released twice. A release past the last reference is counted in
`RefCounting.OverReleases` and breaks into an attached debugger in a Debug build, and when something
else still held the frame, such as a sink that keeps the newest one, its buffer went back to its pool
while that holder was still using it (#701).

The pacer no longer disposes a frame it has handed on. The sink owns the frame from the call to
`IVideoSink.PresentAsync`, even when the call throws or is cancelled, which is now what the interface
says, and what a pass and a graph built with `SinkAdapters.AsSinkNode` already did. A frame that
fails before it reaches the sink, because the sink's `OnFormatChangedAsync` threw or the player
stopped while the frame waited for the sink, is still disposed once. `IAudioSink.PresentAsync` now
says the same of a buffer, which its one caller in the library already did.

**Who hits this.** A sink of your own whose `PresentAsync` can throw without disposing the frame.
Under a player, the pacer used to release that frame for it, and now nothing does: the frame leaks,
and a frame from a fixed pool, such as a hardware decoder's, stays out of the pool.

**What to write instead.** Dispose the frame on every path out of `PresentAsync`, including the one
that throws, unless something you handed it to owns it by then:

```csharp
public async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
{
    try
    {
        await DrawAsync(frame, ct);
    }
    finally
    {
        frame.Dispose();
    }
}
```

**Why.** Only the sink knows whether it disposed the frame, kept it or handed it on before it threw,
so a caller that disposes the frame after the throw cannot be right for every sink. ADR-0085 states
the same rule for the targets of a fan-out (decision 8).

### 23. `FrameFlowPlayerView` swaps a surface out under its own claim

**A change in behaviour, not a compile error.** `FrameFlowPlayerView` also gains `IAsyncDisposable`.

Assigning `FrameFlowPlayerView.VideoSurface` took the old surface out of the visual tree and put the
new one in, whatever fed the old one. A player built against the old surface went on feeding it while
the new surface showed nothing, and nothing said so. A player's sink is fixed when it is built, so the
player view now refuses a swap that would strand a player, as far as claims show (ADR-0085, decision
7; #691).

- Swapping out a `FrameFlowVideoView` or a `CompositionInteropVideoView` first claims the surface's
  sink for the player view, as a player would. While a player or pass holds it, the assignment throws
  `InvalidOperationException` naming the holder, and nothing changes. Otherwise no build can claim the
  sink during the swap, and the surface leaves the visual tree keeping its sink (entries 19 and 20). A
  surface you supplied that has no sink yet has nothing to claim.
- The default surface, the `FrameFlowVideoView` the player view constructs, stays alive when it is
  swapped out, and stays claimed by the player view until it is swapped back in or the player view is
  disposed. A build against it in the meantime fails at its claim, naming the player view, instead of
  playing to a surface that is not shown. A default surface with no sink yet is given one to claim.
- A surface you supplied goes back to you with the player view's claim released, and is yours to
  dispose, as before. A surface of your own type exposes no sink to the player view and is swapped out
  without a check, as before.
- `FrameFlowPlayerView.DisposeAsync` disposes the default surface, hosted or swapped out, then
  releases the player view's claim on it, and touches nothing you supplied. The default surface
  disposes its sink even while a player still feeds it, so dispose the player first. Dispose is
  final, as it is on the views: afterwards assigning `VideoSurface` another surface throws
  `ObjectDisposedException`.

The chrome controls that follow a transport now run on the views' lifecycle (decision 1). What they
show does not change. The seek bar's and the position label's refresh ticks run only while a transport
is bound, which changes nothing either shows. `FrameFlowSeekBar` changes in three ways, all at the end
of a binding, which leaving the visual tree or replacing `Transport` brings:

- A bar that has left the visual tree no longer seeks when its `Value` is written, and seeks again
  from its return. One given a transport before it first joins the tree still seeks, as before.
- A scrub target still queued behind a seek in flight when the binding ends is dropped. It used to be
  sent after the bar had left the tree, or to the transport just replaced.
- The refusal of a seek sent before the binding ended is no longer reported. The bar arms its refusal
  report again on every bind, so after every return to the tree as well as when `Transport` is set; it
  used to arm it again only when `Transport` was set or a seek succeeded.

**Who hits this.** Code that swaps `VideoSurface` while a player built against the outgoing surface is
alive, which used to strand that player without a word. Code that keeps a reference to the default
surface, swaps it out and builds a player against it. Code that assigns `VideoSurface` after disposing
the player view. Code that writes a `FrameFlowSeekBar`'s `Value` while the bar is out of the visual
tree, to seek, or counts the warnings a bar logs for refused seeks.

**What to write instead.** Dispose the player, swap, and build the next player against the surface
now hosted:

```csharp
await player.DisposeAsync(); // the sink stays claimed until this completes
playerView.VideoSurface = new CompositionInteropVideoView();
player = await FrameFlowPlayer.Create()
    .WithMedia(path)
    .WithVideoSink(playerView.AttachSink(loggerFactory))
    .BuildPlayerAsync();
```

Avalonia never disposes a control. To release the default surface and its sink, dispose the player
view after the player built against it:

```csharp
await player.DisposeAsync();
await playerView.DisposeAsync();
```

To seek, call `SeekAsync` on the transport rather than writing a seek bar's `Value`.

**Why.** A swap that took the surface out from under a player left it feeding a picture nobody saw,
and the player view cannot move a player to another sink. Taking the claim is the check that no player
feeds the surface, and it keeps a build from claiming the surface while the swap runs. The chrome
controls had their own ways of following a transport across trips out of the visual tree; they now
share the views' lifecycle and its binding number.

### 24. Disposing a pass cancels its run and waits for it to end

**A change in behaviour, not a compile error.**

`MediaPass.DisposeAsync` freed the demux pump pipeline, the decoders and the demux session at once,
without looking at the run. A run still going read packets and decoded from what had been freed, so
a pass disposed during its run, by an `await using` scope left early or by a dispose racing a
cancellation, could crash the process in native code or decode from freed memory (#696).

Now the dispose cancels a run still in flight and waits for it to end, and only then frees the
pass's state and releases the claims on its sinks. A caller awaiting `RunToCompletionAsync` sees
`OperationCanceledException`, as it would for its own token, where it used to see the run carry on
over freed state and succeed or fail. A run asked for once the dispose has begun throws
`ObjectDisposedException`. The dispose throws for neither the run's cancellation nor a fault the
run ends with, and logs the fault at Warning. A dispose made while another is under way waits for
it, where it used to return at once.

**Who hits this.**
- Code that disposes a pass during its run and then awaits or observes the run, expecting it to
  finish as it would have. It now ends in `OperationCanceledException`.
- Code that disposes a pass from inside its run, such as in a sink's `PresentAsync` or a node in a
  configurator, and awaits the dispose there. The dispose waits for the run, which waits for that
  call, so neither ends.
- A sink whose `PresentAsync`, `OnFormatChangedAsync`, `ActivateAsync` or `DrainAsync`, or a node
  in a configurator, that ignores the token it is given. A dispose during that call waits until it
  returns.
- A source whose read blocks, such as a network stream that has stalled. Nothing interrupts the
  demuxer's read (#709), so a dispose waits until it returns. Passing FFmpeg's `rw_timeout` option
  through `MediaSource.DemuxerOptions` bounds it.

**What to write instead.**
- To stop a pass early, dispose it, or cancel the token given to `RunToCompletionAsync`. Either ends
  the run with `OperationCanceledException`.
- To stop a pass from inside its run, cancel the run's token there, and dispose the pass once
  `RunToCompletionAsync` has returned:

```csharp
using var stop = new CancellationTokenSource();
await using var pass = await FrameFlowPass.Create(path)
    .WithVideoSink(sink) // a sink that calls stop.Cancel() when it has seen enough
    .BuildAsync();
try
{
    await pass.RunToCompletionAsync(stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
```

- In a sink of your own, observe the token each call is given.

**Why.** A dispose that leaves a run going frees what the run is using. Waiting for the run is what
lets the dispose free it safely, and lets the claims go only after the last frame has reached the
sinks, as a player's dispose already does (ADR-0085, decision 6).

### 25. A video sink that throws is reported, and one that keeps throwing fails the item

**A change in behaviour, not a compile error.**

When a player's video sink threw from `PresentAsync` or `OnFormatChangedAsync`, the player stopped
handing it frames for the rest of the item. The picture stopped on the last frame presented, the
player stayed in `Playing` and raised nothing, and the item did not end on its own, so a queue did
not move on (#703).

Each call that throws now costs the frame it was for, and the player goes on with the next frame:

- The player raises failures on `ErrorOccurred` and stays in its state: the first, then at most one
  every five seconds, each saying how many failures came since the one before. The `PlaybackError`
  has `ErrorCategory.System`, names the sink's type, and carries what the sink threw as `Inner`. An
  `OperationCanceledException` the sink throws while the player is not stopping counts the same way.
  Each failure raised is logged at Warning, and the others at Debug.
- A frame whose `OnFormatChangedAsync` threw is not presented, and the next frame announces its format
  again first, so no frame of a format the sink refused reaches `PresentAsync`.
- The failures are counted for the sink, across the items the player plays through it, and a frame
  the sink takes starts the count again. The failure that makes more than eight in a row fails the
  item playing, as a fault in its decoding does: the player raises `ItemFailed` with
  `FaultedDuringPlayback`, and `ErrorOccurred`, with an error that wraps what the sink threw, and
  moves on. Until the sink takes a frame, each later item fails at its first failure. An item with
  fewer frames than that, a still for instance, counts toward the run like any other.
- Under `RepeatMode.Off` a last item that fails ends the queue in `Ended`, not `Error`. The player
  enters `Error` when more than eight items fail in a row, as it does for any fault. An item that had
  played for five seconds, or half its length if that is shorter, before it failed does not count
  toward those eight, and starts their count again; that can be the item whose failure made the run
  reach the limit, but not the items after it, which fail at their first failure.
- Anything else that stopped the player's delivery of frames, a clock that throws for instance, now
  fails the item the same way, where it used to freeze it.

A pass does not change. It stops at the first call that throws, and `RunToCompletionAsync` throws a
`GraphFaultException` naming the `video-sink` node, with what the sink threw as its inner exception.

**Who hits this.** An app with a video sink of its own that can throw, which used to see its video
freeze without a word. It now sees `ErrorOccurred` when the sink fails, and the item ends if the sink
keeps failing. Code that treats every `ErrorOccurred` as the end of playback now stops on one dropped
frame.

**What to write instead.** Read the player's state, or `ItemFailed`, to tell a dropped frame from an
item or a player that failed:

```csharp
player.ErrorOccurred.Subscribe(error =>
{
    if (player.State != PlaybackState.Error)
        logger.LogWarning(error.Inner, "Playback carried on after: {Message}", error.Message);
});
```

A sink that cannot draw a frame it can afford to lose can dispose it and return rather than throw.

**Why.** A sink that failed once, a surface rebuild that failed at the boundary to an item of a new
size for instance, stopped the video for the whole item, and the item never ended, where the failure
could have cost one frame. The pacer already kept a throwing callback from ending its delivery; a
throwing sink now gets the same treatment, and one that cannot present at all is reported rather than
hidden (ADR-0085, #703).

### 26. A seek that supersedes another waits for the run the first one stopped

**A change in behaviour, not a compile error.**

A seek stops the run in progress, and waits for its demux pump and its graph to end before it seeks
the demuxer and resets the decoders. A seek cancelled during that wait forgot the run's tasks while
they were still ending. Scrubbing does this all the time, because the seek bar sends the next seek as
soon as the last is accepted, and each seek cancels the one before it. The seek that followed found
nothing to wait for: it sought the demuxer, reset the decoders and launched a run of its own while
the old run still read from and decoded on them. A player disposed at that point freed the decoders
and the demux session under the old run (#707).

Now a seek cancelled during that wait leaves the run for the next stop to wait for. A seek that
supersedes it waits for the old run to end before it touches the demuxer, and so does the dispose of
a player or a controller before it frees anything. One pump and one graph run at a time. On a local
file the old run ends within milliseconds of being cancelled, so a scrub lands as quickly as before.

A play after a seek that was cancelled before it repositioned, as a pause sent during a seek cancels
it, now repositions where the clock stands and plays from there. A resume used to start nothing, so
the picture stayed frozen until the next seek, and a first play launched a run beside the stopped one
(#714).

**Who hits this.**
- An audio sink of your own whose `PresentAsync` or `DrainAsync` ignores the token it is given, or a
  node in a video or audio configurator that does. A seek that supersedes another while such a call
  is in progress now waits until the call returns, where it used to go ahead beside it, and a
  dispose waits too. A scrub stalls for that long.
- A source whose read blocks, such as a stalled network stream. Nothing interrupts the demuxer's
  read (#709), so the next seek, or the dispose, waits until it returns. Passing FFmpeg's
  `rw_timeout` option through `MediaSource.DemuxerOptions` bounds it.
- Code that pauses while a seek is in progress and then plays. The play resumes where the clock
  stood when the seek was cancelled, not at the seek's target, which the cancelled seek never took.

**What to write instead.** In a sink or a configurator node of your own, observe the token each call
is given, and return promptly once it is cancelled. To land on a target after pausing during a seek,
seek to it again once the pause has completed.

**Why.** The run a cancelled seek left behind was still using the demuxer and the decoders. Going on
beside it raced native state that neither the demux session nor the audio decoder guards, and the
dispose freed that state while the run still used it.

### 27. A second dispose waits for the first and ends as it did

**A change in behaviour, not a compile error.**

A `DisposeAsync` on a player or a controller made while another was under way returned at once,
before the first had disposed the session and released the claims on the sinks. A caller that
awaited it and then built the next player against the same sink found the sink still claimed, and
the player disposed its queue's coordinator while the first dispose was still tearing the session
down (#708). Two `OpenAlAudioSink.DisposeAsync` calls that overlapped could throw
`ObjectDisposedException`, and a second `CompositionInteropVideoView.DisposeAsync` returned a
completed task while the first one's teardown was still running.

Now the first `DisposeAsync` on a player, a controller, `OpenAlAudioSink` or
`CompositionInteropVideoView` tears down, and every other call, made while it runs or after, waits
for it and ends as it did: it returns once the first has finished, and throws what the first threw.
Nothing is torn down twice. A player's video pacer does the same, and when its dispose has waited
five seconds for a call into the video sink or the clock that has not returned, it logs once, at
Warning, naming the sink.

**Who hits this.**
- Code that disposes the same object from two places, such as a window's close handler and a host's
  shutdown. The second dispose now takes as long as the first.
- Code that disposes again after a dispose that threw, such as an `await using` around a controller
  already disposed by hand. The second dispose throws the same exception, where it used to return. A
  player's dispose does not throw: it logs a failure of its controller and goes on.
- Code that disposes a player from inside a call the player makes, such as a video sink's
  `PresentAsync`, while another dispose of it is under way. The first waits for that call to return,
  and the call now waits for the first, so neither ends. A call made there with no other dispose
  under way already waited for itself.
- An implementer of `IAudioSink` or `IVideoSink`. Their disposal contract said a second
  `DisposeAsync` is a no-op that never throws. It now says only the first call tears down, a later
  one touches nothing, and a later one may wait for the first and end as it did, so it throws only
  when the first threw. A sink whose second call returns at once still meets it; code that disposes
  a sink twice and relied on the second never throwing should expect the first's exception.

**What to write instead.**
- Dispose from one place, or let every caller await the dispose. A caller that cannot wait long
  bounds its own wait, and the dispose goes on without it:

```csharp
try
{
    await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
}
catch (TimeoutException)
{
    // The player still holds its sinks; it lets them go once its work has ended.
}
```

- Dispose once, or expect a second dispose to throw what the first did.
- To stop a player from inside a sink's call, signal from there and dispose from outside it.

**Why.** A second dispose that returned at once told its caller the object was gone while it was
not: the claims were still held, so a build failed although its caller had awaited a dispose, as
the claim error says to, and the coordinator was disposed under the first dispose. Every owner of
work now disposes one way (ADR-0086, decision 3).

### 28. The zero-copy view holds a converter it replaces until the compositor lets go of it

**A change in behaviour, not a compile error.**

When `CompositionInteropVideoView` replaced its converter while presenting, because a frame of
another size, format or decode device arrived or `DriverSuperResolution` changed, or dropped its CPU
uploader after device loss, it disposed the old one on the thread pool at once. It did not wait for
the presents still in flight on the old ring, and nothing saw an exception from that dispose. A D3D12
converter dropped that way did not hold back the view's next attachment, as one its teardown took
does. The view's dispose did not wait for a stall check already running, so `PresenterStalled` or
`PresenterRecovered` could be raised after it. And a disposed `OpenAlAudioSink` threw
`ObjectDisposedException` from a wait on its clock, and from a present the dispose overtook (#723).

Now a replaced converter or uploader is disposed off the UI thread once the compositor has disposed
the imported images and finished the presents of every ring it may have filled, including one a
switch between hardware and software frames set aside earlier. A failure to dispose it is logged at
Warning, and so, once, is a wait that goes on past five seconds, naming the converter and whether it
waits for the compositor or for its own dispose to return. The converter is held, not freed, until
the compositor lets go. A replaced D3D12 converter holds back the view's next attachment until it is
disposed. `DisposeAsync` waits for the rings within the bounded wait it already makes for the
compositor, and for the replaced converters' disposal within the same bound, then completes once a
stall check already running, with any handler it raised, has returned. `PresenterStallWatchdog`
gained `DisposeAsync`, which waits for a check the same way, says once after five seconds which
handler or sampler it is still waiting for, and makes a second dispose wait for the first. A wait on
the clock of a disposed `OpenAlAudioSink` ends with `OperationCanceledException`, as one in flight at
the dispose does, and a present the dispose overtakes returns.

**Who hits this.**
- A `PresenterStalled` or `PresenterRecovered` handler that waits for the view's `DisposeAsync`, or
  for anything that awaits it. The dispose waits for the handler, so neither ends.
- Code that disposes the view just after it replaced a converter, at a resize, an item of another
  size or a toggle of `DriverSuperResolution`. The dispose waits for that converter too, within its
  bound. A replaced converter whose dispose does not return no longer holds the view's own producers
  or reads as a compositor that did not drain: it is named on its own after five seconds.
- A view taken out of the visual tree and put back just after it replaced a D3D12 converter. It
  presents nothing until that converter is disposed, as after a teardown.
- Code that waits on a disposed `OpenAlAudioSink`'s clock and catches `ObjectDisposedException`.

**What to write instead.**
- In a stall handler, signal and return, and dispose the view from the UI thread.
- To stop a `PresenterStallWatchdog` of your own and know no check runs after, await its
  `DisposeAsync`. `Dispose` still returns at once.
- Catch `OperationCanceledException` from a wait on an `OpenAlAudioSink` that may be disposed.

**Why.** Freeing a converter under a present in flight is what the teardown's order exists to
prevent (ADR-0064), and the live path skipped it. Every owner of work now holds that work until it
ends and reports how it ended (ADR-0086, decisions 1, 3 and 5).

### Not breaking, but worth knowing

- **A new timeline is not a stall to the PRESENT GAP check.** `AvaloniaVideoSink` warned PRESENT
  GAP when the first frame of a new item came long after the last frame of the one before, such as
  a new item played after `Ended`. The wait before the first frame of a new timeline is now logged
  at Debug, and the Warning names the timeline it judged the frame on. #624.

- **Nothing from before a seek's target reaches a configurator or the audio sink.** A seek lands on
  the keyframe before its target, and the decoder walks from there. The frames before the target
  went through a video configurator before the pacer refused them, and the audio before it went to
  the audio sink and played. Both are now dropped where they are decoded, so a configurator's taps
  and the audio sink start at the target. The same holds at a media clip's start. #656.
- **A player keeps its hardware decode devices across items.** A player or controller that is not
  lent a device with `WithHardwareDevice` used to create a device for each item and dispose it at
  the item's end. It now keeps one per backend for its lifetime and lends it to every item, which
  takes about 200 ms off a switch on a discrete GPU. A backend's device is dropped and created again
  after an item that decoded on it faults. Frames from different items now carry the same device
  context. #656.
- **A video view's logger factory applies whenever it is given.** `FrameFlowVideoView` builds its
  sink when it joins the visual tree, and gave the sink the factory it held at that moment: the
  null one, unless `LoggerFactory` had been assigned first. `IVideoSurface.AttachSink`,
  `FrameFlowPlayerView.AttachSink` and the `LoggerFactory` setter, used after that, stored the
  factory and did nothing with it. They now reach the sink at any time, and a later factory takes
  over from an earlier one; `CompositionInteropVideoView.Initialize` does the same for that view
  and its sink. A view constructed without a logger also logs its own copy failures through the
  same factory. The difference you can see is log lines that were missing before, except one: the
  sink's `AvaloniaVideoSink created.` line still goes to the factory held when the sink was built.
  A sink you assigned through `Sink` keeps the logger you gave it. You no longer need to assign
  `LoggerFactory` in the window constructor to get ahead of the attach. #630.
- **Chrome controls follow their transport after leaving the visual tree and coming back.**
  `FrameFlowTransportBar`, `FrameFlowStateBadge`, `FrameFlowStreamSummary` and the hover-to-reveal
  in `FrameFlowPlayerView` let go of the transport's observables when they left the tree, and took
  them up again only when `Transport` was set. A control taken out and put back, by a `TabControl`
  switching tabs or a host swapping a panel's `Content`, kept showing what it showed when it left.
  It now reads the transport's state, repeat mode and media when it comes back, and follows it from
  there. The transport bar does not ask for `LoopByDefault` again: that is a request made when
  `Transport` is set. A state or repeat-mode change posted to the UI thread before a control let go
  of a transport, by leaving the tree or by a rebind, is dropped when it arrives after. The badge,
  the stream summary and the bar used to show it, except a repeat-mode change from a transport the
  bar had been rebound away from, which its Loop toggle already dropped. A transport set on a
  control while it is out of the tree is followed from when the control comes back, not at once;
  a control bound before it first joins the tree still follows from the bind. A transport disposed
  while its control was out of the tree is not followed when the control comes back, and the
  control keeps what it showed until the next transport is bound. #643.

## `v0.15.0` — since `v0.14.0`

### 1. `FrameMemoryDomains` names each GPU API, and `Gpu` is their union

**A binary change, and a change in results for `HasFlag(FrameMemoryDomains.Gpu)`.** Source that
names `Cpu`, `Gpu` or `Any` compiles unchanged.

`FrameMemoryDomains` had one GPU value, 2. It now has one per hardware decode backend (#566):
`D3D11`, `D3D12`, `Cuda`, `Vulkan`, `VaApi` and the rest, with `OtherGpu` for a backend FrameFlow
does not name. `Gpu` is the union of them, and `Any` is `Cpu | Gpu`. A binary compiled against
`v0.14.0` has `Gpu` and `Any` baked in as 2 and 3, which now mean `D3D11` and `Cpu | D3D11`.
`HasFlag(FrameMemoryDomains.Gpu)` asks for every GPU API at once, so it is false for a single one.

Declarations narrowed with it. `CompositionInteropVideoSink` takes `Cpu | D3D11 | D3D12`, `Infer`
with a `D3D12ImageToTensor` stage takes `Cpu | D3D12`, and `ToGpu` takes CPU frames and its
device's domain and emits that domain. `IDeviceImageToTensor.AcceptedDomains` is new and defaults to
no GPU domain, so `Infer` with a stage that does not declare it takes CPU frames only. A
player or pass now downloads the frames of a backend its path does not take, where before it
handed them on: VP8 decoded on CUDA reaches the interop view as CPU frames and plays.
`WithHardwareFrames(true)` is not narrowed, so the same player with it now fails to load, naming
the sink, where before it loaded and presented nothing.

**Who hits this.** An assembly that uses `FrameMemoryDomains` and is not rebuilt. Code that tests
for a GPU domain with `HasFlag(FrameMemoryDomains.Gpu)`. A host that sets `WithHardwareFrames(true)`
on a backend its sink cannot read. A custom `IDeviceImageToTensor`, whose node now gets CPU frames
until it declares the GPU domains it reads.

**What to write instead.** Rebuild. Test for a GPU domain with
`(domains & FrameMemoryDomains.Gpu) != 0`. A sink or node that reads GPU frames names the APIs it
reads rather than `Any`. A custom `IDeviceImageToTensor` implements `AcceptedDomains` with the
domains its `CanWrite` accepts.

### 2. `device_resolution_rebuilds` counts only the converter rebuilds a resize could not avoid

**Not a compile error.** A metric's meaning narrowed.

`CompositionInteropVideoView` rebuilt its D3D11 converter whenever a frame arrived at another size,
and `frameflow.presenter.device_resolution_rebuilds` counted each one. In the shader mode the
converter now resizes in place, keeping its device and shader pipeline (#571), and that is counted
by the new `frameflow.presenter.resolution_resizes`. `device_resolution_rebuilds` now counts only
the size changes that still rebuild: the driver super-resolution mode, a size change that also
changes the sample format, and a resize that failed.

**Who hits this.** A dashboard or alert that reads `device_resolution_rebuilds` as "how often the
video size changed". On a mixed-resolution playlist it drops to near zero.

**What to write instead.** Count size changes as `resolution_resizes + device_resolution_rebuilds`.

## `v0.14.0` — since `v0.13.0`

### 1. Presenters show anamorphic and rotated video at its display geometry

**A change in results, and a binary change to `CpuVideoFrame.Create`.**

Frames carry the sample aspect ratio and display rotation their source declares (#542), as
`IVideoFrame.SampleAspectRatio` and `IVideoFrame.Rotation`. `FrameFlowVideoView` and
`CompositionInteropVideoView` letterbox to the display shape and turn the picture upright. A DVD
frame at 32:27 shows at 16:9, where it used to show at 3:2, and phone video shows portrait.
`VideoStreamInfo` and `VideoFormatInfo` report both, with `DisplayWidth` and `DisplayHeight`.

`CpuVideoFrame.Create` gained two optional parameters, `sampleAspectRatio` and `rotation`. Source
that calls it compiles unchanged; a binary compiled against the old signature needs a rebuild.

**Who hits this.** A host that corrected the layout itself, by rotating the view or stretching it to
a known aspect, now corrects twice. So does an `IVideoFrame` implementation that wraps a decoded frame
without forwarding the two properties: the defaults are square and upright.

**What to write instead.** Drop the host-side correction. A wrapper forwards `SampleAspectRatio` and
`Rotation` from the frame it wraps.

### 2. A stalled source reports `Rebuffering`

**A change in results.** Nothing fails to compile.

The player enters `PlaybackState.Rebuffering` when video frames stop arriving while it plays
(#547): after three of the last frame's display intervals, and at least 1 s. It returns to
`Playing` when a frame arrives or the input completes. Before, nothing invoked the buffer
callbacks, so a stalled source reported `Playing` throughout and `Rebuffering` was never entered.
A pause leaves `Rebuffering` for `Paused`, and a stream that ends while starved goes from
`Rebuffering` to `Ended`. `Rebuffering` holds no clock (ADR-0026 §7).

**Who hits this.** A host that switches on the playback state and treats `Rebuffering` as
unreachable, or as an error. An item with no video does not report a stall yet (#550).

**What to write instead.** Treat `Rebuffering` as a transient state that leads back to `Playing`,
or on to `Paused` or `Ended`.

### 3. `IPlayerBuilder` and `IPassBuilder` gained `WithPreferredBackends`

**A compile error for a type that implements either builder, and a binary change to
`PlaybackController.Create`.** Code that calls them compiles unchanged.

A caller or a video sink can set the order hardware decode backends are tried in (#532). A type
outside FrameFlow that implements `IPlayerBuilder` or `IPassBuilder`, such as a test double or a
decorator, stops compiling until it adds the member. One compiled against `v0.13.0` and not rebuilt
fails to load with `TypeLoadException` when the application uses the type.
`PlaybackController.Create` gained an optional `preferredBackends` parameter: source that calls it
compiles unchanged, and a binary compiled against the old signature needs a rebuild.
`IVideoSink.PreferredBackends` is a default member and breaks nothing.

**Who hits this.** An implementer of either builder, and an assembly that calls
`PlaybackController.Create` and is not rebuilt. FrameFlow's own builders come from
`FrameFlowPlayer.Create` and `FrameFlowPass.Create`.

**What to write instead.** Implement it, or forward it to the builder you wrap:

```csharp
public IPlayerBuilder WithPreferredBackends(params HardwareDecodeBackendKind[] backends)
{
    _inner.WithPreferredBackends(backends);
    return this;
}
```

## `v0.13.0` — since `v0.12.0`

### 1. BlazeFace letterboxes its input

**A change in results, not a compile error.**

`BlazeFacePreprocessor` fits the ROI into the square model input with its aspect kept and black
bars, as MediaPipe's detector does, instead of stretching it (#473). A ROI that reaches past the
frame's edge reads black there instead of the repeated edge pixel. `BlazeFacePostprocessor.Decode`
maps boxes back through the same letterbox, so on a non-square ROI a face's box now has the face's
aspect: square, where it used to take the ROI's.

**Who hits this.** Anyone running BlazeFace on a non-square frame or ROI, which is most video. Boxes
and keypoints move. A caller that paired `Preprocess` with its own mapping through
`FaceRoi.ToSource` gets wrong positions, since that maps the ROI, not the model input.

**What to write instead.** Decode through the `TensorTransform` that `Preprocess` now returns, or
through `Decode(boxes, scores, roi)` with the same ROI.

**Why.** On a 1280x720 clip the stretched boxes were 1.78 times wider than tall, and a landmark
crop built from them was nearly twice the right size.

### 2. BlazeFace merges overlapping detections by weighted average

**A change in results, not a compile error.**

`BlazeFacePostprocessor.Suppression` defaults to `FaceSuppression.Weighted`: overlapping detections
become their score-weighted average box and keypoints under the highest score, as MediaPipe's face
detector does. It used to keep the highest-scoring box and drop the rest.

**What to write instead.** Set `Suppression = FaceSuppression.Hard` for the old behaviour.

### 3. A `null` or `default` logger to `DmlInferenceSession` is ambiguous

**A compile error (CS0121).**

`DmlInferenceSession` has constructors and `OnDevice` overloads that take a
`DmlInferenceSessionOptions` in the position the logger had (#472). A call that passes a bare `null`
or `default` there, such as `new DmlInferenceSession(path, null)` or
`OnDevice(path, device, queue, default)`, now matches both.

**What to write instead.** Drop the argument, or name it: `new DmlInferenceSession(path, logger: null)`.

### 4. A `default` policy to `WinMLInferenceSession` is ambiguous

**A compile error (CS0121).**

`WinMLInferenceSession` has constructors that take a `WinMLInferenceSessionOptions` in the
position the device policy had (#481). `new WinMLInferenceSession(path, default)` now matches both.
`CreateAsync` and `OnProvider` are unaffected.

**What to write instead.** Name the policy: `WinMLDevicePolicy.PreferGpu`, which is what `default`
meant.

### 5. `Yolov8Detector.Create` and `CreateAsync` take a thresholds parameter

**A binary break, not a compile error.** Source that calls them compiles unchanged.

All three take `YoloThresholds? thresholds = null` last (#23). An assembly compiled against
`v0.12.0` that calls one of them throws `MissingMethodException` against this release until it is
rebuilt.

**Who hits this.** A library built against `v0.12.0` and loaded with this release without being
rebuilt.

**What to write instead.** Rebuild.

### 6. `BlazeFacePreprocessor.Preprocess` returns the transform

**A binary break, not a compile error.** Source that calls it compiles unchanged.

`Preprocess` returns a `TensorTransform` where it returned `void`. An assembly compiled against
`v0.12.0` that calls it throws `MissingMethodException` until it is rebuilt.
`BlazeFacePostprocessor.Decode(boxes, scores, roi)` now throws `ArgumentException` for a ROI with no
area, as `Preprocess` already did.

### 7. `IPassBuilder` gained `WithDecodeDiscard` and `WithRange`

**A compile error for a type that implements `IPassBuilder`.** Code that calls it compiles unchanged.

A pass can decode keyframes only (#482) and be bounded to a time range (#483). A type outside
FrameFlow that implements `IPassBuilder`, such as a test double or a decorator, stops compiling
until it adds the members. One compiled against `v0.12.0` and not rebuilt fails to load with
`TypeLoadException` when the application uses the type.

**Who hits this.** Only an implementer. FrameFlow's own builder comes from `FrameFlowPass.Create`.

**What to write instead.** Implement them, or forward them to the builder you wrap:

```csharp
public IPassBuilder WithDecodeDiscard(DecodeDiscardLevel level)
{
    _inner.WithDecodeDiscard(level);
    return this;
}

public IPassBuilder WithRange(TimeSpan start, TimeSpan? end)
{
    _inner.WithRange(start, end);
    return this;
}
```

### 8. `TensorShape` accepts a dimension of 0

**A change in behaviour, not a compile error.**

`TensorShape`'s constructors take a dimension of 0, for a tensor with no elements, such as the boxes
of a detector that found nothing (#479). They threw `ArgumentException` for it before. A negative
dimension still throws.

**Who hits this.** Code that relied on the constructor to refuse 0, or that divides by a dimension of
a shape it did not build. A model output ONNX Runtime allocates can now have one.

**What to write instead.** Check for 0 where a dimension must be positive.

### 9. A detector whose model returns NaN fails to load

**A runtime error at construction, not a compile error.**

`Yolov8Detector.Create` and `BlazeFaceDetector.Create` run the model once on a blank input as they
always did, and now throw `InvalidOperationException` when its output holds a NaN or an infinity
(#498). The message names the session type. Decoding also drops any candidate whose score, box or
keypoint is not a finite number; those used to come back as detections with a NaN or infinite
confidence.

**Who hits this.** A model that overflows on the chosen provider, typically one with fp16 inside,
already on a blank frame. It used to load and then report detections with a NaN confidence wherever
it overflowed.

**What to write instead.** Open the model on another provider, or use an fp32 export.

### 10. An `OperationCanceledException` from a node body is a fault unless the graph was cancelled

**A change in behaviour, not a compile error.**

A node body that throws an `OperationCanceledException` of its own, such as the
`TaskCanceledException` of an `HttpClient` timeout, takes the node's `FailureResponse` like any other
exception (#489). Under `Propagate`, `Graph.RunAsync` throws it, wrapped in a
`GraphFaultException` (#499), where it used to return as though every source had ended. Under
`Discard`, the node drops that input and carries on, where the whole graph used to stop. One thrown
once the graph is cancelled, by the caller's token or by another node's fault, is still the
cancellation.

When more than one node faults, the one a pump caught first is the cause. `RunAsync` used to throw
the fault of whichever of them was added to the graph first.

**Who hits this.** A body that lets its own cancellation escape, and a caller that relied on
`RunAsync` returning when one did. A player reports it as a playback error rather than the end of
the item.

**What to write instead.** A body that means to drop an item on its own timeout catches the
exception and returns `null`, or runs under `FailureResponse.Discard`.

### 11. A graph with two nodes on one id refuses to run

**A runtime error, not a compile error.**

`Graph.RunAsync` throws `InvalidOperationException` before any node runs when more than one node has
the same `Id`, naming each such id and how many nodes hold it (#500). Ids compare ordinally. Such a
graph used to run, and every message that named one of those nodes or its ports could mean either.

**Who hits this.** A graph that reuses an id: two `Infer` branches given one id, which collide on
the operator and on the `{id}-results` sink, a node of your own on an id a helper derives, such as
`yolo-results`, or a configurator node on an id the player uses (`video-sink`, `audio-sink`).

**What to write instead.** Give each node its own id.

### 12. A node's fault surfaces as `GraphFaultException`

**A change in behaviour, not a compile error.** Code that catches a node's exception type compiles
and stops matching.

`Graph.RunAsync` throws `GraphFaultException` when a node faults, where it threw the node's own
exception (#499). Its `InnerException` is that exception, `NodeId` names the node, and the message
reads `Node '<id>' faulted: <inner message>`. When more than one node faulted, `Faults` holds each
node's fault, the cause first, and the message counts the others. A cancelled run still throws
`OperationCanceledException`, and a graph refused before any node runs still throws
`InvalidOperationException`; neither is wrapped.

`MediaPass.RunToCompletionAsync` passes the wrapper on. A player's `PlaybackError` carries it as
`Inner`, with the same category, and its message now names the node:
`Playlist item 'clip.mp4' faulted during playback: Node 'inject-fault' faulted: <inner message>`.
An exception that wraps the last fault, such as the playlist's give-up error, has the node's
exception one level further down.

**Who hits this.** Code that catches or tests for a node's exception type from `RunAsync`,
`RunToCompletionAsync` or `PlaybackError.Inner`, such as `catch (TimeoutException)` around a run or
`Assert.ThrowsAsync<MyException>(() => graph.RunAsync())`.

**What to write instead.** Match the wrapper and look inside it:

```csharp
catch (GraphFaultException ex) when (ex.InnerException is TimeoutException)
```

### 13. A seek on MPEG-TS lands on a keyframe

**A change in behaviour, not a compile error.**

`DemuxSession.SeekAsync` lands on the first video stream's nearest keyframe at or before the
position, as `IDemuxSession` documents (#495). On a container that seeks by timestamp, such as
MPEG-TS, it landed on the packet nearest the position, and a decoder fed from there dropped every
frame up to the next keyframe: after a seek the player showed that keyframe first, up to a GOP past
the target. It now shows the first frame at or after the target.

**Who hits this.** Code that reads packets straight after `SeekAsync` on MPEG-TS, which now gets
the keyframe rather than the packet nearest the position, and anything sensitive to seek latency.
To find the keyframe the seek reads the video packets after where it lands, without decoding them,
and seeks further back when none is a keyframe at or before the position; on MPEG-TS it reads the
packets from somewhat before the keyframe up to the position about once. On a container that seeks
to keyframes, such as MP4, it reads one video packet and seeks a second time.

**What to write instead.** Nothing.

### 14. The inference factory throws when a provider runs out of memory

**A change in behaviour, not a compile error.**

`IInferenceSessionFactory.Open` reads why a provider failed to open a model (#497). When the provider
ran out of memory, `Open` throws `ProviderOutOfMemoryException`, naming the provider and the model,
with the provider's exception inside. It no longer opens the model on the next provider, and the
cached provider does not change, so the next model opens where the last one did. The factory used to
fall back and cache the provider that opened, which sent every later model in the process to CPU.

The cache also moves less. Only a lost GPU device or an unavailable provider (a missing library,
entry point or adapter) caches the provider that opens after it. After any other failure, such as an
error ONNX Runtime raises for this model alone, the model still opens further down the chain but the
cached provider stays, and `ActiveProvider` stays `null` if nothing was cached yet.

**Who hits this.** Code that relied on a model too large for the GPU opening on CPU instead, and code
that read `ActiveProvider` after a fallback past a failure the factory does not recognise.

**What to write instead.** Catch `ProviderOutOfMemoryException` and decide: free memory and open
again, open a smaller model, or open with a factory that prefers CPU. It derives from
`InvalidOperationException`, so a catch of that still catches it.

## `v0.12.0` — since `v0.11.0`

### 1. A join with a frame secondary must set `maxLead`

**A runtime error at construction, not a compile error.**

`SyncJoinNode` now throws `ArgumentException` (parameter `maxLead`) when its secondary type is an
`IFrame` and no lead bound is given. `VideoFrameRef`, `DetectedVideoFrameRef` and
`DetectedFaceFrameRef` implement `IFrame` for this, and `CameraFrameAdapter` already did.

**Who hits this.** Anyone joining frames as the secondary without a lead. No join in this
repository does; the one in-tree join retains a `RefBox<DetectionSet>`. A join over
`Media.CpuVideoFrame` secondaries threw at its first match anyway (#91), so the case this breaks is
a join over frames that already counted references, such as `GpuVideoFrame`.

**What to write instead.** Pass `maxLead`, above the furthest the secondary can run ahead of the
primary. The lead's documentation on `SyncJoinNode.MaxLead` covers choosing it and the one shape
where holding the secondary edge can deadlock.

**Why.** Every retained frame keeps its storage alive, and a hardware decode slice or a camera
buffer comes from a fixed pool. Without a lead, a join retains every secondary that arrives ahead of
the primary (#90). ADR-0080 decision 8.

### 2. Video sinks no longer have a frame pool

**A compile error.**

`IVideoSink.FramePool`, `IFramePool`, `CpuFramePool` and the internal `PooledCpuVideoFrame` are
deleted. The sink constructors lose their pool parameter:

| Before | After |
|---|---|
| `new AvaloniaVideoSink(pool, logger)` | `new AvaloniaVideoSink(logger)` |
| `AvaloniaVideoSink.CreateHeadless(pool)` | `AvaloniaVideoSink.CreateHeadless()` |
| `new CompositionInteropVideoSink(pool, logger)` | `new CompositionInteropVideoSink(logger)` |
| `new SdlVideoSink(sdl, pool, title, w, h)` | `new SdlVideoSink(sdl, title, w, h)` |
| `SdlVideoSink.CreateHeadless(pool)` | `SdlVideoSink.CreateHeadless()` |
| `new HeadlessVideoSink(pool, cost)` | `new HeadlessVideoSink(cost)` |

`AddFrameFlowAvaloniaVideoSink` and `AddFrameFlowSdlVideoSink` no longer register an `IFramePool`.

**Who hits this.** Anyone who constructs a sink directly, implements `IVideoSink`, or resolves
`IFramePool` from the container.

**What to write instead.** Drop the pool argument and delete the pool. An `IVideoSink`
implementation deletes its `FramePool` member.

**Why.** Nothing rented from the pool. Each decoder allocates its own frames, so the pool's slots
never filled and its capacity bounded nothing. A slow sink applies backpressure through its edge,
which does not take the next frame until `PresentAsync` returns. ADR-0080, #382.

### 3. CPU frames and PCM buffers are made by a fill factory

**A compile error.**

The `CpuVideoFrame` and `PcmAudioBuffer` constructors are gone, and so are
`CpuVideoFrame.PixelData` and `PcmAudioBuffer.SampleData`. Each type has one factory that rents
storage, runs your callback over it once, and returns the published item:

```csharp
// Before
var owner = MemoryPool<byte>.Shared.Rent(width * height * 4);
Paint(owner.Memory.Span);
var frame = new CpuVideoFrame(owner, width, height, width * 4, PixelFormat.Bgra32, pts, duration);

// After
var frame = CpuVideoFrame.Create(PixelFormat.Bgra32, width, height, pts, duration, state,
    static (planes, state) => Paint(planes.Y, state));

// Before
var buffer = new PcmAudioBuffer(owner, sampleCount, sampleRate, channels, pts);

// After: the callback returns how many samples it wrote, up to the capacity.
var buffer = PcmAudioBuffer.Create(capacity, sampleRate, channels, pts, state,
    static (samples, state) => Convert(samples, state));
```

To read pixels, use `AsCpu()` or `ToCpu()`; to read samples, use `Samples`.

**Who hits this.** Anyone who constructs a CPU frame or PCM buffer, which includes test helpers,
or reads `PixelData` or `SampleData`.

**Also changed.** A CPU frame's layout now comes from its format: tightly packed rows, and up to
three planes (`Yuv420P` uses three, `Nv12` puts its interleaved chroma in `PlaneU`). `AsCpu()`
returns each plane at its exact length, where `PlaneY` used to be the whole rented buffer, which
could run past the image. `CloneCpu` copies every plane, where it used to copy only `PlaneY`, and
its clone is tightly packed even when the source's rows are padded.

**Why.** A frame shared by count must not change after it is published, and `PixelData` and
`SampleData` let any holder write to shared storage or free it. The spans the callback receives
cannot outlive the call. ADR-0080 decision 5, #378, #379.

### 4. Graph items are the frames themselves: `VideoFrameRef` and `PcmAudioBufferRef` are gone

**A compile error.**

`IVideoFrame` and `IAudioBuffer` now extend `IRefCounted`, so a frame or a buffer is the graph item
itself. Video chains are `GraphChain<IVideoFrame>` and audio chains are
`GraphChain<PcmAudioBuffer>`. `VideoFrameRef`, `PcmAudioBufferRef` and their `Detach` are deleted.

| Before | After |
|---|---|
| `GraphChain<VideoFrameRef>`, `SourceNode<VideoFrameRef>`, `OperatorNode<VideoFrameRef, …>` | `GraphChain<IVideoFrame>`, `SourceNode<IVideoFrame>`, `OperatorNode<IVideoFrame, …>` |
| `GraphChain<PcmAudioBufferRef>` and friends | `GraphChain<PcmAudioBuffer>` and friends |
| `new VideoFrameRef(frame)` | `frame` |
| `item.Frame`, `item.Buffer` | `item` |
| `(VideoFrameRef)item.AddRef()` | `item.AddRef()`, which returns the same instance |
| `ref.Detach()` to hand a frame on | return the input from an operator; take `AddRef()` to keep one |

The configurator hooks change with them: `ConfigureVideo` and `ConfigureAudio` on the player
builders, and `configureVideo` / `configureAudio` on `PlaybackController.Create`, take and return
`GraphChain<IVideoFrame>` and `GraphChain<PcmAudioBuffer>`.

**Who hits this.** Anyone who writes a configurator, builds a graph over decoded frames or audio,
or implements an operator or sink node over them.

**Two rules replace the wrapper's.** To forward the input from an operator, return it; returning
`input.AddRef()` leaks a reference per item, because `AddRef` returns the same instance. To keep an
item past a node body, call `AddRef()` and dispose that reference when done. `DetectedVideoFrameRef`
and `DetectedFaceFrameRef` keep their names and count their own references the same way.

**Why.** The wrappers existed to give frames a refcount the graph could see, and cost an allocation
per frame. Frames now count references themselves (ADR-0080), so the frame can be the item.
ADR-0080 decision 6, #42.

### 5. Fan-out has no cloner

**A compile error.**

`EdgeConfig<T>`, `EdgeOptionsExtensions.WithCloner`, `Graph.Connect(from, to, EdgeConfig<T>)` and
`GraphChain<T>.Branch(EdgeConfig<T>)` are deleted. Every branch of a fan-out shares the item by
`AddRef`.

| Before | After |
|---|---|
| `EdgeOptions.LatestWins().WithCloner<IVideoFrame>(f => f.CloneCpu())` | `EdgeOptions.LatestWins()` |
| `graph.Connect(from, to, options.WithCloner(...))` | `graph.Connect(from, to, options)` |
| `chain.Branch(options.WithCloner(...))` | `chain.Branch(options)` |

A graph that forks a chain with `Branch` and then wires its trunk twice is no longer refused: with
every branch holding a reference, it is an ordinary fan-out.

**Who hits this.** Anyone who used `WithCloner` to fan out frames that could not be shared.

**What to write instead.** Drop the cloner. A branch that wants a private copy it can keep
independently of the source's storage calls `CloneCpu()` in its own node. That is the case for a
long-held frame from a fixed pool, such as a camera lease (ADR-0081).

**Why.** The cloner existed because CPU frames could not be shared, and it copied every frame for
every branch: about 750 MB/s across three 1080p presenters at 30 fps, estimated from frame sizes.
Frames count references now, so a branch can share the frame. ADR-0080 decision 7, #380, #93.

### 6. The node constructors take new optional parameters

**A binary break, not a compile error.** Source that constructs these nodes compiles unchanged.

`OperatorNode`, `MultiOperatorNode` and `SinkNode` take `Holding? holding = null`.
`OperatorNode` and `MultiOperatorNode` also take `Func<ValueTask>? cleanup = null`, which runs
each time the node's pump exits, once per run, as `SourceNode`'s does. `SyncJoinNode` takes
`int? maxRetained = null`. A parameter with a default still changes the constructor's signature. An assembly compiled against `v0.11.0` that constructs one of these nodes
throws `MissingMethodException` against this release until it is rebuilt.

**Who hits this.** A library built against `v0.11.0` and loaded with this release without being
rebuilt. An application that builds from source does not.

**What to write instead.** Rebuild. A node that carries video frames should also pass `holding`,
the most frames it keeps at once. A node that passes none counts as holding without bound, and a
pool cannot be sized for a path through it (ADR-0081).

### 7. Hardware frames through a holder that declares nothing are refused

**A runtime error, not a compile error.**

A player built with `WithHardwareFrames()` now sizes its decoder's pool from what its video path
can hold (ADR-0081). The path's nodes and its sink have to declare how much they hold:
`holding:` on a node, `MaxHeldFrames` on an `IVideoSink`. When the decoder binds a fixed pool
(D3D11VA, DXVA2, or an uncharacterised one such as NVDEC or VAAPI) and anything on the path declares
nothing, `LoadAsync` fails and names that node. A plain graph over such a decoder fails `RunAsync`
the same way. Software decode, readback and VideoToolbox are unaffected.

**Who hits this.** Anyone yielding hardware frames through their own `IVideoSink`, or through
their own nodes in `ConfigureVideo`. Every sink and node in this repository declares.

**What to write instead.** Give each node `holding:`, the most frames it keeps at once counting the
call. A node that emits a new frame and releases its input is `FrameHolding.Boundary`, and the path
stops counting there. Give each sink `MaxHeldFrames`, the most it keeps after `PresentAsync`
returns.

**Why.** An exhausted fixed pool fails the decode (#370). A pool sized from declarations does not
run out, and a holder with no declaration could hold any number of frames.

### 8. A face ROI with no area is refused

**A runtime error, not a compile error.**

`BlazeFaceDetector.Detect(frame, roi)` and `BlazeFacePreprocessor.Preprocess` throw
`ArgumentException` for a `FaceRoi` whose width or height is zero, negative or not finite. They
used to sample it: a zero-width ROI repeated one column across the model input, and a negative one
mirrored it.

**Who hits this.** A caller that derives the ROI from a tracked box and can produce an empty one,
for example by clamping a box that has left the frame. `FaceRoi.Full` is unaffected.

**What to write instead.** Skip detection for an empty ROI; it holds no face.

**Why.** Both preprocessors now call `ImageToTensor` (#363), which needs a crop it can invert to
map results back to the frame.

### 9. `FrameFlow.Inference.Dml` is marked Windows-only

**A build warning, CA1416, not a runtime change.**

The assembly carries `[SupportedOSPlatform("windows")]`. A call into it from code the analyzer
considers reachable on other platforms now warns, and fails a build that treats warnings as errors.

**Who hits this.** A project that targets plain `net10.0` and calls `DmlInferenceSession` or
`DmlProbe` without a Windows check. Both DirectML examples in this repository did.

**What to write instead.** Mark the project Windows-only, as the examples now do:

```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Runtime.Versioning.SupportedOSPlatformAttribute">
    <_Parameter1>windows</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

Or guard the call with `OperatingSystem.IsWindows()`.

**Why.** DirectML exists only on Windows. A Linux or macOS app compiled cleanly and failed only
when it ran; the warning moves that to the build (#440).

### 10. Hardware frames from a pool that grows are budgeted too

**A runtime error, not a compile error.**

Entry 7 left VideoToolbox, and with it every pool that grows, unaffected. It no longer does. When
a decoder hands a graph hardware frames from a D3D12VA, Vulkan or VideoToolbox pool, a path with a
node or sink that declares nothing is refused as it is on a fixed pool: `LoadAsync` or
`BuildAsync` fails, or a plain graph's `RunAsync`, and the message names the holder. A path that
declares is guarded at its budget, so a holder that keeps more than it declared parks the decoder
instead of taking another surface.

**Who hits this.** Anyone yielding hardware frames on macOS, or on D3D12VA or Vulkan through a
`HardwareDevice`, through their own nodes or `IVideoSink`.

**What to write instead.** What entry 7 says: `holding:` on each node, `MaxHeldFrames` on each
sink.

**Why.** A growable pool never faults, so an undeclared holder took a GPU surface per frame until
an allocation failed: all 600 frames of a 1080p60 clip, about 1.9 GB, on D3D12VA (#416).

### 11. A 10-bit hardware frame reports `P010`

**A behaviour change, not a compile error.**

`GpuVideoFrame.Format` is the pool's software format. A 10-bit stream's frames report the new
`PixelFormat.P010`; they reported `Nv12`, which their surfaces never held. An exhaustive `switch`
over `PixelFormat` gains a member to handle.

**Who hits this.** Code that tests a GPU frame's `Format` for `Nv12`, and a `switch` over
`PixelFormat` with no default arm.

**Why.** The frame said `Nv12` whatever its texture held (#430).

### 12. The inference factory tries CPU last, and re-probes after a failure

**A behaviour change, not a compile error.**

`InferenceSessionFactoryBuilder.Create`'s default fallback, the one used when `fallbackOrder` is
null, tries the other registered providers narrowest first: CUDA, Windows ML, DirectML, then CPU.
It used to follow `ExecutionProvider`'s numeric order, where `Cpu` is first, so a failed preferred
provider fell straight to CPU with a GPU provider still untried.

An `Open` whose cached provider fails no longer throws while another provider opens. The factory
walks the rest of the chain and caches the provider that opens, so a DirectML session that stops
opening after a GPU reset falls back to CPU. When every provider fails, it throws as before and
keeps the provider it had cached.

**Who hits this.** A factory with three or more providers and no `fallbackOrder`, and code that
expected a cached provider's failure to reach the caller.

**What to write instead.** Pass `fallbackOrder` to keep a specific order.

**Why.** CPU is the provider that nearly always opens, so trying it second hid every GPU provider
after the first (#431).

### 13. The managed ONNX Runtime assembly is the native runtime's version

**Not a compile error for code that uses FrameFlow's sessions.**

`FrameFlow.Inference.Ort` references `Microsoft.ML.OnnxRuntime.Managed` at 1.24.4 or above instead
of the newest release. An app now gets the managed layer at the version of its native one: 1.24.4
with `.Dml`, 1.26.0 with `.Cuda`, 1.30.0 with `.Cpu`. It got 1.30.0 over each of them.

**Who hits this.** Code that calls ONNX Runtime's managed API directly and uses a member added
after its native package's version.

**What to write instead.** Reference `Microsoft.ML.OnnxRuntime.Managed` at the version you need,
no higher than your native package's.

**Why.** A managed layer newer than the native one can call a C API entry the native lacks, and
fails in the app rather than here (#438).

### 14. A node that cannot take GPU frames is refused before the run

**A runtime error, and a binary break for code compiled against the node constructors.**

Nodes and video sinks now declare the memory domains they take, and a graph that can hand one a
domain it does not take is refused before anything runs, naming it (#435): `BuildAsync` on a
pass, `LoadAsync` on a player, and `RunAsync` on a graph whose source says which domains its frames
can be in. A decoder says so: CPU and GPU when it yields hardware frames, CPU otherwise.

The library declares CPU only for the sws converters (`ConvertPixelFormat`, `Resize`,
`ResizeAndConvert`), `YoloOperators.DetectWith`, `FaceOperators.DetectWith`, `Infer` without a
device stage, `Mp4VideoWriter`'s sink, and the Avalonia and SDL video sinks. Each of them failed on
the first GPU frame before, or in the Avalonia view's case dropped every one; now the graph does
not start. A node that declares nothing takes either domain and is never refused. A video sink
that declares nothing is taken to read CPU pixels (entry 16).

`OperatorNode`, `MultiOperatorNode` and `SinkNode` take a new optional `domains` parameter, and
`SourceNode` a new optional `emits` parameter. Source that calls them compiles unchanged; a binary
compiled against the old constructors needs a rebuild. `IVideoSink` gains `AcceptedDomains`, with a
default of `FrameMemoryDomains.Cpu`.

**Who hits this.** A player or pass with `WithHardwareFrames()` whose path reaches one of the nodes
above, or a sink that reads CPU pixels, with no `ToCpu` before it.

**What to write instead.** Put `VideoOperators.ToCpu` before the node, give `Infer` a device stage
for the decoder's API, or drop `WithHardwareFrames()`. A node of your own that reads CPU pixels
should declare `domains: FrameDomainRule.CpuOnly`, and a sink `AcceptedDomains =>
FrameMemoryDomains.Cpu`, so the refusal names it.

**Why.** A GPU frame reached a node that could not read it and failed there, one frame into the run,
with a message about the pixel format rather than the memory domain.

### 15. Hardware frames stay on the GPU when the path takes them

**A behaviour change, not a compile error.**

A player or pass with no `WithHardwareFrames` call used to download every hardware-decoded frame to
system memory. It now keeps them on the GPU when every node they reach declares that it takes GPU
frames, the sink included, and the path holds a bounded number of them (#294). It logs which it
chose and why. `WithHardwareFrames(true)` and `WithHardwareFrames(false)` still decide on their
own.

Unless hardware frames are off (`WithHardwareFrames(false)` or `HardwareDecodeMode.Disabled`), the
video configurator now runs once more before the decoder opens, on a pass as on a player, to decide.
Its contract already said to build the same path on each call and to do nothing else there.

**Who hits this.** A path whose sink is `CompositionInteropVideoSink`, `HeadlessVideoSink`,
`NullVideoSink`, or one of your own declaring GPU frames, with nothing undeclared on the way. A
`HeadlessVideoSink` run now measures decode without the download it used to include.

**What to write instead.** Nothing, to take the change. `WithHardwareFrames(false)` restores the
download.

**Why.** Each consumer had to say twice that it takes GPU frames: once on the sink and once on the
builder, where the Avalonia surfaces passed their own flag.

### 16. A video sink that declares nothing is taken to read CPU pixels

**A runtime error at build or load, not a compile error.**

`IVideoSink.AcceptedDomains` defaults to `FrameMemoryDomains.Cpu`, not `Any` (entry 14). With
`WithHardwareFrames()`, a sink that does not override it is refused before anything runs, naming
`'video-sink'`. `HeadlessVideoSink` and `NullVideoSink` declare `Any`, since they read no pixels.

**Who hits this.** A sink of your own that handles GPU frames, on a path with
`WithHardwareFrames()`.

**What to write instead.** `public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.Any;`
on the sink.

**Why.** Entry 15 hands GPU frames to a sink that takes them without being asked. A sink that says
nothing could be one that reads pixels, and handing it GPU frames would break it one frame in.

### 17. `IVideoSurface.PrefersHardwareFrames` is gone

**A compile error.**

The player derives the same answer from the surface's sink (entry 15), which is where the
composition-interop view and the CPU view already declare it.

**Who hits this.** Code calling `WithHardwareFrames(surface.PrefersHardwareFrames)`, and a surface of
your own implementing the property.

**What to write instead.** Drop the call, and the property.

### 18. `PlaybackController.Create` takes `bool? yieldHardwareFrames`

**A binary break; source that passes `true` or `false` compiles unchanged.**

The parameter defaults to `null`, which derives the choice as entry 15 describes. `true` and `false`
mean what they did.

### 19. `DecodeStageMetrics.RecordHardwareTransfer` and `RecordColorConvert` are internal

**A compile error.**

Only the decoder records its own stages. A caller could write samples into the process-wide
collector and skew what every other reader saw (#435). `Enabled`, `Reset` and `Snapshot` are
unchanged. `FrameCopyMetrics` now counts every copy and conversion by site, including the
decoder's.

### 20. `Branch` returns a `BranchChain<T>`

**A compile error where a branch is kept or passed on before its first hop.**
`head.Branch(options).Then(op)` and `head.Branch(options).To(sink)` compile unchanged.

`GraphChain<T>.Branch` returns `BranchChain<T>`. Its `Then`, `To`, `ToPrimary` and `ToSecondary`
take no `EdgeOptions`, because `Branch` configured that edge, and each returns or terminates as the
`GraphChain<T>` method of the same name does. Options passed on that hop used to throw
`ArgumentException` at run time.

| Before | After |
|---|---|
| `var b = head.Branch(o); if (gpu) b = b.Then(f); b.Then(g)` | `var b = head.Branch(o); var c = gpu ? b.Then(f).Then(g) : b.Then(g);` |
| `head.Branch(o).Then(op, other)` | `head.Branch(other).Then(op)` |

**Who hits this.** Code that reassigns a branch variable, as LiveCaptioning did. Code that passed a
bare branch to `Join` has nothing to migrate: it threw at run time, after wiring the join's primary
edge (#246).

`Join` also checks both join inputs before it connects either, so a join input that is already
connected is refused without wiring the other.

### 21. The chrome controls take `Transport`, not `MediaPlayer`

**A compile error at every use, and a mechanical one.**

The eight chrome controls in `FrameFlow.Avalonia` had a property named `MediaPlayer` whose type is
`IMediaTransport`. It is `Transport` now, and its styled property is `TransportProperty`.
`FrameFlowVolumeControl.RefreshFromPlayer()` is `RefreshFromTransport()`.

| Was | Is |
|---|---|
| `view.MediaPlayer = player` | `view.Transport = player` |
| `FrameFlowSeekBar.MediaPlayerProperty` | `FrameFlowSeekBar.TransportProperty` |
| `volume.RefreshFromPlayer()` | `volume.RefreshFromTransport()` |

The controls are `FrameFlowPlayerView`, `FrameFlowPlayerChrome`, `FrameFlowPositionLabel`,
`FrameFlowSeekBar`, `FrameFlowStateBadge`, `FrameFlowStreamSummary`, `FrameFlowTransportBar` and
`FrameFlowVolumeControl`. XAML that sets `MediaPlayer` on one of them takes the same rename.

An `IMediaPlayer` is an `IMediaTransport`, so what you assign does not change.

**Why.** `v0.10.0` entry 38 renamed the small interface to `IMediaTransport` because every consumer
of it is a transport widget. The property that takes it still said player, which is the mismatch
that rename removed from the type names (#335).

### 22. `SyncJoinNode.ResetWindow()` is gone

**A compile error.**

Nothing in this repository called it. The pump clears the window at the start of every run, so a
seek, which rebuilds the graph, and a loop, which re-runs it, each start with an empty window. A
backward step in the primary's time within a run starts a new timeline in the window (#92).
ADR-0073 kept the method until a consumer appeared, and none did.

**Who hits this.** Code that called `ResetWindow()` on a join it holds across runs, or mid-run on
a discontinuity.

**What to write instead.** Across runs, delete the call. Mid-run, a step back in the primary's
timestamps is handled by the window. A discontinuity the primary's timestamps do not show needs
the graph rebuilt, which is what a seek does.

## `v0.11.0` — since `v0.10.1`

A new FFmpeg major under the bindings, and one platform that is no longer pretended to be
supported. Neither is a compile error, and neither changes the public API.

**Why the minor, not a patch.** FrameFlow is pre-1.0, where the minor position is the breaking
one. Nothing here is a compile error, so a consumer on `~0.10.1` would have taken it silently:
a self-supplied FFmpeg 7 install is now refused at bootstrap, and `osx-x64` is gone from the
develop-time path.

### 1. FrameFlow targets FFmpeg 9.0, not 7.1

**Not a compile error for consumers**, unless you name a library file yourself.

`FrameFlow.Native.Runtime` now ships FFmpeg 9.0, and the bindings are generated from its headers.
The library file names all change, and they do not change by the same amount:

| Library | 7.1 | 9.0 |
|---|---|---|
| `libavutil` | 59 | 61 |
| `libavcodec` | 61 | 63 |
| `libavformat` | 61 | 63 |
| `libavdevice` | 61 | 63 |
| `libavfilter` | 10 | 12 |
| `libswscale` | 8 | 10 |
| `libswresample` | 5 | 7 |

**Who hits this.** Nobody on the bundled package: it carries the matching binaries, and the ABI
check `v0.10.1` added confirms it at bootstrap.

You are affected if you supply FFmpeg yourself, through `CustomFfmpegPath` or
`ProbeSystemLibraries`. A 7.x install that worked before is now refused at bootstrap, by name.
That is `v0.10.1`'s ABI check doing its job rather than a second break: before it existed, this
upgrade would have decoded against the wrong struct layouts instead of failing.

**What to write instead.** Install FFmpeg 9.x. On macOS that is `brew install ffmpeg`, the
unversioned formula, because Homebrew has no `ffmpeg@9` and will not cut one until FFmpeg 10
ships. That formula floats, which the bootstrap check makes safe: when Homebrew moves it to 10,
FrameFlow refuses to start rather than decoding wrong.

**The public API does not change.** No type, member or signature moves. All 57 functions FrameFlow
P/Invokes exist in both majors, and the struct fields it reads (`ch_layout`, `pts`, `dts`,
`time_base`, `hw_device_ctx`, `nb_streams` and the rest) are unchanged. What moved is which bytes
are on disk.

**What you get.** swscale was rebuilt on a new op-dispatch backend in FFmpeg 8, which is the path
`ConvertPixelFormat` runs on, and autovectorization is no longer disabled on x86, ARM and AArch64.
Neither is measured here. The codec additions across 8 and 9 (VVC, APV, ProRes RAW, animated WebP,
Vulkan hwaccels) are available to a consumer who asks for them by name.

**macOS is LGPL-2.1, the rest is LGPL-3.** Unchanged in shape from `v0.11.0`, and both build
identities in `THIRD-PARTY-NOTICES.md` move with this. The BtbN build still carries
`--enable-version3` with `libopencore-amr`, `libaribb24` and `gmp`, read off the shipped binary
rather than inferred; the macOS build still links nothing external.

### 2. Intel macOS is not supported

**Not a compile error, and not a change to anything that shipped.** No `osx-x64` RID has ever
been in `FrameFlow.Native.Runtime`, so nothing is removed from the package. What is removed is
the develop-time path that pretended otherwise: `scripts/fetch-ffmpeg.cs` no longer knows the
RID, and the 165 lines of Homebrew-keg handling and `install_name` patching that existed only
for it are gone.

**Why now.** Apple's last Intel Mac shipped in 2023 and macOS 26 is the final release supporting
them. The path had no CI coverage, no known user, and had just been changed by entry 1 without
anyone running it.

**Not a cost problem.** `macos-26-intel` is a current GitHub runner, so an Intel leg would have
been one matrix entry. It was dropped because nobody is served by it, not because it is hard.

**If you are on an Intel Mac.** Install FFmpeg 9.x and point at it with
`FrameFlowNativeOptions.CustomFfmpegPath`, or let `ProbeSystemLibraries` find it. The ABI check
`v0.10.1` added tells you by name if the major is wrong. That is the same arrangement every
macOS consumer had before `v0.11.0`.

## `v0.10.1` — since `v0.10.0`

Two refusals where the pipeline used to carry on. Neither is a compile error.

### 1. A source FFmpeg opened but could not resolve is refused

**Not a compile error.** Nothing you write changes; what runs does.

`avformat_find_stream_info` logs "Could not find codec parameters" at warning level and still
returns success, so a container FFmpeg opened without resolving anything loaded looking fine,
carrying streams whose parameters were still zero. The session decoded nothing and ended at zero
duration, which a caller cannot tell apart from a valid empty clip (#340).

A source is now refused when it declares no streams, or when none of the streams it declares
resolved: a video stream counts only with positive dimensions, an audio stream only with a
positive sample rate and channel count. An unusable track alongside a playable one warns rather
than fails, so an unsupported video stream in an otherwise fine container still loads.

A caller who relied on the load succeeding gets a failed load with the reason on
`PlaybackError.Inner`. An animated WebP is the reproducible case (#341, deferred).

### 2. A mismatched FFmpeg major fails the bootstrap instead of decoding wrong

**This one is not a compile error**, and before this release it was not an error at all.

FrameFlow reads FFmpeg structs by overlaying the layouts from `FFmpeg.AutoGen.Abstractions`
onto native pointers (ADR-0017). Those layouts come from one FFmpeg major version's headers,
and FFmpeg changes struct layouts every major. Every function FrameFlow P/Invokes still exists
and still links against a different major, so loading one produced a running process that read
frame, packet and stream fields at the wrong offsets. The bootstrap logged the version it found
and carried on.

It now compares each loaded library's major against the one its bindings were generated for and
fails the bootstrap when any of them differ, naming the offender and both versions:

```
FFmpeg ABI mismatch. One loaded FFmpeg library is not the major version FrameFlow's struct
bindings were generated for: libavutil is 61.1.100 (FFmpeg 9.x), expected 59.x (FFmpeg 7.x). ...
```

All five required libraries are checked, not just `libavutil`. The layouts FrameFlow overlays are
owned by different libraries, and each one is resolved independently, so a search path can serve
them from different FFmpeg generations.

Library majors are not FFmpeg release numbers and do not match each other. FrameFlow targets
FFmpeg 7.1, which is `libavutil` 59, `libavcodec` 61, `libavformat` 61, `libswscale` 8 and
`libswresample` 5.

The check runs in the loader, so it covers both entry points. An explicit
`FrameFlowBootstrapper.Initialize()` returns a failed `FrameFlowBootstrapResult` carrying that
message. Code that decodes without bootstrapping first gets a `DllNotFoundException` from the
implicit bootstrap, with the same message inside it.

The refusal is terminal for the process. FFmpeg loads once per process, so a mismatch cannot be
repaired by pointing at different binaries afterwards: the rejected libraries are already mapped.
`ProbeSystemLibraries` does not fall back after one, and every later P/Invoke throws rather than
resolving. Bootstrap explicitly if you need to choose the binaries.

**Who hits this.** Nobody using the bundled binaries. `FrameFlow.Native.Runtime` ships the major
the bindings were generated for and matches by construction. `v0.11.0` changes which one that
is. It reaches you if you set `CustomFfmpegPath`, or if you rely on
`ProbeSystemLibraries` against a system FFmpeg that is not 7.x.

macOS used to be the likely case, because the package shipped no macOS RID and `brew install
ffmpeg` is whichever major Homebrew ships that week. As of `v0.11.0` it carries `osx-arm64`, so
an Apple silicon consumer on the bundled binaries is in the first group. Intel Macs have no RID
and never had one; `v0.11.0` removes what remained of the path.

**What to write instead.** Take the bundled package. If you are pinning your own, install the
major the package targets and point at it. `v0.11.0` moves that from 7.1 to 9.0, so read its
first entry before installing anything.

**Why it is worth a break.** The previous behaviour had no failure mode that pointed at the cause.
A wrong offset surfaces as a wrong resolution, a wrong timestamp, a wrong pixel format or a crash
somewhere downstream, none of which names FFmpeg. Refusing at bootstrap costs a startup error and
replaces a silent wrong answer.

### Not breaking, but worth knowing

- **`MediaSource.FromStill(path, dwell)` opens a still, so the `image2` recipe has one home.**
  Opening a single image as a clip of a known length took three settings a caller had to know:
  name `image2`, pass `framerate` as a rational, and pass `pattern_type=none`. Getting any one
  wrong failed in a different way, and the failures read as decode problems rather than
  configuration ones. The factory does all three and works the rational out from the dwell, in
  integer terms, so a fractional dwell needs no invariant formatting: 7.5 seconds is `2/15`.
  `MediaSource.FromFile` and the `with` form still work, and nothing that used them changes.
  It takes no position on the extension — whether a file is one image is a fact about the
  content, and a caller that hands it a format that can be animated gets the first frame
  reported as the whole file, unless FFmpeg resolved nothing at all, which entry 1 now refuses.
  The dwell is rounded to the nearest millisecond, and one outside
  `MediaSource.MinimumStillDwell` to `MaximumStillDwell` (one millisecond to thirty minutes)
  is refused: FFmpeg re-derives the rational it is handed, and past that range the duration it
  reports stops tracking the dwell without saying so. #304.

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
that follows it throws rather than silently winning or losing. A branch runs ahead of the
player's pacer, so it suits consumers that do not present, such as inference or recording.

To show each frame in several views in step, register one `FanOutVideoSink` over their sinks
with `WithVideoSink`: the pacer presents to all of them at once, as
`examples/FrameFlow.Examples.Multicast.Shared/MulticastPanes.axaml.cs` does for three panes. To
end the chain in code of your own, register a `DelegatingVideoSink`, and declare its
`MaxHeldFrames` and `AcceptedDomains`: a sink that declares neither is taken to keep frames
without bound and to read CPU pixels.

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
- **`Auto` no longer binds CUDA for MJPEG.** The CUDA MJPEG decoder maps a full-range JPEG as
  if it were limited range, so a gray ramp comes back with 0 to 16 as 0 and 235 to 255 as 255, and
  a video from an MJPEG camera or a baseline JPEG still loses its shadows and highlights (#574).
  `Auto` picked CUDA for MJPEG wherever it was the only backend with a config, as it was on the
  Windows machine this was measured on. It now decodes in software, or on the next backend with a config, whatever
  `PreferredBackends` says. `Required` and a borrowed `HardwareDevice` still bind CUDA, because
  they name the backend. `HardwareBackend` reads `null` for MJPEG where it read `Cuda`. Nothing to
  change; the picture just stops being wrong.
- **`MediaSource.FromStill` opens a GIF again.** The FFmpeg 9.0 build has no decoder `image2` can
  name for GIF from its extension, so a GIF opened with a stream and no codec and the load failed
  with "no stream has resolved codec parameters" (#575). The factory now reads the file's first six
  bytes when an `image2` source resolves nothing, and names the GIF decoder. An animated GIF is its
  first frame for the dwell, as the remarks always said. AVIF and ICO are containers `image2` cannot
  read and still fail, now with a message that names `MediaSource.FromFile`.
- **A codec can be kept on software while the others use hardware.**
  `HardwareDecodeOptions.ExcludedCodecs` lists codecs, named as `VideoStreamInfo.CodecName`
  reports them (`"av1"`, `"h264"`), that decode in software under `Auto` and fail as unbound
  under `Required`. `IPlayerBuilder.WithExcludedCodecs` and `IPassBuilder.WithExcludedCodecs` set it
  on a player or a pass, and `PlaybackController.Create` takes an
  `excludedCodecs` parameter. Empty by default. Source that calls it compiles unchanged, and a
  binary compiled against the old `Create` signature needs a rebuild, as it did when
  `preferredBackends` was added. A type that implements `IPlayerBuilder` or `IPassBuilder` itself
  no longer compiles until it adds the method.
  `HardwareDecodeUnavailableException` gained a constructor that takes the
  reason, for a codec that cannot decode on hardware without having failed to bind. It is the way
  out for a codec a driver decodes wrongly, and `WithHardwareDecode(Disabled)` turns hardware off for
  every codec instead.
- **AV1 decodes on hardware.** `avcodec_find_decoder` returns `libdav1d` for AV1, which has no
  hardware configs, so no backend was ever a candidate and AV1 always decoded in software (#417).
  The decoder now looks past the default for a registered decoder that can run on hardware when the
  default has no config, and finds FFmpeg's own `av1` decoder. `libdav1d` stays the software
  candidate, and `Disabled` is unchanged. Other codecs are untouched: their default decoder already
  has configs. On the five Windows backends tried (CUDA, D3D11VA, D3D12VA, DXVA2, Vulkan) 8-bit and
  10-bit 4:2:0 AV1 engages, and `Auto` picks D3D11VA first. An AV1 stream the hardware refuses at
  its first packet, such as 4:4:4 on NVDEC, decodes in software under `Auto` and faults under
  `Required`.
  What changes for a caller: an AV1 stream takes the paths a hardware-decoded stream takes. The
  decoder holds a surface pool with a budget, `CheckFrameBudget` refuses a path that holds frames
  without a bound, and the frames are GPU frames by default where the whole path takes them. A
  player that loaded AV1 in software before can fail to load, or present in another frame domain.
  `HardwareBackend` reads the backend where it read `null`. To keep AV1 on software while the
  other codecs use hardware, name it: `WithExcludedCodecs("av1")`, or
  `HardwareDecodeOptions.ExcludedCodecs`. `WithHardwareDecode(Disabled)` turns hardware off for
  every codec instead.
