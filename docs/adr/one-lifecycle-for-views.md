# One lifecycle for views: the sink follows the view, presenting follows the attachment

## Status

Draft, pending number assignment at merge. **Proposed** (2026-10-13). Not implemented.

Motivating issues: #663 (`FrameFlowVideoView` never binds a caller's sink again after a detach and
reattach) and #664 (`CompositionInteropVideoSink` keeps a frame presented after its disposal). It
would also replace the separate mechanisms that #630, #643, #650 and #665 added, each fixing one
control. Not covered: #286 (two players driving one sink) and the `VideoSurface` swap on
`FrameFlowPlayerView`; see [Open questions](#open-questions).

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

### Staging

1. **Sinks.** Decision 4 on both in-box sinks, with tests that a frame given after dispose is
   disposed and that a second dispose does nothing. Closes #664. It needs nothing else first.
2. **The core.** `SurfaceLifecycle` in `FrameFlow.Avalonia.Core` with the exhaustive test, and the
   interop view moved onto it with today's behaviour, except that an owned sink survives a detach.
3. **The CPU view.** `FrameFlowVideoView` onto the core: decisions 2, 3 and 5, `IAsyncDisposable`,
   and headless reattach tests for an owned and a borrowed sink (a frame given after reattach is
   presented). Closes #663.
4. **The chrome.** The chrome controls and `TransportFollower` onto the core. No behaviour change is
   intended; the gain is one model.
5. **Docs.** `IVideoSurface`'s remarks, the BREAKING-CHANGES entry, and the examples that construct
   their own sink only to survive a detach.

## Consequences

### Positive

- One model, tested once, for every control that binds to something: what was three mechanisms
  and six late-callback checks becomes one machine and one number.
- A player keeps working across a tab switch, a panel swap or a docking move, with no rebuild and
  no app-owned sink needed for it.
- `IVideoSurface.AttachSink` becomes as idempotent as it says.
- The two views agree before attach, after detach and after dispose.
- No frame is pinned by a view that is not on screen.

### Negative

- Behaviour changes an app can see: the owned sink is no longer disposed on detach, `Sink` no longer
  reads null after one, `EnsureSink` returns the same sink after a reattach, the CPU view no longer
  presents before its first attach, and a disposed CPU view throws from `EnsureSink`. Each needs a
  BREAKING-CHANGES entry.
- An app that relied on detach to release the owned sink now has to dispose the view. A view that
  is never disposed keeps its sink object.
- The interop view's lifecycle moves out of `FrameFlow.Avalonia.Windows`, which replaces the core
  #662 added.

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

## Open questions

1. **The surface swap.** `FrameFlowPlayerView.VideoSurface` is meant to be swapped before the first
   playback. With decision 2 the old surface keeps its sink, detached, and a player still feeds it.
   Should the player view dispose the surface it swaps out, or refuse a swap while a sink is bound?
2. **One player per sink (#286).** The lifecycle makes a view's sink last longer, so two builders
   given the same view are more likely to share it. Is a lease (a side interface, per ADR-0066) part
   of this work or its own record?
3. **Garbage collection.** Should an undisposed view's owned sink be released by a finaliser, or is
   holding no frames while detached enough?
4. **Counters.** The views' frame counters only ever go up today. With the sink kept, should drops
   while detached count toward them, or toward a separate detached-drop count?
