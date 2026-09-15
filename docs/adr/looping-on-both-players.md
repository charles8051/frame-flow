# ADR-XXXX: Looping on both players: what the repeat modes mean, who loops, and how a loop is reported

## Status

Proposed (2026-09-14). Draft pending number assignment. Revised the same day after an independent
review, and on 2026-09-15 onto the playlist session protocol; *Revision history* says what changed.
**The playlist half is implemented** (decisions 5 and 6 on a playlist); *As implemented* says how.
Nothing for a single source is.

This record supersedes [ADR-0021](ADR-0021-looped-playback-strategy.md). It decides:
- what each `RepeatMode` means on a single-source player and on a playlist player;
- which layer decides that an item repeats;
- how both players rewind;
- how a loop is reported, and when the loop-stall watchdog watches it.

It answers three of the questions that
[End of queue, replay and faults on the playlist player](playlist-end-of-queue-replay-and-faults.md)
says must have recorded answers before a single player type is proposed: `RepeatMode.All` on one
item, loop ownership, and loop reporting. It settles #172, and the reporting half of #173.

The playlist half is a change to the playlist session's protocol core. A single source gets the same
behaviour by running as a queue of one on the playlist session, which a separate record decides
(decision 2). If that record is rejected, decision 8 changes the controller instead.

Line numbers cite commit 930a84e. The probes and measurements in *Context* ran on f4aa57a, before
the playlist session became a protocol. None of the five behaviour differences that record lists
touches a loop.

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
through its seek state machine (`src/FrameFlow.Playback/PlaybackControllerCore.cs:1050-1062`).

A playlist player loops in its session. `PlaylistQueue.DecideNext` replays the current item under
`One` (`src/FrameFlow.Playback/PlaylistQueue.cs:316-317`). It also wraps to the first item under
`All` (`:333`), which is the same item in a playlist of one. When the player is playing and the item
has played, the session's protocol rewinds that item in place
(`src/FrameFlow.Playback/PlaylistSessionProtocol.cs:740-752`); otherwise it rebuilds it. Since #197
the controller never loops a playlist.

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
  that have already finished (`src/FrameFlow.Playback/SubstrateSession.cs:1104-1125`).
- **The graph.** The seek builds a new graph; the rewind re-runs the retained one. The video
  configurator therefore runs on every pass of a single-source loop, and once per load on a
  playlist loop. Operators in a retained graph keep their state across the rewind: the seek reset
  covers only the decoders and the demux pipeline (`SubstrateSession.cs:565-572`).
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
  (`PlaybackControllerCore.cs:563-573`). On this tree the full seek keeps the same device, so it does
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
  is `One` (`PlaybackControllerCore.cs:212`, `src/FrameFlow.Playback/LoopStallEvaluator.cs:112-116`).
  A playlist of one under `All` loops through the same in-place rewind as under `One`, and is not
  watched.

### What the playlist session protocol changed

