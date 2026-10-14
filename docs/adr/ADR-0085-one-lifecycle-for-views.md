# ADR-0085: One lifecycle for views: the sink follows the view, presenting follows the attachment

## Status

**Accepted (2026-10-13) and implemented (2026-10-14)** under #691: staging slice 1 in #693, slice 2
in #695, slice 3 in #702, slice 4 in #692 (the core) and #697 (the interop view), slice 5 in #699,
slice 6 in #705 and slice 7 in #706. #704 fixed a defect in the pacer that slice 3 found.
[Implementation](#implementation) lists what shipped, what differs from the decisions, and what is
left. Proposed on 2026-10-13 in #689 and revised by review in #690;
[Revision history](#revision-history) lists what changed.

Motivating issues: #663 (`FrameFlowVideoView` never binds a caller's sink again after a detach and
reattach), #664 (`CompositionInteropVideoSink` keeps a frame presented after its disposal), #286
(nothing stops two players driving one sink) and #611 (the only delegate sink lives in the
examples). It would also replace the separate mechanisms that #643, #650 and #665 added, each
fixing one control, and it settles what swapping `FrameFlowPlayerView.VideoSurface` does to a
player built against the old surface.

Line numbers are on `main` at c4cb0b5.

**Related:**

- [ADR-0044](ADR-0044-sink-ownership-and-disposal.md): whoever constructs a sink owns and disposes
  it. Decisions 2, 3 and 7 apply that rule to the sinks and surfaces a view or player view
  constructs. Decision 6 amends its non-guarantee, "nothing enforces the 'at a time'"
  (`IVideoSink.cs:69-71`): for a claimable sink, one player at a time is enforced.
- [ADR-0016](ADR-0016-avalonia-presenter-frame-delivery-strategy.md),
  [ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md) and
  [ADR-0082](ADR-0082-driver-video-super-resolution.md): which thread copies, presents and tears
  down. The shell keeps all of them (decision 5).
- [ADR-0054](ADR-0054-fan-out-with-explicit-cloning.md) and
  [ADR-0078](ADR-0078-graph-chain-forks-joins-and-termination.md): a graph has one fan-out
  mechanism, `Branch`. Decision 8 adds a sink, not a second graph mechanism, and says when to use
  each.
- [ADR-0062](ADR-0062-gapless-playlist-warm-presenter.md): one warm sink across a playlist. This
  record keeps a view's sink warm across a trip out of the visual tree as well.
- [ADR-0066](ADR-0066-the-sink-contract-shape.md): a sink is one dataflow method, lifecycle is not
  the adapter's, and a capability only some sinks have is a side interface (§4). Decision 6 is such
  a capability. No member is added to `IVideoSink` or `IAudioSink`.
- [ADR-0084](ADR-0084-frames-carry-their-timeline.md): the record before this one in the same
  series of fixes. It replaced five local guesses at one fact with one value; this record does the
  same for a view's lifetime.

## Context

### Three mechanisms answer one question

Every view and chrome control has to answer the same question: what do attach, detach, reattach and
dispose do to the thing it is bound to? Three mechanisms answer it today, each added by one fix,
and two chrome controls answer it on their own.

