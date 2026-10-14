# One lifecycle for views: the sink follows the view, presenting follows the attachment

## Status

Draft, pending number assignment at merge. **Proposed** (2026-10-13). Not implemented.

Motivating issues: #663 (`FrameFlowVideoView` never binds a caller's sink again after a detach and
reattach), #664 (`CompositionInteropVideoSink` keeps a frame presented after its disposal) and #286
(nothing stops two players driving one sink). It would also replace the separate mechanisms that
#630, #643, #650 and #665 added, each fixing one control, and it settles what swapping
`FrameFlowPlayerView.VideoSurface` does to a player built against the old surface. Decision 8 also
closes #611 (the only fan-out sink lives in the examples).

Line numbers are on `main` at c4cb0b5.

**Related:**

- [ADR-0044](ADR-0044-sink-ownership-and-disposal.md): whoever constructs a sink owns and disposes
  it. Decisions 2 and 3 apply that rule to the sinks a view constructs.
- [ADR-0016](ADR-0016-avalonia-presenter-frame-delivery-strategy.md),
  [ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md) and
  [ADR-0082](ADR-0082-driver-video-super-resolution.md): which thread copies, presents and tears
  down. The shell keeps all of them (decision 5).
- [ADR-0062](ADR-0062-gapless-playlist-warm-presenter.md): one warm sink across a playlist. This
  record keeps a view's sink warm across a trip out of the visual tree as well.
- [ADR-0066](ADR-0066-the-sink-contract-shape.md): a sink is one dataflow method, and lifecycle is
  not the adapter's. Decision 4 changes no sink interface.
- [ADR-0084](ADR-0084-frames-carry-their-timeline.md): the record before this one in the same
  series of fixes. It replaced five local guesses at one fact with one value; this record does the
  same for a view's lifetime.

## Context

### Three mechanisms answer one question

Every view and chrome control has to answer the same question: what do attach, detach, reattach and
dispose do to the thing it is bound to? Today three mechanisms answer it, each added by one fix.

