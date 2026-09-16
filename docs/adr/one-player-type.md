# ADR-XXXX: One player type: every player is a queue

## Status

Proposed (2026-09-15). Draft pending number assignment. **Nothing here is implemented.** The spike
it rests on is [#224](https://github.com/charles8051/frame-flow/pull/224), a draft, and
[docs/investigations/2026-09-15-single-source-as-a-queue-of-one.md](../investigations/2026-09-15-single-source-as-a-queue-of-one.md)
is its report.

This record decides that a single source runs as a queue of one on the playlist session, that one
implementation serves both player factories, and what that changes for a caller who plays one file.

It supersedes:

- **[End of queue, replay and faults on the playlist player](playlist-end-of-queue-replay-and-faults.md)
  decision 1**, which kept single-source playback on `SubstrateSession`. Its *Deferred: one player
  type* section lists the conditions this record answers.
- **[Looping on both players](looping-on-both-players.md) decision 8**, the controller's fallback
  loop, which exists only for a single source that does not run as a queue of one.
- **ADR-0028 §2**, which routed the single-source loop through the controller's seek state machine.
- **ADR-0027 §3** on `RepeatMode.All`, already answered by the looping record's decision 1.

Related: [The playlist player's queue](playlist-queue-model.md),
[The playlist session as a pure protocol](playlist-session-protocol.md),
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

### 2. One implementation, two interfaces for now

`MediaPlayer.CreateAsync` and `MediaPlaylistPlayer.CreateAsync` build the same player. The
single-source factory passes a queue of one, and its declared return type becomes
`Task<IMediaPlaylistPlayer>`.

`IMediaPlaylistPlayer` stays a separate interface. Folding its eleven members into `IMediaPlayer`
would break every type outside FrameFlow that implements the smaller surface — test doubles, UI
adapters — for no behaviour a caller cannot already reach by taking the larger interface. The fold
is deferred to the next release that breaks implementers for other reasons.

A caller who wants the small surface keeps naming `IMediaPlayer`; the returned object satisfies it.

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
a time. A host that used `SeekStateChanged` to gate UI during a loop sees fewer transitions, and a
host that used it to detect looping should take `LoopRestarted`.

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
- **`MediaPlayer.CreateAsync`'s return type changes**, so callers recompile.

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

1. `MediaPlayer.CreateAsync`'s return type, which needs a recompile and no source edit.
2. `RepeatMode.All` looping a single source, where it used to end.
3. A mid-stream fault reaching `Ended` with `ErrorOccurred`, where it used to reach `Error`.
4. `SeekStateChanged` staying silent across a loop.
5. The video chain being built once per load, which an operator holding state across a loop now
   sees.

## Revision history

- **First draft (2026-09-15).** Written from the spike on #224 and its ten-minute hardware run.
