# ADR-XXXX: The playlist session as a pure protocol, with its queue as a value

## Status

Proposed (2026-09-14). Draft pending number assignment. Revised the same day after an independent
review; *Revision history* says what changed. **All four steps of the migration are implemented**
(decision 8, *As implemented*).

This record moves the playlist player's decision logic into a pure core. ADR-0055 did the same for
the codec loop, and `PlaybackProtocol` for the controller's main state machine. The record:
- makes the queue an immutable value with pure operations;
- turns `PlaylistSession` into a shell that runs a pure step function around an awaiting reader,
  the pattern the controller already uses;
- adds deterministic tests for orderings of events, including an explorer that checks invariants
  over a model.

It is meant to land before the draft [Looping on both players](looping-on-both-players.md) is
implemented and before a single player type is proposed. Both then become changes to a tested core
rather than to async code.

It also provides the deterministic ordering tests that the end-of-queue record and #195 say they
need #143's tooling for, for the races inside the playlist session. #143 itself targets races between
sink and worker threads in the real code, and stays open.

Line numbers cite commit f4aa57a.

Related: [ADR-0023](ADR-0023-hierarchical-state-machine-with-channel-dispatch.md),
[ADR-0055](ADR-0055-decode-protocol-as-a-pure-mealy-core.md),
[ADR-0072](ADR-0072-tests-do-not-depend-on-elapsed-time.md),
[End of queue, replay and faults on the playlist player](playlist-end-of-queue-replay-and-faults.md),
[The playlist player's queue](playlist-queue-model.md),
[Looping on both players](looping-on-both-players.md). Issues #143, #195.

## Context

### Where the pure cores already are

| Core | Shell | What it bought |
|---|---|---|
| `DecodeProtocol` (ADR-0055) | `DecodeDriver` | The four hand-inlined copies of the send/receive loop became one table, tested without FFmpeg |
| `EncodeProtocol` | `EncodeDriver` | The same, for encoding |
| `PlaybackProtocol.Advance` (`src/FrameFlow.Playback/PlaybackProtocol.cs:240-244`) | `PlaybackControllerCore.RunPlaybackAsync` | The controller's main table, tested cell by cell and by transcript without a session (`PlaybackProtocolTests`) |

Each of these is a total function of its state and one input. It returns the next state and the
effects as data. It owns no IO, no `await`, no clock, no lock and no mutable state, and its shell
performs the effects. Smaller pure helpers follow the same idea: `LoopStallEvaluator`,
`PresenterStallEvaluator` and `PresentPlanner`. `PresentPlanner` takes a probe function as an
argument.

The controller's shell awaits each effect before it takes the next command from its channel
(`PlaybackControllerCore.cs:871-876`). No other command runs while an effect is in flight, which is
the ordering the tests of the table rely on.

### Where the logic is not pure

**`PlaylistSession`** is 1,051 lines of async code. Its behaviour is spread over:
- **Fields:** a run state, the current item runtime and item, a session token, an item generation, the
  generation of the last fault handled, a give-up flag, two flags about whether the current item has
  played, and a disposed flag (`src/FrameFlow.Playback/PlaylistSession.cs:72-108`).
- **A transition gate:** a `SemaphoreSlim` (`:69`) serializes controller commands against advances.
  An advance holds it across its whole length: dispose the old item, stop the clock, open, warm up and
  start the new one, then take any pending jump.
- **Thread-pool hops:** item end-of-stream, faults, skips and jumps each hop to the thread pool before
  they wait on the gate (`:536`, `:575`).
- **Decisions between awaits:** each step of an advance reads and writes fields in between.

**`PlaylistCoordinator`** (653 lines) is deterministic and needs no media, but it is a mutable object
behind a lock (`src/FrameFlow.Playback/PlaylistCoordinator.cs:43`). Its decisions are methods that
change it, not functions that return a new value.

**`PlaylistSession` builds its item runtimes itself** (`CreateItemSession`, `PlaylistSession.cs:463`).
No test can drive it without FFmpeg, a clip and real time.

### What that has cost

The last six playlist changes found these defects in review:

| Change | Defect | Kind |
|---|---|---|
| #191 (#180) | A first item that faulted before the first play was skipped while the controller was still loading. | Session ordering |
| #191 (#180) | A fatal error from the session that replay from `Ended` replaced reached the new session. | Controller |
| #194 (#182) | A Play or Pause queued behind the advance that ended the queue replaced `Ended`. | Session ordering |
| #194 (#182) | The gate was released before a warm-up finished. | Session ordering |
| #194 (#182) | A latched skip rewound an item that had never played. | Session rule |
| #194 (#182) | A deferred start failure escaped `PlayAsync`. | Session rule |
| #194 (#182) | A cancelled Play was counted as an item failure. | Session rule |
| #196 (#183) | A current-item update was dropped when the controller's channel was full, and a stale session's update could displace a newer one. | Controller |
| #197 (#170) | A repeat-mode change racing the end of the queue let the controller loop an ended playlist. The PR listed it as not covered before review raised it. | Controller |
| #198 (queue ADR) | A failed start would be retried, a removal left the cursor undefined, replay could fall into `Error`, and an item still opening could not be named. | Rule gaps in a draft |
| #198 (queue ADR) | A jump arriving during an advance, or just after the advance's last check, was lost or delayed. | Session ordering, in a draft |
| #199 (#171) | A replace after a replay's take opened the stale item; a jump to a removed current item succeeded. | Coordinator |

Four of the twelve rows are orderings inside the session, five are rules in the session or the
coordinator, and three are in the controller. The controller's defects are outside this record.

The tests for the session's orderings are the expensive ones:
- They wait on real playback of 3-second clips.
- They hold a thread inside the gate with a clock that blocks in `Stop` or `Pause`.
- Several passed with the bug put back, until they also counted frames to prove an item had not
  played through.
- #199's own mutation table records a rule, the advance's check for a jump, whose removal made no
  test fail.

The controller's defects in #196 and #197 were pinned by fast `PlaybackDispatchProtocolTests` with a
fake session, and #199's by `PlaylistCoordinatorTests`. The session has no such seam.

## Decision

### 1. The queue is an immutable value

`PlaylistQueue` is an immutable record. It holds:
- the playlist and the cursor;
- the next and queued items;
- the pending jump;
- the current item, with whether it has started and whether it has been removed;
- the reserved replay item;
- the reported item, with its `MediaInfo` and duration;
- the repeat mode;
- the failure count;
- the latched advance;
- the revision and the transition count.

Every rule of [The playlist player's queue](playlist-queue-model.md) becomes a pure operation, such
as `(queue, reason) → (queue', decision)` for a take, or `(queue, item) → (queue', result)` for a
remove. `PlaylistFailureGuard`'s rules become operations on the same value. The revision rises on the
same changes that raise it today: edits and hand-offs. A repeat-mode change, a latch and a failure
count do not.

`PlaylistCoordinator` becomes a thread-safe cell around the current value. It applies an operation
under its lock and stores the result, and it raises `SourceTransitioned` outside the lock. Its public
members, and the internal members the player facade uses, keep their signatures. The internal
`Failures` property becomes a read of the value.

### 2. The session is a pure step function

```text
PlaylistSessionProtocol.Step(SessionState, PlaylistQueue, SessionInput, StepContext)
    → (SessionState', PlaylistQueue', SessionStep)
```

`StepContext` carries what the shell knows at the moment of the call and the core does not own:
whether the session is disposing (decision 6). The shell reads it from a volatile flag on every call,
so an input already being handled sees disposal at its next step.

`SessionState` is an immutable record. It holds:
- the run state;
- the item slot: empty, or an item with its generation, its `PlaylistItem`, whether it has played,
  whether it waits for a Play, the last run number the core knows, and the `Wrapped` flag carried
  from its take to its start;
- the generation of the last fault handled;
- the give-up flag.

A `SessionStep` is:
- an ordered list of immediate actions;
- at most one awaited action;
- and whether the step is the last one for this input.

**Inputs from the channel:**

| Source | Inputs |
|---|---|
| The controller | `Initialize`, `WarmUp`, `Play`, `Pause`, `Seek(position)`, `Rewind`, `Dispose`. Each carries a command id. |
| An item runtime | `EndOfStream(generation, run)`, `Fault(generation, playedFor, error)` |
| The coordinator | `SkipRequested(generation, runAtRequest)`, `JumpRequested` |

**Inputs the shell feeds back:**

| Input | When |
|---|---|
| `Outcome(action, Ok \| Failed(error) \| Cancelled, run, info)` | The awaited action has finished. It carries the item's run number read at completion, and the `MediaInfo` and duration after an open. |
| `Continue` | The step's immediate actions have run and the step was not the last. The core reads the queue again, which is how a jump requested by a `SourceTransitioned` subscriber is seen in the same advance. |

**Actions:**

| Kind | Actions |
|---|---|
| Item runtime, awaited | `OpenItem(generation, source)`, `WarmUpItem`, `PlayItem(command?)`, `PauseItem(command?)`, `SeekItem(position, command?)`, `RewindItem`, `DisposeItem` |
| Immediate | `StopClock`, `ReportCurrentItemChanged(info)`, `ReportRecoverableError(error)`, `ReportEndOfStream`, `ReportFatal(error)`, `RaiseTransition(item, wrapped)`, `CompleteCommand(id, result)` |

An action that serves a command carries its id, so the shell can pass that command's cancellation
token to the item operation. The queue decisions happen inside `Step`, through decision 1's
operations. The function is total: every input in every state returns a step, and an input that
means nothing in a state is dropped as data.

### 3. The shell awaits each step

`PlaylistSession` keeps its `IPlaybackSession` contract. Internally it becomes a shell with one
channel and one reader.

- **The channel is unbounded.** Posting never blocks and never drops. A `SourceTransitioned`
  subscriber that skips or jumps posts from the reader's own thread, and that must not wait on the
  reader.
- **The reader handles one input to the end.** It takes an input from the channel and calls `Step`.
  It then performs the immediate actions, and awaits the awaited action if there is one. It feeds
  back the `Outcome`, or `Continue` if the step was not the last, and repeats until a step is the
  last. Only then does it take the next input from the channel.
- **What that preserves.** An advance runs from its take to its settled item, including its pending
  jump, without another input in between. That is the ordering the gate gives today. The gate goes.
- **Posts from item runtimes.** An item callback posts from whatever thread raised it, and the
  coordinator's pokes post from the caller's thread. Nothing hops to the thread pool first.
- **Commands.** A controller call posts its command and awaits the command's completion. A command's
  token reaches the item operation that serves it, so the controller's Pause-drain can cancel a seek
  in flight, as today. The cancellation comes back as a `Cancelled` outcome, and the command completes
  with it.

The synchronous members of `IPlaybackSession` do not go through the channel:
- **`TryBeginReplay`** is an operation on the coordinator's cell.
- **`MediaInfo`, `Duration`, `CanSeekFromEnded` and `GetPipelineDiagnostics`** read a snapshot the
  shell publishes, as a volatile reference, after each step. They describe the item runtime the
  session holds, not the queue. Only a step changes that runtime. An edit to the queue does not: a
  removed or cleared current item keeps playing, as the queue record decides, so these members keep
  describing it until an advance replaces it. That is what they do today, where they read the
  current runtime directly. Queue-level metadata, such as `CurrentSource` and the player facade's
  `MediaInfo` and `Duration`, stays on the coordinator's value.

### 4. The queue is updated under the coordinator's lock

The shell calls `Step` inside the coordinator's cell update, under its lock. It passes the current
queue value and stores the returned one. `Step` is pure and does no IO, so holding the lock for it is
cheap. A player edit applies its own operation under the same lock, and the next `Step` sees it. No
compare-and-set retry is needed.

`RaiseTransition` is performed by the shell through the coordinator, outside the lock, so a
subscriber can call back into the coordinator.

### 5. Run numbers are compared after the outcome

The core keeps the run number it last learned for the item. `Outcome` for a seek or rewind carries
the run number read when the operation finished, whether it succeeded, failed or was cancelled. An
end-of-stream is stale unless its run equals the known run.

Because the reader awaits the seek or rewind, an end-of-stream posted during it is taken up only
after its outcome. By then the known run is the one the reposition left:
- **A cancelled seek** that stopped before the increment leaves the run unchanged, so an
  end-of-stream from that run is still live.
- **A seek cancelled after the increment** carries the new run.
- **A paused seek** increments without relaunching. The next Play relaunches under that run.

### 6. Disposal stops everything and drains

`DisposeAsync`:
1. **Detaches** the session from the coordinator synchronously, so a skip or jump from then on
   latches or waits for the next session, as it does today.
2. **Marks the session disposing** with the volatile flag that `StepContext` carries. While it is
   set, the core completes a command as a no-op, and ends a multi-step advance at its next step.
3. **Cancels the item operation in flight.** The shell passes a disposal token, linked with any
   command's token, to every awaited item action: open, warm-up, start, pause, seek, rewind.
   Disposing cancels it, and the operation's outcome is `Cancelled`. The controller also cancels an
   active seek before it disposes the session.
4. **Posts `Dispose`.** The reader takes it up after the input it is handling, which now ends at its
   next step.
5. **Completes after the drain.** It completes once the item is disposed and the reader has exited.

`DisposeItem` itself is not cancelled. `SubstrateSession.DisposeAsync` stops its graph with no token,
as it does today.

### 7. Tests are tables, transcripts and explored orderings

- **Table tests.** They enumerate an abstraction of the state, and assert each input against it:
  - the run state;
  - the item slot's kind, and whether the item has played;
  - whether a jump is pending and an advance is latched;
  - the relation of an input's generation and run to the known ones: current, older or newer.

  The data behind them, items and lists, is fixed per test.
- **Transcripts.** A transcript is a sequence of inputs, with the effects, actions and states it
  must produce. Each transcript is shown to fail with its fix reverted, where the fix can be
  reverted behind a switch. Integration tests stay for the mechanism: decode, present and rewind.
- **An item model.** Fake item runtimes and the explorer share one model of `SubstrateSession`:
  - a run launches at the first play, and at a seek or rewind that is not paused;
  - a paused seek advances the run without launching;
  - an end-of-stream comes only from a launched run that has not been stopped;
  - an operation can succeed, fail, or be cancelled before or after the run advances.
- **An ordering explorer.** A scenario is a starting state and a set of channel inputs that can
  arrive in any order, with the outcomes each awaited action may take.
  - It enumerates the orderings and outcomes up to a bound, and runs the shell's loop over the core.
  - It keeps a visited set over the state, the queue and the inputs still to arrive, with structural
    equality supplied for the lists.
  - It ends each run with a quiescence phase in which nothing new arrives, so liveness invariants can
    be checked.

The explorer's invariants:

- **Presenting.**
  - `PlayItem` is emitted only while the run state is `Playing`.
  - `SeekItem` and `RewindItem` on an item that has not played are emitted only while `Playing`.
    The end-of-queue record lists that case as an open gap, so this invariant starts as an expected
    failure.
- **The end.**
  - After `ReportEndOfStream`, no `PlayItem` and no `SeekItem` are emitted until a warm-up out of
    `Ended`.
- **Commands.**
  - Every command completes exactly once.
  - None completes before the advance it triggers has settled.
- **Items.**
  - At most one item runtime is live.
  - Every opened item is disposed exactly once.
- **Notifications.**
  - An end-of-stream or fault from a replaced generation causes no action.
  - An end-of-stream from a run that a seek or rewind replaced causes no advance.
- **Reports.**
  - `ReportCurrentItemChanged` comes before `RaiseTransition` for the same item, and each item that
    starts raises one transition.
  - No controller callback follows a fatal report or disposal.
- **Jumps and latches.**
  - A recorded jump is taken, replaced or discarded by an edit. It is never lost.
  - A latched skip is consumed by the first Play.
- **Giving up.**
  - The player gives up after the ninth failure in a row: as `ReportFatal` during playback, or as a
    failed `Initialize` for a replay. Nothing starts an item afterwards.
- **At quiescence while `Playing`.**
  - A started item is current.
  - No jump is pending and no advance is latched.
- **The queue.**
  - The cursor is within the playlist.
  - An item is in at most one collection.
  - A removed current item is not repeated.

The explorer runs as an ordinary unit test with no rewrite step and no timing. Scenarios and bounds
are chosen per defect class, starting from the transcripts.

### 8. Migration keeps today's behaviour

1. **Seams for today's session.** `PlaylistSession` creates item runtimes through a factory of an
   internal interface. That interface is `IPlaybackSession` plus `RunNumber`, and `SubstrateSession`
   implements it. The session also takes an injectable scheduler for its thread-pool hops, and an
   injectable clock, as the integration harness already does. Transcripts then run against today's
   session with fake items on a manual scheduler, as scripts of observable effects. They pin the
   behaviour the last six reviews settled.
2. **The queue value.** `PlaylistQueue` and its operations land, and the coordinator becomes the cell
   around them. `PlaylistCoordinatorTests` move to the value, including the rows for the replace after
   a replay's take and the jump to a removed current item.
3. **The protocol and shell.** `PlaylistSessionProtocol` and the awaiting shell replace the session's
   internals. The same transcripts, the table tests and the full suite must pass.
4. **The explorer** lands with the item model and the invariants, and with a seeded defect to show
   that it finds one.

**No behaviour is meant to change.** Taking one input at a time narrows today's orderings. Every
ordering the new shell produces is one today's code allows. Five differences are observable, and
none changes a documented contract:
- **The subscriber's thread.** `SourceTransitioned` subscribers always run on the reader's thread.
  Today they can run on the controller's dispatch-loop continuation, when an advance runs inside
  `PlayAsync`.
- **Detach timing.** The session detaches before it drains, as today.
- **A subscriber's jump.** A jump requested from a `SourceTransitioned` subscriber is still taken in
  the same advance, before a waiting Pause, through `Continue`.
- **A command after giving up.** Step 4's explorer found that a command could still start an item
  after the session had handed the controller a fatal error. Once the session has given up, a command
  completes as a no-op.
- **Disposal during an open or warm-up.** Today `DisposeAsync` waits on the gate until an advance's
  open or warm-up finishes, because those calls take no token. The new shell cancels them (decision
  6). A disposal that would have waited on a stuck open now ends it.

A transcript that the core cannot pass is a defect in the core, or a behaviour to decide in another
record.

#### As implemented: step 1

- **The item-runtime seam.** `IPlaylistItemRuntime` is `IPlaybackSession` plus `RunNumber`, and
  `IPlaylistItemRuntimeFactory` creates one per item. `SubstrateSession` implements the first and
  `SubstrateSessionFactory` the second. `PlaylistSessionFactory` builds the substrate factory from
  the sinks and options it already took, so `PlaylistSession` no longer holds them.
- **The scheduler.** `IPlaylistSessionScheduler` starts the session's two hops: the advance after an
  item ends, faults or is skipped, and the advance that takes a jump. The default runs them on the
  thread pool, as before.
- **The clock.** Nothing was added. The session already takes the controller's clock.
- **The rig.** `PlaylistSessionRig`, in `FrameFlow.Playback.Tests`, runs the session over fake item
  runtimes. It records each item call, controller report, clock call and transition as a transcript
  line. A test can hold an item call until it releases it, or make one throw. A fake seek or rewind
  advances the run number when it completes, so a hold on it is a hold before the increment.
- **Hops run inline.** The rig's scheduler runs each hop on the thread that asked for it, up to its
  first await that does not complete at once, and keeps its task. `SettleAsync` waits for every
  hop. The draft named a manual scheduler that queues hops. Running them at once is closer to the
  thread pool, and it lets a test assert what a request did before it releases a hold.
- **Deferred hops.** On the thread pool a hop can also start after a later call. That differs from
  making the request later only in what the session reads when the request is made: an
  end-of-stream's run number, and the generation an end-of-stream or skip is tagged with. A test can
  defer hops to reproduce it. Two transcripts do: an end-of-stream whose advance starts after a seek
  is stale, and a skip requested before an end-of-stream's advance starts collapses into that
  advance.
- **What step 3 changes.** The transcripts use controller calls, item notifications, queue requests,
  holds, deferred hops and `SettleAsync`. Step 3 changes `SettleAsync` to wait for the reader, and a
  deferred hop becomes a delivery the rig posts after the later call. The two deferred transcripts
  keep their outcome because the inputs carry what today's session reads at the request: the run
  number on `EndOfStream`, and the generation on `SkipRequested`. The draft's `SkipRequested` carried
  no generation, so the skip would have advanced a second time. Decision 2's input table now gives it
  one.

#### As implemented: step 2

- **The value.** `PlaylistQueue` is a sealed record with private init accessors, so only its
  operations make new values. Its collections are `ImmutableList`s. Each rule of the queue record
  and of `PlaylistFailureGuard` is an operation that returns the new value and, where a caller needs
  one, a result as a tuple. `NextKind` and `NextDecision` moved onto it from the coordinator.
- **Equality is structural.** The collections compare item by item, and items by reference. The
  explorer's visited set needs this, and a record's generated equality would have compared the
  lists by reference.
- **The coordinator is the cell.** It holds the current value and applies each operation under its
  lock. The session's handlers and the transition stream stay on the coordinator, outside the
  value. Its public members and the members the player facade uses keep their signatures.
- **What the session calls.** `Failures` is gone. The session calls `ItemFailed`, `ItemEnded` and
  `ConsecutiveFailures` on the coordinator. `PlaylistFailureGuard` keeps the rule's constants,
  `ProgressNeeded` and its description, as a static class.
- **The latch.** A skip with no session attached is now latched under the coordinator's lock, in
  the same step that reads the handler. It used an `Interlocked` exchange outside the lock.
- **The tests.** `PlaylistQueueTests` holds the queue record's rows and the basic orders, run
  against the value. `PlaylistCoordinatorTests` keeps what belongs to the cell: argument checks,
  the handlers, the transition stream, and the failure count shared across sessions.
  `PlaylistFailureGuardTests` runs the rule through the value.

#### As implemented: step 3

- **The core.** `PlaylistSessionProtocol.Step` is in `PlaylistSessionProtocol.cs`. Its types are in
  `PlaylistSessionState.cs` (`PlaylistSessionState`, `PlaylistItemSlot`, `PlaylistRunState`,
  `PlaylistSessionWork`, `PlaylistStepContext`) and `PlaylistSessionInput.cs` (the inputs, the
  actions, `PlaylistSessionStep` and `PlaylistCommandResult`). The names carry a `Playlist` prefix,
  since they share the `FrameFlow.Playback` namespace with the controller's.
- **Where a multi-step input is.** The state has a `Work` member that decision 2 did not list. It names
  what the core is waiting for: an awaited action, such as an advance's open or warm-up, the in-place
  rewind or the pause at the end of the queue, or the `Continue` after a transition. It is null
  between inputs.
- **The skip's run state.** `SkipRequested` carries the run state the session had published when the
  skip was requested, as well as the generation. Today's skip is dropped when it is requested at
  `Ended`, even if the warm-up of a seek out of `Ended` has finished by the time it is handled.
  `SkipDuringTheWarmUpOutOfEnded_IsDropped` pins that.
- **Actions.** Besides decision 2's, there is `AttachToCoordinator`. `ReportItemFailed(source, what,
  error)` replaces `ReportRecoverableError`, so the shell writes both the log line and the message.
  `Log` covers the log lines with no report. `RaiseTransition` carries the index the queue's
  `ReportCurrent` returned. `DisposeItem` carries no generation: the session holds at most one
  runtime.
- **Delivery.** An item callback, or the coordinator's skip or jump handler, reads what it needs at
  once and delivers the input through `IPlaylistSessionScheduler`. What it reads is the run number,
  the generation and run state, or the fault's played time. The default posts at once, on the calling
  thread. Nothing hops to the thread pool.
- **The coordinator.** `Update<T>` applies a function to the queue under the lock, and the shell steps
  through it. `RaiseTransition` raises `SourceTransitioned` for a start the queue has already
  recorded.
- **Commands.**
  - A command posts its input and waits for the core's `CompleteCommand`. The command method rethrows
    a failure or a cancellation, so callers see today's exceptions.
  - A command cancelled while it waits in the channel completes with the cancellation and has no
    effect, as a wait on the gate did.
  - `Initialize` is never cancelled while it waits. It takes its start item from the queue first, as
    today, and its open then fails with the cancellation.
- **The synchronous members** read the runtime the shell publishes after each step. That is the held
  runtime only while the state has an item slot, which is when today's `_current` was set.
- **Disposal** follows decision 6, with these details:
  - A command still running when disposal begins completes as a no-op, as one that arrives during
    disposal does.
  - Nothing is reported to the controller once disposal has begun. Today's session could report an
    end-of-stream or a failed item from an advance that finished during disposal.
  - The clock still stops once an advance has disposed its old item.
  - A skip requested before the first Play, and handled during disposal, is latched on the queue.
    The next session's first Play takes it, as it took today's skip, which was latched at once.
  - Once disposal has closed the channel, a command throws `ObjectDisposedException`, as the disposed
    gate did. `Initialize` threw nothing today and opened a runtime that was never disposed. It now
    throws too.
  - The token is cancelled with `CancelAsync`, so its callbacks run on the thread pool. `Dispose` is
    posted whether or not a callback throws. A second `DisposeAsync` waits for the first.
- **If the reader stops.** A failure outside an input's handling stops the reader, such as a controller
  callback that throws while the shell reports an earlier failure. The reader then closes the channel
  and fails every waiting command with `ObjectDisposedException`, so no caller waits forever.
- **Real item runtimes.** Disposal can now cancel a `SubstrateSession`'s open, warm-up or play.
  `SubstrateSession.DisposeAsync` already stops the graph without a token, and handles a runtime that
  never finished loading or never played. Today's session reaches both, when an open fails and when a
  paused item is skipped.
- **A test hook.** `WhenIdleAsync` sends probes through the channel until every input posted has been
  handled. The rig's `SettleAsync` uses it.
- **The tests.**
  - `PlaylistSessionTranscriptTests` ran unchanged apart from `SettleAsync`. Four transcripts were
    added: `DisposalDuringAnAdvancesOpen_CancelsTheOpen`, `SkipDuringTheWarmUpOutOfEnded_IsDropped`,
    `SkipBeforeTheFirstPlay_OutlivesADisposalThatStartsFirst` and
    `AfterDisposal_ACommandThrows_AndASecondDisposalCompletes`.
  - `PlaylistSessionProtocolTests` holds the table tests, 63 cases. Each row is a run state, an item
    slot, what the queue takes next, a pending jump or a latch, and whether an input's generation and
    run are current.
  - The rig's deferred hops became deferred deliveries. A held delivery is posted after the later
    call, with what was read when it was raised.

#### As implemented: step 4

- **The item model.** `PlaylistItemModel`, in `FrameFlow.Playback.Tests`, is the model decision 7
  describes. The rig's fake runtimes follow it when a call completes, and the explorer draws its
  outcomes from it.
- **The explorer.** `PlaylistSessionExplorer` runs the shell's loop over the core, one step per move,
  so events land between steps. A state of the search holds:
  - the core's state and queue;
  - the channel, and the input the reader is handling with what it waits for;
  - the item runtime, as the model describes it;
  - the controller's progress through its calls, and the events still to happen;
  - what has been reported.
- **The moves.** From each state it explores every move that can come next:
  - The reader takes its next step: the next input, a continue, or one outcome of the action it
    awaits. An action succeeds, and can fail where the scenario allows it. It can be cancelled during
    disposal, or when the scenario lets the controller cancel its commands. A seek or rewind can be
    cancelled before or after the run advances.
  - The controller makes its next call, once its previous call has completed.
  - One of the scenario's events happens: a skip, a jump, removing the current item, an enqueue, an
    end-of-stream from a launched run, a worker fault, or disposal.
- **Pruning and quiescence.** A visited set holds a SHA-256 prefix of each state, including the
  events still to happen. Every state where the reader is idle and the channel is empty is a point of
  quiescence, and the liveness invariants are checked there.
- **Scenarios**, in `PlaylistSessionExplorerTests`, start from the transcripts' defect classes:
  - the end of the queue;
  - seeks and a stale end-of-stream;
  - jumps during advances;
  - replay under `One`;
  - failures;
  - disposal;
  - edits;
  - a seek before the first play.

  The largest has about 16,000 states. All of them, and the seeded defects, run in about a second in
  the unit suite.
- **The invariants.** They are decision 7's, as the code checks them, with these differences:
  - A changed item must be reported with its transition straight after it, and no step raises two
    transitions. That stands in for "each item that starts raises one transition".
  - A command must not complete in a step that awaits an action or expects a continue. That stands in
    for "none completes before the advance it triggers has settled".
  - Giving up is checked as "nothing starts an item after a fatal report". The ninth failure in a row
    is not counted; `PlaylistQueueTests` and `PlaylistFailureGuardTests` cover the count.
  - Three invariants were added. An advance must not open an item before the first play. An item
    whose worker faulted must not be rewound in place. An item call needs an opened runtime.
  - A latched skip must be consumed by the first Play the session acts on. A Play during disposal, or
    after the session has given up, is a no-op, and the latch waits for a later session.
  - The checks at quiescence while playing are skipped once a command has failed or been cancelled.
    The controller is then in `Error`, or not playing.
- **What it found.** A command could still start an item after the session had given up. For
  example, a fault before the first play hands the controller a fatal error, but a Play the controller
  sent before it saw that error then played the item. The gated session did the same. The core now
  completes every command as a no-op once it has given up, as it does during disposal.
  `FaultBeforeTheFirstPlay_IsFatal_AndNothingAdvances` and
  `AfterGivingUp_NotificationsAreDropped_AndCommandsComplete` pin it.
- **The expected failure** is found twice: a seek before the first play, and a seek after a skip
  while paused, which leaves the next item open and unplayed.

### 9. What stays the same

- `IPlaybackSession`, `PlaybackControllerCore`, `PlaybackProtocol`, `SubstrateSession` and the public
  API.
- The rules of the end-of-queue, queue and loop records. This record changes where the rules are
  written, not what they are.

## Consequences

### Positive

- Every decision the playlist session makes is a step that can be read and asserted without media.
- Orderings of skips, jumps, seeks, ends, faults and edits are enumerated mechanically. That covers
  the session orderings and rules in *What that has cost*.
- The loop draft and a single player type become changes to the core, with transcripts.
- Most playlist decision tests run in milliseconds instead of waiting on clips.

### Negative

- **A large rewrite.** The session's 1,051 lines and the coordinator's internals change, behind a
  contract that does not. The migration order in decision 8 is what keeps it safe.
- **The explorer checks the model, not the shell.** A defect in how the shell performs an action, in
  the item model's fidelity to `SubstrateSession`, or inside `SubstrateSession`, is not found this
  way. #143's tooling is still needed for races in the real code.
- **The shell is thin, not trivial.** Feeding outcomes back, passing tokens, disposal and the
  synchronous snapshot must be right. The transcripts and the integration suite test them.
- **Step 1 adds seams to code that is then replaced.** They are what lets the transcripts pin
  today's behaviour first.
- **The state space needs care.** Orderings grow factorially with the inputs in a scenario, so
  scenarios stay small, and the visited set is required.

### Neutral

- The controller keeps its own channel and its own pure table. The two meet at the `IPlaybackSession`
  contract, as they do today.

## Alternatives considered

### A. Keep the imperative session and add integration tests

Rejected. The session orderings in *What that has cost* are reached in real playback only with holds
inside the gate, and several such tests passed with the defect put back.

### B. Pure decisions called from the gated methods

Keep the gate, and call a pure `Decide` between the awaits of each method. Rejected. The multi-step
advance would still be spread over methods that read and write fields between awaits, and there would
be no sequence of inputs to enumerate.

### C. A Stateless machine for the session

Rejected, for the reason `PlaybackProtocol` replaced one in the controller. The session's state
carries items, generations and run numbers, which do not fit an enum of states, and actions buried in
entry handlers cannot be asserted as data.

### D. A non-awaiting event loop

The first draft of this record. The reader would start each operation and take the next input at
once, with every operation posting a completion event, and a busy item slot holding waiting commands.
Rejected after review:
- It needs failure and cancellation completions for every operation, and cancellation plumbing for
  waiting commands.
- It hung disposal: the controller cancels a seek without awaiting it.
- It had to classify end-of-stream during a seek before the seek's outcome was known, which dropped
  live ones.
- It left how long an advance holds off other commands undefined.
- It is not the pattern ADR-0023's controller uses, and its state space is larger.

### E. Seams and a scheduler only, without the rewrite

Step 1 alone gives deterministic transcripts on today's code. Rejected as the end state, because the
decisions stay spread over async code with no table to assert. It is kept as step 1.

### F. Exploring schedules of the real code first (#143)

Not rejected; insufficient on its own. It needs a rewrite step in the build and assertions that fail
when an invariant breaks. The model's invariants become those assertions. Both are worth having.

### G. An actor or reactive library

Rejected. It adds a dependency and leaves the same testing gap: the decisions would still be inside
handlers.

## Not settled here

- **The controller's seek and repeat machines, and its dispatch rules.** Seek drain, stale seek
  outcomes, replay from `Ended`, session generations and the pending item update are hand-written in
  the controller's shell. #191, #196 and #197 found defects there. A later record can fold them into
  `PlaybackProtocol`'s state and inputs.
- **`SubstrateSession`'s reposition recipe as a protocol.** Decision 7's item model describes its
  effect on run numbers. Making the recipe itself a core is a separate change.
- **One protocol for a single player type.** Whether the controller's and the session's protocols
  merge when a single source runs on the playlist session.
- **The explorer's bounds.** How many inputs a scenario may hold before enumeration is too slow for
  the unit suite, and whether larger bounds run in a separate CI job. Today's scenarios run in about
  a second, so they stay in the unit suite.
- **Rules the explorer does not see.** Some rules decide which item plays or what is reported, and
  breaking them breaks no invariant. They are pinned by the transcripts and tables. Step 4's
  *Validation* lists which rules those are.

## Validation

- **Step 1.**
  - **Transcripts.** They pass against today's session with fake items on a manual scheduler. The
    first ones are the session orderings and rules in *What that has cost*:
    - a first item faulting before the first play;
    - a Play or Pause queued behind the end of the queue;
    - a warm-up held under the gate;
    - a latched skip on an unplayed item;
    - a deferred start failure;
    - a cancelled Play;
    - a jump arriving during an advance, and just after its last check.
  - **Reverted fixes.** Each transcript whose fix can be reverted behind a switch is shown to fail
    with it reverted.
  - **The jump check.** This includes the advance's check for a jump, which no integration test could
    isolate.
  - **As implemented.** The transcripts are in
    `tests/FrameFlow.Playback.Tests/PlaylistSessionTranscriptTests.cs`, and run in milliseconds. Each
    rule below was removed behind a temporary switch, and the suite was run once per switch. Each
    removal failed the transcripts in its row and no others. `LoadPlayHandOffAndEnd` pins a load, a
    hand-off and the end of the queue, and has no fix of its own.

    | Rule removed | Transcripts that failed |
    |---|---|
    | A fault before the first play goes to the controller as fatal (#191) | `FaultBeforeTheFirstPlay_IsFatal_AndNothingAdvances` |
    | A Play at `Ended` does nothing (#194) | `PlayQueuedBehindTheEndOfTheQueue_LeavesTheSessionEnded` |
    | A Pause is recorded only from `Playing` (#194) | `PauseQueuedBehindTheEndOfTheQueue_LeavesTheSessionEnded` |
    | A seek at `Ended` does nothing (#197) | `SeekQueuedBehindTheEndOfTheQueue_IsDropped` |
    | The warm-up holds the gate throughout (#194) | `WarmUpOutOfEnded_HoldsOffAJumpUntilItFinishes` |
    | Only an item that has played is rewound in place (#194) | `LatchedSkipUnderOne_RebuildsTheUnplayedItem` |
    | A failed deferred start is handled as a failed start (#194) | `DeferredStartFailure_IsSkippedLikeAFailedStart` |
    | A cancelled Play is not an item failure (#194) | `CancelledPlay_OfAnItemWaitingToStart_KeepsTheItem` |
    | An advance takes a pending jump once its item has started (#199) | `JumpDuringAnAdvance_IsTakenBeforeAWaitingPause`, `JumpFromATransitionSubscriber_IsTakenBeforeAWaitingPause` |
    | The jump's advance acts only on a jump still pending (#199) | `JumpDuringAnAdvance_IsTakenBeforeAWaitingPause`, `JumpFromATransitionSubscriber_IsTakenBeforeAWaitingPause` |
    | A jump request starts an advance (#199) | `JumpDuringASeek_IsTakenWhenTheSeekCompletes`, `WarmUpOutOfEnded_HoldsOffAJumpUntilItFinishes` |
    | An end-of-stream from a run a seek replaced is dropped (#197) | `EndOfStreamRaisedDuringASeek_IsDropped_AndOneAfterItIsNot`, `EndOfStreamWhoseAdvanceStartsAfterASeek_IsDropped` |
    | A notification tagged with a replaced generation is dropped | `SkipRequestedBeforeAnEndOfStreamsAdvanceStarts_AdvancesOnce` |

    The advance's check for a jump is isolated by a Pause that waits behind the advance. With the
    check, the jump's target plays and is then paused. Without it, the advanced item is paused and
    the jump's target opens paused.
- **Step 2.** The coordinator's tests pass against the value.
  - **As implemented.** The Playback tests pass, including step 1's transcripts against the new
    coordinator. Each rule below was removed on its own, and the tests were run once per removal:

    | Rule removed | Tests that failed |
    |---|---|
    | A replace clears a reserved replay start | `Row16_AReplaceAfterTheReplaysTake_StartsTheReplayOnTheNewPlaylist` |
    | A jump to a removed current item is refused | `Row18_ATakenItem_IsCurrentBeforeItStarts` |
    | Removing a jump's target discards the jump | `Row10_ClearAndRemovingTheTarget_DiscardAPendingJump` |
    | A repeat-mode change leaves the revision alone | `Revision_DoesNotRiseOnARepeatModeChangeALatchOrAFailure` |
    | Equality compares the queued items | `Equality_IsStructural` |
    | A skip latches only with no session attached | `RequestSkip_InvokesTheAttachedSession`, and the transcripts `DeferredStartFailure_IsSkippedLikeAFailedStart` and `CancelledPlay_OfAnItemWaitingToStart_KeepsTheItem` |
- **Step 3.** The same transcripts, the table tests and the full suite pass against the protocol.
  - **As implemented.** The 19 transcripts and 63 table cases pass, and passed 30 runs in a row. The
    full suite passes. Each rule below was removed on its own, and the session's tests were run once
    per removal:

    | Rule removed | Tests that failed |
    |---|---|
    | A fault before the first play is fatal (#191) | `FaultBeforeTheFirstPlay_IsFatal_AndNothingAdvances`, `Fault_FromTheCurrentGeneration` |
    | A Play at `Ended` does nothing (#194) | `PlayQueuedBehindTheEndOfTheQueue_LeavesTheSessionEnded`, `Play` |
    | A Pause is recorded only from `Playing` (#194) | `PauseQueuedBehindTheEndOfTheQueue_LeavesTheSessionEnded`, `Pause_RecordsPausedOnlyFromPlaying_AndPausesTheItem` |
    | A seek at `Ended` does nothing (#197) | `SeekQueuedBehindTheEndOfTheQueue_IsDropped`, `Seek_ReachesTheItemExceptAtEnded` |
    | `Ended` is left only once the warm-up has finished (#194) | `SkipDuringTheWarmUpOutOfEnded_IsDropped`, `WarmUp_LeavesEnded_OnlyOnceTheItemHasWarmed` |
    | Only an item that has played is rewound in place (#194) | `LatchedSkipUnderOne_RebuildsTheUnplayedItem`, `EndOfStream_ByWhatComesNext` |
    | A failed deferred start is handled as a failed start (#194) | `DeferredStartFailure_IsSkippedLikeAFailedStart` |
    | A cancelled Play is not an item failure (#194) | `CancelledPlay_OfAnItemWaitingToStart_KeepsTheItem` |
    | An advance takes a pending jump once its item has started (#199) | `JumpDuringAnAdvance_IsTakenBeforeAWaitingPause`, `JumpFromATransitionSubscriber_IsTakenBeforeAWaitingPause`, `AStartedItem_IsReportedThenRaised_AndTheQueueIsReadAgain` |
    | A jump request acts only on a jump still pending (#199) | `JumpDuringAnAdvance_IsTakenBeforeAWaitingPause`, `JumpFromATransitionSubscriber_IsTakenBeforeAWaitingPause`, `JumpRequested` |
    | A jump request starts an advance (#199) | `JumpDuringASeek_IsTakenWhenTheSeekCompletes`, `WarmUpOutOfEnded_HoldsOffAJumpUntilItFinishes`, `JumpRequested` |
    | An end-of-stream from a replaced run is dropped (#197) | `EndOfStreamRaisedDuringASeek_IsDropped_AndOneAfterItIsNot`, `EndOfStreamWhoseAdvanceStartsAfterASeek_IsDropped`, `StaleInputs_AreDropped_InEveryRunStateAndSlot` |
    | A command keeps the run number its item operation reports | `EndOfStreamRaisedDuringASeek_IsDropped_AndOneAfterItIsNot`, `EndOfStreamWhoseAdvanceStartsAfterASeek_IsDropped` |
    | A skip tagged with a replaced generation is dropped | `SkipRequestedBeforeAnEndOfStreamsAdvanceStarts_AdvancesOnce`, `StaleInputs_AreDropped_InEveryRunStateAndSlot` |
    | A skip requested at `Ended` is dropped | `SkipDuringTheWarmUpOutOfEnded_IsDropped`, `StaleInputs_AreDropped_InEveryRunStateAndSlot` |
    | Only a skip pauses an item that ends the queue | `LoadPlayHandOffAndEnd`, `EndOfStream_ByWhatComesNext` |
    | A failed item is never rewound in place | `Fault_FromTheCurrentGeneration` |
    | Disposal cancels the item operation in flight | `DisposalDuringAnAdvancesOpen_CancelsTheOpen`, `SkipBeforeTheFirstPlay_OutlivesADisposalThatStartsFirst` |
    | A skip before the first Play is latched during disposal | `SkipBeforeTheFirstPlay_OutlivesADisposalThatStartsFirst`, `WhileDisposing_NotificationsAreDropped_AndCommandsComplete` |
    | A command after disposal throws | `AfterDisposal_ACommandThrows_AndASecondDisposalCompletes` |

    Moving the end of `Ended` to the start of the warm-up fails no jump transcript. The reader takes
    no input while it awaits the warm-up, so a jump waits either way. A skip requested during the
    warm-up is what shows it.
- **Step 4.**
  - **The seeded defect.** The explorer is run with the rule that keeps `Ended` against a queued Play
    removed from the core. It must report an ordering that breaks the invariant on the end.
  - **The unchanged core.** It then passes, apart from the expected failure named under *Presenting*.
  - **As implemented.**
    - **The seeded defects.** `ASeededDefect_IsFound` removes a rule at the core's boundary, by
      changing the state or input one step sees, and requires the invariant that rule keeps to break.
      It covers four defects:
      - a Play at `Ended` that plays: the invariant on the end;
      - an end-of-stream whose run is not compared: an end-of-stream from a replaced run has an effect;
      - a fault whose generation is not compared: a notification from a replaced runtime has an
        effect;
      - a jump request that starts no advance: a jump is left pending while the session could take it.
    - **The unchanged core.** `TheCore_BreaksNoInvariant_OtherThanTheExpectedFailure` passes for every
      scenario, and `TheExpectedFailure_IsFound` finds the expected failure.
    - **Rules removed from the core.** Step 3's rules were removed again, one at a time, with the
      explorer's tests run alone. The explorer caught ten of the nineteen:

      | Rule removed | Explorer alone |
      |---|---|
      | A fault before the first play is fatal (#191) | caught: an advance opens an item before the first play |
      | A Play at `Ended` does nothing (#194) | caught |
      | A Pause is recorded only from `Playing` (#194) | caught |
      | A seek at `Ended` does nothing (#197) | caught |
      | `Ended` is left only once the warm-up has finished (#194) | not caught |
      | Only an item that has played is rewound in place (#194) | caught |
      | A failed deferred start is handled as a failed start (#194) | not caught |
      | A cancelled Play is not an item failure (#194) | not caught |
      | An advance takes a pending jump once its item has started (#199) | not caught |
      | A jump request acts only on a jump still pending (#199) | not caught |
      | A jump request starts an advance (#199) | caught |
      | An end-of-stream from a replaced run is dropped (#197) | caught |
      | A skip tagged with a replaced generation is dropped | not caught |
      | A skip requested at `Ended` is dropped | not caught |
      | A command keeps the run number its item operation reports | caught |
      | Only a skip pauses an item that ends the queue | not caught |
      | A failed item is never rewound in place | caught: an item whose worker faulted is rewound in place |
      | A skip before the first Play is latched during disposal | not caught |
      | Once the session has given up, a command is a no-op | caught |

      Every rule the explorer misses fails a transcript or table test when removed. The rules it
      misses decide which item plays, or when, and breaking them breaks no safety invariant.

## Revision history

- **First draft (2026-09-14).** It proposed a non-awaiting event loop with completion events, a busy
  item slot, and compare-and-set writes of the queue.
- **Revision after independent review (2026-09-14).** The review found five problems:
  - **Missing outcomes.** Operations had no failure or cancellation outcomes. Disposal would hang,
    because the controller cancels a seek without awaiting it before disposing the session.
  - **Run numbers.** The rule classified an end-of-stream during a seek as stale before the seek's
    outcome was known. That dropped a live end-of-stream when the seek was cancelled before it
    advanced the run, or a later one after the run advanced.
  - **Length of an advance.** How long an advance held off other commands was undefined. A seek
    taken up mid-advance could relaunch an item while the controller said `Paused`.
  - **Synchronous members.** `TryBeginReplay` and the other synchronous members would have waited on
    the channel.
  - **The channel.** Its capacity was unspecified, and a bounded channel would drop completions or
    deadlock a subscriber.

  The design changed:
  - The reader now awaits each step, as the controller's does (decision 3, alternative D).
  - Outcomes carry failure, cancellation and the run number (decisions 2 and 5).
  - Disposal is specified (decision 6).
  - The synchronous members read a snapshot.
  - The channel is unbounded.
  - The queue is updated under the coordinator's lock, not by compare-and-set (decision 4).

  The review also corrected the Context:
  - #191 was missing from the table.
  - The share of defects that are session orderings was overstated.
  - #197 had been listed as not covered before review.
  - #143 targets races between threads in the real code, not a model.
  - The fast tests for #196, #197 and #199 already exist.

  It completed the test plan:
  - the table tests' abstraction;
  - transcripts that fail with their fix reverted;
  - a scheduler seam for step 1;
  - an item model shared by fakes and the explorer;
  - a visited set and a quiescence phase;
  - corrected and added invariants.
- **Revision after automated review of #202 (2026-09-14).**
  - **The disposing flag.** It is now an explicit argument, `StepContext`, read on every call.
  - **Disposal.** It cancels the item operation in flight through a token passed to every awaited
    action. Today's dispose waits for an open or warm-up to finish, so this is listed as an
    observable difference.
  - **The synchronous members.** Decision 3 now says they describe the item runtime, which only a
    step changes. A finding that a queue edit leaves them stale was answered on the PR: an edit does
    not change the loaded runtime, today or in this design.
- **Amendment (2026-09-14), step 1 implemented.** The session creates item runtimes through a
  factory and starts its hops through a scheduler. Fifteen transcripts pin today's behaviour, and
  thirteen rules were each shown to fail their transcripts when removed. The rig runs hops inline
  rather than queueing them, which decision 8 records.
  - **Deferred hops, after automated review of #204.** The review found that a hop the thread pool
    starts late is an ordering the inline rig did not reproduce. The rig can now defer hops, and two
    transcripts use it. Writing them showed that the draft's `SkipRequested` needed the generation
    current at the request, or a skip that races an end-of-stream would advance twice under a single
    reader. Decision 2 now carries it.
- **Amendment (2026-09-14), step 2 implemented.** `PlaylistQueue` holds the queue as an immutable
  value with structural equality, and `PlaylistCoordinator` is the cell around it. The queue's
  tests run against the value. A skip's latch moved under the coordinator's lock.
- **Amendment (2026-09-15), step 3 implemented.** `PlaylistSessionProtocol` holds the session's rules
  as a pure step, and `PlaylistSession` is a shell around it. Decision 2's state gained `Work`, and
  `SkipRequested` gained the run state at the request.
  - **Independent reviews.** Two reviews compared the rewrite with the gated session and read the
    shell for races. They found:
    - a skip requested before the first Play that disposal dropped instead of latching;
    - reports and a clock stop that disposal handled inconsistently;
    - an `Initialize` with a cancelled token that could skip taking its start item;
    - failure paths where a stopped reader, or a cancellation callback that throws, left callers or
      `DisposeAsync` waiting.

    Each is fixed, with a test where one can pin it.
  - **A correction.** The step 1 text on what step 3 changes said a deferred hop would become an input
    posted before the later call. It becomes a delivery posted after it, and that text is corrected.
- **Amendment (2026-09-15), step 4 implemented.** `PlaylistSessionExplorer` enumerates orderings of
  scenarios over the core, with `PlaylistItemModel` shared with the rig's fakes. The unchanged core
  breaks no invariant apart from the expected failure, and four seeded defects are each found.
  - **What the explorer found.** A command could start an item after the session had given up. Commands
    are now no-ops once it has.
  - **What changed from decision 7.** It added three invariants, restated two in checkable form, and
    left the ninth-failure count to the queue's tests. *As implemented: step 4* lists each.
