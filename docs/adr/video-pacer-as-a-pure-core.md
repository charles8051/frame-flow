# The video pacer as a pure core: time is an input, and every wait is a deadline

## Status

Draft, pending number assignment at merge. **Proposed** (2026-10-15). Not implemented.

Motivating issues, all open: #746 (the post-seek settle gives up on wall-clock bounds, so a loaded
machine gets the seek run-up back), #671 (a file whose audio ends before its video waits out the
5 s pace cap on every frame after the audio's end), #528 (frames from before a seek can reach the
video sink), #127 (the pause path), #195 (an end of stream raised before a seek can end playback
after it), #680 (a pause reads as a PRESENT GAP at the sink) and #636 (a player paces one sink).

Evidence from fixes that landed here: #701 and #704 (the pacer released a frame the sink owned),
#703 and #711 (a sink that threw stopped delivery), #299 and #744 (a late drop, counted, read as
frame loss), #609 (the end-of-playback drain), and #745 and #749 (tests that depended on how fast
the runner was, several of them through the pacer's timing).

Line numbers are on `main` at 32ce40e. A bare `:N` is in
`src/FrameFlow.Playback/ClockSelectVideoSink.cs`.

**Related:**

- [ADR-0003](ADR-0003-audio-master-sync-policy.md): the audio sink masters the clock when there is
  audio, and the playback layer decides when a late frame is dropped. Unchanged. The master reading
  the core takes as an input is that clock's, so its phase errors reach the pacer as readings
  (#753).
- [ADR-0057](ADR-0057-pull-based-master-clock.md) Stage 2: `ClockSelectBuffer` is the pacer's pure
  core, a shell's delivery loop waits on the clock, a `maxWait` cap of 5 s degrades a stalled master
  to choppy-but-alive, the end of stream waits for the ring to drain at clock cadence, and a seek
  flushes the ring. This record keeps every one of those and widens the core from the ring's
  selection to every decision the loop makes. It amends Stage 2's description of the loop, not its
  decision.
- [Declared pull](declared-pull-clock.md) (draft): the clock stays a pull, and a clock wait belongs
  in a shell the pump hands off to. Decision 2 keeps the wait in the shell. The core reads no clock.
- [ADR-0067](ADR-0067-high-resolution-pacing-timers.md): the shell's clock wait is the clock's own
  `WaitUntilAsync`, on its high-resolution provider, as today.
- [ADR-0072](ADR-0072-tests-do-not-depend-on-elapsed-time.md): wall time is a `TimeProvider`, faked
  with `FakeTimeProvider`. Its decision 3 names the pacer's park seam as the reference for a test
  that waits on a background worker. Decision 6 moves the pacer's decision tests off that seam onto
  the core, and the seam stays for the shell's tests as the reader's idle signal.
- [ADR-0079](ADR-0079-the-pass-and-the-player.md): only the player paces; a pass has no pacer.
  Unchanged.
- [ADR-0080](ADR-0080-one-ownership-contract-for-graph-items.md): each `Dispose` releases one
  reference. Decision 3.12 states who releases each frame on each path, and the walk checks that
  each frame leaves the pacer once.
- [ADR-0081](ADR-0081-fixed-pool-budget.md): the pacer declares its ring, the frame on its way to
  the sink, and what the sink keeps (`MaxHeldFramesOver`, `:695-696`). The core's bound is that
  count, checked by the walk.
- [ADR-0084](ADR-0084-frames-carry-their-timeline.md): frames carry the timeline the session minted
  for their run, and the pacer "is told" of a break by `Flush` and `BeginRun`. Decision 3.1 has the
  pacer read the timeline at admission, and decision 3.8 makes the timeline the pacer's run
  identity, which is that record's optional slice 4 for the pacer.
- [ADR-0085](ADR-0085-one-lifecycle-for-views.md): a claim is released after the dispose awaits the
  delivery loop. Unchanged.
- [ADR-0086](ADR-0086-work-in-flight-held-until-it-ends.md): the pacer's loop is held by a
  `WorkScope`, its dispose is `DisposeOnce`, and #703's policy is decision 5's instance for sink
  failures. All stay. Its decision 2, `ItemRuntimeProtocol`, is the shape this record follows, and
  that core's actions (`FlushPacer`, `ReleaseSettle`, `StartRun`'s floor and `HoldForSettle`,
  `OpenGates`, `CloseGates`, `Settle`, `Free(Pacer)`) are the pacer core's commands.
- [ADR-0026](ADR-0026-state-bound-worker-lifecycle-binding.md), amendment of 2026-10-02: the pacer
  raises the buffer triggers through the pure `PacerStarvation`. Unchanged; the core absorbs it.
- [Lateness-driven decode skip](lateness-driven-decode-skip.md) (draft) reads `PresentationLag`
  and the late-drop count. Their meaning does not change.
- [ADR-0055](ADR-0055-decode-protocol-as-a-pure-mealy-core.md) and
  [ADR-0076](ADR-0076-playlist-session-protocol.md): the pattern, a total step cranked by one shell.
- #752 records the controller's half of seek coordination, which is where #195's dropped end of
  stream has to be decided. #753 records the audio sink's phase, which sets what the master clock
  reads after the audio's end (#671) and across a pause (#127).

## Context

