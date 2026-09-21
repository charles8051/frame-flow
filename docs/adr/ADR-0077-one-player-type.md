# ADR-0077: One player type: every player is a queue

## Status

Accepted (2026-09-16). Proposed 2026-09-15; numbered and accepted once the implementation landed.
**Implemented**; *As implemented* says how.
It grew out of the spike in
[docs/investigations/2026-09-15-single-source-as-a-queue-of-one.md](../investigations/2026-09-15-single-source-as-a-queue-of-one.md),
which is also where the hardware run is recorded.

This record decides that a single source runs as a queue of one on the playlist session, that one
implementation serves both player factories, and what that changes for a caller who plays one file.

It supersedes:

- **[End of queue, replay and faults on the playlist player](playlist-end-of-queue-replay-and-faults.md)
  decision 1**, which kept single-source playback on `SubstrateSession`. Its *Deferred: one player
  type* section lists the conditions this record answers.
- **[Looping on both players](ADR-0075-looping-on-both-players.md) decision 8**, the controller's fallback
  loop, which exists only for a single source that does not run as a queue of one.
- **ADR-0028 §2**, which routed the single-source loop through the controller's seek state machine.
- **ADR-0027 §3** on `RepeatMode.All`, already answered by the looping record's decision 1.

Related: [The playlist player's queue](ADR-0074-playlist-queue-model.md),
[The playlist session as a pure protocol](ADR-0076-playlist-session-protocol.md),
[ADR-0062](ADR-0062-gapless-playlist-warm-presenter.md),
[ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md),
[ADR-0072](ADR-0072-tests-do-not-depend-on-elapsed-time.md).
Issues #44, #143, #172, #173, #195, #203, #216, #217, #223.

## Context

### Two players, two sessions, one controller

`PlaybackController.Create` builds a controller over `SubstrateSession`, one source at a time.
`PlaybackController.CreatePlaylist` builds one over `PlaylistSession`, which composes a
`SubstrateSession` per item over the same warm sinks. Both are driven by the same
`PlaybackControllerCore`.

The two paths have drifted, and the drift is where the defects have been. The end-of-queue record
lists six on the playlist player; the looping record's probes list seven behaviour differences
around loops. Each fix has had to name which player it was for.

### What the conditions asked for, and what answers them

The end-of-queue record set eight conditions before this proposal could be made again.

| Condition | Answered by |
|---|---|
| The decisions in that record have landed, and its open defects have decisions | #170, #180, #182, #183, #171 |
| A spike of the real design: one coordinator with the facade, `RepeatMode` honoured by the queue, one construction path, queues that advance and wrap | #224 |
| A mid-stream fault test that fails on a build which loses the fault | `SingleSourceAsAQueueOfOneTests.AMidStreamFault_IsReported_AndThePlayerEnds`, which fails when the shell's `ReportItemFailed` is removed |
| A deterministic interleaving test for late end-of-stream | **Not answered.** It needs the tooling #143 asks for. The session already drops a stale end-of-stream by run number, which is what #195 asks of the controller |
| Hardware decode and the GPU presenter, before and after, with no new stall and no drop in frames presented | Ten minutes each: the two builds present the same frames at the same rate, with the same loops per pane, no stall and no error |
| `RepeatMode.All` on one item, loop ownership and loop reporting each recorded | The looping record, implemented for a playlist in #215 and #222 |
| The public break accounted for | *Migration*, below |

### What the spike measured

The whole suite passes on the spike. The playlist suites pass again when every one-item playlist is
built through `PlaybackController.Create` instead, which is the queue of one: end of queue, skips,
faults, queue edits, current-item metadata and loop reporting. One test failed, and it pinned the
controller's own loop route, which this record removes.

## Decision

### 1. Every player is a queue

`PlaybackController.Create` builds a `PlaylistSession` over a coordinator of its own. Each load makes
the loaded source that queue's only item. `PlaylistSession` becomes the only session the controller
drives, and `SubstrateSession` becomes only an item runtime.

A caller who plays one file gets the queue's behaviour: the item is kept at `Ended` so it can be
sought and its counters read, a fault is reported rather than terminal, and a loop is the session's
in-place rewind.

> **Amended 2026-09-17.** The queue may be empty. A player built with no sources has its sinks
> attached and warm and nothing loaded, and sits at `Idle`. `Play` at `Idle` asks the session
> factory for the item its queue would start with, reserving it so the load that follows plays the
> queue rather than replacing it — the mark a replay from `Ended` already sets. With nothing
> queued the factory answers null and `Play` is refused, as it was before. This is for a host that
> builds its presenter once at startup and receives content afterwards; the alternative was a
> placeholder source and a load and teardown to be rid of it.
> [Breaking change 22](../BREAKING-CHANGES.md).

### 2. One implementation, two interfaces for now

`MediaPlayer.CreateAsync` and `MediaPlaylistPlayer.CreateAsync` build the same player. The
single-source factory passes a queue of one, and its declared return type becomes
`Task<IMediaPlaylistPlayer>`, which says what the caller is given.

`Task<T>` is invariant, so this is a source break as well as a binary one. `var player = await
MediaPlayer.CreateAsync(…)` and `IMediaPlayer player = await MediaPlayer.CreateAsync(…)` both keep
compiling, because the awaited value is assignable. What stops compiling is naming the task:
`Task<IMediaPlayer> t = MediaPlayer.CreateAsync(…)`, and passing the call where a
`Task<IMediaPlayer>` or a `Func<…, Task<IMediaPlayer>>` is expected. The edit is to name the new
type, or to await first.

`IMediaPlaylistPlayer` stays a separate interface. Folding its eleven members into `IMediaPlayer`
would break every type outside FrameFlow that implements the smaller surface — test doubles, UI
adapters — for no behaviour a caller cannot already reach by taking the larger interface. The fold
is deferred to the next release that breaks implementers for other reasons.

A caller who wants the small surface keeps naming `IMediaPlayer`; the returned object satisfies it.

> **Amended 2026-09-16.** The two factories are now one. `MediaPlaylistPlayer` is gone, and
> `MediaPlayer.CreateAsync` has a second overload taking `IEnumerable<IMediaSource>`. One name with
> two defaults for the same omitted argument would have been the wart, so both overloads default
> `initialRepeatMode` to `RepeatMode.Off`; the queue factory defaulted to `RepeatMode.All`.
> [Breaking changes 17 and 18](../BREAKING-CHANGES.md). The interfaces are unchanged, so the rest of
> this decision stands.

> **Amended 2026-09-20. Accepted.** The fold is declined and the condition retired; the rename
> below is taken and implemented — [breaking change 38](../BREAKING-CHANGES.md). Records written
> before this date use the old names, and mean by `IMediaPlayer` what is now `IMediaTransport`.
>
> The deferral's condition had fired, and the answer is not to take the fold. Recorded here because
> a condition that fires unnoticed is worse than no condition.
>
> **It fired, on one entry rather than the three I first counted.** "The next release that breaks
> implementers for other reasons" is the release now pending, and breaking change 11 is what fires
> it: adding `LoopRestarted` to `IMediaPlayer` stops every external implementation compiling with
> CS0535. One qualifying break is all the condition asks for.
>
> The other two entries that look like candidates are not. Entry 3 removes
> `IMediaPlayer.Diagnostics`, and its own migration note says so: "Leaving it compiles, but nothing
> reads it" — a removal is source-compatible for an implicit implementation, and breaks only an
> explicit one. Entry 27 removes `IMediaPlayerBuilder`, which is a different interface and says
> nothing about implementers of this one.
>
> The playlist-events record still says "No such release is planned", which was true when it was
> written and is not now.
>
> **What #318 changed, which is less than I first claimed.** `PlaylistItem` and `PlaylistSnapshot`
> had internal constructors until this release, and I argued twice that this had gated the fold. It
> did not. A decorator wrapping a real player could always forward `AddAsync`, `GetPlaylist` and
> the rest and return the inner player's values, so a folded interface was always implementable
> that way. What was blocked was the *standalone* double — one with no real player behind it — and
> that is the shape the measurement below is about. #318 unblocked it, and that is the whole of the
> change: it makes the cost of a fold easier to state, not the fold newly possible.
>
> **The reason to decline is not the one recorded above.** This decision said folding gives a
> caller nothing they cannot already reach by taking the larger interface, which is true and is not
> the load-bearing part. The load-bearing part is what `IMediaPlayer` is *for*: its own summary
> says it is an interface rather than a concrete type because the `FrameFlow.Avalonia` chrome
> controls take it as a polymorphic dependency. It exists to be *consumed* polymorphically, not to
> be implemented. Folding takes the interface from 17 members to 31.
>
> That summary also names `FrameFlow.Audio.OpenAL`'s fluent extension as the second reason, and
> that half is stale: `FrameFlow.Audio.OpenAL` references `IMediaPlayer` nowhere, because
> `WithOpenAlAudio` was retired in this same release (breaking change 29). The chrome is the whole
> population now. Worth fixing wherever the summary is next edited; it is the same shape of defect
> as #279, a doc naming a consumer that is not there.
>
> That is measured rather than asserted. The one implementer of `IMediaPlayer` in this tree is
> `FrameFlowVolumeControlTests.FakePlayer`, whose own summary says "only the volume projection is
> exercised; the transport and observables are never touched". It writes 18 members — the 17 above
> plus `DisposeAsync` — and uses three of them: `SupportsVolumeControl`, `Volume`, `Muted`. A fold
> would take it to 32, still using three. External test doubles and UI adapters are the same shape,
> which is the population the deferral was protecting.
>
> **So the condition is retired rather than satisfied.** Batching a large break behind whatever
> small break happens to land next is not a reason; it treats "already breaking" as "any further
> break is free", and the two are not the same size. Removing a member, adding one, and renaming a
> type are mechanical edits. Adding fourteen members to an interface written for consumers is not.
> (Fourteen, not the eleven above: `ItemFailed`, `ItemLooped` and `ItemStalled` joined the
> interface after this was written.)
>
> The split stands until there is evidence the two surfaces cost a consumer something, rather than
> until an unrelated break gives cover. The duplicated channels that wait on the fold — #308, #306,
> #203 — are decided on their own merits, not by it; the playlist-events record already fixes their
> ordering and their shared instances, which is what made the duplication safe to carry.
>
> **The names, which the split now has to carry.** Declining the fold makes this sharper rather
> than moot: two interfaces that are permanent have to explain themselves at the call site, where
> the documentation is not. These do the opposite.
>
> `IMediaPlaylistPlayer` reads as a *kind* of player, the one to reach for when there is a
> playlist. Decision 1 is that there is no such kind: every player is a queue and a single file is
> a queue of one. `IMediaPlayer` reads as the general case and is the narrower view. So a reader
> meeting both concludes "the plain one for a file, the playlist one for several", which is wrong
> in both directions, and the type names are the only thing most callers read.
>
> The word for the small surface is already in this repo, just not in the type name. The playlist
> interface's own remarks call it "the inherited `IMediaPlayer` transport", and every consumer of
> it is a transport widget: `FrameFlowTransportBar`, `FrameFlowSeekBar`, `FrameFlowVolumeControl`,
> `FrameFlowPositionLabel`, `FrameFlowStateBadge`, `FrameFlowStreamSummary`, `FrameFlowPlayerChrome`,
> `FrameFlowPlayerView`.
>
> So the proposal is a swap:
>
> | Now | Proposed | What it is |
> |---|---|---|
> | `IMediaPlaylistPlayer` | `IMediaPlayer` | the player; what `BuildPlayerAsync` returns |
> | `IMediaPlayer` | `IMediaTransport` | the view the chrome depends on |
>
> `MediaPass` is unaffected, and the asymmetry is worth naming because it is the next question a
> reader asks. A pass has no interface because nothing takes a pass polymorphically; the player has
> one because eight chrome controls do. Interface count follows polymorphic need, not
> symmetry. ADR-0079 already separates pass from player on a clock, and
> `FrameFlowPass.Create` beside `FrameFlowPlayer.Create` says that much at the call site.
>
> **The cost runs the other way from the fold, and is worth stating plainly.** The fold taxes
> implementers, who are few; a rename breaks everyone who *names* either type, which is every
> consumer. What makes it the better of the two anyway is the shape of the edit: a rename is
> find-and-replace with a compiler error at every site, while the fold is fourteen method bodies
> that a test double has to write and will never call. And the surface is already moving — the
> pending release has 37 entries — so a caller editing for entries 3, 11 and 27 is in the same
> files.
>
> **Decided together or not at all.** If the fold is taken later the rename is wasted work, and if
> the split is permanent the names are load-bearing. Taking one without the other is the outcome
> to avoid.

### 3. `RepeatMode.All` loops a single source

A queue of one repeats its one item, so `All` and `One` behave alike there. The enum's summary for
`All` loses its "for a single-source player this behaves like `Off`" paragraph. This is the looping
record's decision 1, and it is what ADR-0027 §3 asked for when it said `All` would come back with
"distinct, testable behavior": the distinction is a queue of more than one item.

### 4. A fault is reported, and the queue decides what follows

The end-of-queue record's decision 7 applies to every player. A fault while playing raises
`ErrorOccurred` with the item's exception, and the queue advances as it would at an end:

- under `Off` a queue of one has nothing left, so the player reaches `Ended`, where `PlayAsync`
  starts it again;
- under `One` and `All` the item is rebuilt, and a run of nine failures without progress ends the
  player in `Error`.

A single source used to enter `Error` on the first mid-stream fault. `Error` is terminal
(decision 6 of that record), so the player was unusable; now the same failure is recoverable and is
still reported. A host that watches `State` alone for failure sees `Ended` where it used to see
`Error`, and must take `ErrorOccurred` instead. *Migration* says so.

### 5. The loop is the session's rewind, and the controller's loop goes

The controller no longer loops. What is deleted:

- `PlaybackActionKind.RunLoopRewind` and `RunLoopRewindAsync`;
- `PlaybackInputs.RepeatOne` and the two protocol cells that read it, leaving one cell:
  `LastFrameRendered` ends the player, whatever the repeat mode;
- `IPlaybackSession.LoopsInternally`, which every session the controller drives now answers the same
  way;
- the looping record's decision 8.

`IPlaybackController.LoopRestarted` keeps its meaning and its count, published from the session's
report, and the loop-stall watchdog keeps reading the session's `ExpectsRepeat`.

A loop no longer drives the seek state machine. `SeekStateChanged` is silent across a loop and
`IsActivelyPresenting` stays true, where ADR-0028 §2 made a loop look like a seek so a user seek
could cancel it. The session orders a seek against a loop itself: both are its inputs, taken one at
a time.

A host that used `SeekStateChanged` to gate UI during a loop sees fewer transitions. One that used
it to detect looping takes `LoopRestarted` instead, which #222 put on `IMediaPlayer` as well as on
`IPlaybackController`, so the smaller surface has the replacement without reaching for the
controller. It carries the loop's count, which the seek transitions never did.

### 6. A load replaces the queue; a replay keeps it

`IPlaybackController.LoadAsync` on a controller built by `Create` makes the loaded source the
queue's only item. It does not keep what was enqueued before it: a load is a new queue.

A replay from `Ended` is the exception. The controller's replay reserves the item it will start with
before it unloads, and that reservation is what tells the new session to keep the queue, so an item
enqueued at `Ended` still plays. The two run inside one dispatched command, so no other load can
fall between them.

### 7. The controller owns the coordinator it made

A coordinator handed to `CreatePlaylist` belongs to the player facade, which disposes it. The one
`Create` builds belongs to the controller, which disposes it with its session factory. The spike
leaves that one ownerless.

### 8. What else goes

- `MediaPlayerCore`, the single-source player wrapper, along with the projection it shares — the
  projection moves to the remaining core.
- `FRAMEFLOW_SPIKE_SINGLE_SOURCE`, the spike's test switch. The one-item integration tests become
  theories over both construction paths instead.

## Consequences

### Positive

- **One path for every fix.** The defects the end-of-queue record found were all in one of the two
  paths. There is one now.
- **A single source gains what the playlist has**: an item kept at `Ended` that can be sought and
  read, a fault that does not end the player, `All` that loops, and a loop that keeps its graph.
- **The video chain is built once per load** rather than once per loop, which is where the
  configurator work of #216 and the operator reset of #217 land.
- **Less machinery.** The controller loses a loop path, a protocol input, two protocol cells and a
  session predicate.
- **#172 and #173 close**, and #195 is answered for every player by the session's run number.

### Negative

- **A mid-stream fault reads as `Ended`.** A host that watches `State` for failure goes quiet until
  it takes `ErrorOccurred`.
- **A loop is no longer a seek.** Hosts reading `SeekStateChanged` see fewer transitions.
- **Every player carries a queue**, including the caller who will only ever play one file. The queue
  is a value with no thread of its own, so the cost is a coordinator per player.
- **`MediaPlayer.CreateAsync`'s return type changes.** Callers that name `Task<IMediaPlayer>` edit
  one line; the rest recompile.

### Neutral

- `IMediaPlayer` is unchanged. `IMediaPlaylistPlayer` is unchanged.
- `PlaybackController.Create`'s signature is unchanged; what it builds is not.

## Alternatives considered

### A. Keep two player types

Rejected. It is the status quo the drift came from, and every fix since #170 has had to say which
player it applied to.

### B. Fold `IMediaPlaylistPlayer` into `IMediaPlayer` now

Rejected for this record. It breaks every external implementer of the small interface, and it gives
a caller nothing they cannot get by naming the larger one. Decision 2 defers it.

> **Amended 2026-09-20. Accepted**, with decision 2's amendment. Reconsidered when that
> decision's condition fired, and rejected again for a better reason: `IMediaPlayer` exists to be consumed polymorphically, not implemented, so the fold taxes
> implementers for a surface consumers already reach. Decision 2's amendment has the measurement,
> retires the condition, and proposes the rename the permanent split then needs.

### C. Keep the controller's loop for a single source

This is the looping record's decision 8. Rejected: it builds a second loop mechanism to keep a
single source behaving the way it does today, and this record's whole point is that there is one.

### D. Make a fault terminal for a queue of one

Rejected. It restores the single-source behaviour by giving a queue of one a rule a queue of two
does not have, which is the drift again. A caller who wants a fault to be terminal disposes the
player on `ErrorOccurred`.

## Not settled here

- **A deterministic interleaving test for late end-of-stream** (#143).
- **Session notifications dropped when the controller's command channel is full** (#223).
- **The configurator's arguments** (#216) and **the operator reset across a rewind** (#217). Both
  become sharper once a single source loops in place, but neither gates this record.
- **Pairing a loop with its transition** (#203).
- **The hour-long loop soak.** Ten minutes each is what has run.
- **`SourceTransitioned` on a controller-level player.** The coordinator raises it, and a caller
  holding only `IPlaybackController` cannot read it.

## As implemented

- **The queue of one.** `PlaybackController.Create` builds a `PlaylistSessionFactory` over a
  coordinator of its own, with `loadsSource` set, and both entry points assemble the controller
  through one `Assemble` (#44). `PlaylistSession.InitializeAsync` calls
  `PlaylistCoordinator.LoadSource`, which makes the loaded source the queue's only item unless a
  replay from `Ended` has reserved an item on it.
- **The repeat mode.** `IPlaybackSessionFactory.RepeatModeChanged` carries the controller's mode to
  the coordinator, at construction and on every change.
- **The player.** Both `MediaPlayer.CreateAsync` overloads share `MediaPlayer.CreateCoreAsync`, and
  the single-source one passes a queue of one. `MediaPlayerCore` is gone; `ProjectionObservable`
  moved to its own file. The overloads were `MediaPlayer.CreateAsync` and
  `MediaPlaylistPlayer.CreateAsync` when this record was accepted; see the amendment to decision 2.
- **The controller's loop.** `RunLoopRewind`, `RunLoopRewindAsync`, the seek runner's loop mode,
  `PlaybackInputs.RepeatOne`, `PlaybackDecision.Internal` and `IPlaybackSession.LoopsInternally` are
  deleted. `Playing × LastFrameRendered` and `Paused × LastFrameRendered` both go to `Ended`. The
  watchdog reads the session's `ExpectsRepeat` with no mode of its own.
- **Ownership.** `PlaylistSessionFactory` is `IDisposable` and disposes the coordinator it was built
  for; the controller disposes its factory. A player's coordinator is still the player's.
- **The docs.** `RepeatMode.All`'s summary, and entries 12 to 16 in `docs/BREAKING-CHANGES.md`.
- **The tests.** `SingleSourceAsAQueueOfOneTests` (six, over real playback),
  `OnePlayerTypeTests`, `PlaybackControllerFactoryTests.SetRepeatMode_ReachesTheQueueTheControllerPlays`
  and `.Dispose_DisposesTheCoordinatorTheControllerBuilt`,
  `PlaybackProtocolTests.Playing_LastFrameRendered_EntersEnded_WhateverTheRepeatMode`,
  `PlaybackDispatchProtocolTests.LastFrameRendered_EndsPlayback_WhateverTheRepeatMode`, and the
  one-item cases of `PlaylistEndOfQueueTests`, `PlaylistSkipStateTests` and `PlaylistFaultTests` as
  theories over both construction paths.

## Validation

Write each test first and confirm it fails on the tree before this record lands, for the reason
given. The rows the spike already has are named as such; they move from the spike to the change.

| # | Decision | Test | Today |
|---|---|---|---|
| 1 | 1, 2 | `PlayerTests`: a player from `MediaPlayer.CreateAsync` reports a playlist of one holding the loaded source, and is an `IMediaPlaylistPlayer`. | [no playlist] |
| 2 | 1, 6 | `SingleSourceAsAQueueOfOneTests.AReload_ReplacesTheQueue_SoNothingEnqueuedBeforeItSurvives` (spike). | [no queue] |
| 3 | 1, 6 | `SingleSourceAsAQueueOfOneTests.AnItemEnqueuedAtEnded_PlaysWhenThePlayerReplays` (spike). | [no queue] |
| 4 | 3 | `SingleSourceAsAQueueOfOneTests.UnderAll_ItLoops_AndReportsEachLoop` (spike), row 6 of the looping record. | [one pass, then `Ended`] |
| 5 | 4 | `SingleSourceAsAQueueOfOneTests.AMidStreamFault_IsReported_AndThePlayerEnds` (spike). | [`Error`] |
| 6 | 4 | `SingleSourceAsAQueueOfOneTests.AFaultOnEveryPass_IsReportedEachPass_ThenThePlayerGivesUp` (spike). | [`Error` at the first fault] |
| 7 | 5 | `SingleSourceAsAQueueOfOneTests.UnderOne_ItLoopsInPlace_AndBuildsItsVideoChainOnce` (spike), row 8. | [three chains] |
| 8 | 5 | `PlaybackControllerIntegrationTests.RepeatOne_LoopRewindsInPlace_WithoutTheSeekStateMachine` (spike). | [the loop drives the seek machine] |
| 9 | 5 | `PlaybackProtocolTests`: `LastFrameRendered` ends the player from `Playing` and from `Paused`, with no repeat input in the table. | [two cells, gated on `RepeatOne`] |
| 10 | 5 | `PlaybackDispatchProtocolTests`: a session's end-of-stream ends the controller under every repeat mode, and no loop rewind is run. | [`RunLoopRewind` under `One`] |
| 11 | 7 | `PlaybackControllerFactoryTests`: disposing a controller from `Create` disposes the coordinator it built; one from `CreatePlaylist` leaves the caller's alone. | [no coordinator; none disposed] |
| 12 | 8 | The one-item integration tests run over both construction paths as theories, with no environment switch. | [the spike's switch] |

## Migration

Every public entry is in `PublicAPI.Unshipped.txt`; nothing is shipped, so no baseline promises are
broken. The edits are:

- `FrameFlow.Player`: `MediaPlayer.CreateAsync`'s return type becomes
  `Task<IMediaPlaylistPlayer!>!`.
- `FrameFlow.Media`: no signature change. `RepeatMode.All`'s documentation changes.
- `FrameFlow.Playback`: no signature change.

`docs/BREAKING-CHANGES.md` gains entries for:

1. `MediaPlayer.CreateAsync`'s return type: a binary break for every caller, and a source break for
   one that names the task rather than awaiting it. Decision 2 has the cases.
2. `RepeatMode.All` looping a single source, where it used to end.
3. A mid-stream fault reaching `Ended` with `ErrorOccurred`, where it used to reach `Error`.
4. `SeekStateChanged` staying silent across a loop.
5. The video chain being built once per load, which an operator holding state across a loop now
   sees.

## Revision history

- **Fold condition retired, and the interfaces renamed (2026-09-20).** Decision 2's "next release that
  breaks implementers" condition fired and was not taken; the deferral now rests on what the small
  interface is for rather than on waiting for cover. With the split permanent, the two names are
  swapped: the playlist interface becomes `IMediaPlayer` and the transport view becomes
  `IMediaTransport`. Alternative B amended to match. Accepted and implemented; breaking change 38.
- **First draft (2026-09-15).** Written from the spike on #224 and its ten-minute hardware run.