- **`InteropViewLifecycle`** (#662, `InteropViewLifecycle.cs:79-128`): a pure state machine for
  `CompositionInteropVideoView`, with phases New, Presenting, Detached and Disposed, and work flags
  for releasing the sink, starting and stopping presentation. It is tested over every sequence of
  its four events up to length 8. It lives in `FrameFlow.Avalonia.Windows`, which references
  `FrameFlow.Avalonia` and not the reverse, so the CPU view cannot use it. It does not model
  assigning a sink or creating one.
- **`TransportFollower`** (#661, `TransportFollower.cs:38-87`): a shell helper for the chrome
  controls, with a live subscription and a suspended flag. It is not pure.
- **`FrameFlowVideoView`** has no model. Its attach creates a sink only when it has none
  (`FrameFlowVideoView.cs:271-280`), and its detach ends the binding and disposes an owned sink
  (`:283-291`).
- **`FrameFlowSeekBar`** and **`FrameFlowPositionLabel`** start and stop a `DispatcherTimer` on
  attach and detach themselves (`FrameFlowSeekBar.cs:139-155`, `FrameFlowPositionLabel.cs:54-71`).

The same meanings are spread across them:

| Meaning | Where it lives today |
| ------- | -------------------- |
| In the visual tree | `InteropViewLifecycle.Attached`; `TransportFollower`'s suspended flag, set only by a detach (`TransportFollower.cs:25`, `:41`); nothing on the CPU view |
| This attachment has ended, drop late callbacks | `SinkBinding.Detached` on both views (`FrameFlowVideoView.cs:131`, `CompositionInteropVideoView.cs:161`); the interop view's `_tornDown` flag (`CompositionInteropVideoView.cs:149`, `:719`, `:798`); `Follow._stopped` (`TransportFollower.cs:120`); the interop view's surface identity check (`CompositionInteropVideoView.cs:981`) |
| The bound target was replaced | `SinkAttachment.Ended`, which keeps diagnostics until the sink is replaced and deliberately survives a detach (`CompositionInteropVideoView.cs:243-245`, `:545`); the seek bar's transport generation (`FrameFlowSeekBar.cs:109-112`) |
| Take up a kept borrowed sink on return | the interop view does (`CompositionInteropVideoView.cs:504-505`); the CPU view does not (#663) |
| Something bound while out of the tree | followed at once before the first attach and deferred after a detach (`TransportFollower`); created and bound, with presenting deferred (the interop view's `Initialize`, `:378`, `:429-437`); bound and holding frames (the interop view's `Sink` setter); bound and copying frames (the CPU view) |
| A final state | `InteropViewLifecycle.Disposed` only; the CPU view and the chrome have none |

#663 is #650's defect in the neighbouring control: the interop view's fix taught it to rebind a
kept sink, and the CPU view never learned.

### A view disposes its sink while a player still feeds it

A view owns a sink it constructs and borrows one a caller assigns (ADR-0044). Both views dispose an
owned sink on every detach and make a new one on the next `EnsureSink`
(`FrameFlowVideoView.cs:283-314`, `CompositionInteropVideoView.cs:523-531`). The player was built
with the old sink (`FrameFlowAvaloniaBuilderExtensions.cs:46`, `:104`) and is not told. After a tab
switch, a panel swap or a docking move:

- on the CPU view, every frame is disposed on arrival (`AvaloniaVideoSink.cs:194-198`), and the
  picture freezes until the app rebuilds the player;
- on the interop view, the disposed sink keeps the last frame (#664, no disposed check in
  `CompositionInteropVideoSink.cs:157-186`) and its accepted-frame count keeps rising.

The views' `Sink` setters do the same to an owned sink when the app assigns another
(`FrameFlowVideoView.cs:183-184`, `CompositionInteropVideoView.cs:256`).

`IVideoSurface.AttachSink` documents "Idempotent: repeated calls return the same sink"
(`IVideoSurface.cs:27-28`). That holds only within one attachment. `BREAKING-CHANGES.md` currently
steers an app that needs a player to outlive a trip out of the tree to construct its own sink
(`:345-356`), which is a workaround for the view's own rule.

### The two views disagree before they are attached

A sink bound before the first attach is copied and counted as presented on the CPU view, raising
`FramePresented` for a view that is not on screen (`FrameFlowVideoView.cs:654-685` never checks
attachment). The interop view holds frames until it presents. Neither is documented as the rule.

Both views keep the newest frame on purpose. An item with one frame, a paused seek or an ended
stream offers no other, and a view that let it go would show nothing until playback moved (#287,
`FrameFlowVideoView.cs:80-87`; the interop view's sweeps at `CompositionInteropVideoView.cs:678-683`
and `:991-994`).

### Nothing enforces one player per sink

ADR-0044 says one sink serves one player at a time, and nothing enforces it (#286,
`IVideoSink.cs:69-71`). `AvaloniaVideoSink`'s gap tracker assumes its calls never overlap
(`AvaloniaVideoSink.cs:56-60`), and `IVideoSurface.AttachSink` is documented as idempotent, which
makes it easy to give one view's sink to two builders. Today a detach bounds the damage, because it
disposes the sink. Once the sink lives as long as the view (decision 2), two players built against
one view can feed it for as long as both live, interleaving two sources on one picture. #286 names
audio too: two sessions on one `IAudioSink` race `ActivateAsync` and `DeactivateAsync`, and an
audio sink that is also the `IClockSource` would be mastered by both.

A sink reaches a session through the player and pass builders, through the public
`PlaybackController.Create` (`PlaybackController.cs:146-168`), which the DI registration feeds with
a view's sink (`FrameFlowAvaloniaServiceCollectionExtensions.cs:92-96`), and through graphs built
by hand: `SinkAdapters.AsSinkNode` (`SinkAdapters.cs:53-67`), `Branch` edges inside a configurator,
and `RecorderPipeline`'s preview sink (`RecorderPipeline.cs:68`).

### One player, several views

One player can feed several views. The multicast examples register one sink with the player
(`PlayerWindow.axaml.cs:60`), and that sink presents each frame to three views' sinks
(`MulticastPanes.axaml.cs:29-34`, `:110-121`). Camera.Multicast ends a hand-built graph at the same
sink (`Camera.Multicast/MainWindow.axaml.cs:56`). The player paces the one sink, so the three views
stay in step. A `Branch` to each view would not be paced (#636).

That sink is `DelegatingVideoSink`, which lives in the examples and is not packed (#611). It drops
`OnFormatChangedAsync` (`DelegatingVideoSink.cs:49`), and the app declares its `MaxHeldFrames` by
hand. A claim the builder takes on it would not reach the views' sinks behind it: one of them could
be handed to a second player while the first still feeds it.

### A surface swap strands the player

`FrameFlowPlayerView.VideoSurface` removes the old surface from the tree and adds the new one
(`FrameFlowPlayerView.cs:176-188`). Removing it runs the old surface's detach, which disposes its
owned sink today, so a player built against the old surface goes on feeding a disposed sink while
the new surface shows nothing. The swap does not move the sink, does not tell the player, and does
not dispose the old surface, and `FrameFlowPlayerView` has no dispose of its own (`:53`). The
property is documented as meant for use before the first playback (`:172-174`), and nothing holds
to that.

A surface's sink is fixed when the chain is written, not when the player is built:
`WithVideoSurface` calls `AttachSink` at once (`FrameFlowAvaloniaBuilderExtensions.cs:104`).

### Threads

The lifetime events run on the UI thread. Frame arrival runs on the presenter thread, presents and
the first teardown steps on the UI thread, image release on the compositor's render thread, the
present continuation on whatever thread completes it, and producer disposal on the thread pool
(ADR-0064). The stall watchdog reads the sink from a timer thread
(`CompositionInteropVideoView.cs:856`), and `OnFrameArrived` reads it on the presenter thread
(`:740`), both without a barrier.

Some of the checks are locks, not flags. The CPU view holds its lock for the whole copy, so a detach
waits for a copy already running (`FrameFlowVideoView.cs:401-405`). The interop view checks
`Detached` under `_presentGate`, which also guards its posted-present flag
(`CompositionInteropVideoView.cs:722-727`).

## Decision

### 1. One pure lifecycle for every surface and chrome control

`SurfaceLifecycle`, a pure state machine in the `FrameFlow.Avalonia.Core` namespace of the
`FrameFlow.Avalonia` assembly, replaces `InteropViewLifecycle` and generalises it.
`FrameFlow.Avalonia.Windows` already sees `FrameFlow.Avalonia`'s internals
(`src/FrameFlow.Avalonia/AssemblyInfo.cs:10`), so both views and the chrome can use it without
making it public.

- **State.** A phase (New, Live, Detached, Disposed), whether the control is in the visual tree,
  whether it is initialised (only the interop view needs `Initialize`; the others start
  initialised), the target it is bound to (none, owned or borrowed), and a binding number.
- **Events.** `Initialize`, `Attach`, `Detach`, `Dispose`, `Assign(target)` (a caller's sink, a
  transport, or none) and `EnsureOwned` (a view creating its own sink).
- **Work.** Each transition returns what the shell must do, in order: create, bind, unbind or
  dispose the target, start or stop presenting (or following), and forget a borrowed target. The
  shell does that work and nothing else.

`InteropViewLifecycle`'s exhaustive test carries over and grows with the new events. The chrome
controls that follow a transport, including the seek bar's and position label's timers, use the
same machine with a transport as a borrowed target and no dispose; `TransportFollower` becomes its
shell.

### 2. The sink follows the view; presenting follows the attachment

An owned sink lives as long as the view, not as long as an attachment.

- **Detach** stops presenting: the binding ends, the compositor and GPU state go (ADR-0064's
  rebuild on detach stands), and the sink stays. While detached, a claimed sink (decision 6) keeps
  only its newest frame, in the latest-wins slot it already has and within the `MaxHeldFrames` it
  already declares. A newer frame replaces it, and the replaced frame is disposed. An unclaimed sink,
  fed by a graph built by hand, keeps nothing: it disposes each frame it is given, because no
  release would say when to let a kept frame go. Nothing is presented, and `FramePresented` is not
  raised.
- **Reattach** presents the kept frame, then goes on with the same sink, owned or borrowed. A player
  built before the trip keeps working, and a paused picture, a still or an ended item is on screen
  again at once. This is the fix for #663. A reattach builds no new D3D12 converter until the
  previous converter's teardown has completed, so the decoder frames the old one still holds are
  released before the new one takes any.
- **Claim released.** When the claim on a detached view's sink is released (decision 6), the sink
  disposes its kept frame. A view off screen does not pin the decoder surfaces of a player that is
  gone.
- **`EnsureSink` and `AttachSink`** return the same sink for the view's whole life, unless the app
  assigns `Sink`. That is what `IVideoSurface.AttachSink` already documents.
- **Assigning `Sink`** first claims the outgoing sink, as decision 7's swap does, so no build can
  claim it during the replacement. If a player holds it, the assignment throws
  `InvalidOperationException` naming the holder, because the player would be left feeding a sink no
  view presents. Otherwise it disposes an owned sink or forgets a borrowed one, as today, and
  releases that claim.
- **Dispose** disposes an owned sink and forgets a borrowed one. After it, the view refuses new
  sinks with `ObjectDisposedException`, as the interop view does today (#665).
- **Counting.** A frame replaced or disposed while the view is not attached is counted as discarded
  off screen, separately from the drop counts (the interop view's `FramesDropped`). A drop count
  stays a health signal: a background tab at 60 fps would otherwise add 3,600 drops a minute.
- **No finaliser.** A live player holds the sink and the sink's callback holds the view, so a
  finaliser would never run while it mattered. Once neither is referenced, the collector takes
  both. A finaliser would also run lifecycle work off the UI thread, against decision 5.

`FrameFlowVideoView` gains `IAsyncDisposable` for the dispose step. A view that is never disposed
keeps one sink object, and at most its newest frame until the claim on its sink is released.

### 3. Before the first attach, both views keep the newest frame and present nothing

A sink bound before the view first joins the tree keeps its newest frame when it is claimed, and
keeps nothing when it is not, as on a detached view.
Neither view copies, presents or raises `FramePresented` until it is attached, and the first attach
presents the kept frame. The CPU view's current pre-attach copying stops; the interop view already
works this way. Nothing waits on a view's first present: player warm-up waits on the decoder's first
frame (`SubstrateSession.cs:889`), and the pacer never waits for a present.

### 4. A disposed sink disposes what it is given

Every in-box sink with state disposes a frame it is given after its own disposal, does not count it
as accepted or presented, and treats a second dispose as a no-op that logs nothing (ADR-0044,
`IVideoSink.cs:57-60`). That covers `AvaloniaVideoSink`, `CompositionInteropVideoSink`,
`SdlVideoSink`, `HeadlessVideoSink`, and decision 8's `FanOutVideoSink` and `DelegatingVideoSink`.
`HeadlessVideoSink` keeps counting such frames in `AbandonedCount`, which tests read.

Each sink's frame slot has an open or closed state under its own gate. Dispose takes the frame and
closes the slot in one step, and a frame installed into a closed slot is disposed. That is the fix
for #664 on `CompositionInteropVideoSink`, and it closes the check-then-set race in
`AvaloniaVideoSink` (`:194`, `:250`, `:326-328`) and in `SdlVideoSink` (`SdlVideoSink.cs:137-145`,
`:276-282`), where a frame set after dispose's final take stays in the slot.

### 5. One binding number, compared where work commits

The UI thread runs the lifecycle. Each transition that starts or ends a binding (attach, detach and
`Assign`) advances one binding number.

- **Work that commits a result** (a copy, a swap, a present) compares the number under the lock the
  transition also takes, at the point where it commits, and drops its result when the number has
  moved. The CPU view's lock and the interop view's `_presentGate` stay; the comparison moves inside
  them. Reading the number once and acting later would race the detach.
- **Readers that take no lock** (the stall watchdog's timer and diagnostics) read one immutable
  snapshot that the shell publishes with `Volatile.Write` after each transition: whether the view
  presents, the binding number, and the sink.

That replaces the four "this attachment has ended" mechanisms and the seek bar's transport
generation in the Context table. The interop view's surface identity check stays only if it guards
something the number does not. `SinkAttachment` stays: it keeps a sink's diagnostic baselines
together until the sink is replaced, which is a different window from an attachment.

Work that finishes off the UI thread keeps snapshotting what it owns at the transition that started
it, as `StopPresenting` does today, so the keyed-mutex order of ADR-0064 does not change.

### 6. One player per sink, by a claim taken where the sink is attached

`FrameFlow.Media` gains a public side interface, `IClaimableSink` (ADR-0066 §4), for video and audio
sinks alike.

- **Shape.** A non-blocking try-claim that takes a description of the claimant and returns a lease
  or fails, and a read of the current claimant's description for error messages. Releasing a lease
  is idempotent, safe from any thread, and releases only the claim that lease took. Claiming a
  disposed sink throws `ObjectDisposedException`.
- **Who implements it.** Every in-box sink with state: `AvaloniaVideoSink`,
  `CompositionInteropVideoSink`, `SdlVideoSink`, `HeadlessVideoSink`, `OpenAlAudioSink` and decision
  8's `FanOutVideoSink`. `NullVideoSink` has no state and is exempt. A sink that presents to another
  sink claims that sink when it is claimed, and releases it on its own release. The internal
  `ClockSelectVideoSink` needs nothing: it wraps a sink after that sink has been claimed.
- **Who claims.** `PlaybackController.Create`, the internal `CreatePlaylist` behind the player
  builder, and the pass builder claim every sink they are given, video and audio, before they build
  anything else. The DI registration goes through the same factory. A second claim fails with
  `InvalidOperationException` naming the sink and its holder, and saying to await the disposal of
  the player or pass already built against it. Stopping it is not enough.
- **Release.** A claim is released after the controller's or pass's dispose completes, which awaits
  the graph and the pacer's delivery loop (`SubstrateSession.cs:1541-1557`). No frame from the
  first player can reach the sink after a second has claimed it. A build that fails or is cancelled
  after claiming releases its claims in a `finally` before it throws. A pass holds its claim until
  it is disposed, not until its run ends.
- **Sequential use.** A sink can still serve several players in sequence, as ADR-0044 and ADR-0062
  require. A playlist's prepared-ahead items live inside one player and share its claim.
- **Limits.** Graphs built by hand take no claim: `SinkAdapters.AsSinkNode`, `Branch` edges inside
  a configurator and `RecorderPipeline`'s preview sink keep ADR-0044's documented rule. So does a
  sink without the interface. This is the fix for #286 for players and passes.

### 7. A surface is swapped out under the player view's own claim

`FrameFlowPlayerView.VideoSurface` swaps out an in-box surface by first claiming that surface's
sink itself. It reads the sink through an internal interface that both views implement, which
`FrameFlow.Avalonia.Windows` can see.

- **Held by a player.** The swap throws `InvalidOperationException` naming the holder, and telling
  the app to dispose that player first or to build against the new surface.
- **Claim taken.** No player feeds the surface, as far as claims show, and no build can claim it
  while the swap runs. The surface leaves the tree and keeps its sink (decision 2).
- **The default surface**, which the player view constructed, stays alive and stays claimed by the
  player view until it is swapped back in or the player view is disposed. A build written against
  it after it was swapped out fails at its claim, instead of playing to a surface that is not
  shown. `FrameFlowPlayerView` gains `IAsyncDisposable` to dispose it.
- **A caller's surface** goes back to the caller with the player view's claim released.
- **A third-party `IVideoSurface`** exposes no sink to the player view (`IVideoSurface.cs:24`,
  `:39`). It is swapped out without a check and stays the caller's.

"As far as claims show" is the limit. A player that feeds a view's sink through a sink that does
not pass the claim on, or through a graph built by hand, is not seen, and the swap strands it. The
player view never moves a player from one sink to another: a player's sink is fixed when it is
built.

### 8. A fan-out sink passes the claim to the sinks it feeds

`FrameFlow.Media` gains `FanOutVideoSink`. It sits behind the player's pacer, so its targets are
paced together. A `Branch` is a graph fan-out and is not paced (#636). The fan-out is a sink, not a
second graph fan-out mechanism (ADR-0054, ADR-0078). Use `Branch` for consumers that do not present,
such as inference or recording, and a fan-out sink for pictures that must stay in step.

For example, the multicast panes become:

```csharp
Sink = new FanOutVideoSink(
    _direct,
    _detection,
    FanOutTarget.Transform(_filtered, frame => ColorFilters.Apply(frame, CurrentFilter)));
```

- **Targets.** Given at construction: at least one, none null, and none repeated, including through
  nested fan-outs. They are fixed at construction, so fan-outs cannot form a cycle.
- **Transforms.** A target may come with a transform, which borrows the incoming frame and returns a
  new frame for that target. The target owns the frame it is given. A target with a transform
  declares the domains its transform accepts, CPU by default. A transform keeps the frame's
  dimensions and pixel format, so the format forwarded to its target stays true. The fan-out checks
  each transform's output against the incoming frame. When they differ, it disposes the output and
  fails that target's call.
  A change of format belongs in the graph, as a node before the sink.
- **Delivery and ownership.** For each target in order, the fan-out takes a reference just before
  the call (`AddRef` returns the same instance, `IVideoFrame.cs:77`), and the target owns that
  reference even when it throws. The fan-out disposes its own reference in a `finally`. It calls
  every target even when one throws or is cancelled, then rethrows what it collected.
- **The other members.** `OnFormatChangedAsync` goes to every target by the same rule.
  `MaxHeldFrames` is the sum of the targets' values, or null when any is null. The sum over-counts,
  because the targets hold the same frames, which is the safe direction for the pacer and the
  decoder's pool guard. `AcceptedDomains` is the intersection of what each target, or its
  transform, accepts. An empty intersection fails construction, and an intersection narrower than a
  target's own domains is logged once. `PreferredBackends` is the first non-empty list among the
  targets. `GetDiagnostics` reports the fan-out's own counts; each target keeps its own.
- **Claim.** It implements `IClaimableSink`. Claiming it claims every claimable sink beneath it,
  flattened through nested fan-outs, in one global order given by a per-sink sequence number, all or
  none. The flattened set is fixed at construction, because the targets are. When one sink is held,
  the fan-out releases those it took and fails naming that sink and its holder. With one global
  order and a non-blocking try, two builds racing for overlapping fan-outs cannot both fail: for
  both to fail, each would have to hold a sink that comes before the one it failed on in the other's
  order. A target without the interface is fed but not claimed. A fan-out that no player is using holds no claims,
  so it does not block a surface swap (decision 7).
- **Ownership.** It does not own its targets (ADR-0044). Disposing it disposes none of them; after
  its own disposal it disposes what it is given (decision 4).
- **Views.** A target view that is detached keeps its newest frame (decision 2), and the other
  targets go on.

`DelegatingVideoSink` moves from the examples to `FrameFlow.Media`, for a sink that ends in the
app's own code, such as one that writes frames to disk. It takes the delegate in its constructor,
and `MaxHeldFrames`, `AcceptedDomains` and `PreferredBackends` as init properties, so members can be
added later without a binary break. It claims nothing, because it cannot see what its delegate
calls, and its documentation says not to present to another sink from it. Together the two sinks
are the fix for #611.

### Staging

Each slice carries its own BREAKING-CHANGES entry.

1. **Disposed sinks.** Decision 4 on the in-box sinks, with tests that a frame given after dispose
   is disposed, and that a second dispose does nothing and logs nothing. The fix for #664.
2. **Claims.** Decision 6: `IClaimableSink` on the in-box sinks, claimed by the controller factories
   and the pass builder. Tests: a second build against a held sink fails naming the holder; a build
   after the first player's awaited disposal succeeds; a build that fails or is cancelled releases
   its claims; a lease released twice does not release a later claim; the same for an audio sink.
   The slice moves any example that builds a player before disposing the previous one. The fix for
   #286 for players and passes.
3. **Fan-out.** Decision 8: `FanOutVideoSink` and `DelegatingVideoSink` in `FrameFlow.Media`. The
   slice deletes the example copy, because a second type of that name would not compile beside
   `using FrameFlow.Media` (`MulticastPanes.axaml.cs:4`, `:6`). It moves the multicast examples onto
   `FanOutVideoSink` and rewrites the v0.10.0 note that cites them (`BREAKING-CHANGES.md:1638`).
   Tests: claims are all or none and released together; of two builds racing for overlapping
   fan-outs, nested ones included, one wins; construction refuses an empty intersection and a
   repeated target; a target that throws leaks no reference; formats reach every target; a
   transform that changes the format fails its target's call. The fix for #611.
4. **The core and the interop view.** `SurfaceLifecycle` with the exhaustive test, and the interop
   view moved onto it with decisions 2, 3 and 5 and the `Sink` setter's refusal over a claimed sink.
5. **The CPU view.** `FrameFlowVideoView` onto the core with decisions 2, 3 and 5, the `Sink`
   setter's refusal, and `IAsyncDisposable`. Headless reattach tests for an owned and a borrowed
   sink: a frame given after reattach is presented, and a frame kept while detached is presented on
   reattach. The fix for #663.
6. **The chrome and the player view.** The chrome controls, `TransportFollower` and the seek bar's
   and position label's timers onto the core, with no behaviour change intended. Decision 7 on
   `FrameFlowPlayerView.VideoSurface`, and `IAsyncDisposable` on the player view. Tests: a held
   sink is refused; an idle default surface is kept and claimed, a build against it fails, and
   swapping it back releases the claim; an idle caller's surface is returned unclaimed; a
   third-party surface is swapped without a check.
7. **Docs.** `IVideoSurface`'s remarks. The remarks that promise presenting before attach
   (`FrameFlowAvaloniaBuilderExtensions.cs:17-21`) or describe disposal on detach
   (`FrameFlowVideoView.cs:172`, `:241`; `CompositionInteropVideoView.cs:213`, `:395`).
   BREAKING-CHANGES entry 12, which steers apps to their own sink (`BREAKING-CHANGES.md:324`,
   `:345-356`). The example comments that say the view disposes its own sink
   (`AvaloniaPlayer/MainWindow.axaml.cs:198`, `DualPlayer/MainWindow.axaml.cs:103`). The README
   quick start. The docs also say that a view hidden with `IsVisible = false` stays attached and
   keeps copying, and that removing it from the tree is what stops it.

## Implementation

| Slice | Change | BREAKING-CHANGES | Shipped in |
| ----- | ------ | ---------------- | ---------- |
| 1 | Decision 4: `LatestWinsFrameSlot` gains a closed state (`Close`, `Install`, `Installed`, `FrameSlotInstall`), which the three slot-based in-box sinks close on dispose; `HeadlessVideoSink` already disposed a late frame (#664) | 17 | #693 |
| 2 | Decision 6: `IClaimableSink` and `SinkClaimGate` in `FrameFlow.Media`, on the five in-box sinks with state; `PlaybackController.Create`, `CreatePlaylist` and the pass builder claim (#286) | 18 | #695 |
| 3 | Decision 8: `FanOutVideoSink`, `FanOutTarget` and `DelegatingVideoSink` in `FrameFlow.Media`; the multicast examples on the fan-out; the examples' copy deleted (#611) | 21 | #702 |
| 4 | Decision 1: `SurfaceLifecycle`, tested by a walk over every reachable state | none | #692 |
| 4 | `CompositionInteropVideoView` on the core, with decisions 2, 3 and 5 and the `Sink` setter's claim; the sink's off-screen mode; `InteropViewLifecycle` removed | 19 | #697 |
| 5 | `FrameFlowVideoView` on the core, with decisions 2, 3 and 5, the `Sink` setter's claim and `IAsyncDisposable` (#663) | 20 | #699 |
| 6 | The chrome on the core through `TransportFollower`; decision 7 on `FrameFlowPlayerView.VideoSurface`, and `IAsyncDisposable` on the player view; the interop view takes its sink off screen before the binding number moves (#700) | 23 | #705 |
| 7 | The remarks the code slices left; BREAKING-CHANGES entries 12 and 18; the README; an amendment to ADR-0044; the test bench disposes its surface rather than the surface's sink; this section | | #706 |

#704 fixed what slice 3 found in the pacer (#701): when a sink's present threw, the pacer disposed
the frame the sink already owned. A sink now owns the frame from the call, even when the call
throws, which decision 8 already said of a fan-out's targets. BREAKING-CHANGES entry 22 records it.
A sink that throws no longer stops the pacer for the rest of the item without a word (#703). Each
failure costs its frame and is reported on `ErrorOccurred`, and a sink that throws on more than
eight calls in a row faults the item, as a fault in its decoding does. BREAKING-CHANGES entry 24
records it.

What differs from the decisions, slice by slice. None reverses one.

**The core (slice 4, #692).** Every control on the core relies on these.

- The binding number moves exactly when a binding starts or ends, not on every attach, detach and
  `Assign`. A first attach that finds the control already bound leaves it alone, so a follow bound
  before that attach stays current. A dispose that unbinds moves it.
- A target assigned or created before the first attach is bound at once, and nothing presents until
  the attach, so decision 3 holds. One assigned while detached is bound when the control rejoins.
  A chrome control given a transport before its first attach follows it from then, as it did.
- Presenting needs a target. Assigning none while presenting stops presenting: clearing the interop
  view's `Sink` while it is attached takes its compositor side down, where the last frame used to
  stay up. The CPU view keeps its picture.
- A control that never presented runs no teardown. `Initialize` after dispose is refused. The
  teardown runs unbind, stop presenting, then dispose, and each step runs even when one before it
  threw.
- The core knows nothing of claims. The shells and the sinks hold the claim checks.

**Claims (slice 2, #695).**

- `IClaimableSink` has a third member, `ClaimOrder`, the one process-wide order a claim on several
  sinks follows, so a sink of an app's own can take part in decision 8's ordering. `SinkClaimGate`
  is public for the same reason.
- A pass disposed during its run keeps its claim until the run ends, because its dispose does not
  wait for the run.
- The player builder claims after the FFmpeg bootstrap, and the pass builder before it.

**The fan-out (slice 3, #702).**

- Decision 8's example does not compile: C# allows no user-defined conversion from an interface. A
  fan-out over plain sinks takes them directly, and one that mixes them with transforms names each
  target with `FanOutTarget.Of` or `FanOutTarget.Transform`.
- The format check also covers pixel shape and rotation, and a transform's output is checked
  against the memory its sink takes.
- When every target was cancelled, the first `OperationCanceledException` is thrown as it is, so the
  pacer still reads a cancellation.
- `PreferredBackends` is the first list among the targets without a transform, kept to the backends
  whose frames the fan-out takes.
- The fan-out lists an entry of its own among its claim targets, so a build over a disposed fan-out
  throws `ObjectDisposedException`.

**The views (slices 4 and 5, #697 and #699).**

- The off-screen mode lives in `LatestWinsFrameSlot`, under the slot's own gate, so a change of mode
  cannot race an arriving frame. Keeping follows the claim, through the sink's `TryClaim` and the
  claim gate's release hook. Both sinks count what they let go of off screen in a new
  `FramesDiscardedOffScreen`.
- The interop view holds the frame it presented last until it presents the next, and hands it back
  to its sink when it stops presenting, which is how a paused picture comes back. The CPU view needs
  no hand-back: its bitmaps hold none of the player's frames, and they survive the detach.
- After D3D12VA frames, a reattach holds every present of the new attachment, not only the new
  converter's build, until the previous converter has been disposed.
- The CPU view copies a frame its sink kept on the UI thread, once per attach or rebind, an
  exception to ADR-0016 like its blit after a size change. A copy that a transition strands counts
  as discarded off screen on the sink that made it.
- Both views take a held sink off screen before the binding number moves, so a frame that arrives in
  between is not counted as a drop. The interop view has done so since #705 (#700).
- Assigning `Sink` over a caller's sink that its owner has already disposed goes ahead without a
  claim.
- The interop view's `FramesCommitted` still counts a hand-off that completes after its binding
  ended. Only `FramePresented` is gated.
- The CPU view's `DisposeAsync` also releases its bitmaps.

**The chrome and the player view (slice 6, #705).**

- A default surface with no sink yet is given one before the player view claims it, so that a build
  against it fails at its claim, as decision 7 says.
- `FrameFlowPlayerView.DisposeAsync` disposes the default surface before it releases its claim, and
  is final: a later swap throws.
- The internal interface both views implement carries the sink only. The player view disposes only
  the `FrameFlowVideoView` it made.
- The seek bar changes at the end of a binding, where decision 1 intended no change: out of the tree
  it no longer seeks on a `Value` write, a scrub target still queued is dropped, and a refusal from
  an ended binding is not reported. The seek bar's and position label's ticks run only with a
  transport bound. BREAKING-CHANGES entry 23 lists them.

What is left:

- Open question 1, whether a graph built by hand claims its sinks for its run, is #694. Until it is
  settled, a view fed only by such a graph keeps no frame while detached.
- #696: a pass disposed during its run frees the pipeline, decoders and demux the run still uses.
  That predates this record. Slice 2 keeps the claim until the run ends, and the native teardown
  order is still open.
- #698: the interop view can keep a gone player's last frame, when that frame was pending as the
  claim ended and was presented after it, until the next present, a detach or its dispose.

## Consequences

### Positive

- One model, tested once, for every control that binds to something. Three mechanisms, two
  hand-written timer handlers and five late-callback checks become one machine and one number.
- A player keeps working across a tab switch, a panel swap or a docking move, with no rebuild and
  no app-owned sink needed for it. A paused picture comes back with the view.
- `IVideoSurface.AttachSink` becomes as idempotent as it says.
- The two views agree before attach, after detach and after dispose.
- A view off screen pins at most one frame, and none once its player is gone.
- Two players or passes can no longer drive one in-box sink at once, video or audio. A surface
  swap can no longer strand a player that claims can see. Both fail at the call that would cause
  them, naming the holder.
- One player feeds several views through an in-box sink. The views are paced together, and each
  view's sink is claimed.

### Negative

- Behaviour changes an app can see, each with a BREAKING-CHANGES entry in its slice:
  - the owned sink is no longer disposed on detach, `Sink` no longer reads null after one, and
    `EnsureSink` returns the same sink after a reattach;
  - the CPU view no longer copies or raises `FramePresented` before its first attach;
  - a disposed CPU view throws from `EnsureSink`;
  - assigning `Sink` over a claimed sink throws;
  - a second player or pass built against a held in-box sink fails, so an app awaits the previous
    player's disposal before it builds the next;
  - a pass holds its claim until it is disposed, not until its run ends;
  - swapping out an in-box surface that a player holds throws, and a swapped-out default surface
    stays claimed until it is swapped back in.
- The claim sees only what passes it on. A graph built by hand, an app's decorator that does not
  forward the claim, and a sink without the interface are invisible to decisions 6 and 7. A view
  fed only by a graph built by hand keeps no frame while detached, so a paused picture there is
  blank after a reattach until the next frame.
- An app that relied on detach to release the owned sink now has to dispose the view. Avalonia never
  disposes controls, so only the docs tell it. A view that is never disposed keeps its sink object.
- The interop view's lifecycle moves out of `FrameFlow.Avalonia.Windows`, which replaces the core
  #662 added.
- A paused player keeps one decoder frame pinned in each detached view it feeds, for as long as the
  player lives. It is the frame an attached view would hold anyway, and the sink's `MaxHeldFrames`
  already counts it against the decoder's pool, so it adds no pool pressure the attached case does
  not. It is the cost of a paused picture coming back on reattach.
- A fan-out presents to its targets in turn on the presenting thread, so a transform for one
  target, such as a filter, delays all of them. A paced branch stays with #636.
- A fan-out over the interop view and the CPU view accepts CPU frames only, so the interop view
  loses zero-copy in that pairing.
- An app with its own copy of the example `DelegatingVideoSink` gets a name clash with the library
  type.

### Neutral

- GPU and compositor teardown on detach does not change, and neither does the keyed-mutex order
  (ADR-0064). The sink and its newest frame survive the detach.
- `SdlVideoSink` owns a window, not a place in a visual tree. Decisions 1-3, 5 and 7 do not apply
  to it. Decisions 4 and 6 do.

## Alternatives considered

### Keep fixing controls one at a time

#643, #650 and #665 did this, and #663 shows what it costs: each fix teaches one control, and the
neighbouring control keeps the defect. It also leaves the late-callback checks to drift.

### Keep disposing the owned sink on detach, and tell the player

The view could raise an event when it disposes its sink, and the player could swap to the
replacement. That makes a player depend on a view's tree position, couples `FrameFlow.Player` to
the views, and still rebuilds a sink on every tab switch. ADR-0062 went the other way for playlists:
keep the sink warm.

### Drop every frame while detached

The first draft dropped every frame while a view was detached or not yet attached, so that nothing
was pinned. That loses the only frame of a still, a paused seek or an ended item, which the views
keep today for that reason (#287). The sink's `MaxHeldFrames` already budgets for one kept frame.
Releasing that frame with the claim covers the case where pinning matters: a player that is gone.
An unclaimed sink has no such release, so it still drops.

### Let the player own the view's sink

ADR-0044 settled that whoever constructs a sink disposes it, and a player is a user of a sink, never
its owner. Reversing that here would make the view's own sink the one exception.

### Put the lifecycle on `IVideoSurface`

Adding members to `IVideoSurface` breaks every implementation, and ADR-0066 keeps lifecycle off
the sink contract and puts an optional capability on a separate interface. The lifecycle is an
implementation of the in-box views, so it stays internal.

### Two machines, one for views and one for chrome

The chrome has no ownership and no dispose, so it would use part of the machine. Keeping it on its
own helper keeps two models of "in the tree" and "late notification", which is the duplication this
record removes. A borrowed target with no dispose is a case the one machine already has.

### Rebind the player on a swap

The player view could move the player onto the new surface's sink. A player's sink is fixed when it
is built, and no transport member replaces it. Adding one for this caller would put sink
replacement, with its ordering and draining questions, on every player. Refusing to swap out a
surface a player may still feed keeps the rule that a player's sink does not change.

### Check for idleness without claiming

The first draft swapped a surface out when its sink held no claim. That check is a read, so a build
on another thread could claim the sink between the check and the swap. A chain written with
`WithVideoSurface` before the swap and built after it would also claim the swapped-out sink and
play to a surface that is not shown. Taking the claim is the check and closes both.

### Enforce one producer inside the sink

A sink could detect two producers from the frames it receives, for example by their timelines
(ADR-0084). Frames from two players interleave as alternating new timelines, which a sink cannot
tell from fast seeks. A claim taken at build fails before anything is decoded and names the cause.

### A registry of attached sinks in the builder

#286 offered a cheaper check: the builder records attached sinks by identity in a static table and
refuses a second attach. It needs no interface. It is process-wide mutable state, and it cannot see
the sinks behind a fan-out unless the fan-out reports them, which needs a member anyway. An app's
own decorator could not take part.

### Let the builder take several sinks

#636 suggests `WithVideoSink` could accumulate sinks, and the builder could then claim each one.
That changes how a player paces, which is #636's question, and the builder still cannot see sinks
behind a delegate. A fan-out sink works with today's one paced sink, and a builder that took
several sinks could wrap them in one.

### One delivery delegate on the fan-out

The previous revision let the app replace the fan-out's whole delivery with one delegate given the
frame and the targets. The fan-out then cannot know what the delegate holds or converts, so it
cannot derive `MaxHeldFrames` or `AcceptedDomains`, and the app has to get the ownership of every
reference right. A transform per target keeps both derivable and fixes ownership in one place.

## Open questions

1. **Graphs built by hand.** `SinkAdapters.AsSinkNode` could claim its sink when the graph starts
   and release it when the graph completes. That would bring the camera examples and
   `RecorderPipeline`'s preview under decisions 6 and 7, and let their views keep a frame while
   detached. Is a graph's run a clear enough lifetime for a claim?

## Revision history

- **2026-10-13, review turn 1.** The surface swap and #286 were open questions. Review pointed out
  that a sink kept for the view's life makes both user-visible failures rather than cleanup
  details: a swap strands the player on the old sink, and two players built against one view feed
  it for as long as both live. Decisions 6 (one player per sink, by a claim) and 7 (no swap of a
  claimed surface) settle them, with Context sections and staging to match.
- **2026-10-13, review turn 2.** The claim interface is optional, so decision 7 could not see a
  player feeding a sink without it. Decision 7 now swaps out a surface only when it is provably
  idle (no sink, or a claimable sink with no claim) and otherwise refuses; for a sink without the
  interface the app clears the outgoing surface's `Sink` to say no player feeds it.
- **2026-10-13, fan-out.** One player per sink does not break the multicast examples, which use one
  player. But the claim covered only the sink handed to the builder, so a view's sink behind the
  examples' fan-out could be handed to a second player. Decision 8 adds an in-box fan-out sink that
  passes the claim on, and brings `DelegatingVideoSink` into the library (#611).
- **2026-10-13, review panel.** Four reviewers read the whole record against the code.
  - Decisions 2 and 3 keep the newest frame instead of dropping every frame, so a paused or
    single-frame picture survives a detach; the kept frame goes when the claim is released. The
    first draft's open questions, a finaliser and counting detached drops, are settled in
    decision 2.
  - Decision 5 compares the binding number under the transition's lock where work commits, and the
    lock-free snapshot serves only lock-free readers. The Context table separates an ended
    attachment from a replaced target, adds `_tornDown`, and keeps `SinkAttachment` for
    diagnostics.
  - Decision 6 is a public `IClaimableSink` for video and audio, claimed in the controller factories
    and the pass builder, released after dispose completes and on a failed build. Graphs built by
    hand are named as outside it. The views' `Sink` setters refuse to replace a claimed sink.
  - Decision 7 takes the claim instead of reading it, applies to in-box surfaces only, keeps the
    default surface alive under the player view's claim, and gives `FrameFlowPlayerView`
    `IAsyncDisposable`. A sink without the claim interface cannot sit in an in-box surface, so the
    turn 2 rule for one is gone.
  - Decision 8 replaces the whole-delivery delegate with per-target transforms, specifies every
    sink member, orders claims so racing builds cannot both fail, and says how a fan-out sink
    relates to `Branch` (ADR-0054, ADR-0078).
  - Decision 4 lists every in-box sink. Staging has seven slices, each with its own BREAKING-CHANGES
    entry, and the example `DelegatingVideoSink` goes in the slice that adds the library one.
  - The merge of the first draft (#689) closed three issues it only proposed to fix (#286, #663,
    #664), through closing keywords in its description and commit message. They were reopened.
- **2026-10-13, automated review turn 1.**
  - A kept frame was released only with a claim, and graphs built by hand take none, so such a frame
    had no release. Only a claimed sink keeps a frame while detached now.
  - The `Sink` setter's refusal was a check followed by a replacement. It now claims the outgoing
    sink for the replacement, as the swap does.
  - Ordering each fan-out's own targets did not order claims through nested fan-outs, so two racing
    builds could both fail. A fan-out now claims its flattened set in one global order.
  - A transform could change the format its target had been told. Transforms now keep dimensions and
    pixel format, and the fan-out checks it.
- **2026-10-13, automated review turn 2.** The cost of keeping a paused frame for the player's life
  is stated under Negative. A transform output that fails the format check is disposed.
- **2026-10-14, on implementation.** Status names the pull requests, and
  [Implementation](#implementation) lists what shipped, what differs and what is left. The
  decisions are unchanged.
