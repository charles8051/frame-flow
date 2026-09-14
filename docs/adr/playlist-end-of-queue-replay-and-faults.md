# ADR-XXXX: End of queue, replay and faults on the playlist player

## Status

Proposed (2026-09-12). Draft pending number assignment.

This record proposes fixes for three defects in how the playlist player behaves at the end of its
queue and on replay, decides how it reports failed items, and decides how an advance follows the
controller's state. It records one more defect, and several found in review, without deciding their
fixes. The changes are to behaviour, not public API. Single-source playback changes in two
respects: decision 7 drops notifications from a session the controller has replaced, and decision 8
lets an end-of-stream end a paused controller.

It started as a proposal to run every player on the playlist session. A spike and two independent
reviews narrowed it to this. That history is kept under *Alternatives considered* and *Revision
history*, because it is the reason this record is narrow.

**Decisions 2, 3, 4, 5, 7 and 8 are implemented** (#170, #183, #180, #182). Decision 6 is not.
Every defect below was reproduced on this codebase except where it says "read from code".

Related: [ADR-0028](ADR-0028-internal-layering-and-ownership-cleanup.md),
[ADR-0034](ADR-0034-diagnostics-surfaces.md),
[ADR-0062](ADR-0062-gapless-playlist-warm-presenter.md),
[ADR-0068](ADR-0068-command-driven-test-bench-host.md),
[ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md),
[ADR-0072](ADR-0072-tests-do-not-depend-on-elapsed-time.md). Issues #29, #44, #125, #143.

## Context

### Two sessions behind one controller

`PlaybackController.Create` drives a `SubstrateSession`
(`src/FrameFlow.Playback/PlaybackController.cs:116`). `PlaybackController.CreatePlaylist` drives a
`PlaylistSession` that composes one `SubstrateSession` per item (`PlaybackController.cs:213`).
Both run under the same `PlaybackControllerCore`, whose rules were written for one source.

ADR-0062 put the queue inside the session so the controller would not have to know about it. The
controller therefore cannot see which item is playing, and several of its rules act on the wrong
thing once a playlist is involved.

### How the defects were reproduced

The runs used the generated corpus, software decode, a counting video sink and no audio sink.
Most used the 3-second video-only clip. Defect 5 also used the half-second clip, whose audio stream
was discarded for want of a sink. Faults were injected with a `configureVideo` operator that throws
on its 21st frame. The probes were scratch tests and were not committed.

Line numbers outside decisions 7 and 8 cite commit 628083b, the tree this record was first written
against. Several of those files have changed since, and decisions 7 and 8 changed
`PlaylistSession.cs`, `PlaybackControllerCore.cs` and `PlaybackProtocol.cs` themselves. Decision
7's own citations are to the tree it was implemented on.

### Defects on the playlist player

1. **Play from `Ended` with an empty queue faults the player.** Under `RepeatMode.Off`, play to the
   end and call `PlayAsync`. The controller's replay unloads and reloads
   (`PlaybackControllerCore.cs:1059`, `:1075-1077`). The new `PlaylistSession` takes its first item
   from the shared queue, which is empty, and `PlaylistCoordinator.First()` dereferences a null node
   (`PlaylistCoordinator.cs:184`). The call returns "Session initialization failed" with a
   `NullReferenceException` inside, and the controller enters `Error`.

2. **Seek from `Ended` plays nothing.** Play to the end, seek to zero, play. The controller reports
   `Paused` and then `Playing`. The sink stays at 72 frames and the position stays at zero. When
   the queue runs out, `PlaylistSession` disposes the last item before it reports end-of-stream
   (`PlaylistSession.cs:368-387`), so the seek and the play find no runtime
   (`PlaylistSession.cs:209-212`, `:177-178`). A single-source controller given the same sequence
   goes from 72 frames to 83. The same happens after a skip on the last item: presentation stops at
   10 frames, and seek then play leaves it at 10.

3. **A fault on the last item ends playback silently.** Under `Off`, an item that faults mid-stream
   with nothing after it ends in `Ended` with no `ErrorOccurred`. The fault is routed into the
   advance path (`PlaylistSession.cs:271`), logged, and turned into an ordinary end-of-stream
   (`:384-388`). A single-source controller given the same fault enters `Error` and raises
   `ErrorOccurred` once. `IMediaPlayer` documents that "failures that arise mid-playback rather than
   in answer to a command surface on `ErrorOccurred`" (`src/FrameFlow.Player/IMediaPlayer.cs:34-35`).

4. **An item that faults on every pass loops forever, silently.** Under `All`, a single item that
   faults at the same frame on every pass stayed in `Playing` with no `ErrorOccurred`. It faulted
   10 times in 6 seconds and was rebuilt each time. `RepeatMode.One` behaved the same. The
   consecutive-failure guard (`PlaylistSession.cs:53`) is only compared to its limit when an item
   fails to open (`:410`). A mid-stream fault increments it (`:310`) without that check, and the
   next successful start resets it (`:426`).

5. **The controller keeps the first item's duration.** On a playlist of a 0.5 s clip followed by a
   3 s clip, after the advance the controller's `Duration` still reads 0.5 s. The player facade
   hides this by reading the coordinator (`src/FrameFlow.Player/PlaylistMediaPlayerCore.cs:61-67`).
   The controller's loop-stall evaluator does not (`PlaybackControllerCore.cs:205`). Switching to
   `RepeatMode.One` on the 3 s item raised `LoopStalled` once during healthy playback. ADR-0062 said
   a boundary callback would have to refresh the controller's snapshot (ADR-0062:411-416). That
   callback was never built.

6. **`SetNext` under `All` grows the rotation.** `SetNext` pushes onto the front of the queue
   (`PlaylistCoordinator.cs:138`), and every dequeued item is copied into the loop buffer
   (`:250`). A coordinator unit probe of `a b c` with one jump to `c` looped as `a c b c` from then
   on. Every further jump adds another permanent entry. The in-tree AvaloniaPlayer example jumps
   this way under `All` (`examples/FrameFlow.Examples.AvaloniaPlayer/MainWindow.axaml.cs:406`).

One related behaviour was checked and is not a defect on today's tree: `EnqueueAsync` while
`Ended`, followed by `PlayAsync`, plays the enqueued item.

### Drift that is not a defect

`SubstrateSessionFactory` passes `LatenessRecoveryOptions` to its session
(`SubstrateSessionFactory.cs:86`). `PlaylistSession` builds its items without it
(`PlaylistSession.cs:276-288`). `PlaybackController.Create` exposes the option
(`PlaybackController.cs:127`), but no playlist entry point does, so no playlist consumer can ask for
it today. The two controller factories duplicate their wiring (#44), and the video configurator runs
at different points depending on how the player was built (#125).

## Decision

This record decides fixes for defects 1, 2 and 5, a documentation change for `Error`, in decision 7
the fix for defects 3 and 4, and in decision 8 the fix for advances that ignore the controller's
state, which review found. Defect 6 is recorded here and left under *Not settled here*, because its
proposed fix failed review.

### 1. Single-source playback stays on `SubstrateSession`

The two session types remain. This record changes `PlaylistSession`, five internal points in
`PlaybackControllerCore` (the replay and seek checks at `Ended` in decision 4, the repeat input for a
session that loops internally in decision 2, the snapshot update in decision 5, and the recoverable
error in decision 7), one cell of `PlaybackProtocol` (decision 8),
and one internal counter in `SubstrateSession` (decision 2). `IMediaPlayer`,
`IMediaPlaylistPlayer`, `IPlaybackController` and both player factories keep their shapes.

### 2. The end of the queue keeps the last item

When the queue runs out after a clean end-of-stream, the session reports end-of-stream and keeps
the last item's runtime. It is disposed at the next advance, on unload, or on disposal. At `Ended`
the item can be sought, its counters can be read, and audio already queued on the device plays out.

This covers an item that played to its end. It does not cover a queue whose final item fails to
open, where the previous item has already been disposed; that case is under *Not settled here*.

A kept item also needs protection from its own late end-of-stream. An item raises end-of-stream on
its graph thread, and `PlaylistSession` hands it to the advance through the thread pool
(`PlaylistSession.cs:297`). The advance checks only that the item has not been replaced (`:305`). A
seek that arrives in between takes the transition gate first and relaunches the item's graph, which
re-arms its end-of-stream (`SubstrateSession.cs:1505`). The queued advance then runs and ends the
item that was just sought. Today that disposes it. With the item kept, the controller would report
`Ended` while the item plays, and the controller's own check for stale triggers does not catch it
because the state is `Playing` (`PlaybackControllerCore.cs:1286-1291`).

So the session numbers each run of an item. The number advances whenever the session seeks or
rewinds the item, each end-of-stream carries the number that was current when it was raised, and
the advance drops an end-of-stream whose number is out of date. This was read from code and has not
been reproduced.

Evidence:

- The spike under alternative A ran this change through the single-source integration suites, and
  it fixed the failures that disposal caused there: diagnostics read after `Ended`, the audio tail,
  and replay ordering. Those runs used a queue of one.
- On a scratch build of the playlist player with decisions 2 and 3 applied, seek then play from
  `Ended` went from 72 frames to 83. Two further seek-and-play passes each played the item through
  again, reaching 144 and then 216 frames. Pipeline diagnostics at `Ended` reported 72 decoded
  frames instead of 0.
- The existing playlist tests still passed, but none of them reaches the end of a queue, so they
  are not evidence for this decision.

**As implemented (#170).**

- **What is kept.** An item that reached its end or was skipped is kept when the coordinator decides
  the queue has ended. An item that faulted is disposed as before. So is the previous item when the
  final item fails to open, because it was disposed before the final item was tried.
- **The run number lives in `SubstrateSession`.** `PlaylistSession` cannot number runs itself
  without a race. Advancing the number before it calls the item's seek lets the interrupted run
  raise an end-of-stream that reads the new number. Advancing it after the seek returns lets the new
  run, sought close to its end, raise one that reads the old number, and dropping that would leave
  the player `Playing` at the end of the item. `SubstrateSession.RunNumber` advances inside the
  reposition that seeks and rewinds use, after the interrupted run has stopped and before anything
  relaunches. Stopping a run waits for the task that raises its end-of-stream, so the number read
  inside the callback is that run's.
- **The number is checked for every item, and only for end-of-stream.** A skip is not stale: a
  seek between the request and the advance does not cancel it. A fault is handled as decision 7
  says.
- **A seek at `Ended` that did not come through its warm-up is dropped.** The controller dispatches
  a seek while `Playing` if it has not yet seen the end-of-stream the session posted. That seek
  used to find no item. With the item kept, it would relaunch the item, and the controller would
  then end while the item played. The session drops it, as it drops a Play or Pause in the same
  race (decision 8), and the controller ends. Read from code.
- **The controller never loops a playlist.** `PlaylistMediaPlayerCore` sets the controller's repeat
  mode before the coordinator's. If the session decided the queue had ended under `Off` in between,
  the controller received that end-of-stream under `One`. While `Playing` it ran its loop rewind on
  the kept item and stayed `Playing`; the item's next end-of-stream was dropped at `Ended`, so the
  player stayed `Playing` after the item finished. While `Paused` it dropped the trigger. A
  playlist session runs the repeat mode itself and reports end-of-stream only when its queue has
  ended, so `IPlaybackSession.LoopsInternally` says so, and the controller gives the protocol
  `RepeatOne` false for such a session. The loop-stall watchdog still reads the controller's own
  mode. Review of #197 found this.

### 3. A skip at the tail pauses the item and then keeps it

A skip on the last item under `Off` pauses that item before reporting end-of-stream, then keeps it
as decision 2 does. The pause is `SubstrateSession.PauseAsync` on the item itself, which pauses its
workers, clock and audio sink. It cannot go through `PlaylistSession.PauseAsync`, which waits on the
transition gate the advance already holds.

Keeping the item without pausing it lets it run on: frames went from 10 at `Ended` to 35 one second
later. With the pause, on the scratch build, presentation stopped at 10, seek moved to `Paused`, play
reached 35 frames, and the item then ended normally.

**As implemented (#170)**, the pause applies to a skip of any item that has played, whether the
session was playing or paused, because pausing a paused item changes nothing. An item that has
never played is not paused: its gates have not opened. An item that reached its end is not paused
either, so the audio already queued on the device plays out. If the pause throws, the item is
disposed instead, as it was before items were kept.

### 4. Play from `Ended` on an empty queue is refused

- If items were enqueued while `Ended`, `PlayAsync` plays the next one. This is today's behaviour.
- If the queue is empty, `PlayAsync` returns a failed `Result` with `ErrorCategory.InvalidOperation`
  and the player stays in `Ended`. It does not unload, and it does not enter `Error`.

The refusal has to happen before the controller's replay unloads anything. Today the replay checks
only that a session and a loaded source exist (`PlaybackControllerCore.cs:1066-1072`) and then
unloads. The controller asks the session whether a replay has something to play before it unloads;
a playlist session with an empty queue answers no. This is an internal member on
`IPlaybackSession`, so no public API changes.

Refusing is the smaller change of the two options, and it can be relaxed later without breaking a
caller. Alternative D records the other.

**As implemented (#170).** `IPlaybackSession.CanReplay` is the member. It defaults to true, and
`PlaylistSession` answers whether the coordinator's queue holds an item. The same check refuses a
seek from `Ended` when nothing was kept, through `IPlaybackSession.CanSeekFromEnded`. That covers a
last item that faulted and a final item that failed to open. Without it, the seek succeeded and a
following Play reported `Playing` with nothing current, which is defect 2 in the cases decision 2
does not reach. `PlaylistCoordinator.First` returns null on an empty queue instead of throwing, and
a `PlaylistSession` loaded over one fails its load with an `InvalidOperationException`.

### 5. The controller's duration follows the current item

At each hand-off the session tells the controller the new item's `MediaInfo`. The controller's
`Duration`, `MediaInfo` and loop-stall evaluator then use the current item. This is the refresh
ADR-0062 called for.

The update is applied on the controller's dispatch loop. It must be dropped in two cases, and both
can happen:

- **It came from a session the controller has since unloaded.** The controller zeroes its snapshot
  when it disposes a session (`PlaybackControllerCore.cs:1439-1440`), and a playlist advance runs on
  the thread pool, so an update can arrive after the unload.
- **It came from an item earlier than one already applied.** An item boundary does not change the
  controller's session generation (`PlaybackControllerCore.cs:822`, `:1447`), so the generation
  alone cannot tell two items of one session apart.

The coordinator already numbers every hand-off (`PlaylistCoordinator.cs:277`). The playlist player
keeps one coordinator across reloads (`src/FrameFlow.Player/MediaPlaylistPlayer.cs:97`), so that
number keeps rising from one session to the next, and an advance in flight at an unload produces a
higher number than any applied. Checking the hand-off number alone is therefore not enough. Either
mechanism below is acceptable:

- Tag each update with both the session generation and the hand-off number. The controller drops an
  update whose generation is not the current one, or whose number is not higher than the last number
  it applied for that generation.
- Keep one watermark. The controller drops any update at or below the watermark. When it applies an
  update, it raises the watermark to that update's hand-off number, which covers an earlier item of
  the same session. After disposing a session, which waits for any in-flight advance
  (`PlaylistSession.cs:241-242`), it raises the watermark to that session's last hand-off number,
  which covers an unloaded session.

No state machine changes.

**As implemented (#183), neither mechanism was needed.** Decision 7 had since given every session
notification the session generation, and the two drop cases are covered this way:

- **An unloaded session.** The update travels as `SessionCallbacks.OnCurrentItemChanged`, which
  carries the generation like the other notifications. The controller drops an update whose
  generation is no longer current, both when it is reported and when the dispatch loop applies it,
  and `DisposeSessionAsync` advances the generation before it awaits disposal. A report from an
  older generation is also never allowed to replace a newer session's update waiting to be applied.
  `PlaylistSession` cannot report after its disposal, but the controller does not rely on that.
- **An earlier item.** The update is state, and only the latest one matters, so the controller
  stores it in a single slot rather than queueing it. `PlaylistSession` reports from inside the
  advance, under its transition gate, so one session's updates are stored in hand-off order and a
  later item's replaces an earlier one's.

The controller then queues a wake-up command. The dispatch loop takes the stored update at the top
of every iteration, not only for the wake-up. If the wake-up finds the bounded command channel full,
the update is still applied before the next command the loop dispatches. The review of #196 found
that dropping a queued update there would leave `Duration` describing the previous item.

The update is stored immediately before the coordinator raises `SourceTransitioned`, so a transition
subscriber can wait on a no-op command and then read the new values. An in-place replay keeps the
same item and reports nothing.

### 6. The docs say `Error` is terminal

`Error` keeps its only exit, a `Reset` trigger that nothing fires (`PlaybackProtocol.cs:398-402`).
`PlaybackState.Error` already says "an unrecoverable error occurred"
(`src/FrameFlow.Media/PlaybackState.cs:44`). The XML docs for `PlaybackState.Error`,
`IPlaybackController` and `IMediaPlayer` add what that means for a caller: a player in `Error` accepts
no further commands and must be disposed.

The docs do not promise that a new player built over the same sinks recovers. That path is untested
(`tests/FrameFlow.Integration.Tests/ErrorPathTests.cs:18-23`, #29).

### 7. A failed item is reported, and the player gives up only on a run of failures

This fixes defects 3 and 4, and is implemented (#180).

**Failed items are reported.** An item that faults while it plays, or an item after the first that
cannot be opened or started, raises `ErrorOccurred` with the item's exception as `Inner`. The
session reports through a new internal callback, `SessionCallbacks.OnRecoverableError`. The
controller raises the error on its dispatch loop and fires no trigger, so the state does not
change.

**A first item that fails before anything has played is treated as a single source's.** One that
cannot be opened is still a load failure. One that opens and then faults before the first
`PlayAsync`, during warm-up or while paused on the loaded item, goes to the controller as a fatal
error, and the controller enters `Error`. Skipping it would start the next item while the
controller is still loading or paused: `SubstrateSession`'s workers catch their own faults, so
`WarmUpAsync` returns and the load succeeds. This case is read from code. The integration tests'
injected operator cannot reach it, because the configured video chain receives no frame before the
first play.

Two reports can be missed or arrive late. A fault that races a skip of the same item is not
reported if the skip's advance runs first, because the fault then belongs to an item already
replaced. And `SourceTransitioned` for the next item fires on the advance thread, so it can reach a
subscriber before the previous item's error, which waits in the controller's command channel. The
error names its item by display name.

**The playlist carries on as before.** A failed item is skipped as if it had ended. Under `Off`, a
last item that faults while playing ends the playlist in `Ended`, and a caller can still enqueue
and play. Under `All` and `One` the rotation continues.

**The player gives up on a run of failures.** `PlaylistFailureGuard` counts items that fail in a
row without making progress. On the ninth it gives up: the session hands the controller a fatal
error, and the controller enters `Error` and raises `ErrorOccurred` once more.

An item has made progress once it has played for five seconds, or for half its length if that is
shorter. An item whose length is not known needs the full five seconds.

| Event | Effect on the count |
|---|---|
| An item cannot be opened or started | adds one |
| An item faults before it has made progress | adds one |
| An item faults after it has made progress | resets to zero |
| An item reaches its end, or is skipped, without failing | resets to zero |
| An item starts | none |

How far an item played is read from the position clock when the fault is raised, not when the
advance runs, because the advance can wait on the transition gate behind a seek. The clock starts
at zero for each item and each in-place rewind, and stops while paused. It does not measure play
alone: a seek moves it, so an item sought past the threshold that then faults counts as having made
progress.

A successful start does not reset the count. That reset is why defect 4 never tripped the guard: an
item that faults on every pass starts successfully on every pass.

The guard stops a playlist in which every item keeps failing. It does not stop a rotation in which
some item plays: a bad item there is reported on every pass and never given up on, because the
items between its failures reset the count. That includes a queue of an item that cannot be opened
and an item that ends at once, which reports on every pass with nothing slowing it down. That case
looped without reporting before this decision.

**Stale notifications are dropped.** Every session notification carries the controller's session
generation, captured when the session is created: recoverable errors, and the end-of-stream, fatal
error and buffer triggers. The controller drops one whose generation is no longer current.
`PlaybackControllerCore.DisposeSessionAsync` advances the generation before it awaits disposal, so
a notification from a session being torn down cannot reach the next one.

Before this decision only the controller's state filtered triggers, which cannot catch one case.
Replay from `Ended` unloads and reloads inside one dispatch command. A fatal error or end-of-stream
the old session posted during its teardown waited in the channel and was dispatched against the new
session, putting it in `Error` or `Ended`. `PlaybackDispatchProtocolTests` reproduces both. A
playlist that gives up while the controller replays is one way to post such a fatal error.

**A second fault from one run of an item is ignored.** An item's demux pump and its graph each
report their own faults (`SubstrateSession.cs:1538`, `:1588`). A faulted item is never rewound in
place, so the session records the item generation whose fault it handled and drops another fault
carrying it. Without this, a last item under `Off` whose pump and graph both faulted would be
reported twice, because reaching the end of the queue does not advance the item generation. Read
from code; the tests inject a fault into the graph only.

**How this answers the review of the earlier proposal:**

| Review finding | Decision 7 |
|---|---|
| The limit was checked only when an item failed to open | The fault path checks it too. |
| Resetting only on a natural end breaks rotation driven by skips | A skip resets the count, as a natural end does. |
| A source with no natural end would go fatal on faults hours apart | A fault after five seconds of play resets the count. A source with no known length always needs the full five seconds. |
| Making the last item's fault fatal makes a recoverable state terminal | It is reported and the playlist ends in `Ended`. Only a run of failures is fatal, and the guard was already fatal for a run of items that failed to start. |
| "Nothing after it" has no meaning under `One` | The rule does not ask what follows. Under `One` a faulted item is rebuilt and counted like any other. |
| The controller has no non-fatal error channel | `OnRecoverableError` is one. It is internal. The public carrier is `ErrorOccurred`, whose summary on `IMediaPlayer` says it "fires when a failure arises during playback" and does not tie it to `Error`. |

**The numbers.** Eight is the guard's existing limit. Five seconds is a judgement: it separates an
item that cannot get going from one that played and then failed. An item that faults later than
that on every pass is reported on every pass and never given up on. That is not a hot loop, and a
caller that wants a stricter rule can count the errors.

Half the length covers clips shorter than ten seconds. Without it a three-second clip that faults
near its end on every pass, such as one whose tail is damaged, could never reach five seconds. It
would be given up on after nine passes even though it shows nearly all of itself each time. Before
this decision that clip looped forever, and the review of this change raised it as a regression.

**Alternatives.**

- *A new observable on `IMediaPlaylistPlayer` for failed items.* Rejected: it adds public API for
  what `ErrorOccurred`'s documentation already covers.
- *A window of wall-clock time, such as nine failures within thirty seconds.* Rejected: the session
  would need a `TimeProvider`, and a run of items that each take longer than the window to fail to
  open, such as unreachable network sources, would never trip it. Today's guard trips on those.
- *Fatal when nothing follows the faulted item.* Rejected by the review above.

### 8. An advance follows the controller's state

This fixes "advances ignore the controller's state", a defect review found, and is implemented
(#182).

`PlaylistSession` records what the controller last asked of it: nothing yet, play, or pause. Once
it has reported the end of the queue, it records that instead. The record is kept from the calls
the session receives, with no new channel, under three rules:

- **`Ended` is replaced only by a seek out of `Ended`.** A Play or Pause can reach the session after
  it has ended the queue, because the controller dispatched it before the end-of-stream arrived. The
  controller ends when that end-of-stream arrives, so the session keeps `Ended`. A Play from `Ended`
  never reaches the session: the controller replays on a new one.
- **The seek out of `Ended` is recorded at the end of its warm-up.** The controller warms up on load
  and on that seek, and settles in `Paused` straight after, so a skip issued once it is `Paused` sees
  the session paused. The session holds its transition gate for the whole warm-up, so no advance
  can replace the item while it warms. The seek itself is not used: it runs later, and can be
  cancelled.
- **A pause is recorded only from playing.** `Rebuffering` counts as playing here: the session sees
  no call when the controller enters it.

A skip, an end-of-stream and a fault all advance, and the advance follows the record:

| Session record | Skip or end-of-stream | Fault |
|---|---|---|
| Not played yet | waits for the first `PlayAsync` | goes to the controller as a fatal error (decision 7) |
| Playing | the next item plays | reported; the next item plays |
| Paused | the next item opens and warms, and stays paused until `PlayAsync` | reported; the next item opens and stays paused |
| Ended | dropped | dropped |

- **A same-source replay rewinds in place only while playing, and only an item that has played.**
  Otherwise it rebuilds the item. The in-place rewind is built for a loop reached while playing. The
  comment in `SubstrateSession.RepositionAsync` says a loop rewind is only fired from `Playing`, and
  its paused branch does not relaunch the graph. An item that never played is the other case: a
  skip before the first play under `RepeatMode.One` is a replay taken by that first play, and
  rewinding the unstarted item leaves its clocks stopped.
- **The skip's two non-advancing cases are settled when the skip is requested.** At `Ended` it is
  dropped, and before the first play it is latched, before the call returns. A caller that skips and
  then plays sees the skip's effect in order. If the first `PlayAsync` starts between the check and
  the latch, the skip takes the latch back and advances.
- **An item opened while paused starts at `PlayAsync`.** If that start fails, the item is reported
  and skipped as one that fails to start inside an advance is, and does not escape `PlayAsync`.
- **When the queue runs out while paused**, the session reports end-of-stream as it does while
  playing. `PlaybackProtocol` gains `Paused × LastFrameRendered → Ended`, which freezes the clock,
  unless the repeat mode is `One`. Without it the controller dropped the trigger and stayed `Paused`
  with nothing current. Under `One` the trigger is still dropped, because `Paused` has no loop to
  run. Since #170 a playlist session never counts as `One` here (decision 2, as implemented).
- **At `Ended`, a skip is dropped and the queue is left alone**, so `PlayAsync`'s replay path takes
  the enqueued item. Before, the skip took it and started it while the state said `Ended`, and a
  following Play found an empty queue and hit defect 1. A late notification from the item that ended
  the queue is dropped for the same reason: that item is gone.
- **A skip before the first play is latched in the coordinator.** It is the same latch that a skip
  issued before load uses, and the first `PlayAsync` consumes it.

The new controller cell changes single-source playback in one case: an end-of-stream dispatched
after a pause. It used to be dropped. `PlaybackDispatchProtocolTests` reproduces that with a fake
session; the race itself is read from code.

This decision did not implement decision 3, and a skip on the last item while playing still
disposed the item. #170 implemented decision 3 later.

**What it leaves open.** Both were found in review and read from code.

- **A seek before an item's first play starts it.** `SubstrateSession.RepositionAsync` decides
  whether it was paused from the position clock, which reads as not paused before the first play.
  So a seek taken then relaunches the graph and opens the gates while the controller says `Paused`.
  A single source does this on Load then Seek. A playlist now reaches it after a skip while paused,
  where before the skip had already started the item. Since #170 it also reaches it on a
  one-item queue under `Off` that is skipped before its first play: the item is kept unplayed at
  `Ended`, and a seek from there starts it. Before, the seek found nothing.
- **An end-of-stream raised before a seek can end the player after it.** Nothing tells a stale
  end-of-stream from a current one after a seek. That was already true while `Playing`. The new
  cell extends it to `Paused`, in a narrow window. The end-of-stream has to be raised by the run
  before the pause, since a paused pacer does not drain, and dispatched after the seek. One posted
  before the seek command is dispatched ahead of it, which ends the player and lets the seek run
  from `Ended` as usual. Decision 2's run number is the fix inside the playlist, implemented with
  #170: an end-of-stream an item raised before a seek no longer reaches the controller. The
  controller has no equivalent for a single source (#195).

**Alternatives.**

- *Refuse a skip unless the player is playing.* Rejected: `SkipToNextAsync` returns no `Result` to
  refuse with, and "next" pressed while paused is an ordinary control.
- *Keep playing the next item, and move the controller to `Playing`.* Rejected: a skip is not a
  request to play, and the controller has no trigger a session can use to start itself.

## Consequences

### Positive

- With decisions 2 to 4, defects 1 and 2 are fixed. A playlist at `Ended` can be sought, reports
  its counters, and does not fault when Play finds nothing to play.
- With decision 5, defect 5 is fixed. The controller's `Duration`, `MediaInfo`, diagnostics snapshot
  and loop-stall watchdog describe the current item.
- With decision 7, defects 3 and 4 are fixed. Failed items are observable, and a playlist whose
  items all fail before making progress no longer loops forever.
- With decision 8, a playlist presents only while it says `Playing`, and a skip while paused on the
  last item ends it.
- None of it changes public API. Single-source playback changes only in that a fatal error or
  end-of-stream from a session replaced by replay from `Ended` no longer reaches the new session,
  and an end-of-stream that reaches a paused controller ends it.

### Negative

- **`ErrorOccurred` no longer means a playlist player stopped.** A consumer that disposes the player
  on any error would dispose one that is still playing. In this repository the SdlPlayer example
  logs errors and the Avalonia controls do not subscribe, so neither is affected.
  `docs/BREAKING-CHANGES.md` has the entry.
- **A playlist whose items all fail before making progress now stops.** A single item that faults
  early on every pass used to be rebuilt forever. It now enters `Error` after nine passes and has to
  be rebuilt.

- **A skip no longer resumes a playlist at `Ended`.** A caller that enqueued and then skipped to
  resume must call `PlayAsync`. The skip used to present while the state said `Ended`.
  `docs/BREAKING-CHANGES.md` entry 6 has the change.
- **The last item holds its resources after `Ended`.** That is its demuxer, decoders and graph, an
  active audio sink, a hardware decode device when one is in use, and GPU frames under
  `yieldHardwareFrames`. A single source already holds these at `Ended`. A playlist did not.
- **Play from `Ended` differs between the two players.** A single source replays. A playlist with an
  empty queue refuses. Before this record the playlist faulted, so no working caller loses anything.
- **Decision 5 relaxes ADR-0028's immutable loaded snapshot for playlists.** ADR-0062 already
  accepted this and it was never done.

### Neutral

- Two ways to loop remain. Under `RepeatMode.One`, a single source loops through the controller
  and a playlist loops inside the session. This record does not change either.

## Alternatives considered

### A. Run every player on the playlist session now

This was the first draft of this record. Behind an environment switch, `PlaybackController.Create`
built single-source controllers over `PlaylistSession` as a queue of one, with the last item kept at
the end of the queue and `RepeatMode.One` left with the controller. The integration, playback and
player suites all passed that way, three runs out of three. Without keeping the last item, fourteen
tests failed.

An independent review found the evidence did not support the proposal:

- **The queue never advanced or repeated.** Each load got its own coordinator with repeat fixed at
  `Off`. The advance, wrap, in-place replay and skip code did not run in any passing test.
- **Mid-stream faults went silent on single sources.** A single source that faults ends in `Error`
  today. On the spike build it ended in `Ended` with no error under `Off`, and stayed in `Playing`
  with frames frozen under `One`. The suites have no mid-stream fault test against a real session,
  so they could not catch it.
- **The controller's replay and duration are wrong once a queue advances.** These are defects 1
  and 5, and a unified path would have spread them to every player.
- **`RepeatMode.All` on one item cannot mean one thing.** Its public doc says a single-source
  player treats `All` like `Off` (`src/FrameFlow.Media/RepeatMode.cs:17-24`). The playlist player
  defaults to `All` and loops a single clip, and existing tests pin that.
- **Keeping the last item without pausing it** let a skipped item keep presenting (decision 3).

Rejected. The conditions for revisiting it are under *Deferred: one player type*.

### B. Put the queue in the controller

Still rejected, for ADR-0062's reason (ADR-0062:479-486): it threads queue policy through three
state machines. Decision 5 gives the controller the one fact it needs, the current item, without
giving it the queue.

### C. Document today's behaviour instead of changing it

Rejected. Defect 2 reports `Playing` while nothing plays, and defect 1 turns an ordinary call into a
terminal state.

### D. Replay the item that ended

Considered for decision 4: on an empty queue, `PlayAsync` replays the last item from the start, so a
playlist of one item behaves like a single source. Rejected for now. It picks the last item
arbitrarily, since it does not restart the playlist, and it raises `SourceTransitioned` for an item
nobody queued. It is also harder to take back: a refused call that later succeeds is unlikely to
break a caller, while a replay that later becomes a refusal breaks anyone who relied on it.

## Not settled here

### Defect fixes that failed review

- **Faults at the end of the queue (defects 3 and 4)** are now settled by decision 7. The first
  proposal failed review: a fault with nothing after it became fatal, mid-stream faults counted
  toward the guard, and the guard reset only when an item reached its natural end. Decision 7's
  table lists each review finding and its answer. Recovering onto the same sinks after `Error` is
  still untested (#29).

- **`SetNext` under `All` (defect 6).** The proposal was that an item placed by `SetNext` plays once
  and never joins the loop buffer. Review found it changes a documented way to change source:
  ADR-0068 lists `SetNextAsync` plus `SkipToNextAsync` as one (ADR-0068:196-200), and under the
  default `All` the new source would play once and the old rotation would return. The AvaloniaPlayer
  example picks entries that are already in the rotation (`MainWindow.axaml.cs:390-407`), so the
  picked file would repeat one item later. An implementation would also have to mark queue nodes,
  not sources, because the queue can hold the same source twice. ADR-0062 designed a replaceable
  "next" slot instead (ADR-0062:317). The shipped doc describes a push
  (`src/FrameFlow.Player/IMediaPlaylistPlayer.cs:41-45`).

### A direction for defect 6: a playlist and an up-next queue

This is not decided, and no work on it is planned. It is recorded because it answers defect 6 and
part of the review's objection above.

The coordinator keeps two structures that can disagree: the upcoming queue and the loop buffer
(`PlaylistCoordinator.cs:39-40`). Defect 6, and a switch to `All` mid-queue that never wraps, are
both cases of them disagreeing. The session asks the coordinator only for the first item and for
what follows the current one (`First`, `DecideNext`), so the structure behind those calls can change
without touching the session.

The direction replaces the two with structures that do different jobs:

- **The playlist** is an ordered list with a cursor. Under `All` it wraps. Under `Off` it stops at
  the end and keeps the items that played. A jump verb moves the cursor, and the rotation continues
  from the chosen item.
- **Up next** is a queue of one-off items. They play before the cursor's next item, leave once they
  have played, and never join the loop.

The existing verbs map onto them:

| Verb | Goes to | Under `All` |
|---|---|---|
| `EnqueueAsync` | the end of the playlist | joins the loop, as it does today |
| `SetNextAsync` | up next | plays once, which fixes defect 6 |

What it answers:

- `SetNext` no longer grows the rotation.
- A switch to `All` mid-queue wraps, because the items that played are still in the playlist.
- The AvaloniaPlayer example's pick moves the cursor instead of imitating a jump with `SetNext`.
- Play from `Ended` could later restart from the first item. Decision 4 leaves room for that: a
  refused call can start succeeding without breaking a caller.

What it does not answer:

- **The other half of the review's objection.** Under `All`, `SetNextAsync` plus `SkipToNextAsync`
  with a source that is not in the playlist plays it once, and then the old rotation returns.
  ADR-0068's way to change source still changes meaning. Replacing what plays needs its own verb,
  such as clearing the playlist and then enqueueing.
- **Anything between the controller and the session.** Advances that ignore the controller's state,
  faults at the end of the queue, and a late end-of-stream are unaffected.

What it costs:

- **New public members.** A jump, a way to read the playlist, remove, and clear or replace. These
  are additions. The change to `SetNextAsync` under `All` is a break that does not show up as a
  compile error, so it needs an entry in `docs/BREAKING-CHANGES.md`.
- **A growing playlist under the documented rotation pattern.** Enqueueing on every transition
  under `Off` (`src/FrameFlow.Player/IMediaPlaylistPlayer.cs:28-30`) keeps working, but the played
  items stay as a history. The doc should point that pattern at `SetNextAsync`, whose items leave
  once played. The memory involved is small. An entry is a reference to an `IMediaSource`, and
  `MediaSource` holds a display name, a `Uri`, a path and a flag (`src/FrameFlow.Media/MediaSource.cs:8-13`).
  Stream metadata is not kept per item.
- **Sources must open more than once.** A playlist that keeps played items opens them again on a
  wrap or a restart. The draft
  [Stream-backed media sources](stream-backed-media-sources.md) takes a factory for this reason
  rather than a `Stream` that is spent after one play.
- **Entries need their own identity.** The same source can appear twice, so marking an entry as
  played or as an up-next item has to mark the entry, not the source. The coordinator compares
  sources by reference today (`PlaylistCoordinator.cs:257`).

### Defects found in review

- **Advances ignore the controller's state.** Settled by decision 8. `AdvanceLockedAsync` always
  played the next item (`PlaylistSession.cs:401`). A skip while `Paused` presented the next item, 10
  frames to 46, while the state stayed `Paused`. Enqueue then skip while `Ended` presented, 72 frames
  to 109, while the state stayed `Ended`. A skip on the last item while `Paused` dropped the
  end-of-stream, because `Paused` had no transition for it (`PlaybackProtocol.cs:308-313`), and a
  later play presented nothing.
- **The final item fails to open.** On `[clean, corrupt]` under `Off`, the clean item is disposed
  before the corrupt one is tried, the queue then ends in `Ended` with no error, and seek then play
  leaves the sink at 72 frames. Decision 2 does not cover this. Decision 7 now reports the corrupt
  item on `ErrorOccurred`, and since #170 a seek from `Ended` is refused rather than reporting
  success. The queue still ends with nothing to seek.
- **Switching to `All` mid-queue never wraps.** A coordinator created under `Off` and switched to
  `All` after its first item played ran `a b` and ended. Items dequeued under `Off` never enter the
  loop buffer (`PlaylistCoordinator.cs:186-187`, `:249-250`).

### Open questions

- **Loop reporting on a playlist.** A playlist raises no `LoopRestarted`. `SourceTransitioned` with
  `Wrapped = true` fires on a natural wrap. From reading the code it also fires on each rebuild
  after a fault; the probe for defect 4 counted 11 transitions but did not record `Wrapped`. Neither
  tells a consumer "the item played to its end".
- **In-place rewind or full seek for a same-clip loop.** Under `One`, a single source rewinds with a
  full `SeekAsync(0)` (`PlaybackControllerCore.cs:483`). A comment at `:473` records that the
  in-place rewind was reverted on 2026-06-12 over a suspected presenter stall. The playlist's
  same-clip loop uses the in-place rewind (`PlaylistSession.cs:451`). Answering this needs runs
  with hardware decode and the GPU presenter.
- **The audio tail at item boundaries.** Disposing an item deactivates the audio sink
  (`SubstrateSession.cs:1268`), which stops the OpenAL source and unqueues every buffer on it
  (`OpenAlAudioSink.cs:873-887`). The spike measured that loss when `PlaylistSession` disposed the
  item of a queue of one: 8,918 samples short against a budget of 4,800. The same disposal runs at
  every item boundary. That the tail is cut there too is inferred, not measured.
- **Diagnostics across item boundaries.** The session returns the current item's counters
  (`PlaylistSession.cs:121`), so they restart at each boundary. The controller's diagnostics
  generation advances only when it creates or disposes its session
  (`PlaybackControllerCore.cs:822`, `:1447`). Two snapshots either side of a boundary carry the same
  generation while their counters are not comparable. Read from code.
- **Late end-of-stream on items that are not kept.** Decision 2 covers a seek or rewind of the kept
  item. The same thread-pool hop exists for every item (`PlaylistSession.cs:297`), so a seek that
  lands between a middle item's end-of-stream and its advance would skip to the next item. As
  implemented with #170, the run number is checked for every item, which covers this too. Read from
  code; testing it needs the interleaving tooling #143 asks for.

## Deferred: one player type

One player type, where a single source is a queue of one and `IMediaPlaylistPlayer` folds into
`IMediaPlayer`, is still a reasonable direction. It would remove the drift above and give one way
to change source instead of ADR-0068's three. It does not make switching between different sources
cheaper: opening the source, creating decoders and rebinding the presenter's converter happen at
every such boundary either way.

It should not be proposed again until all of these hold:

- The decisions in this record have landed, and the defects under *Not settled here* have decisions.
- A spike of the real design exists: one coordinator shared with the player facade, `RepeatMode`
  honoured by the queue, `Create` and `CreatePlaylist` on one construction path, and queues that
  advance and wrap.
- That spike passes a mid-stream fault test against a real session that fails on a build which
  loses the fault.
- A deterministic interleaving test for late end-of-stream exists and passes. That needs the tooling
  #143 asks for.
- The same machine has run hardware decode and the GPU presenter before and after the change, with
  no new presenter stall and no drop in frames presented in the player's diagnostics.
- `RepeatMode.All` on one item, loop ownership and loop reporting each have a recorded answer.
- The public break is accounted for: the `PublicAPI` baselines and an entry in
  `docs/BREAKING-CHANGES.md`.

## Validation

Write each test first and confirm it fails on the tree before this record lands, for the reason
given. These tests wait on real playback, so they belong in `FrameFlow.Integration.Tests`, the one
suite ADR-0072 permanently allows to read the clock. The probe results are in brackets.

| # | Decision | Test | Today |
|---|---|---|---|
| 1 | 2 | Playlist, `Off`: play to `Ended`, seek to 0, play. Frames advance. | [72 → 72 frames, position 0] |
| 2 | 2 | Playlist, `Off`: play to `Ended`. Pipeline diagnostics report decoded frames. | [0 decoded frames] |
| 3 | 2 | Playlist of two items, `Off`: play to `Ended`, seek to 0, play. The second item's frames advance. | [not probed] |
| 4 | 3 | Playlist, `Off`: skip on the last item, then seek to 0 and play. Frames advance. | [frames stay at 10] |
| 5 | 4 | Playlist, `Off`: play to `Ended`, play with an empty queue. Failed `Result` with `InvalidOperation`; state stays `Ended`; no `ErrorOccurred`. | [`NullReferenceException`, `Error`] |
| 6 | 5 | Playlist of a 0.5 s then a 3 s clip: after the advance `Duration` is 3 s; under `One`, no `LoopStalled` across two passes. | [0.5 s; one `LoopStalled`] |

Two guard tests pass today and must keep passing:

| # | Decision | Test | Today |
|---|---|---|---|
| 7 | 4 | Playlist, `Off`: enqueue while `Ended`, play. The enqueued item plays. | [passes] |
| 8 | 3 | Playlist, `Off`: skip on the last item. `Ended`, and presentation stops. | [passes] |

Test 8 cannot fail on today's tree for the reason decision 3 exists. It fails on a build that keeps
the last item without pausing it, where frames went from 10 to 35.

Tests 1 to 5, 7 and 8 are implemented in `tests/FrameFlow.Integration.Tests/PlaylistEndOfQueueTests.cs`,
over the 3-second clip. The tests that seek and play wait for the item to end again, so they also
fail if the run number drops the end-of-stream of the run the seek started. The bracketed results
are from the tree before decisions 2 to 4 (commit eae3a9e).

| # | Test | Before |
|---|---|---|
| 1 | `SeekThenPlayFromEnded_PlaysTheLastItemAgain` | [no further frame within 30 s] |
| 2 | `DiagnosticsAtEnded_DescribeTheLastItem` | [0 decoded frames] |
| 3 | `SeekThenPlayFromEnded_OnATwoItemPlaylist_PlaysTheSecondItemAgain` | [no further frame within 30 s] |
| 4 | `SeekThenPlayAfterSkippingTheLastItem_PlaysIt` | [no further frame within 30 s] |
| 5 | `PlayFromEndedWithNothingQueued_IsRefused_AndThePlayerStaysEnded` | [`ErrorCategory.System`] |
| 7 | `PlayFromEndedWithAnItemQueued_PlaysIt` | [passes] |
| 8 | `SkipOnTheLastItem_StopsPresentation` | [passes; with the pause removed, 10 → 34 frames one second after `Ended`] |
| 19 | `PlaylistFaultTests.SeekAndPlayFromEnded_AfterTheLastItemFaulted_AreRefused` | [the seek succeeded] |

Test 8 has no signal for frames that stop, so it watches the sink for one second. It cannot fail on
a correct build; a slow machine only makes it less likely to catch a missing pause. Test 19 covers
the seek refusal decision 4 gained as implemented.

`PlaybackDispatchProtocolTests` covers the controller half with a fake session: Play from `Ended`
when the session cannot replay, and Seek from `Ended` when it holds nothing, are each refused
without unloading or warming up. With the checks removed, both succeeded.
`EndOfStream_UnderRepeatOne_FromASessionThatLoopsInternally_EndsPlayback` sends end-of-stream to
a controller under `One` from a session that loops internally, while playing and while paused.
With the controller taking its own repeat mode for such a session, it stayed `Playing` and
`Paused`.
`PlaylistCoordinatorTests.First_OnASpentQueue_ReturnsNull_UntilSomethingIsEnqueued` failed with a
`NullReferenceException` against the unguarded `First`.

Test 17 held the advance on the clock's `Stop`, which the advance no longer calls when it keeps the
item, so it now holds on `Pause`, which the tail skip calls. With Play allowed to replace `Ended`
it still fails: Play is refused, because the later skip took the enqueued item from the queue.

The run number and the dropped seek at `Ended` have no test, for the reason given for decision 2's
run number below.

Test 6 is implemented, as two tests in `tests/FrameFlow.Integration.Tests/PlaylistCurrentItemTests.cs`
over `test-subsecond.mp4` then the 3-second clip. On the tree before decision 5 (commit 9bd87cd),
`MediaInfo` after the advance still had a 0.5 s duration, and two further passes under `One` raised
`LoopStalled` twice. `PlaybackDispatchProtocolTests` covers the controller half: an update from the
current session replaces `Duration`, `MediaInfo` and the diagnostics snapshot's duration, one
from an unloaded session is dropped, and does not displace the loaded session's waiting update, and
one reported while the command channel is full is still applied. That last test holds the dispatch loop inside a play and fills the channel. With a queued
update it failed with the loaded item's `MediaInfo`.

Decision 7's tests are in `tests/FrameFlow.Integration.Tests/PlaylistFaultTests.cs`. They inject a
fault on the 21st frame of the 3-second clip, well before that clip makes progress at 1.5 seconds.
The bracketed results are from the tree before decision 7 (commit 8ea83a7).

| # | Decision | Test | Before |
|---|---|---|---|
| 9 | 7 | Playlist, `Off`: the only item faults. One `ErrorOccurred`; `Ended`. | [no `ErrorOccurred`] |
| 10 | 7 | Playlist of two, `Off`: the first item faults. One `ErrorOccurred`; the second item plays; `Ended`. | [no `ErrorOccurred`] |
| 11 | 7 | Playlist, `All` and `One`: the item faults on every pass. Nine `ErrorOccurred`, then `Error` and a tenth that says it gave up. | [no `Error` within 60 s] |
| 12 | 7 | Playlist of two, `All`: the first item faults on every pass and the second is skipped whenever it starts. Twelve `ErrorOccurred`, and no `Error`. | [fewer than twelve `ErrorOccurred` within 60 s; with the reset on a clean end removed, `Error`] |

Test 12 guards the reset rule rather than the defect. It was run against a build with decision 7
applied and the reset on a clean end removed, and failed there as shown. It would also pass under
the old rule that reset on a successful start; test 11 is the one that tells those rules apart.

Tests 9 and 10 assert one error when the player reaches `Ended`. A duplicate fault would be posted
after that, so they do not test the duplicate-fault check.

The counting rule is also unit-tested without FFmpeg in `PlaylistFailureGuardTests`, including the
five-second threshold and the half-length rule, which the integration tests do not reach.
`PlaybackDispatchProtocolTests` covers the controller half: a recoverable error is raised without a
state change or a disposed session, one from an unloaded session is dropped, and a fatal error or
end-of-stream from the session replay from `Ended` replaced leaves the new session playing. Before
the generation was added to triggers, those last two ended in `Error` and `Ended`.

Decision 8's tests are in `tests/FrameFlow.Integration.Tests/PlaylistSkipStateTests.cs`, over the
same 3-second clip. The bracketed results are from the tree before decision 8 (commit 3803efa).

| # | Decision | Test | Before |
|---|---|---|---|
| 13 | 8 | Playlist of two, `Off`: pause at frame 10, skip. The second item is current, the state is `Paused` and the position is zero. Play presents it. | [position 0.27 ms: the skipped-to item had started] |
| 14 | 8 | Playlist of one, `Off`: pause at frame 10, skip. `Ended`. | [still `Paused` after 30 s] |
| 15 | 8 | Playlist of one, `Off`: play to `Ended`, enqueue, skip, play. Play succeeds and the enqueued item plays. | [Play failed: "Session initialization failed"] |
| 16 | 8 | Playlist of two, `Off`: load, skip, play. The second item plays. | [passes] |
| 17 | 8 | Playlist of one, `Off`: pause at frame 10; skip, and hold the skip's advance inside its gate while Play is queued; then enqueue, skip, play. Play succeeds and the enqueued item plays. | [with Play allowed to replace `Ended`: Play failed, "Session initialization failed"] |
| 18 | 8 | Playlist of one, `One`: load, skip, play. Fifteen frames present. | [with the in-place rewind allowed for an unplayed item: timed out after 30 s] |

Test 13's position check is the deterministic one. The item's clock starts on its first play, so
an item the advance played reads past zero by the time its transition has been observed, and one
it only warmed reads exactly zero. Test 16 passes before decision 8 and guards the deferral. The
skip is latched before `RequestSkip` returns, so the first play always takes it.

Tests 17 and 18 guard rules added after review, so their brackets come from builds with decision 8
applied and that one rule removed. Test 17 holds the advance with a test clock that blocks the
thread calling `Stop`, which the advance does while it holds the gate. Without the hold, the
controller's Play usually reached the session first, and the test passed with the rule removed.

`PlaybackProtocolTests` pins the new cell and its `RepeatMode.One` exception, and
`PlaybackDispatchProtocolTests.EndOfStream_WhilePaused_EndsPlayback` drives it through the
controller. Before decision 8 those failed with `Handled` false and `Expected: Ended, Actual:
Paused`.

Decision 2's run number has no row. Testing it means holding an end-of-stream between the item
raising it and the advance handling it, then seeking. That needs a seam in `PlaylistSession` to hold
the advance, or the tooling #143 asks for. A timing-based test would not show the race reliably.

## Revision history

- **First draft (2026-09-12)** proposed running every controller on the playlist session, with a
  single source as a queue of one. An independent review, which reproduced the spike, found that the
  queue under test never advanced or repeated, that single-source mid-stream faults went silent on
  the spike build, and that the controller's replay and duration were wrong once a queue advanced.
  The second draft withdrew that proposal (alternative A) and narrowed the record to the playlist
  player's own defects, with eight decisions.
- **Third draft (2026-09-12)**, after a second independent review that reproduced defects 1 to 4 and
  6 and built decisions 2 and 3. Decision 4 changed from replaying the ended item to refusing the
  call (alternative D). The fixes for faults at the end of the queue and for `SetNext` moved to *Not
  settled here*: the guard as proposed never tripped and would have broken skip-driven rotation, and
  one-shot `SetNext` would have changed a documented way to change source. Decision 3 now names the
  pause it uses. Decision 5 now says where its update runs. The review's new defects were added
  under *Not settled here*, and several citations and claims were corrected.
- **Fourth draft (2026-09-12)**, after automated PR review. Decision 5's update now also carries the
  hand-off's transition index. The session generation alone did not stop a late update from an
  earlier item of the same session, because an item boundary does not change it. A second turn
  suggested one token instead of two; decision 5 now says why an index-only check fails across an
  unload. The status and positive consequences now say the fixes are proposed, not implemented.
- **Fifth draft (2026-09-12)**, after a third turn of automated review. Decision 2 now drops a late
  end-of-stream raised before a seek or rewind of the kept item, which would otherwise end an item
  that is playing. Decision 5 now states the two cases an update must be dropped in and allows
  either of two mechanisms. The fourth draft's claim that one token would need a round trip was
  wrong: a watermark raised after disposal drains the in-flight advance does not.
- **Amendment (2026-09-13).** Records a direction for defect 6, a playlist with a cursor plus an
  up-next queue, with what it answers, what it leaves open and what it costs. No decision changed.
  The stream-source draft was amended at the same time to take a factory, because a playlist that
  keeps played items opens its sources more than once.
- **Amendment (2026-09-13), decision 7.** Settles defects 3 and 4, and implements the fix with #180.
  A failed item is reported on `ErrorOccurred` through a new internal session callback, and the
  playlist carries on. The guard now counts faults as well as failures to start, is checked on both
  paths, and is reset by an item that ends or is skipped without failing, or that played for five
  seconds before it faulted. A successful start no longer resets it. The fault proposal moved out of
  *Not settled here*, and decision 7 answers each finding of the review that sent it there.
  An independent review of the implementation then changed it: progress became five seconds or
  half the item's length, after the review showed that a short clip faulting near its end on every
  pass would be given up on where it had looped before; the played time is read when the fault is
  raised rather than after the advance waits on the gate; the first-item exception now covers only a
  first item that cannot be opened; and the decision now says which reports can be missed or arrive
  late, and that a bad item in a rotation with items that play is never given up on.
  Automated PR review then found two more, both fixed in the same change: a fatal error from a
  session replaced by replay from `Ended` could put the new session in `Error`, so every session
  notification now carries the session generation; and a first item that faulted before the first
  play was skipped while the controller was still loading, so that fault now goes to the controller
  as a single source's does.
- **Amendment (2026-09-13), decision 8.** Settles "advances ignore the controller's state" and
  implements the fix with #182. The playlist session records whether the controller last asked it
  to play or to pause, and whether the queue has ended. An advance plays the next item only while
  playing, warms it and leaves it paused while paused, waits for the first play before anything has
  played, and is dropped once the queue has ended. `PlaybackProtocol` gains `Paused ×
  LastFrameRendered → Ended` outside `RepeatMode.One`, so a skip on the last item while paused ends
  the playlist.
  An independent review of the implementation then found that a Play or Pause queued behind the
  advance that ended the queue replaced `Ended`, which let a later skip start an item while the
  state said `Ended`; that a skip before the first play under `RepeatMode.One` rewound an item that
  had never started; and that an item opened while paused could fail to start inside `PlayAsync`
  and escape it. Decision 8 now keeps `Ended` until the warm-up of a seek out of it, rewinds in
  place only an item that has played, settles a skip at `Ended` or before the first play when it is
  requested, and handles a failed deferred start as a failed start. It also records two gaps the
  review found and this decision does not close.
- **Amendment (2026-09-13), decision 5 implemented.** Implements decision 5 with #183. Neither of
  the two drop mechanisms the decision allowed was needed: the session generation decision 7 added
  to every notification drops an update from an unloaded session, and storing the latest update
  from under the playlist's transition gate keeps one session's updates in hand-off order. The
  dispatch loop applies it before every command, so it survives a full command channel.
- **Amendment (2026-09-13), decisions 2, 3 and 4 implemented.** Implements them with #170. The
  item that ends the queue is kept unless it faulted, and a skip pauses it first. The run number
  lives in `SubstrateSession`, because a count kept by `PlaylistSession` around the item's seek
  races either the interrupted run or the new one. It is checked for every item. Play from `Ended`
  is refused on an empty queue, and the same check refuses a seek from `Ended` when nothing was
  kept, which decision 4 had not covered. The session also drops a seek that reaches it at `Ended`
  without the warm-up of a seek out of `Ended`, as it drops a Play or Pause in that race. Automated
  review of #197 then found that a repeat-mode change racing the end of the queue let the
  controller loop a playlist that had ended. The draft had recorded that as an open question; the
  controller now never loops a session that runs its own repeat mode.