- **`InteropViewLifecycle`** (#662, `InteropViewLifecycle.cs:79-127`): a pure state machine for
  `CompositionInteropVideoView`, with phases New, Presenting, Detached and Disposed, and work flags
  for releasing the sink, starting and stopping presentation. It is tested over every event sequence
  up to length 8. It is internal to `FrameFlow.Avalonia.Windows`, so the CPU view cannot use it, and
  it does not model assigning a sink or creating one.
- **`TransportFollower`** (#661, `TransportFollower.cs:38-87`): a shell helper for the chrome
  controls, with a live subscription and a suspended flag. It is not pure.
- **`FrameFlowVideoView`** has no model. Its attach creates a sink only when it has none
  (`FrameFlowVideoView.cs:271-280`), and its detach ends the binding and disposes an owned sink
  (`:283-291`).

The same meanings are spread across them:

| Meaning | Where it lives today |
| ------- | -------------------- |
| In the visual tree | `InteropViewLifecycle.Attached`; `TransportFollower`'s suspended flag; nothing on the CPU view |
| This attachment has ended, drop late callbacks | `SinkBinding.Detached` on both views (`FrameFlowVideoView.cs:131`, `CompositionInteropVideoView.cs:161`); `SinkAttachment.Ended` (`CompositionInteropVideoView.cs:545`); `Follow._stopped` (`TransportFollower.cs:120`); the seek bar's transport generation (`FrameFlowSeekBar.cs:112`); the interop view's surface identity check (`CompositionInteropVideoView.cs:981`) |
| Take up a kept borrowed sink on return | the interop view does (`CompositionInteropVideoView.cs:504-505`); the CPU view does not (#663) |
| Something bound while out of the tree | deferred (`TransportFollower`, the interop view's `Initialize`); bound and holding frames (the interop view's `Sink` setter); bound and copying frames (the CPU view) |
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

`IVideoSurface.AttachSink` documents "Idempotent: repeated calls return the same sink"
(`IVideoSurface.cs:27-28`). That holds only within one attachment. `BREAKING-CHANGES.md` currently
steers an app that needs a player to outlive a trip out of the tree to construct its own sink, which
is a workaround for the view's own rule.

### The two views disagree before they are attached

A sink bound before the first attach is copied and counted as presented on the CPU view, raising
`FramePresented` for a view that is not on screen (`FrameFlowVideoView.cs:654-685` never checks
attachment). The interop view holds frames until it presents. Neither is documented as the rule.

### Nothing enforces one player per sink

ADR-0044 says one sink serves one player at a time, and nothing enforces it (#286,
`IVideoSink.cs:69-71`). `AvaloniaVideoSink`'s gap tracker assumes its calls never overlap
(`AvaloniaVideoSink.cs:56-60`), and `IVideoSurface.AttachSink` is documented as idempotent, which
makes it easy to give one view's sink to two builders. Today a detach bounds the damage, because it
disposes the sink. Once the sink lives as long as the view (decision 2), two players built against
one view can feed it for as long as both live, interleaving two sources on one picture.

### One player, several views

One player can feed several views. The multicast examples register one sink with the player, and
that sink presents each frame to three views' sinks (`MulticastPanes.axaml.cs:29-34`, `:110-121`).
The player paces the one sink, so the three views stay in step. That sink is `DelegatingVideoSink`,
which lives in the examples and is not packed (#611). A claim the builder takes on the sink it is
given would not reach the views' sinks behind it. One of them could be handed to a second player
while the first still feeds it through the fan-out.

### A surface swap strands the player

`FrameFlowPlayerView.VideoSurface` swaps the hosted control and nothing else
(`FrameFlowPlayerView.cs:176-188`): it does not move the sink, does not tell the player, and does
not dispose the old surface. A player built against the old surface goes on feeding it while the
new surface shows nothing. The property is documented as meant for use before the first playback
(`:172-174`), and nothing holds to that.

### Threads

The lifetime events run on the UI thread. Frame arrival runs on the presenter thread, presents and
the first teardown steps on the UI thread, image release on the compositor's render thread, the
present continuation on whatever thread completes it, and producer disposal on the thread pool
(ADR-0064). The stall watchdog reads the sink from a timer thread
(`CompositionInteropVideoView.cs:856`), and `OnFrameArrived` reads it on the presenter thread
(`:740`), both without a barrier. Each mechanism above publishes what those threads need in its own
way: a volatile flag, a lock-guarded token, a generation number.

## Decision

### 1. One pure lifecycle for every surface and chrome control

`SurfaceLifecycle`, a pure state machine in `FrameFlow.Avalonia.Core`, replaces
`InteropViewLifecycle` and generalises it. `FrameFlow.Avalonia.Windows` already sees
`FrameFlow.Avalonia`'s internals (`src/FrameFlow.Avalonia/AssemblyInfo.cs:10`), so both views and
the chrome can use it without making it public.

- **State.** A phase (New, Live, Detached, Disposed), whether the control is in the visual tree,
  whether it is initialised (only the interop view needs `Initialize`; the others start
  initialised), the target it is bound to (none, owned or borrowed), and an attachment number.
- **Events.** `Initialize`, `Attach`, `Detach`, `Dispose`, `Assign(target)` (a caller's sink, a
  transport, or none) and `EnsureOwned` (a view creating its own sink).
- **Work.** Each transition returns what the shell must do, in order: create, bind, unbind or
  dispose the target, start or stop presenting (or following), and forget a borrowed target. The
  shell does that work and nothing else.

`InteropViewLifecycle`'s exhaustive test carries over and grows with the new events. The chrome
controls use the same machine with a transport as a borrowed target and no dispose;
`TransportFollower` becomes its shell.

### 2. The sink follows the view; presenting follows the attachment

An owned sink lives as long as the view, not as long as an attachment.

- **Detach** stops presenting: the binding ends, the compositor and GPU state go (ADR-0064's
  rebuild on detach stands), and the sink stays. While detached, the view holds no frames: the sink
  disposes each frame it is given and counts it as dropped, so nothing pins a decoder pool slice.
- **Reattach** starts presenting again with the same sink, owned or borrowed. A player built before
  the trip keeps working when the view comes back. This fixes #663.
- **`EnsureSink` and `AttachSink`** return the same sink for the view's whole life, which is what
  `IVideoSurface.AttachSink` already documents.
- **Dispose** disposes an owned sink and forgets a borrowed one. After it, the view refuses new
  sinks with `ObjectDisposedException`, as the interop view does today (#665).

`FrameFlowVideoView` gains `IAsyncDisposable` for that last step. A view that is never disposed
keeps one small owned sink object and holds no frames while detached.

### 3. Before the first attach, both views hold no frames

A sink bound before the view first joins the tree accepts frames and drops them, as on a detached
view. Neither view copies, presents or raises `FramePresented` until it is attached. The CPU view's
current pre-attach copying stops.

### 4. A disposed sink disposes what it is given

Every in-box video sink disposes a frame it is given after its own disposal, counts it nowhere, and
treats a second dispose as a no-op (ADR-0044). That closes #664 on `CompositionInteropVideoSink`
and the narrower race the research found in `AvaloniaVideoSink`, where a frame set after dispose's
final take stays in the slot. No sink interface changes (ADR-0066).

### 5. One attachment number, published once

The UI thread runs the lifecycle. After each transition the shell publishes one immutable snapshot
with `Volatile.Write`: whether frames are held, the attachment number, and the sink. A callback on
any other thread reads the snapshot once and drops its work when the attachment number is not the
current one. That one check replaces the six "this attachment has ended" mechanisms in the Context
table; the interop view's surface identity check stays only if it guards something the number does
not. Work that finishes off the UI thread keeps snapshotting what it owns at the transition that
started it, as `StopPresenting` does today, so the keyed-mutex order of ADR-0064 does not change.

### 6. One player per sink, by a claim the builder takes

The in-box sinks implement an optional side interface (ADR-0066), for example `IClaimableVideoSink`,
whose `Claim()` returns a lease. `FrameFlowPlayer` and `FrameFlowPass` take the claim when they are
built and release it when the player or pass is disposed. A second claim while one is held fails the
build with `InvalidOperationException` naming the sink, before anything is decoded. A sink can still
serve several players in sequence, as ADR-0044 and ADR-0062 require: each releases its claim when it
is disposed. A sink without the interface keeps the documented rule. This closes #286 for the
in-box sinks.

The builder claims only the sink it is given. A sink that feeds other sinks passes the claim on to
them (decision 8).

### 7. A surface is swapped out only when it is provably idle

`FrameFlowPlayerView.VideoSurface` swaps out a surface only when no player can be feeding it: the
surface has no sink, or its sink implements the claim interface (decision 6) and holds no claim.
Otherwise the swap throws `InvalidOperationException`:

- for a claimed sink, telling the app to dispose the player first or to build it against the new
  surface;
- for a sink without the claim interface, which can only be a caller's own sink, telling the app
  to clear the outgoing surface's `Sink` first. Clearing it is the app saying no player still feeds
  that surface, which the player view cannot establish itself.

An idle outgoing surface that the player view constructed itself (the default surface) is disposed
on the swap. One the caller supplied stays the caller's, detached and holding no frames. The player
view never moves a player from one sink to another: a player's sink is fixed when it is built.

### 8. A fan-out sink passes the claim to the sinks it feeds

`FrameFlow.Media` gains `FanOutVideoSink`. It takes the sinks it feeds when it is constructed and
presents each frame to each of them.

- **Delivery.** By default each target gets its own reference to the frame, in the order given. An
  app that does work for one target, such as the multicast examples' colour filter, passes a
  delivery delegate instead, which receives the frame and the targets. The targets are fixed at
  construction, so fan-outs cannot form a cycle.
- **Holding and domains.** `MaxHeldFrames` is the sum of the targets' values, or null when any
  target's is null. `AcceptedDomains` is the intersection of the targets' domains. Construction
  fails when that intersection is empty or a target appears twice. The app no longer declares
  either value by hand.
- **Claim.** It implements the claim interface. Claiming it claims every target, all or none: when
  one target is already claimed, it releases the claims it took and fails naming that target.
  Releasing it releases them all. A fan-out that no player is using holds no claims, so it does not
  block a surface swap (decision 7).
- **Ownership.** It does not own its targets (ADR-0044). Disposing it disposes none of them.
- **Views.** A target view that is detached drops what it is given (decision 2), and the other
  targets go on.

`DelegatingVideoSink` moves from the examples to `FrameFlow.Media`, taking the delegate,
`MaxHeldFrames` and `AcceptedDomains`, for a sink that feeds no other sink. Its delegate can present
to any sink and the sink cannot see which, so it claims nothing. An app whose delegate presents to
another sink uses `FanOutVideoSink` with a delivery delegate instead. Together they close #611.

### Staging

1. **Sinks.** Decision 4 on both in-box sinks, with tests that a frame given after dispose is
   disposed and that a second dispose does nothing. Closes #664. Then decision 6: the claim
   interface on both sinks, taken and released by the player and pass builders, with a test that a
   second build against a claimed sink fails and a build after the first player's disposal succeeds.
   Closes #286. Then decision 8: `FanOutVideoSink` and `DelegatingVideoSink` in `FrameFlow.Media`,
   with tests that a fan-out claims all its targets or none, releases them together, and refuses an
   empty domain intersection or a repeated target. Closes #611. It needs nothing else first.
2. **The core.** `SurfaceLifecycle` in `FrameFlow.Avalonia.Core` with the exhaustive test, and the
   interop view moved onto it with today's behaviour, except that an owned sink survives a detach.
3. **The CPU view.** `FrameFlowVideoView` onto the core: decisions 2, 3 and 5, `IAsyncDisposable`,
   and headless reattach tests for an owned and a borrowed sink (a frame given after reattach is
   presented). Closes #663.
4. **The chrome and the player view.** The chrome controls and `TransportFollower` onto the core,
   with no behaviour change intended. Decision 7 on `FrameFlowPlayerView.VideoSurface`, with tests
   for a claimed sink (refused), a sink without the claim interface (refused until the outgoing
   surface's `Sink` is cleared), an idle default surface (disposed) and an idle caller's surface
   (left to the caller).
5. **Docs.** `IVideoSurface`'s remarks, the BREAKING-CHANGES entry, the examples that construct
   their own sink only to survive a detach, the multicast examples moved onto `FanOutVideoSink`, and
   the v0.10.0 BREAKING-CHANGES note (`BREAKING-CHANGES.md:1638`) pointed at the in-box sinks.

## Consequences

### Positive

- One model, tested once, for every control that binds to something: what was three mechanisms
  and six late-callback checks becomes one machine and one number.
- A player keeps working across a tab switch, a panel swap or a docking move, with no rebuild and
  no app-owned sink needed for it.
- `IVideoSurface.AttachSink` becomes as idempotent as it says.
- The two views agree before attach, after detach and after dispose.
- No frame is pinned by a view that is not on screen.
- Two players can no longer feed one in-box sink at once, and a surface swap can no longer strand a
  player: both fail at the call that would cause them, with a message saying what to do.
- One player feeds several views through an in-box sink. The views are paced together, and each
  view's sink is claimed.

### Negative

- Behaviour changes an app can see: the owned sink is no longer disposed on detach, `Sink` no longer
  reads null after one, `EnsureSink` returns the same sink after a reattach, the CPU view no longer
  presents before its first attach, a disposed CPU view throws from `EnsureSink`, a second player
  built against a claimed in-box sink fails, and a swap of a surface that is not provably idle
  throws. Each needs a BREAKING-CHANGES entry.
- An app that hands a view its own sink, of a type without the claim interface, must clear the
  outgoing surface's `Sink` before swapping it out, even when it knows no player is running.
- An app that relied on detach to release the owned sink now has to dispose the view. A view that
  is never disposed keeps its sink object.
- The interop view's lifecycle moves out of `FrameFlow.Avalonia.Windows`, which replaces the core
  #662 added.
- A fan-out presents to its targets in turn on the presenting thread, so work done for one target,
  such as a filter, delays all of them. A paced branch stays with #636.

### Neutral

- GPU and compositor teardown on detach does not change (ADR-0064). Only the sink survives.
- `SdlVideoSink` owns a window, not a place in a visual tree, and is outside this record.

## Alternatives considered

### Keep fixing controls one at a time

#630, #643, #650 and #665 did this, and #663 shows what it costs: each fix teaches one control, and
the neighbouring control keeps the defect. It also leaves the six late-callback checks to drift.

### Keep disposing the owned sink on detach, and tell the player

The view could raise an event when it disposes its sink, and the player could swap to the
replacement. That makes a player depend on a view's tree position, couples `FrameFlow.Player` to
the views, and still rebuilds a sink on every tab switch. ADR-0062 went the other way for playlists:
keep the sink warm.

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

### Enforce one producer inside the sink

A sink could detect two producers from the frames it receives, for example by their timelines
(ADR-0084). Frames from two players interleave as alternating new timelines, which a sink cannot
tell from fast seeks. A claim taken at build fails before anything is decoded and names the cause.

### Let the builder take several sinks

#636 suggests `WithVideoSink` could accumulate sinks, and the builder could then claim each one.
That changes how a player paces, which is #636's question, and the builder still cannot see sinks
behind a delegate. A fan-out sink works with today's one paced sink, and a builder that took
several sinks could wrap them in one.

## Open questions

1. **Garbage collection.** Should an undisposed view's owned sink be released by a finaliser, or is
   holding no frames while detached enough?
2. **Counters.** The views' frame counters only ever go up today. With the sink kept, should drops
   while detached count toward them, or toward a separate detached-drop count?

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