### What the pacer decides today

`ClockSelectVideoSink` is 1,807 lines. Its delivery loop, `RunDeliveryLoopAsync` (`:1167-1579`),
makes most of its decisions, interleaved with five awaits: the settle hold (`:1212`), the
end-of-content hold's probe slices (`:1293`), an empty ring's wait for an arrival (`:1367`), the
wait for the earliest frame (`:1432`), and the halt after a fault (`:1624`). Each await links up
to four cancellation tokens (`:1283-1288`). The rest of the decisions are taken on the producer's
thread in `PresentAsync` and in the calls the item runtime makes. Four decisions are already
types of their own: `ClockSelectBuffer` (ADR-0057 Stage 2), a mutable ring with a total selection,
and three pure values, `PacerStarvation` (#547), `SinkFailureRun` (#703) and `MasterClockStall`
(#359).

| Decision | Where | What it decides |
| -------- | ----- | --------------- |
| Admission and the bound | `PresentAsync`, `:780-875`; `_space`, `:453` | A producer waits for a slot; a frame offered after the dispose began is released (`:784-788`, `:838-845`); a cancelled wait releases the frame (`:797-801`) |
| Selection and the late drop | `:1457-1491`; `ClockSelectBuffer.Select` | The newest frame whose PTS the clock has reached is presented; older due frames are released and counted in `DroppedLate`, which the session reports as `VideoFramesDroppedForSync` (`SubstrateSession.cs:372`) |
| The seek floor | `BeginRun`, `:982-1010`; admission, `:815-836`, `:847-857` | Frames below the floor are refused and counted; the first at or past it spends the floor and is the destination frame |
| The settle hold | `:826-830`, `:1058-1067`, `:1146-1165`, `:1208-1214`; `SubstrateSession.cs:1327-1353` | The destination frame closes delivery until the session has reseated the clocks on it, or until a 250 ms backstop (`:542`, `:1151`); the session's wait for that frame gives up after `SeekSettleCap`, 5 s (`SubstrateSession.cs:73`, `SubstrateSession.cs:1337`) |
| The pace cap | `:1398-1455`, `CreateCapSource` `:390-391` | A frame the master does not reach within `maxWait` (`SubstrateMedia.PaceWaitCap`, 5 s, `SubstrateMedia.cs:104`) is presented anyway (`:1464`) |
| The end-of-content hold | `:1216-1341`, `HoldCapFor` `:434-435` | With the input complete and the ring empty, the last frame keeps its display interval until the clock reaches its end, capped at the remaining interval plus `maxWait` (#249) |
| The liveness probe | `:1274-1329`, `HoldProbeSlice` `:418` | The hold is waited in 100 ms slices, and a master that has not advanced for five of them, once the frame has had its interval in wall time, ends the hold early (#359) |
| Pause and resume | `:929-955`; `SubstrateMedia.OpenGates`, `CloseGates` | While paused no cap is armed (#127, #128); a pause forgets starvation; a resume starts the cap again |
| Flush and a new run | `:888-906`, `:982-1010` | A flush releases the ring and breaks the wait in progress; a run sets the floor, the hold-for-settle flag and a new target, and resets the drain state |
| Input complete and the drain | `:1018-1046`, `:1198-1199`, `:1332-1339`; `SubstrateMedia.cs:587-591` | The graph's end of stream waits until the ring is empty and the clock has reached the last presented frame's end; the audio sink's drain follows it (#609, `SubstrateMedia.cs:593-600`) |
| Starvation | `:1342-1395`, `:1200-1207` | An empty ring with more to come, playing, after the run's first present, reports an underrun after three frame intervals (at least 1 s), and a frame or the end of input reports ready |
| Sink failures | `:1520-1551`, `ReportSinkFailure` `:1633-1669`, `HaltAsync` `:1620-1627` | A throwing call costs its frame; reports come at most once per interval; more than eight in a row faults the item and halts delivery; anything else that throws on the loop does the same (`:1561-1569`) |
| Frame ownership | refusals above, `:1483-1487`, `:902-903`, `DrainRing` `:1584-1611`, `:1514-1519` | Each path that ends the pacer's hold on a frame releases it, except a present, after which the announcer and then the sink own it (#701) |
| The lag | `PresentationLag`, `:734-763` | How far the clock has run past the end of the frame on screen, null before the run's first present |

### Its state

The type has 42 instance fields and 34 `lock` statements. About half of the fields are decision
state, and the rest are what a shell owns:

| Fields | Lines | What they are |
| ------ | ----- | ------------- |
| `_capacity`, `_maxWait` | `:108-109` | Configuration |
| `_buffer` (the ring and the floor) | `:447` | Decision state |
| `_runId`, `_holdForSettle`, `_settleHeld` | `:499-510` | Decision state |
| `_paused` | `:516` | Decision state |
| `_starvation`, `_lastPresentedDuration`, `_starvationWait` | `:521-523` | Decision state |
| `_inputComplete`, `_lastFrameEndPts`, `_presentedEndPts` | `:562`, `:575`, `:582` | Decision state |
| `_presented`, `_droppedLate`, `_droppedBeforeTarget`, `_droppedThisSeek` | `:478-484` | Decision state, published as counts |
| `_lastHoldCapTicks` | `:437` | A test's read of a decision the loop made |
| `_seekTargetReached`, `_drained` | `:490`, `:563` | A promise each; whether it is complete is decision state, the promise is the shell's |
| `_arrival`, `_settled`, `_recheck` | `:457`, `:495`, `:464` | Wake signals for the loop's waits |
| `_space` | `:453` | The admission bound, as a semaphore |
| `_gate`, `_parkGate` | `:446`, `:137` | Locks |
| `_shutdownCts`, `_delivery`, `_dispose` | `:468-476` | Lifetime (ADR-0086) |
| `_inner`, `_clock`, `_logger`, `_timeProvider`, `_formatAnnouncer`, `_sinkFailures` | `:105-117`, `:534`, `:586` | Collaborators; the last two are shared by every pacer over one sink |
| `_onStarved`, `_onFed`, `_onSinkFailed`, `_onFaulted` | `:526-533` | Callbacks to the session |
| `_parkGeneration`, `_isParked`, `_parkWaiters` | `:138-140` | The test seam (ADR-0072 decision 3) |

The loop also carries decision state in locals across iterations: the liveness probe and the time
the hold began (`:1274`, `:1278`).

### The decisions are held together by hand

Each decision reads state another thread writes, so each is taken under `_gate` and many are taken
twice. The comments record the races that were found and closed one at a time:

- the settle hold is closed in the same critical section that makes the destination frame visible,
  because closing it after the lock let the loop present that frame against the clock about to be
  corrected (`:822-825`, `:1185-1189`);
- a producer re-checks the dispose after it has its slot, or it hands a frame to a ring nothing will
  drain and releases a permit into a disposed semaphore (`:803-809`);
- starvation is re-checked under the lock after its timer fires, because a frame, a pause or the
  end of input can land at the same moment (`:1376-1389`);
- the lag re-checks the run after reading the clock, or it pairs one run's frame with the next
  run's clock (`:751-758`);
- the clock is read under the lock (`:1028`, `:1198`, `:1235`), and only on the turns that need it,
  because `IClockSource.Latest` may re-enter the pacer (`:1225-1230`).

A flush has to break whichever wait is in progress, so `_recheck` is cancelled and replaced under
the lock (`:910-916`), and each wait tells a recheck from a cap or a probe slice by which token
fired (`:1298-1305`, `:1437-1448`).

### Three wall-clock bounds behind the open issues

Most of the pacer's timers are deliberate: the cap, the hold's cap and its probe, the starvation
threshold. Two bounds are backstops that make a decision when they fire:

- the settle hold's 250 ms backstop (`:542`), which opens the hold if the session's reseat has not
  run, and so presents the destination frame and those after it against the clock that counted the
  decoder's walk;
- the settle's 5 s cap in the session (`SubstrateSession.cs:73`), after which the whole walk from
  the keyframe to the target is counted as playback.

#746 is those two: on a loaded machine either brings back the run-up the settle exists to remove.
The third is the pace cap. It is a deliberate bound, but its rule is "each frame waits the full cap",
and #671 is that rule meeting a master that has stopped for good: 60 frames of video tail at 30 fps
cost about five minutes.

Tests met these bounds before users did. #78 was a 120 ms test delay losing to the 250 ms backstop,
and #745 and #749 reworked integration assertions on how fast the runner presented. The tests that
remain barrier on the loop's park signal, which is a fact rather than a bet, but which can only be
reached by running the loop.

### The open issues, by the decision they live in

| Issue | Decision | Mechanism |
| ----- | -------- | --------- |
| #746 | The settle hold | The backstop and the session's cap are wall-clock bounds on a wait for a signal that load delays |
| #671 | The pace cap | The audio clock stops at the audio's end; every later frame waits the cap; nothing notices the master has stopped for good |
| #528 | Admission; flush | Not traced. The issue's hypothesis is a frame already past admission when the seek starts, which is never checked against the floor; another path is a present under way when the flush comes, which completes after it |
| #127 | Pause and resume | The pacer's half, the cap running during a pause, was the subject of #128. The issue is open for the audio clock's half |
| #195 | Input complete and the drain | The pacer resets its drain per run but does not name the run it belongs to, and the controller cannot tell a stale end of stream from a fresh one |
| #680 | Pause and resume | The sink is not told of a pause, and the first frame after one reads as a gap |
| #636 | The whole pacer | One pacer paces one sink, and it is internal |

### The pacer's floor in a player

`SubstrateMedia` arms the same floor on the video and audio sources (`SubstrateMedia.cs:963-969`,
`SubstrateMedia.cs:1003-1028`), so that a run behind a closed gate decodes on to its start
([media-clips](../feature-specs/media-clips/adr.md) decision 8). The pre-target frames are refused
there, before the gate, and the pacer's floor sees few of them. What the pacer's floor still does is
recognise the destination frame, which closes the settle hold and completes the target. The core
keeps it for that, and as the second line it already is.

## Decision

### 1. A pure core decides

The pacer's decisions move to a pure Mealy core in `FrameFlow.Playback.Core`, for example
`PacerProtocol` (the names here are examples), in the shape of `ItemRuntimeProtocol`: a total step
from a state, one input and the time it was stepped at, to the next state and the actions the shell
must perform. It owns no IO, no `await`, no clock, no lock and no mutable state, and it never sees
an `IVideoFrame`.

- **Frames are descriptors.** The core sees a frame as its id, PTS, duration and `TimelineId`. The
  shell keeps the frames by id. Every action that ends the pacer's hold on a frame names its id.
- **State.** The phase (Running, Halted, Disposing, Disposed); the configuration (capacity, the pace
  cap); the run (its timeline, floor, hold-for-settle, the settle's status and the target's
  outcome); the ring of at most capacity descriptors in arrival order, and the offer waiting for
  room, if any; the present in flight, if any; whether it is paused; the wait it is in, with its deadlines
  (the frame wait and its cap, the end-of-content hold with its cap and probe, the empty ring and
  its starvation threshold, the settle hold); the end of the run (input complete, the last
  presented frame's end, the last presented duration, drained); `PacerStarvation`; and the counts.
- **Inputs.** From the producer: `Offer(frame)` and `OfferAbandoned(id)`. From the item runtime:
  `BeginRun(timeline, floor, holdForSettle)`, `Flush`, `Pause`, `Resume`, `InputComplete`,
  `ReleaseSettle(timeline)` and `Dispose`. Fed back by the shell: `Woke(wait)`, `Presented(id)`,
  `PresentFailed(id, verdict)`, `PresentStopped(id)` and `ShellFaulted(error)`.
- **Time is an input.** Every input is stepped with `PacerNow(Master, Wall)`: the master clock's
  `Latest` and the elapsed time on the shell's `TimeProvider`, both read by the shell when it steps
  the input. The core compares, subtracts and stores them, and never reads a clock. `ShellFaulted`
  is the one input stepped without a reading, because a clock read that throws is one of its causes
  and may be the shell's first: it carries none, and its rule reads no time.
- **Actions.** `Admit(id)` and `Release(id, reason)` complete a producer's offer and dispose a frame;
  `Present(id)` is awaited and hands a frame to the sink; `Arm(wait)` replaces the one armed wait;
  `RaiseStarved`, `RaiseFed`, `ReportSinkFailure(verdict)` and `RaiseFault(error)` go to the
  session's callbacks; `TargetReached(timeline, pts)` and `Drained(timeline)` complete the item
  runtime's promises; logging actions carry what the log lines say today; `CompleteDispose`.

### 2. One reader steps it, and every wait is a deadline the shell arms

`ClockSelectVideoSink` stays the `IVideoSink` the graph presents to, and becomes the core's shell.

- **One reader.** The delivery loop becomes a reader of an unbounded channel of inputs, held by the
  pacer's `WorkScope` as the loop is today. It steps one input at a time in arrival order, reading
  `PacerNow` as it steps each one, and performs the step's immediate actions in order. A caller that
  needs an outcome awaits a promise the reader completes: `PresentAsync` posts an offer and awaits
  its admission, and the item runtime awaits the target and the drain of a run. The other calls post
  and return. The channel is unbounded for control inputs only: a frame enters it only as the offer
  of a `PresentAsync` call that awaits its own admission, and the pacer has one producer, its run's
  sink pump, so at most one offer is outstanding (rule 1).
- **The present is awaited outside the step.** The reader starts the call into the announcer and
  goes on stepping. Its end is posted as an input. At most one present is in flight.
- **One armed wait.** `Arm(wait)` carries a number, an optional master target and an optional wall
  deadline. The shell waits for whichever comes first, the master target with the clock's own
  `WaitUntilAsync` and the deadline with a timer on its `TimeProvider`, and posts `Woke` with the
  number. A new `Arm` cancels the one before. A wake for a wait the core no longer has armed changes
  nothing, so the shell does not have to win a race to cancel one.
- **What goes.** `_gate`, `_space`, `_arrival`, `_settled` and `_recheck` go, and with them every
  `lock` the decisions took. There is nothing left to re-check, because no decision spans two
  critical sections and no wait has to be broken: each input re-decides the wait.
- **What stays.** `_shutdownCts`, the `WorkScope`, `DisposeOnce` and `SlowWorkWarning` (ADR-0086),
  the announcer and the shared sink-failure run, the callbacks, and the park seam, which becomes
  "the reader has stepped everything posted before this generation and is waiting".

A clock read or a wait that throws is posted as `ShellFaulted`, so the core decides what a fault
on the loop means, as it decides what a failed present means.

### 3. The rules

Each rule below is today's behaviour unless it says it changes it.

1. **Admission and the bound.** An offer is admitted while the ring holds fewer than capacity
   frames, and otherwise waits; that wait is the decoder's backpressure (ADR-0057 Stage 2). At most
   one offer waits. The pacer's only producer is its run's sink pump (`AsSinkNode`, one per graph,
   and ADR-0086 ends a run's graph before the next starts), and ADR-0081 counts the frame of that
   pump's call on the sink node, not on the pacer (ADR-0081, decision 1: `AsSinkNode` adds the
   call). An offer that arrives while another waits comes from a second writer, which nothing in
   the tree builds: the core refuses it, releases its frame and logs it, so the bound holds whatever
   the callers. An offer is also refused, and its frame released, once the dispose has begun, when
   it is below the run's floor (counted as before the target), and, a change, when its timeline is
   set and is not the run's (counted apart). A frame of `TimelineId.None`, such as one an
   application's configurator builds as its own frame type without carrying its input's id, is
   judged by the floor alone, and counted, so a test or a log can name a configurator that drops
   the id. Refusing it instead would blank every such configurator's picture. A halted pacer leaves
   the waiting offer waiting until its dispose, as today.
2. **Selection and the late drop.** Running, with nothing in flight and no hold in force, the
   newest frame whose PTS the master has reached is presented, and every older frame in the ring is
   released as late and counted. A frame is dropped late only in a step that presents a newer one.
3. **The floor and the target.** A run's floor is set when it is above zero. The first frame
   admitted at or past it spends it and is the destination frame: the target completes with its
   PTS. The target completes with null at the end of input, at the next run, and at the dispose,
   once, whichever comes first. A change: today a run that `BeginRun` replaces leaves its target to
   the session's cap.
4. **The settle hold and its release.** A run begun with hold-for-settle and a floor holds delivery
   from its destination frame's admission until `ReleaseSettle` names that run, the next run
   begins, or the dispose. Nothing is presented while it holds. A release ends the settle for its
   run whenever it comes: one that arrives before the destination frame, because the settle ended
   first, cancelled or with no target reached, clears the run's hold-for-settle, and the
   destination frame then holds nothing. A change: today the release clears the hold but not the
   flag (`:1058-1067`), so a destination frame admitted after it closes the hold again
   (`:826-830`), and only the backstop opens it. A change (#746): no wall deadline releases a
   hold. A hold still in force after a warning interval (for example one second) is logged once,
   naming the run, and stays in force. Every way a settle ends sends the release:
   `ItemRuntimeProtocolWalkTests` already checks that a command that started a run to settle
   settles it or releases it before it ends, and cancels no run whose settle is owed
   (`ItemRuntimeProtocolWalkTests.cs:452`, `ItemRuntimeProtocolWalkTests.cs:474`,
   `ItemRuntimeProtocolWalkTests.cs:567`), and the shell's `SettleAsync` releases in a `finally`
   however its wait ends (`SubstrateSession.cs:1346-1352`). With the release ending the settle
   whenever it comes, those checks, not a timer, are what keep a hold from being left in force.
5. **The pace cap.** Waiting for the earliest frame, while running and not held, arms the cap as a
   wall deadline: the wall time that frame became the one waited for, plus the configured cap
   (`PaceWaitCap`, 5 s). A resume or a flush starts it again. When it passes with the
   frame still not due, that frame is presented as if the master had reached it, and the next
   frame's wait arms a cap of its own. That last clause is the rule #671 meets; open question 1 is
   whether it changes.
6. **The end-of-content hold and the liveness probe.** With the input complete, the ring empty,
   nothing in flight and the master short of the last presented frame's end, the core holds: it
   arms the master target at that end, and a wall deadline at the earlier of the hold's cap
   (`HoldCapFor(remaining, cap)` from the hold's start) and the next probe slice. Reaching the end or
   the cap drains the run. A probe slice folds the master reading into `MasterClockStall.Observe`
   with the wall time since the hold began, and a stopped verdict drains the run (#359). A flush or a
   new run ends the hold without a drain.
7. **Pause and resume.** While paused, no cap, hold cap, probe slice or starvation deadline is armed,
   and the hold-warning interval is the only wall deadline that runs. A pause forgets starvation
   (`PacerStarvation.Forgotten`). A resume starts every deadline again from its own wall time, so a
   stalled master after a resume still meets a full cap (#127, #128).
8. **Flush and a new run.** A flush releases every frame in the ring and ends the end-of-content
   hold. A present already in flight completes, and the sink owns its frame. `BeginRun` carries the
   timeline `SubstrateMedia.BeginRun` mints for the run, and that timeline is the pacer's run
   identity: the pacer keeps no run counter of its own, and `PacerRun` and `ReleaseSettle` name a
   timeline. It sets the floor and hold-for-settle, completes the previous run's target, ends the
   previous run's hold, and resets the input-complete flag, the last frame's end, the presented end
   and the drain.
9. **Input complete and the drain.** The run is drained when its input is complete, the ring is
   empty, nothing is in flight and the master has reached the last presented frame's end, or when
   the end-of-content hold ends on its cap or its probe. `Drained` is raised once per run, for the
   run that is current, and never after a halt. A change: today `SignalInputComplete` can complete
   the drain on its own thread whatever the loop's state (`:1028-1029`).
10. **Starvation.** `PacerStarvation` decides. With the ring empty, more input to come, playing,
    after the run's first present, the core arms a wall deadline at
    `PacerStarvation.Threshold(last presented duration)`; still empty at it, it raises the underrun
    once. An admitted frame or the end of input after an underrun raises ready.
11. **Sink failures and faults.** The run of failed calls belongs to the sink and goes on across
    the pacers over it (#703), so it stays in the shared `VideoSinkFailures`, and the core does not
    hold it. When a present fails, the shell steps that per-sink `SinkFailureRun` and feeds the
    verdict in. The core decides what the pacer does with it: drop the frame and go on, reporting it
    when the verdict says to, or, on an escalation, raise the fault and halt. `ShellFaulted` raises
    the fault and halts. A halted pacer presents nothing and raises no drain until it is disposed.
12. **Ownership (ADR-0080).** Who releases each frame:

    | Path | Released by |
    | ---- | ----------- |
    | An offer refused: disposing, below the floor, another timeline | The pacer, at once |
    | An offer from a second writer, an offer abandoned by its producer, or one still waiting at the dispose | The pacer |
    | A frame in the ring dropped late, flushed, or left at the dispose | The pacer; what a dispose throws is the pacer's dispose's (ADR-0086, decision 3.4) |
    | A frame presented | The announcer until the hand-off, then the sink, even when the call throws (#701) |

13. **Disposal.** The dispose refuses new offers, releases the ring and the offer still waiting
    (whose producer's call ends cancelled, as today), completes the run's target with null, disarms
    the wait, and waits for the present in flight, which the shell cancels through its shutdown
    token. A drain still awaited ends on its waiter's token, as today. `CompleteDispose` comes when
    nothing is in flight. `DisposeOnce` keeps the second caller waiting for the first and the faults
    returned.
14. **The lag.** `PresentationLag` is a pure function of a published snapshot of the state and a
    master reading: null before the run's first present, zero while the reading is inside the
    presented frame's window, and otherwise how far past its end the reading is. The run re-check
    stays, on the snapshot rather than a lock.

### 4. The invariants the walk checks

After every step, over every reachable state:

1. Each frame offered leaves the pacer exactly once, by one row of rule 12, and the counts account
   for it: offered equals presented, failed, dropped late, refused, flushed and released at the
   dispose, plus what is in the ring, waiting and in flight.
2. The ring never holds more than capacity, at most one present is in flight and at most one offer
   waits. So the pacer holds at most capacity plus one frames once `PresentAsync` has returned,
   which is what it declares, and the waiting offer is its producer's call, which ADR-0081 counts on
   the sink node.
3. Within a run, presents are in non-decreasing PTS order. Nothing presented is below the run's
   floor, and nothing presented carries a timeline set to a run other than the current one. A frame
   with no timeline is outside this invariant: the pacer cannot tell its run, and rule 1 judges it
   by the floor alone. The guarantee against a stale frame therefore holds for frames that carry
   their timeline, which ADR-0084 decision 3 requires of every in-repo producer and derived item.
4. The newest frame offered in a run is never dropped late (#744's account).
5. Nothing is presented while a settle hold is in force, and no hold is in force once its run has
   been released, replaced or the pacer disposed, whichever order the release and the destination
   frame come in. No wall deadline releases a hold.
6. While paused, the only wall deadline armed is the hold warning, and nothing is presented that the
   master has not reached.
7. A run's drain is raised at most once, only with its input complete, its ring empty and nothing
   in flight, never for a replaced run and never after a halt.
8. A run's target completes exactly once.
9. An underrun is raised at most once until ready is raised, never while paused, before the run's
   first present or after its input completes; ready only after an underrun.
10. After a halt nothing is presented.
11. From every reachable state, a dispose and the end of the present in flight reach Disposed with
    nothing held, nothing armed, and every offer and target complete.
12. A stale wake changes nothing, and no input throws.

### 5. What the open issues get

| Issue | What the core does |
| ----- | ------------------ |
| #746 | The core removes both bounds. The hold has no backstop (rule 4); the target ends with its run (rule 3), so `SubstrateSession.SettleAsync` drops `SeekSettleCap` and waits until the target completes or its command is cancelled or superseded. Invariant 5 and a shell test that holds the reseat past any interval on a `FakeTimeProvider`, as #746 describes, pin it. |
| #671 | Makes the cap a named rule over named inputs (rule 5), and the walk can state the scenario: a master that stops at t with frames due after it. The fix is left to a later decision, open question 1: the per-frame clause changes in the core, or the master keeps advancing after the audio's end, which is the audio sink's phase (#753) or a hand-off of the master in the item runtime. |
| #528 | Makes the pacer's part a one-line rule: a frame of another timeline is refused at admission (rule 1), and invariant 3 holds over every state. A present already under way when the flush comes, and a frame the sink keeps, are outside the pacer and are left to a later decision; ADR-0084's followers are the sink's means. The record does not trace the issue's failure. |
| #127 | The pacer's half holds by construction (rule 7, invariant 6). The audio clock's half is the audio sink's (#753). |
| #195 | Makes the pacer's part a one-line rule: a drain is its run's and is raised once (rule 9, invariant 7). Dropping a stale end of stream in the controller is #752's, a later decision. |
| #680 | Left to a later decision, open question 3. The core knows a pause and the first present after it; telling the sink needs a public surface. |
| #636 | Left to a later decision. Pacing becomes a value a second shell could reuse, which is what a paced branch or a public pacer would need; this record decides neither. |

### 6. Testing

- **A bounded walk over the core**, `PacerProtocolWalkTests`, in the shape of
  `ItemRuntimeProtocolWalkTests`. The walk plays the shell over a script of a few frames per run:
  from each state it sends every input (the next scripted offer, an offer from an older timeline, an
  untagged offer, an offer from a second writer, an abandoned offer, a new run with and without a floor and a hold, a flush, a pause, a resume, the end
  of input, a release for the current and an older run, a dispose, each wake, each present outcome
  including every verdict kind, and a shell fault), each at a set of times: the master unchanged,
  at the next frame's PTS, at the last frame's end, or past it, and the wall unchanged or past the
  next armed deadline. A master stopped short, stalled mid-stream and paused are among the paths.
  The clock and the wall only move forward, except that a new run may reseat the master. States are
  keyed with frame ids and timelines relative to the current ones and the counts left out, so the
  walk is exhaustive over the script. Shapes: capacities 1 and 3, a wallclock-mastered item that
  settles, one that does not, and a sink that fails. It checks section 4 after every step.
- **Transcripts** for the decisions the walk's grid cannot place exactly, such as the hold cap's
  arithmetic, the probe's window and the starvation threshold.
- **Shell tests** on `FakeTimeProvider` and a fake `IClockSource` whose waits the test completes,
  barriered on the reader's idle signal: backpressure on a full ring, a present held inside the sink
  while other inputs are stepped, a stale wake, a clock read that throws, the announcer's
  ownership, and the dispose.
- **What moves.** Of `ClockSelectVideoSinkTests`' 43 tests, the eight on `ClockSelectBuffer` and
  every test of a decision become core transcripts or walk scenarios: selection and late drops, the
  floor, the target, the hold, the drain, the cap, the hold cap and the probe, pause and resume, and
  the lag. `AHeldDestinationFrameIsReleasedEvenIfNobodyReseats` is inverted by rule 4, and
  `WaitForSeekTarget_GivesUpOnTheCapRatherThanThrowing` becomes "completes null when the run
  ends". The dispose test and the backpressure test stay with the shell. The decision tests in
  `ClockSelectVideoSinkStarvationTests` and `ClockSelectVideoSinkFailureTests` move; the run of
  failures across pacers over one sink, the logging, and a fault callback that throws stay with the
  shell, as do `ClockSelectVideoSinkOwnershipTests` and `ClockSelectVideoSinkDisposeTests`.
  `MasterClockStallTests`, `PacerStarvationTests`, `SinkFailureRunTests` and `HoldingTests` stay as
  they are. The integration tests (`SeekPreTargetFramesTests`, `TimelineBreakTests`,
  `SeekClockSettleTests`, `NominalRunHealthTests`, `ElapsedPlaytimeTests`) stay.
- **Mutation.** Each invariant is shown to be load-bearing by reintroducing the defect it names in
  the core: the backstop, a release before the destination frame that leaves the flag set,
  admission without the timeline, a drain with a present in flight, a cap armed while paused, a
  late drop of the newest frame. The walk goes red on each.

### Public surface

No public type changes. `ClockSelectVideoSink`, `ClockSelectBuffer`, `PacerStarvation`,
`SinkFailureRun`, `MasterClockStall` and the new core are internal. What the pacer exposes is
through `PipelineDiagnosticsSnapshot.VideoFramesDroppedForSync` and `VideoPresentationLag`, both in
`FrameFlow.Playback`'s `PublicAPI.Unshipped.txt` (its `PublicAPI.Shipped.txt` is empty), and their
meaning does not change: a frame refused for another timeline is not a sync drop and is not counted
there. The internal `IItemMedia` changes: `PacerRun` and `ReleaseSettle` name a timeline, and
`WaitForSeekTargetAsync` loses its cap.

### Staging

Each slice leaves `main` green and carries its own BREAKING-CHANGES entry where an app can see a
change.

1. **The core, not wired.** `PacerProtocol` with today's rules, the ring and floor of
   `ClockSelectBuffer`, `PacerStarvation`, and `MasterClockStall` moved into
   `FrameFlow.Playback.Core`; its walk and transcripts. The walk records where today's loop differs from its invariants, as
   ADR-0086's slice 3 did.
2. **The shell.** `ClockSelectVideoSink` on the core with one reader. `_gate`, `_space`, `_arrival`,
   `_settled` and `_recheck` go, and the decision tests move. The rules are slice 1's, so nothing an
   app sees changes; the slice measures the reader's cost per frame (open question 4).
3. **Runs are timelines.** `BeginRun` carries the timeline; admission refuses other timelines
   (#528's pacer half); the pacer's run identity is the timeline; the drain is its run's (#195's
   pacer half). Its entry covers a stale frame no longer presented.
4. **The settle without wall bounds (#746).** The backstop becomes a warning, the target ends with
   its run, and `SubstrateSession.SettleAsync` drops `SeekSettleCap`. Its entry covers a seek that
   no longer gives up on a destination frame that does not arrive.
5. **Docs.** Notes on ADR-0057 Stage 2 (its core is now the loop's), ADR-0072 decision 3 (the seam's
   reference is the reader), and ADR-0084 (the pacer's run is the timeline); the remarks the slices
   leave; this record's Implementation section.

An answer to open question 1 that keeps the fix in the pacer adds a slice after slice 2.

## Consequences

### Positive

- Every decision the pacer makes is testable with no clock, thread or sink, and the invariants hold
  over every state the walk reaches, not over the interleavings a test happens to reach.
- The waits become one armed deadline, and the locks the decisions took go. The races the comments
  record cannot be written, because no decision is taken twice.
- The two wall-clock bounds that decided (#746) go, and the remaining timers are named rules with
  their inputs in the step.
- Each frame is accounted for on every path (ADR-0080), and the bound ADR-0081 budgets is checked.
- A later rule, #671's or #680's, is a change to a total function with a walk to check it, not a
  change to a loop with five awaits.

### Negative

- An offer goes through the reader, so every frame costs a hop that `PresentAsync` does not pay today
  when the ring has room. Slice 2 measures it.
- Slice 2 rewrites the shell of a type every video item plays through.
- The walk's clock is a grid. A rule that depends on an exact duration is checked by a transcript.
- A seek on a playing, wallclock-mastered item over a decoder that never reaches its target no
  longer gives up after 5 s: it waits until it is superseded, paused or cancelled, or the item is
  disposed (open question 2).
- More internal types: a state, inputs, actions and a walk.
- The run of sink failures stays outside the core, because it belongs to the sink.

### Neutral

- No public type changes, and the diagnostics keep their meaning.
- `ItemRuntimeProtocol` and `PlaybackProtocol` do not change. The item runtime's shell changes only
  where `SettleAsync` drops its cap.
- The cap stays 5 s, the ring three frames, the probe five slices of 100 ms, and the starvation
  threshold three frame intervals and at least 1 s.

## Alternatives considered

### Keep the loop and extract helpers one by one

This is how the pacer got its four types of its own: `PacerStarvation` for #547, `SinkFailureRun`
for #703, `MasterClockStall` for #359, and `ClockSelectBuffer` before them. Each made one decision
testable and left the composition where it was: which of five awaits runs, what wakes it, what a
recheck means, which flag is read under which lock. That composition is where #746's two bounds sit
in two classes, and it is what the comments listed in Context defend by hand. Each extraction adds a
type and leaves the 42 fields.

### A timer-driven state machine without a core

Make the loop's phases explicit in a mutable machine, a Stateless machine or a loop that
re-evaluates on a fixed tick. A fixed tick is the cadence ADR-0057 removed, because a tick starves
under load, and it quantises every present to its period. A mutable machine in the shell is the
shape #752 records as debt in the controller. Either is still testable only by running it.

### Fold the pacer into the item runtime's core

`ItemRuntimeProtocol` would take every frame's arrival and every clock wake, at the frame rate, into
a core stepped per command, and its walk over five item shapes would multiply by every ring state.
The run of sink failures spans items, which a per-item core cannot hold. The two already meet
through a narrow interface, the item runtime's pacer actions, and each keeps its own walk.

### Step the core under a lock on the caller's thread

Admission would stay synchronous when the ring has room. But actions decided on two threads are then
performed on two threads: a release on the producer's and a present on the loop's can run out of
the order the core decided, and "nothing in flight" for the drain would need the lock across the
present. If slice 2 measures the hop as too costly, this is the fallback for offers alone.

### Keep the settle's bounds and lengthen them

A longer backstop or cap moves the machine that fails, and #746's mechanism stays. The release and
the run's end are the signals the hold and the wait are for.

## Open questions

1. **#671: where does a master that has stopped for good get handled?** In the pacer, the cap's
   verdict could last until the master advances: after one cap, pace on wall time from the forced
   frame until the master moves again. That costs one cap per stop instead of one per frame, and it
   is local and walkable. But the pacer cannot tell a master that ended from one that stalled, so a
   video would run ahead of an audio device that stalls past the cap, and hold when it resumes. In
   the clock, the master keeps advancing after the audio's end, through the audio sink's phase
   (#753) or a hand-off to a wallclock in the item runtime (ADR-0003, applied mid-item). That knows
   the audio ended rather than guessing it, removes the cap wait entirely, and needs no change to
   the core. The record leans to the clock.
2. **A seek over a stalled decoder.** With the settle's cap gone, such a seek waits. Should an empty
   ring after a floor be timed as starvation, so the controller reports `Rebuffering`, or is a seek
   that waits the honest report?
3. **#680: should the pacer tell the sink about a pause?** It is the one place that knows both the
   pause and the first present after it. Telling the sink needs a member on `IVideoSink` or a flag
   on what it is handed, which is public surface.
4. **The reader's cost.** If one hop per frame measures as too much, are offers stepped under a lock
   on the producer's thread, with every other input on the reader?

## Revision history

- **2026-10-15, proposed.**
- **2026-10-15, review turn 1.** The offer path was unbounded as written: the record said nothing
  of how many offers can wait, so the capacity-plus-one bound did not follow. Rule 1 now admits one
  waiting offer, names the pacer's one producer and where ADR-0081 counts its call, and refuses a
  second writer; decision 2 says the channel is unbounded only for control inputs; invariant 2
  counts the waiting offer. `ShellFaulted` carries the last reading, since a clock read that throws
  is one of its causes. Invariant 3 claimed more than rule 1 gives for a frame with no timeline; it
  now states that limit, and rule 1 counts such frames and says why they are not refused.
- **2026-10-15, review turn 2.** Removing the backstop exposed a hole it was covering: today a
  release that comes before the destination frame, because the settle was cancelled or reached no
  target, clears the hold but not the run's hold-for-settle, so the frame closes the hold again and
  only the backstop opens it. Rule 4 now has a release end the settle for its run whenever it
  comes, invariant 5 holds in either order, and the mutation list covers it. `ShellFaulted` now
  carries no reading at all, since a clock that throws may never have given one.
