# ADR-XXXX: Looping on both players: what the repeat modes mean, who loops, and how a loop is reported

## Status

Proposed (2026-09-14). Draft pending number assignment. Revised the same day after an independent
review; *Revision history* says what changed. **Nothing here is implemented.**

This record supersedes [ADR-0021](ADR-0021-looped-playback-strategy.md). It decides:
- what each `RepeatMode` means on a single-source player and on a playlist player;
- which layer decides that an item repeats;
- how both players rewind;
- how a loop is reported, and when the loop-stall watchdog watches it.

It answers three of the questions that
[End of queue, replay and faults on the playlist player](playlist-end-of-queue-replay-and-faults.md)
says must have recorded answers before a single player type is proposed: `RepeatMode.All` on one
item, loop ownership, and loop reporting. It settles #172, and the reporting half of #173.

Line numbers cite commit f4aa57a.

Related: [ADR-0021](ADR-0021-looped-playback-strategy.md),
[ADR-0027](ADR-0027-public-api-surface-cleanup.md),
[ADR-0028](ADR-0028-internal-layering-and-ownership-cleanup.md),
[ADR-0062](ADR-0062-gapless-playlist-warm-presenter.md),
[ADR-0063](ADR-0063-nv12-pixel-shader-color-conversion.md),
[ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md),
[The playlist player's queue](playlist-queue-model.md),
[The playlist session as a pure protocol](playlist-session-protocol.md). Issues #172, #173.

## Context

### What the existing records decided

- **ADR-0021** put looping in `PlaybackSession`, as a seek to zero when the stream ended.
  - It configured loops through `FrameFlowPlaybackOptions.Loop` and `MaxLoopCount`, exposed
    `CurrentLoopIteration`, and added `SetLoop`.
  - Its status is still *Proposed*.
  - None of those members exists today, and neither does `PlaybackSession` or
    `WatchForEndOfStreamAsync`.
- **ADR-0027** superseded ADR-0021 §1 with the runtime `RepeatMode` state machine. Its §3 removed
  `RepeatMode.All`, because it behaved exactly like `One`, and said that when playlist support
  arrived "it will have distinct, testable behavior" (`ADR-0027:109`). ADR-0027's own status is
  also *Proposed*.
- **ADR-0062** §5 brought `All` back for the playlist player. Its implementation notes say `All` on
  a single source behaves like `Off` (`ADR-0062:584-586`). Its 2026-06-21 update moved the playlist's
  single-clip loop to the in-place rewind.
- **ADR-0028** §2 routed the single-source loop rewind through the controller's seek state machine,
  so a user seek can cancel it.

### How each player loops today

A single-source player loops in the controller. On `LastFrameRendered` under `RepeatMode.One`, the
controller increments its loop count and raises `LoopRestarted`. It then starts a seek to zero
through its seek state machine (`src/FrameFlow.Playback/PlaybackControllerCore.cs:1051-1062`).

A playlist player loops in its session. `PlaylistCoordinator.DecideNext` replays the current item
under `One` (`src/FrameFlow.Playback/PlaylistCoordinator.cs:506`). It also wraps to the first item
under `All` (`:525-526`), which is the same item in a playlist of one. `PlaylistSession` rewinds that
item in place (`src/FrameFlow.Playback/PlaylistSession.cs:939-961`). Since #197 the controller never
loops a playlist.

The probes were scratch integration tests over `PlaybackController.Create` and
`PlaybackController.CreatePlaylist`. They used the 3-second video-only clip, software decode and a
counting video sink. The independent review reproduced all seven.

| # | Player and mode | Result |
|---|---|---|
| 1 | Single source, `One` | `LoopRestarted` on each pass, with counts 1 and 2; the state stays `Playing`. |
| 2 | Single source, `All` | One pass of 72 frames, then `Ended`. No `LoopRestarted`. |
| 3 | Playlist of one, `All` | `SourceTransitioned` on each pass, with `Wrapped` true. No `LoopRestarted`. |
| 4 | Playlist of one, `One` | `SourceTransitioned` on each pass, with `Wrapped` false. No `LoopRestarted`. |
| 5 | Playlist of two, `All` | `Wrapped` true at the wrap. No `LoopRestarted`. |
| 6 | Playlist of one, `All`, skip at frame 10 | The skip's transition has `Wrapped` true. |
| 7 | Playlist of one, `All`, a fault on the first pass | One `ErrorOccurred`, and the rebuild's transition has `Wrapped` true. |

### What the two rewinds differ in

The single-source loop calls `SubstrateSession.SeekAsync(0)`. The playlist loop calls
`SubstrateSession.RewindToStartAsync`. Both run the same reposition recipe. They differ in three
ways:

- **Stopping the run.** The seek cancels the session's token and waits; the rewind waits for tasks
  that have already finished (`src/FrameFlow.Playback/SubstrateSession.cs:1113-1125`).
- **The graph.** The seek builds a new graph; the rewind re-runs the retained one. The video
  configurator therefore runs on every pass of a single-source loop, and once per load on a
  playlist loop.
- **The seek state machine.** It runs for the single source (ADR-0028 §2), not for the playlist.

Neither rewind replaces the decode device. The device is created when `VideoDecoder.Open` runs, inside
`InitializeAsync`. A reposition only flushes the decoder. The review measured this with hardware
decode:
- **A single source under `One`:** one device served every frame of two full-seek loops.
- **A playlist of two items:** three items used three devices.

The review also measured the gap at five loop boundaries on a 0.5-second clip:

| Path | Hardware decode | Software decode |
|---|---|---|
| Single source, full seek | 9–30 ms | 0.7–0.9 ms |
| Playlist of one, in-place rewind | 8–31 ms | 0.6–0.8 ms |
| Playlist of two, item rebuild | 193–198 ms | 3.0–3.8 ms |

### Where the players disagree

- **`RepeatMode.All` on one item.** A single source ends (probe 2), as the enum's summary says
  (`src/FrameFlow.Media/RepeatMode.cs:17-24`). A playlist of one loops (probe 3), and that is
  `MediaPlaylistPlayer.CreateAsync`'s default mode. The README's quick start, and the examples on
  `FrameFlowPlayer` and `IMediaPlayerBuilder`, pass `RepeatMode.All` to a single-file player
  (`README.md:57`, `src/FrameFlow.Player/FrameFlowPlayer.cs:32`,
  `src/FrameFlow.Player/IMediaPlayerBuilder.cs:37`), which then stops at the end.
- **The revert (#172).** A comment records why the single-source loop uses the full seek. The cheap
  rewind was reverted on 2026-06-12, because the decode device it kept across loops was suspected
  of causing a present stall, and "a full seek hands out a clean device each loop"
  (`PlaybackControllerCore.cs:564-572`). On this tree the full seek keeps the same device, so it does
  not do what the comment relies on. The hang ADR-0063 later root-caused was two concurrent
  `VideoProcessorBlt` calls in the converter, and ADR-0063 replaced that call with a pixel shader.
  ADR-0064 gave the converter a device of its own. Both are accepted, and both still list a final
  validation on the hardware where the hang reproduced.
- **A loop that ends while paused.** `Paused × LastFrameRendered` is dropped under `RepeatOne`
  (`src/FrameFlow.Playback/PlaybackProtocol.cs:316`). The review held a single source's end-of-stream
  until after a pause. Under `One`, the player stayed `Paused`. On Play it reported `Playing` with its
  frames stuck at 72, raised no `LoopRestarted`, and raised `LoopStalled` at 5.23 s into a 3-second
  clip. Under `All` it ended, and Play replayed it. The playlist session rebuilds the item in the
  same race.
- **How a loop is reported (#173).** A single source raises `LoopRestarted` and no transition. A
  playlist raises `SourceTransitioned` and no `LoopRestarted` (probes 3 to 5). `Wrapped` is set the
  same way after a pass that played to its end, a skip, and a rebuild after a fault (probes 3, 6
  and 7).
- **Where a loop is visible.** `LoopRestarted` is on `IPlaybackController` only
  (`src/FrameFlow.Playback/IPlaybackController.cs:103`). `IMediaPlayer` forwards `LoopStalled` but
  not `LoopRestarted` (`src/FrameFlow.Player/IMediaPlayer.cs:107`), so a caller of
  `MediaPlayer.CreateAsync` or the builder has no loop event.
- **Which loops are watched.** The loop-stall watchdog is eligible only while the controller's mode
  is `One` (`PlaybackControllerCore.cs:213`, `src/FrameFlow.Playback/LoopStallEvaluator.cs:112-116`).
  A playlist of one under `All` loops through the same in-place rewind as under `One`, and is not
  watched.

## Decision

### 1. The repeat modes describe the queue

- **`Off`** plays the queue through once, then ends.
- **`One`** repeats the current item until the caller moves on, as the queue record's decision 3
  says.
- **`All`** repeats the queue. A queue of one item repeats that item.

A single-source player is a queue of one, so under `All` it loops. Today it ends (probe 2). The
controller gives its protocol `RepeatOne` true for `One`, and for `All` when its session does not
loop internally.

`One` and `All` behave the same on a queue of one item, and ADR-0027 §3 removed `All` for exactly
that reason. This record accepts it. `All` differs from `One` on every queue of two or more items,
which is the distinct behaviour §3 asked for. It differs nowhere else, and a single player type makes
every player a queue. Until then, a host with a three-state repeat control on a single-source player
shows two states that behave the same. It can hide `All` there.

### 2. The queue decides that an item repeats

On a playlist player the coordinator decides, and the session performs the repeat. That is already
so. On a single-source player the controller's repeat region decides, until a single source runs on
the playlist session. A single player type removes the controller's loop instead of extending it.

This record adds no second loop mechanism to the controller. It changes the controller's protocol
input (decision 1), one protocol cell (decision 3), the rewind it calls (decision 4), and what it
publishes and watches (decisions 5 and 6).

### 3. A loop that ends while paused rewinds and stays paused

Under a repeating mode, `Paused × LastFrameRendered` becomes an internal transition that runs the
loop rewind. The item is rewound while paused, the state stays `Paused`, and the next Play starts
it from zero. `SubstrateSession`'s reposition already handles a rewind while paused: it re-arms
audio paused and leaves the relaunch to the next Play (`SubstrateSession.cs:1216-1227`). Outside a
repeating mode the cell keeps ending the player, as #194 decided.

This fixes the stuck `Playing` state the review reproduced under `One`, and keeps a single source
under `All` from inheriting it. It matches what the playlist session does in the same race.

### 4. Both players rewind in place

The single-source loop calls `RewindToStartAsync` instead of `SeekAsync(0)`, still through the seek
state machine that ADR-0028 §2 set up. The revert comment is deleted.

- **The revert's reason does not hold.** A full seek keeps the same decode device, so it hands out no
  clean one.
- **The measurements favour the rewind.** Loop gaps on the two paths measured the same, and the
  rewind skips a graph rebuild on every pass.
- **Its successors changed the suspected cause.** The concurrent-`Blt` hang ADR-0063 found is gone
  from the converter.

The on-hardware validation that ADR-0063 and ADR-0064 list is still to be run. *Validation* adds a
loop soak to it. That is a regression check, not a gate: if it stalls, #172 reopens with evidence,
and the revert can be restored for the single source.

### 5. A loop is reported the same way on both players

- **What a loop is.** `LoopRestarted` means the current item repeats after it played to its end. Both
  players raise it: under `One`, and under `All` when the queue holds only that item.
- **What is not a loop.** It does not fire for a skip, a jump, or a rebuild after a failure. It does
  not fire when a different item of the same source follows, such as a back-to-back duplicate.
- **When it fires.** It fires when the item has been put back at its start, whether the player is
  playing or paused. It does not wait for a frame to be presented. A consumer that needs to know
  playback has resumed watches the state and the position.
  - **A single source** fires it when the loop rewind's seek outcome succeeds, not when the rewind
    is requested. A loop rewind cancelled by a user seek raises none.
  - **A playlist** fires it when the in-place rewind returns. If the repeat rebuilds the item, while
    paused or after a failed rewind, it fires once the rebuilt item is open and warmed, and has
    started if the player is playing.
  - **A failed repeat** that cannot be opened or started is a failed start, reported as one, and
    raises none.
- **`LoopCount`.** It counts consecutive loops of the current item: the first loop is 1.
  - Anything that is not a loop resets it: a load, a skip, a jump, a rebuild after a failure, or a
    hand-off to another item.
  - A user seek does not reset it.
  - Today the controller's count never resets.
- **Who counts.** A single source's controller counts. A playlist session counts under its gate and
  passes the count in a new internal session callback. The controller only publishes it, so the
  controller still does not track playlist items.
- **Loops, skips and jumps are ordered by the session's gate.** A playlist reports a loop from inside
  the advance that performed it, before the gate is released. A skip, jump or removal that arrives
  meanwhile waits for the gate, so its effect comes after the loop, including its reset of the
  count. A jump recorded during the rewind is taken as soon as the item has started again, as the
  queue record's decision 5 says, so the loop is reported and then the jump moves on.
- **Loop reports travel in order.** The controller receives them through its channel in the order
  the session made them, carrying the session generation. It drops a report from a session it has
  replaced, as it drops that session's other notifications.
- **`IMediaPlayer` gains `LoopRestarted`,** forwarded from the controller by both player
  implementations.
- **`SourceTransitioned` keeps firing on a loop,** and `Wrapped` keeps its meaning: the cursor went
  back to the first playlist item, for any reason. The docs on `SourceTransitioned` and
  `PlaylistTransition.Wrapped` say both, which is the documentation fix #173 asks for.
- **No order is promised between `LoopRestarted` and `SourceTransitioned` for the same loop.** The
  first is raised on the controller's dispatch loop, and the second on the session's advance.

### 6. The watchdog watches every expected loop

The watchdog is eligible on a tick when the player expects the current item to loop at its end.
Today the controller computes `RepeatOne` from its own mode. That input becomes `ExpectsRepeat`,
which the controller computes for a single source and the session answers for a playlist:

- **A single source** expects a repeat under `One` or `All`.
- **A playlist** expects one when the current item has started, it has not been removed, and either:
  - the mode is `One`; or
  - the mode is `All`, the current item is the only playlist item, and nothing is set next or
    queued.

The answer is read on every tick, not held across the end of the item.

- **A repeat stays eligible while it runs.** A repeat of the same item, by in-place rewind or by
  rebuild, does not make the item unstarted, so a rewind that hangs is still watched. Today the
  coordinator's take marks every taken item unstarted, including the same item taken again at a
  wrap; that changes for a same-item take.
- **A hand-off to a different item is not eligible.** Taking a different item makes the current
  item one that has not started, so a slow hand-off does not count as an overrun, such as one
  waiting on a `SourceTransitioned` subscriber.
- **What closes an overrun on a healthy loop** is the rewind putting the position back to zero. The
  loop-count gate is a second guard.

This amends #197's note that the watchdog still reads the controller's own mode.

### 7. What this supersedes in ADR-0021

| ADR-0021 | Now |
|---|---|
| §1 `Loop` and `MaxLoopCount` options | `RepeatMode`, as ADR-0027 §2 decided. No loop limit; a caller counts `LoopRestarted` and changes the mode. |
| §2 looping in `PlaybackSession.WatchForEndOfStreamAsync` | Decision 2 |
| §3 `CurrentLoopIteration` | `LoopRestarted.LoopCount`, decision 5 |
| §4 `SetLoop` | `SetRepeatModeAsync` |
| §4 a loop setting changed mid-restart | A mode change takes effect at the next end of the item. A rewind already requested completes. |
| §4 a loop setting changed after `Ended` | No effect until Play or a seek out of `Ended`. Play replays under the new mode. |
| §5 no `Looping` state | Kept: a loop never leaves `Playing`, or `Paused` under decision 3. |
| The gap between iterations | The in-place rewind (decision 4). Gapless audio across a loop is not claimed. |
| Compliance checklist | Replaced by *Validation*. |

ADR-0021's status becomes *Superseded* when this record is accepted.

## Consequences

### Positive

- **One meaning per mode.** Each repeat mode means the same thing on both players, and the README's
  quick start loops, as it reads. Faults are the exception: under `One` a playlist replays a
  faulted item and counts the failure, while a single source enters `Error`.
- **Loops are observable.** They are visible from `IMediaPlayer`, with the same event, timing and
  count on both players.
- **A frozen loop is fixed.** A single source paused as its loop ends no longer freezes in `Playing`.
- **Every expected loop is watched.**
- **The single-source loop is cheaper.** It stops rebuilding its graph on every pass.

### Negative

These go in `docs/BREAKING-CHANGES.md` when the record is implemented. Only the new member and the
renamed property are compile-visible.

- **A single-source player under `All` loops instead of ending.** A caller that relied on `All`
  acting as `Off` must pass `Off`.
- **The video configurator runs once per load on a single source,** not once per loop. A stateful
  operator in a consumer's chain keeps its state across loops, and a configurator-only chain that
  wires its own sinks is not rewired each pass.
- **`LoopRestarted` moves.** It fires after the item starts again, not when the rewind is requested,
  and a rewind cancelled by a user seek raises none.
- **`LoopRestarted.LoopCount` resets** on a load and on any non-loop change of item.
- **`IMediaPlayer` gains `LoopRestarted`.** An implementing type stops compiling until it adds it. In
  this repository that includes the double in
  `tests/FrameFlow.Avalonia.Tests/FrameFlowVolumeControlTests.cs:143`. An implementing assembly that
  is not rebuilt fails to load.
- **`LoopStallSample.RepeatOne` is renamed `ExpectsRepeat`.** The type is public, so this is a
  source break for code that builds samples.

### Neutral

- The controller still owns single-source looping until a single player type.

## Alternatives considered

### A. `All` on one item ends

This would keep the single-source behaviour and change the playlist of one. Rejected. A playlist of
one under `All` is `MediaPlaylistPlayer`'s default, and it is the single-clip loop that ADR-0062's
update built and measured.

### B. Report loops only through `SourceTransitioned`

Rejected. A single-source player has no transitions. `SourceTransitioned` also fires for skips,
jumps and rebuilds, so it cannot say that a loop happened.

### C. Keep the full seek for the single source

Rejected. Its recorded reason is refuted on this tree. It rebuilds the graph and reruns the
configurator on every pass, with no measured benefit at the loop boundary.

### D. Move both players to the full seek

Rejected. It adds a graph rebuild to every playlist loop, and the stall it was meant to prevent has
no evidence against the rewind.

### E. Wait for the hardware run before moving the single source

Rejected. It was the first draft of decision 4. It made a rewind wait on a validation whose premise,
a device refreshed by the full seek, is false on this tree.

### F. Amend ADR-0021

Rejected. Every section of it describes members and a class that no longer exist.

## Not settled here

- **Why a transition happened.** `PlaylistTransition` does not carry a natural end, skip, jump or
  failure (#173). `LoopRestarted` answers the question for loops.
- **Pairing a loop with its transition.** `LoopRestarted` lives in `FrameFlow.Media` and carries no
  item or transition index, and no order between the two events is promised.
- **A stall at a hand-off to a different item.** The watchdog covers expected loops only.
- **A stall on an audio-mastered clock.** The evaluator targets the wall-clock case, where the
  position overruns the duration. An audio-mastered clock stops at the duration instead.
- **Repeat on `BuildAsync`.** The play-to-the-end `PlayerSession` has no repeat mode. Once a chain
  sets one, the builder no longer offers `BuildAsync`.

## Implementation touches

Besides the code, implementing this record changes:
- **`FrameFlow.Media` docs:** the summaries of `RepeatMode.All`, `LoopRestarted`, `LoopStalled` and
  `LoopStallEvaluator`.
- **`FrameFlow.Playback` docs:** the summaries of `IPlaybackController.LoopStalled` and
  `IPlaybackController.LoopRestarted`.
- **The API baseline:** a `FrameFlow.Player` `PublicAPI` entry for `IMediaPlayer.LoopRestarted`.
- **The playback pattern docs:** rows 12 to 14 of `docs/patterns/playback-states.md` and its
  repeat-mode notes, `docs/patterns/playback-statechart.md`, and
  `docs/patterns/playback-controller.md`.
- **ADR-0062's implementation note** that `All` on a single source behaves like `Off`.
- **The queue record's** *Not settled* **entry** on `All` for a playlist of one.
- **The test bench's `repeat all`** on a single source.
- **The summary of `LoopRestartTests`,** which describes a fresh graph per loop.

## Validation

Write each test first and confirm it fails on the tree before this record lands, for the reason
given.

Unit tests without media:

| # | Decision | Test | Today |
|---|---|---|---|
| 1 | 1 | `PlaybackDispatchProtocolTests`: a single-source session under `All` reports end-of-stream; the controller runs the loop rewind and stays `Playing`. | [`Ended`] |
| 2 | 3 | `PlaybackProtocolTests` and `PlaybackDispatchProtocolTests`: under `One`, and under `All` for a single source, end-of-stream while `Paused` runs the loop rewind and stays `Paused`. | [dropped under `One`; `Ended` under `All`] |
| 3 | 5 | `PlaybackDispatchProtocolTests`: a loop rewind cancelled by a seek raises no `LoopRestarted`; a load resets `LoopCount`. | [raised on request; never reset] |
| 4 | 6 | `PlaylistCoordinatorTests`: `ExpectsRepeat` is true for a started, unremoved current item under `One`, and under `All` as the only playlist item with nothing next or queued, including after that item is taken again at a wrap; false for a one-shot current item, a removed current item, a playlist of two under `All`, and a different item taken and not yet started. | [no member] |
| 5 | 6 | `LoopStallEvaluatorTests`: the renamed input gates eligibility as `RepeatOne` did. | [renamed] |

Integration tests over real playback, in `FrameFlow.Integration.Tests`:

| # | Decision | Test | Today |
|---|---|---|---|
| 6 | 1, 5 | Single source under `All`: two loops, with `LoopRestarted` counts 1 and 2. | [one pass, then `Ended`] |
| 7 | 3 | Single source under `One`, end-of-stream held until after a pause: `Paused`, then Play presents from zero and raises `LoopRestarted`. | [`Playing` with frames stuck; `LoopStalled`] |
| 8 | 4 | Single source under `One`: the video configurator runs once across two loops. | [three times] |
| 9 | 5 | Playlist of one under `All`: `LoopRestarted` on each loop, with counts 1 and 2, and `SourceTransitioned` with `Wrapped` true as before. | [no `LoopRestarted`] |
| 10 | 5 | Playlist of one under `One`: `LoopRestarted` on each loop, including a loop rebuilt while paused. | [no `LoopRestarted`] |
| 11 | 5 | Playlist `[a, a]` of one source object, and a playlist of two, under `All`: no `LoopRestarted` at either hand-off. | [passes] |
| 12 | 5 | Playlist of one under `All`: a skip, and a rebuild after a fault, raise no `LoopRestarted`, and the next loop's count is 1. | [no `LoopRestarted`] |
| 13 | 5 | Playlist of two under `One`: the first item loops twice, then a jump to the second; the second item's first loop has `LoopCount` 1. | [no `LoopRestarted`] |
| 14 | 5 | `MediaPlayer` and `MediaPlaylistPlayer`: `IMediaPlayer.LoopRestarted` fires with the controller's event. | [no member] |

Test 11 guards today's behaviour against a rule that reported every same-source hand-off.

**The loop soak.** It is added to the on-hardware validation ADR-0063 and ADR-0064 list, and is not a
gate. Both players loop a clip at the same time, with hardware decode and the GPU presenter each,
because two presenters on one GPU was the condition of the hang ADR-0063 found. Each player's
presented-frame counter must not fall below 90 percent of the clip's frame rate over any one-minute
window, for at least an hour. A stall reopens #172, and the result is recorded in this record's
revision history without machine identifiers.

## Revision history

- **First draft (2026-09-14).** It made the single-source rewind wait on a hardware run. It kept the
  full seek until then, and it withdrew ADR-0062's single-clip loop if the run stalled.
- **Revision after independent review (2026-09-14).** The review reproduced probes 1 to 7 and ran
  further probes:
  - **The decode device.** The full seek does not create a new decode device: one device served every
    loop. Loop gaps measured the same on both paths. The revert's recorded reason is therefore
    refuted, and decision 4 moves the single source to the rewind now. The hardware run became a
    regression check (alternative E).
  - **A loop that ends while paused.** It left a single source `Playing` with its frames stuck under
    `One`, and decision 1 would have spread that to `All`. Decision 3 now rewinds while paused.
  - **`LoopCount`.** The per-item count had no mechanism the controller could run without tracking
    items. The session now counts for a playlist, and decision 5 defines what resets the count and
    when the event fires.
  - **The watchdog's predicate.** It was wrong for a one-shot current item and a removed one, and
    ignored a hand-off in flight. Decision 6 now states it per tick.
  - **Smaller corrections.**
    - Decision 1 now answers ADR-0027 §3.
    - Status no longer contradicts #172.
    - Decision 2 no longer claims to add no mechanism.
    - The configurator change and the renamed sample property are listed.
    - *Implementation touches* lists the docs and baselines.
    - The ADR-0027 quotation is exact.
    - ADR-0021's edge cases are mapped.
    - The Validation rows gained the pause case, duplicates, a reset after a real loop, and the
      predicate's negative cases.
- **Revision after automated review of #201 (2026-09-14).** Three changes:
  - **Timing.** `LoopRestarted` is defined as the item being put back at its start, playing or
    paused, not as a frame presented. The first revision said "started again", which did not fit a
    loop that ends while paused.
  - **Races.** Decision 5 states how a loop, a skip and a jump are ordered by the session's gate, and
    that loop reports travel in order with the session generation.
  - **The watchdog.** Decision 6 dropped "no advance in flight", which would have left a playlist of
    one blind during its own rewind. A same-item repeat now keeps the item started, and only a
    different item taken and not yet started is ineligible.