The first two drafts were written against `PlaylistSession` and `PlaylistCoordinator`, before
[The playlist session as a pure protocol](playlist-session-protocol.md) landed (#204, #208, #209 and
#210). Three things they relied on have moved:

- **Ordering.** The session's gate is gone. Its shell steps one input at a time, and an advance runs
  from its take to its settled item, including a jump taken on the way, before the next input.
- **The queue.** `PlaylistQueue` is an immutable value that the coordinator holds. The predicate
  decision 6 needs is a property of that value.
- **Run numbers.** Every item runtime numbers its runs, and the core drops an end-of-stream from a
  run that a seek or rewind replaced. The controller has no run numbers, which is #195.

The playlist session is now the one place where a repeat is decided, performed and tested with
transcripts, tables and an explorer. What decisions 1, 3 and 4 of the first two drafts added to the
controller's loop, that session already does for a queue of one.

## Decision

### 1. The repeat modes describe the queue

- **`Off`** plays the queue through once, then ends.
- **`One`** repeats the current item until the caller moves on, as the queue record's decision 3
  says.
- **`All`** repeats the queue. A queue of one item repeats that item.

A single-source player is a queue of one, so under `All` it loops. Today it ends (probe 2). It gets
this by running as a queue of one on the playlist session (decision 2), or through the controller's
protocol input under decision 8.

`One` and `All` behave the same on a queue of one item, and ADR-0027 §3 removed `All` for exactly
that reason. This record accepts it. `All` differs from `One` on every queue of two or more items,
which is the distinct behaviour §3 asked for. It differs nowhere else, and a single player type makes
every player a queue. Until then, a host with a three-state repeat control on a single-source player
shows two states that behave the same. It can hide `All` there.

### 2. The queue decides that an item repeats

The queue decides, and the playlist session's protocol performs the repeat. On a playlist player
that is already so.

A single-source player gets the same by running as a queue of one on the playlist session. Its own
record decides that change, after a spike and the conditions the end-of-queue record lists for a
single player type. That change removes the controller's loop instead of extending it.

This record therefore adds nothing to the controller's loop. It changes the playlist session's
protocol and queue, what the controller publishes (decision 5), and what the watchdog reads
(decision 6). Decision 8 lists the controller changes to make instead, if a single source does not
come to run as a queue of one.

### 3. A loop that ends while paused puts the item back at its start and stays paused

The item returns to its start while the player is paused, the state stays `Paused`, and the next
Play starts it from zero. Outside a repeating mode the player ends, as #194 decided.

The playlist session does this today. An in-place rewind needs a playing player, so an end-of-stream
while paused rebuilds the item instead. The session opens and warms the new runtime and leaves it
waiting for Play (`PlaylistSessionProtocol.cs:724-773`, `:908-935`). A queue of one does the same.

A single-source player on the controller does not. Under `One` the review left it `Playing` with its
frames stuck, and decision 1 would spread that to `All`. Decision 8 fixes it on the controller, if a
single source does not run as a queue of one.

### 4. Both players rewind in place while playing

A repeat while the player is playing rewinds the retained runtime in place, through
`RewindToStartAsync`, instead of rebuilding or seeking it. If the rewind fails, the item is rebuilt.
A repeat while paused rebuilds, as decision 3 says.

The playlist session does this today (`PlaylistSessionProtocol.cs:740-752`, `:775-803`). A queue of
one does the same, so a single source stops using the full seek when it runs as one. The revert
comment goes with the controller's loop. If a single source does not run as a queue of one, decision
8 moves the controller's loop to the rewind.

- **The revert's reason does not hold.** A full seek keeps the same decode device, so it hands out no
  clean one.
- **The measurements favour the rewind.** Loop gaps on the two paths measured the same, and the
  rewind skips a graph rebuild on every pass.
- **Its successors changed the suspected cause.** The concurrent-`Blt` hang ADR-0063 found is gone
  from the converter.

A rewind that hangs is not rebuilt: the rebuild follows only a rewind that fails. Decision 6 keeps a
hanging repeat eligible, so the loop-stall watchdog reports it as `LoopStalled`, and the presenter
stall watchdog reports a wedged presenter. Recovering from the hang is left to the host, as it is on
a playlist today. *Not settled here* records it.

The on-hardware validation that ADR-0063 and ADR-0064 list is still to be run. *Validation* adds a
loop soak to it. For a playlist, which has rewound in place since ADR-0062's 2026-06-21 update, the
soak is a regression check, not a gate: if it stalls, #172 reopens with evidence. A single source
moves to the rewind only when it runs as a queue of one, and the end-of-queue record makes a hardware
run before and after that change one of its conditions. Decision 8 alone would move it without that
run, for the reasons in alternative E.

### 5. A loop is reported the same way on both players

- **What a loop is.** `LoopRestarted` means the current item repeats after it played to its end. Both
  players raise it: under `One`, and under `All` when the queue holds only that item.
- **What is not a loop.** It does not fire for a skip, a jump, or a rebuild after a failure. It does
  not fire when a different item of the same source follows, such as a back-to-back duplicate.
- **Which advance is a loop.** An advance is a loop when it began with an end-of-stream from the
  current run of an item that has played, and the item its decision names is the current
  `PlaylistItem` itself. The test is on the decision's item, not on whether the queue took one:
  - Under `One` it is `DecideNext`'s replay of the current item, which takes nothing.
  - Under `All` it is a wrap that takes the only playlist item again.
  - A different item of the same source is a replay decision, but not a loop.
  - An end-of-stream latched before the first Play is not a loop, because the item has not played.
  - The protocol records the answer on the advance when it decides, so the steps that complete the
    advance do not work it out again.
- **When it fires.** It fires when the item has been put back at its start, whether the player is
  playing or paused. It does not wait for a frame to be presented. A consumer that needs to know
  playback has resumed watches the state and the position.
  - **In place.** When the rewind's outcome is `Ok` (`PlaylistSessionProtocol.cs:775-803`). If the
    rewind fails, the repeat falls back to a rebuild, and the rebuild's rule applies.
  - **Rebuilt while playing.** When the rebuilt item's Play outcome is `Ok` (`:937-956`).
  - **Rebuilt while paused.** When the rebuilt item's warm-up outcome is `Ok` (`:908-935`). The item
    then waits at its start for Play, and that Play reports no second loop.
  - **A failed repeat** that cannot be opened, warmed or started is a failed start, reported as one,
    and reports no loop.
  - **During disposal, or after the session has given up,** no loop is reported, as no other report
    is.
  - **A single source** fires it at the same points when it runs as a queue of one. On the
    controller, decision 8 says when it fires.

  At each of these points the item's position clock reads zero, or later if the player is playing.
- **`LoopCount`.** It counts consecutive loops of the current item: the first loop is 1.
  - Anything that is not a loop resets it: a load, a skip, a jump, a rebuild after a failure, or a
    hand-off to another item.
  - A user seek does not reset it.
  - Today the controller's count never resets.
- **Who counts.** The playlist session's protocol counts, in its state. It reports each loop with a
  new report action, which the shell passes to the controller through a new internal session
  callback. The controller only publishes it, so it still does not track playlist items. On the
  controller, decision 8 counts.
- **Loops, skips and jumps are ordered by the session's inputs.** The shell steps one input at a
  time, and a loop is reported by a step of the end-of-stream input that performed it.
  - A skip requested meanwhile is a later input, so its effect comes after the loop, including its
    reset of the count.
  - A jump recorded on the queue during the rewind is taken by the same input once the item has
    started again, as the queue record's decision 5 says. The loop is reported, and then the jump
    moves on.
  - A removal edits the queue. It does not undo a repeat already under way, and the next end of the
    item moves on.
- **Loop reports travel in order.** The controller receives them through its channel in the order
  the session made them, carrying the session generation. It drops a report from a session it has
  replaced, as it drops that session's other notifications.
- **`IMediaPlayer` gains `LoopRestarted`,** forwarded from the controller by both player
  implementations.
- **`SourceTransitioned` keeps firing on a loop,** and `Wrapped` keeps its meaning: the cursor went
  back to the first playlist item, for any reason. The docs on `SourceTransitioned` and
  `PlaylistTransition.Wrapped` say both, which is the documentation fix #173 asks for.
- **No order is promised between `LoopRestarted` and `SourceTransitioned` for the same loop.** The
  first is raised on the controller's dispatch loop, and the second by the session's shell.

### 6. The watchdog watches every expected loop

The watchdog is eligible on a tick when the player expects the current item to loop at its end.
Today the controller computes `RepeatOne` from its own mode. That input becomes `ExpectsRepeat`,
which the session answers:

- **A queue** expects a repeat when its current item has started, it has not been removed, and
  either:
  - the mode is `One`, including for a one-shot current item, which `One` replays; or
  - the mode is `All`, the current item is the only playlist item, and nothing is set next or
    queued.
- **A single source** is a queue of one. On the controller, decision 8 says when it expects a
  repeat.

`ExpectsRepeat` is a property of the `PlaylistQueue` value. The playlist session reads it from the
value its coordinator holds, under the coordinator's lock and without entering its input channel, as
the protocol record's synchronous members do. The answer is read on every tick, not held across the
end of the item.

While the session performs a loop, it answers true whatever the queue now says. After each step the
shell publishes whether the input under way is a loop, and clears it when that input is handled. A
removal made during the repeat therefore does not hide a rewind that hangs, which keeps decision 5's
rule that a removal does not undo a repeat already under way. Between inputs the queue's predicate
answers alone.

- **A repeat stays eligible while it runs.** A repeat of the same item, by in-place rewind or by
  rebuild, does not make the item unstarted, so a rewind that hangs is still watched. A replay under
  `One` takes nothing, so it already keeps the item started. Today a take marks every taken item
  unstarted, including the same item taken again at a wrap (`PlaylistQueue.cs:470`); that changes
  for a same-item take.
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

### 8. If a single source does not run as a queue of one

If the record that runs a single source as a queue of one is rejected, the controller keeps
single-source looping, and these changes apply to it instead. The first two drafts made them the plan.

- **Decision 1.** The controller gives its protocol `RepeatOne` true for `One`, and for `All` when
  its session does not loop internally (`PlaybackControllerCore.cs:759`).
- **Decision 3.** Under a repeating mode, `Paused × LastFrameRendered` becomes an internal transition
  that runs the loop rewind (`PlaybackProtocol.cs:316`). The item is rewound while paused, and the
  next Play starts it from zero. `SubstrateSession`'s reposition already handles a rewind while
  paused: it re-arms audio paused and leaves the relaunch to the next Play
  (`SubstrateSession.cs:1216-1227`).
- **Decision 4.** The loop calls `RewindToStartAsync` instead of `SeekAsync(0)`, still through the
  seek state machine that ADR-0028 §2 set up. The revert comment is deleted.
- **Decision 5.** `LoopRestarted` fires when the loop rewind's seek outcome reports success, not
  when the rewind is requested. A loop rewind cancelled by a user seek raises none. The controller
  counts, and a load resets the count.
- **Decision 6.** The controller expects a repeat under `One` or `All`.

The decision 3 cell can ship on its own, ahead of either path. It changes no loop ownership, and it
ends the stuck `Playing` state under `One` while a single source still loops on the controller.

## Consequences

### Positive

The playlist half brings these:
- **Loops are observable on a playlist.** `LoopRestarted` fires for a playlist's loops, with a
  per-item count, and joins `IMediaPlayer`.
- **Every expected loop on a playlist is watched,** including a playlist of one under `All`.

These hold for a single source only once it runs as a queue of one, or under decision 8. Until then
it keeps today's behaviour, as *Negative* says:
- **One meaning per mode.** Each repeat mode means the same thing on both players, and the README's
  quick start loops, as it reads. Faults are the exception: under `One` a playlist replays a
  faulted item and counts the failure, while a single source enters `Error`.
- **The same loop event on both players,** with the same timing and count.
- **A frozen loop is fixed.** A single source paused as its loop ends no longer freezes in `Playing`.
- **The single-source loop is cheaper.** It stops rebuilding its graph on every pass.
- **One loop mechanism,** when a single source runs as a queue of one. It repeats through the
  playlist session, so the two loop paths #172 describes become one, tested in the protocol core.

### Negative

These go in `docs/BREAKING-CHANGES.md` when they land. Only the new member and the renamed property
are compile-visible.

The playlist half brings two:
- **`IMediaPlayer` gains `LoopRestarted`.** An implementing type stops compiling until it adds it. In
  this repository that includes the double in
  `tests/FrameFlow.Avalonia.Tests/FrameFlowVolumeControlTests.cs:143`. An implementing assembly that
  is not rebuilt fails to load.
- **`LoopStallSample.RepeatOne` is renamed `ExpectsRepeat`.** The type is public, so this is a
  source break for code that builds samples.

The rest change a single source. They land when it runs as a queue of one, or with decision 8:
- **A single-source player under `All` loops instead of ending.** A caller that relied on `All`
  acting as `Off` must pass `Off`.
- **The video configurator runs once per load on a single source,** not once per loop. A stateful
  operator in a consumer's chain keeps its state across loops while timestamps go back to zero, with
  no reset. `SyncJoin` is the exception: it clears its window at the start of every run of the graph
  (`src/FrameFlow.Graph/NodePumps.cs:287-290`). A playlist that rewinds in place has this today. A
  configurator-only chain that wires its own sinks is not rewired each pass.
- **`LoopRestarted` moves.** It fires once the item is back at its start, not when the rewind is
  requested, and a rewind cancelled by a user seek raises none.
- **`LoopRestarted.LoopCount` resets** on a load and on any non-loop change of item.

Until then, a single-source player under `One` still freezes in `Playing` when its loop ends while
paused, and one under `All` still ends. The decision 8 cell for the paused case can ship first if
that wait is too long.

### Neutral

- The controller still owns single-source looping until a single source runs as a queue of one, and
  for good if decision 8 applies.

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

### G. Change the controller's loop first

The first two drafts did. Decisions 1, 3 and 4 changed the controller's loop, and a single player
type would later remove it. Rejected as the plan, and kept as decision 8.

- **The playlist session already does it.** It loops a queue of one under `All`, puts a paused loop
  back at its start, and rewinds in place, in a core with transcripts, tables and an explorer.
- **It builds what the next change deletes.** Running a single source as a queue of one removes the
  controller's loop. Every change to that loop would be written, tested and then removed.
- **The session drops a stale end-of-stream by run number.** That is the case #195 describes for a
  single source, which the controller cannot tell apart today.

The cost is time. A single-source player keeps the paused freeze under `One`, and keeps ending under
`All`, until it runs as a queue of one. The decision 8 cell for the paused case can ship on its own
if that is too long.

## Not settled here

- **Running a single source as a queue of one.** Its own record decides it, after the spike and the
  conditions the end-of-queue record lists for a single player type. This record's single-source
  rows in *Validation* run against it.
- **Why a transition happened.** `PlaylistTransition` does not carry a natural end, skip, jump or
  failure (#173). `LoopRestarted` answers the question for loops.
- **Pairing a loop with its transition (#203).** `LoopRestarted` lives in `FrameFlow.Media` and
  carries no item or transition index, and no order between the two events is promised. Whether it
  should name the item depends on whether every player raises transitions, which the single player
  type's record decides.
- **Recovering from a rewind that hangs.** A rewind that never completes is reported by the
  watchdogs, but nothing rebuilds the item or bounds the wait. That is so on a playlist today.
- **Operator state across a loop.** When a retained graph re-runs from zero, nothing resets an
  operator except `SyncJoin`, which clears its own window. A stateful operator a consumer adds to the
  chain sees its timestamps return to zero. A playlist that rewinds in place does this today. A
  single-source loop rebuilds its graph, so it does not do it until it moves to the rewind.
- **A stall at a hand-off to a different item.** The watchdog covers expected loops only.
- **A stall on an audio-mastered clock.** The evaluator targets the wall-clock case, where the
  position overruns the duration. An audio-mastered clock stops at the duration instead.
- **Repeat on `BuildAsync`.** The play-to-the-end `PlayerSession` has no repeat mode. Once a chain
  sets one, the builder no longer offers `BuildAsync`.

## Implementation touches

Besides the code, the playlist half changes:
- **`FrameFlow.Media` docs:** the summaries of `LoopRestarted`, `LoopStalled` and
  `LoopStallEvaluator`.
- **`FrameFlow.Playback` docs:** the summaries of `IPlaybackController.LoopStalled` and
  `IPlaybackController.LoopRestarted`.
- **The API baselines:** a `FrameFlow.Player` `PublicAPI` entry for `IMediaPlayer.LoopRestarted`,
  and the `FrameFlow.Playback` entries for the renamed `LoopStallSample` member.
- **The queue record's** *Not settled* **entry** on `All` for a playlist of one.

A single source's move to a queue of one, or decision 8, changes:
- **`FrameFlow.Media` docs:** the summary of `RepeatMode.All`.
- **The playback pattern docs:** rows 12 to 14 of `docs/patterns/playback-states.md` and its
  repeat-mode notes, `docs/patterns/playback-statechart.md`, and
  `docs/patterns/playback-controller.md`.
- **ADR-0062's implementation note** that `All` on a single source behaves like `Off`.
- **The test bench's `repeat all`** on a single source.
- **The summary of `LoopRestartTests`,** which describes a fresh graph per loop.

## Validation

Write each test first and confirm it fails on the tree before it lands, for the reason given. Test
numbers are stable across revisions, so the tables below are grouped by where each test runs, not in
number order.

### The playlist half

Unit tests without media:

| # | Decision | Test | Today |
|---|---|---|---|
| 15 | 5 | `PlaylistSessionProtocolTests`: under `One`, an end-of-stream from the current run of a played item rewinds in place and reports a loop with count 1 when the rewind succeeds; the next loop reports 2. | [no report] |
| 16 | 5 | `PlaylistSessionProtocolTests`: a rewind that fails reports the loop when the rebuilt item's Play succeeds. A loop that ends while paused reports it when the rebuilt item's warm-up succeeds, and the Play that follows reports none. | [no report] |
| 17 | 5 | `PlaylistSessionProtocolTests`: a skip, a jump, a fault's rebuild, a failed start, an end-of-stream latched before the first Play, a hand-off to another item of the same source, and a repeat completed during disposal report no loop. Each start that is not a loop resets the count. | [no report or count] |
| 18 | 5 | `PlaylistSessionTranscriptTests`: a skip requested during an in-place rewind takes effect after the loop is reported, and the next loop's count is 1. | [no report] |
| 19 | 5 | `PlaylistSessionExplorerTests`: a loop is reported only by an input begun with an end-of-stream from the current run of a played item, and never while disposing or after giving up. A seeded defect that reports a loop on a skip is found. | [no invariant] |
| 4 | 6 | `PlaylistQueueTests`: `ExpectsRepeat` is true for a started, unremoved current item under `One`, a one-shot one included, and under `All` for the only playlist item with nothing next or queued, including after that item is taken again at a wrap. It is false for a one-shot current item under `All`, a removed current item, a playlist of two under `All`, and a different item taken and not yet started. | [no member] |
| 5 | 6 | `LoopStallEvaluatorTests`: the renamed input gates eligibility as `RepeatOne` did. | [renamed] |
| 20 | 6 | `PlaylistSessionTranscriptTests`: removing the current item while its in-place rewind is held leaves the session expecting a repeat until the rewind completes, and expecting none once the input is handled. | [no member] |

Integration tests over real playback, in `FrameFlow.Integration.Tests`:

| # | Decision | Test | Today |
|---|---|---|---|
| 9 | 5 | Playlist of one under `All`: `LoopRestarted` on each loop, with counts 1 and 2, and `SourceTransitioned` with `Wrapped` true as before. | [no `LoopRestarted`] |
| 10 | 5 | Playlist of one under `One`: `LoopRestarted` on each loop, including a loop rebuilt while paused. | [no `LoopRestarted`] |
| 11 | 5 | Playlist `[a, a]` of one source object, and a playlist of two, under `All`: no `LoopRestarted` at either hand-off. | [passes] |
| 12 | 5 | Playlist of one under `All`: a skip, and a rebuild after a fault, raise no `LoopRestarted`, and the next loop's count is 1. | [no `LoopRestarted`] |
| 13 | 5 | Playlist of two under `One`: the first item loops twice, then a jump to the second; the second item's first loop has `LoopCount` 1. | [no `LoopRestarted`] |
| 14 | 5 | `MediaPlayer` and `MediaPlaylistPlayer`: `IMediaPlayer.LoopRestarted` fires with the controller's event. | [no member] |

Test 11 guards today's behaviour against a rule that reported every same-source hand-off.

### A single source

Rows 6 to 8 run against a single source running as a queue of one, in that record's spike. Rows 1 to
3 test the controller, so they apply only if decision 8 is taken. On a queue of one, rows 15 to 17
check the session's rules, and rows 6 and 7 check the behaviour over real playback. A seek cannot
cancel a repeat there: the session takes the seek as a later input, after the repeat completes.

| # | Decision | Test | Today |
|---|---|---|---|
| 1 | 8 | `PlaybackDispatchProtocolTests`: a single-source session under `All` reports end-of-stream; the controller runs the loop rewind and stays `Playing`. | [`Ended`] |
| 2 | 8 | `PlaybackProtocolTests` and `PlaybackDispatchProtocolTests`: under `One`, and under `All` for a single source, end-of-stream while `Paused` runs the loop rewind and stays `Paused`. | [dropped under `One`; `Ended` under `All`] |
| 3 | 8 | `PlaybackDispatchProtocolTests`: a loop rewind cancelled by a seek raises no `LoopRestarted`; a load resets `LoopCount`. | [raised on request; never reset] |
| 6 | 1, 5 | Integration. Single source under `All`: two loops, with `LoopRestarted` counts 1 and 2. | [one pass, then `Ended`] |
| 7 | 3 | Integration. Single source under `One`, end-of-stream held until after a pause: `Paused`, then Play presents from zero and raises `LoopRestarted`. | [`Playing` with frames stuck; `LoopStalled`] |
| 8 | 4 | Integration. Single source under `One`: the video configurator runs once across two loops. | [three times] |

**The loop soak.** It is added to the on-hardware validation ADR-0063 and ADR-0064 list, and is not a
gate. Both players loop a clip at the same time, with hardware decode and the GPU presenter each,
because two presenters on one GPU was the condition of the hang ADR-0063 found. Each player's
presented-frame counter must not fall below 90 percent of the clip's frame rate over any one-minute
window, for at least an hour. A stall reopens #172, and the result is recorded in this record's
revision history without machine identifiers.

## As implemented: the playlist half

- **The queue.** `PlaylistQueue.ExpectsRepeat` is decision 6's predicate. An advance that takes the
  current item again keeps it started; a new session's first take does not.
- **The core.** `PlaylistAdvanceRun.Loop` records whether a pass is a loop when the pass takes its
  item: an end-of-stream of a played item, whose decision names that same item. A failed start
  clears it. `PlaylistSessionState.LoopCount` counts loops, and every other start resets it. The
  report is a new action, `ReportLoopRestarted`, emitted after the transition when the item is back
  at its start. `PlaylistSessionState.LoopUnderWay` is true while such a pass is under way.
- **The shell and the controller.** The shell passes the report to a new session callback,
  `OnLoopRestarted`, and publishes `LoopUnderWay` after each step. `PlaylistSession.ExpectsRepeat` is
  that flag or the queue's predicate. The controller posts the report through its channel with the
  session generation, drops it from a replaced session, and publishes the session's count on
  `LoopRestarted`. Its `_loopCount`, the watchdog's gate, rises on every loop. The watchdog reads the
  session's `ExpectsRepeat` for a session that loops internally.
- **The public surface.** `IMediaPlayer.LoopRestarted` forwards the controller's event on both
  players. `LoopStallSample.RepeatOne` is `ExpectsRepeat`. `docs/BREAKING-CHANGES.md` has both.
- **The tests.**
  - Row 4: `PlaylistQueueTests.ExpectsRepeat_*`.
  - Row 5: the existing `LoopStallEvaluatorTests`, over the renamed input.
  - Rows 15 to 17: `PlaylistSessionProtocolTests.ALoopRewoundInPlace_ReportsItsCount_WhenTheRewindSucceeds`,
    `ALoopThatRebuilds_ReportsWhenTheItemIsBackAtItsStart` and `ANonLoopStart_ReportsNoLoop_AndEndsTheCount`.
  - Row 18: `PlaylistSessionTranscriptTests.SkipDuringAnInPlaceRewind_TakesEffectAfterTheLoopIsReported`.
  - Row 19: the explorer's `ReportsALoopThatIsNotOne` invariant, a new scenario for a playlist of one
    under `All`, and the seeded defect "A skip's advance is counted as a loop".
  - Row 20: `PlaylistSessionTranscriptTests.RemovingTheItemDuringAnInPlaceRewind_KeepsARepeatExpected_UntilTheRewindCompletes`.
  - Rows 9 and 11 to 13: `PlaylistLoopReportingTests`, over real playback.
  - Row 10: `PlaylistLoopReportingTests.APlaylistOfOneUnderOne_ReportsEachLoop`, without its paused
    case. An end-of-stream that arrives after a pause took effect cannot be produced on demand over
    real playback, so row 16 pins the loop rebuilt while paused.
  - Row 14: `PlayerLoopEventTests`, as a unit test that both players hand out the controller's event.
  - The controller's handling: `PlaybackDispatchProtocolTests.LoopRestarted_From*`.

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
- **Revision after the second turn of automated review of #201 (2026-09-14).** Decision 5 now names
  one completion point for each path: the seek outcome for a single source, `RewindToStartAsync`
  completing for an in-place rewind, and `PlayAsync` or `WarmUpAsync` completing for a rebuild while
  playing or paused. A finding that the in-place rewind should wait for the hardware soak was
  answered on the PR and not adopted, for the reasons in alternative E.
- **Revision onto the playlist session protocol (2026-09-15).** The protocol record landed after the
  second revision (#204, #208, #209 and #210), and this record was rebased onto it:
  - **Citations.** Line numbers now cite 930a84e. `PlaylistCoordinator` and `PlaylistSession`
    citations moved to `PlaylistQueue` and `PlaylistSessionProtocol`. The probes and measurements
    still stand: none of the protocol record's five behaviour differences touches a loop.
  - **The controller half.** Decisions 1, 3 and 4 no longer change the controller. A single source
    gets them by running as a queue of one on the playlist session, which its own record decides.
    The controller changes moved to decision 8, the path if that record is rejected. Alternative G
    records why.
  - **Decision 3** now says the item is put back at its start. A playlist rebuilds an item whose loop
    ends while paused; only a playing loop rewinds in place.
  - **Decision 5** now orders loops, skips and jumps by the session's inputs instead of its gate. It
    names each completion point as a protocol outcome, and says which advance is a loop: one begun by
    an end-of-stream from the current run of a played item that takes the same item again. That
    excludes an end-of-stream latched before the first Play, which the earlier text did not decide.
    The count lives in the protocol's state, and a report action carries it.
  - **Decision 6** puts `ExpectsRepeat` on `PlaylistQueue`, read under the coordinator's lock. Row 4
    now says a one-shot current item repeats under `One`, which the earlier row left ambiguous.
  - **Not settled** gained the queue-of-one record, and #203 for pairing a loop with its transition.
  - **Validation** gained rows 15 to 19 for the protocol core, and groups the rows by where they run.
- **Revision after automated review of #215 (2026-09-15).** Five changes:
  - **A rewind that hangs.** Decision 4 now says a hang is not rebuilt, only reported by the
    watchdogs, and *Not settled* records recovering from it. It also says which changes the hardware
    soak gates: a single source moves to the rewind only with the hardware run the end-of-queue
    record requires. For a playlist, which already rewinds in place, the soak stays a regression
    check. The finding's request to gate the playlist half on the soak was answered on the PR.
  - **Which advance is a loop.** Decision 5 tests the item the decision names, since a replay under
    `One` takes nothing from the queue.
  - **Consequences.** The positive consequences now separate what the playlist half brings from what
    a single source gains only once it runs as a queue of one, or under decision 8.
  - **Operator state.** An in-place rewind keeps every operator's state while timestamps go back to
    zero, except `SyncJoin`, which clears its window on each run. *Context*, the configurator
    consequence and *Not settled* now say so.
  - **A removal during a repeat.** Decision 6 read eligibility only from the queue, so removing the
    item mid-repeat would have hidden a rewind that hangs. The session now answers true while it
    performs a loop, and row 20 tests it.
