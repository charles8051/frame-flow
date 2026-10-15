# ADR-0086: Work in flight is held until it ends: one owner, one disposal

## Status

**Accepted (2026-10-14) and implemented (2026-10-14)** under #718: staging slice 1 in #721, slice 2
in #732, slice 3 in #722, slice 4 in #740, slice 5 in #733 (the owners in Player and Playback) and
#727 (the views and OpenAL), and slice 6 in #741. #735 added the Debug CI leg that runs decision
3.5's use counters. [Implementation](#implementation) lists what shipped, what differs from the
decisions, and what is left. Proposed on 2026-10-14 in #712 and revised by review there;
[Revision history](#revision-history) lists what changed.

Motivating issues: #707 (a cancelled seek forgets the session's pump and graph tasks while they
still run), #708 (a second player dispose returns before the first has finished), #696 (a pass
disposed during its run frees what the run is still using), #703 (a sink that throws once stops the
pacer, and nothing reports it) and #709 (a demuxer read over a stalled source cannot be
interrupted). #701 (the pacer released a frame the sink owned) is the same family and is fixed
(#704). Two more of the same shape were found while the local fixes were made: #713 (a pass whose
graph faults can hang, because its run cancels the demux pump only after both sides have ended) and
#714 (a seek cancelled before it repositions leaves the run stopped, and a later play starts
nothing). The local fixes land first: #696 in #710 and #703 in #711, both merged, and #707 in #715.
This record replaces the mechanisms they patch.

Line numbers are on `main` at 7af3212.

**Related:**

- [ADR-0005](ADR-0005-native-resource-ownership-rules.md): the component that allocates a native
  resource frees it. This record adds when it may: only once no work that uses it can run.
- [ADR-0013](ADR-0013-cancellation-token-propagation-policy.md): decision 3 supersedes its bounded
  disposal timeout, which disposes native resources after the timeout "regardless" (`:72-74`,
  `:87`). Decision 4 answers the hung-native-call concern that motivated it (`:142`).
- [ADR-0020](ADR-0020-lifecycle-decoupled-from-processing-logic.md) and
  [ADR-0022](ADR-0022-long-lived-workers-with-pause-gate.md): cancel the source, await the
  workers, then free (`ADR-0020:53-65`, `ADR-0022:159-172`). This record makes that the only order
  and drops ADR-0020's bounded wait.
- [ADR-0026](ADR-0026-state-bound-worker-lifecycle-binding.md): decision 3 amends its
  `WorkerBinding` "Abandon" (`:216`), its compare-and-swap "no double-stop" that returns a second
  caller at once (`:253`), and its fire-and-forget seek (`:358`).
- [ADR-0036](ADR-0036-decoded-media-stream-decoupled-from-playback.md) and
  [ADR-0048](ADR-0048-seek-discipline-on-the-new-substrate.md): a seek cancels, awaits the tasks,
  then seeks and resets (`ADR-0036:321-324`, `ADR-0048:67-73`). Decision 2 makes that recipe the
  transitions of a pure core, so it cannot be skipped by an interrupted wait.
- [ADR-0044](ADR-0044-sink-ownership-and-disposal.md): decision 3 amends "a second `DisposeAsync`
  must be a no-op" (`:174-175`) to "a second call awaits the first".
- [ADR-0055](ADR-0055-decode-protocol-as-a-pure-mealy-core.md) and
  [ADR-0076](ADR-0076-playlist-session-protocol.md): the pattern decision 2 follows, a total step
  function cranked by one shell. ADR-0076 names "`SubstrateSession`'s reposition recipe as a
  protocol" as a separate change (`:626-627`); this is that change.
- [ADR-0056](ADR-0056-seek-invalidation-via-iseekresettable.md): it rejected a pure seek plan
  because the reset "has no input-dependent decision" (`:80-82`). Whether a run's work is still
  pending is such a decision. `ISeekResettable` itself stays.
- [ADR-0062](ADR-0062-gapless-playlist-warm-presenter.md) alternative F (`:497-506`) and
  [ADR-0077](ADR-0077-one-player-type.md) (`:71`): `SubstrateSession` is the item runtime. Decision
  2 gives that runtime the core alternative F deferred.
- [ADR-0042](ADR-0042-pipeline-error-handling-via-fluent-operators.md),
  [ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md) and ADR-0077 decision 4: a
  worker's fault is reported, not dropped. Decision 5 applies that to every loop an owner runs.
- [ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md) and `PresenterTeardownReaper`: the
  in-repo precedent for never freeing until the work that gates it has completed. Decision 3
  generalises it.
- [ADR-0080](ADR-0080-one-ownership-contract-for-graph-items.md),
  [ADR-0084](ADR-0084-frames-carry-their-timeline.md) and
  [ADR-0085](ADR-0085-one-lifecycle-for-views.md): their guarantees depend on a stop that has
  actually stopped. ADR-0084 installs a new timeline "after the run's tasks have stopped"
  (`:157-161`), and ADR-0085 decision 6 releases a claim after a dispose that "awaits the graph and
  the pacer's delivery loop" (`:294-296`). #707 voids both.

## Context

### The decisions are pure; the work is not

FrameFlow's pure cores decide what happens and in what order. `PlaybackProtocol` drives the
controller, `PlaylistSessionProtocol` drives the queue, `SurfaceLifecycle` and `SinkClaimState`
drive the views and claims, and `DecodeProtocol` drives the codec loop. None of them sees a task.
They emit "stop the run", "seek", "dispose the item", and a shell performs each and reports Ok,
Cancelled or Failed (`PlaylistSession.cs:639-743`).

The layer that owns work in flight has no core. "Is anything still running that uses this?" is
answered by task fields, flags and counters that each owner keeps in its own way:

- `SubstrateSession`, the item runtime, is 2,219 lines with no model of its phase. Its phase is
  spread over `_sessionCts`, `_pumpTask`, `_graphTask`, `_recoveryTask`, `_graph`, `_runNumber`,
  `_eofFired`, `_attached`, `_disposed`, the clock's `IsPaused` and the state of two gates.
- `MediaPass` on `main` frees its pipeline, decoders and demux session without looking at its run
  (`MediaPass.cs:458-497`).
- `ClockSelectVideoSink`'s delivery loop ends on any exception from the sink, and its dispose
  swallows the result (`ClockSelectVideoSink.cs:1493`).
- `PlaybackControllerCore.DisposeAsync` returns at once on a second call, guarded by a plain,
  non-volatile bool (`:1806-1808`).

### Five defects, one shape

| Issue | What happened | Where |
| ----- | ------------- | ----- |
| #696 | A pass's dispose freed the pipeline, decoders and demux under a running run | `MediaPass.cs:458-497` |
| #701 | The pacer released a frame the sink already owned, after the sink threw | `ClockSelectVideoSink.cs:1440-1453` (fixed by #704) |
| #703 | One sink throw ended the delivery loop; nothing reported it, and the item hung | `ClockSelectVideoSink.cs`, the loop and `:1493` |
| #707 | A cancelled wait forgot the pump and graph; the next seek or dispose ran beside them | `SubstrateSession.cs:2190-2211` |
| #708 | A second player dispose returned before the first had released claims; the coordinator was disposed under it | `PlaybackControllerCore.cs:1806-1808`, `PlaylistMediaPlayerCore.cs:333-346` |

None was introduced by ADR-0085. All five surfaced while implementing it, because its claim rules
made every teardown path worth tracing. #707 is on the main interactive path: the seek bar's
dispatcher issues the next seek as soon as the last is accepted (`PlaybackControllerCore.cs:1700`),
and `StartSeekRunner` cancels the previous runner (`:651-663`), so an ordinary drag supersedes
seeks whose wait is still in progress.

### SubstrateSession forgets work in five places

- **An interrupted wait.** `WaitForSessionTasksAsync` waits with `WhenAll(pump, graph)
  .WaitAsync(token)` (`:2202`), swallows the cancellation and every fault (`:2204-2205`), and its
  `finally` nulls both fields (`:2206-2209`). The next `StopGraphAsync` sees a cancelled source and
  waits on nothing (`:2159-2162`). A dispose then frees the pipeline, the decoders and the demux
  (`:1587-1623`), and a superseding seek runs `av_seek_frame` and `ResetForSeek` (`:1405-1419`),
  beside a pump that may still be in `av_read_frame`.
- **A source disposed without being cancelled.** `LaunchSessionTasks` disposes the previous
  cancellation source (`:1990`) without cancelling it. Work still on its token can no longer be
  cancelled.
- **Overwritten task fields.** The same method overwrites `_pumpTask`, `_recoveryTask` and
  `_graphTask` (`:2029`, `:2060`, `:2062`).
- **A task nothing awaits.** `_recoveryTask` is written and never read (`:175`, `:2060`). It is
  memory-safe only because `VideoDecoder.SetDiscardLevel` checks `_disposed` under `_codecSync`
  (`VideoDecoder.cs:959-963`).
- **A fire-and-forget.** The end of stream for an empty graph is `_ = Task.Run(...)` (`:2001`).

The interrupted wait breaks invariants that the pure cores rely on:

- `_runNumber` advances at `:1384` even when the wait was interrupted, so an end of stream raised
  by the old run is stamped with the new run's number. `RunNumber`'s remark promises "stopping a
  run waits for the task that raises it" (`:303-309`), and `PlaylistSessionProtocol` drops a stale
  end of stream by that number (`PlaylistSessionProtocol.cs:397-410`).
- ADR-0084's guarantee that no packet read before a break is decoded after it (`:2120-2124`).
- `DecodingPipeline.ReadUntil`, documented as "set while no pump runs" (`DecodingPipeline.cs:148-154`).
- A resume after an interrupted reposition finds a cancelled source that is not null (`:1022`), so
  it opens the gates and nothing flows.

There is also a hang. At the end of stream the pump flushes the decoders with
`CancellationToken.None` (`:2050`), writing into a bounded queue that nothing drains once the graph
is cancelled (`VideoDecoder.cs:977`, `AudioDecoder.cs:427`). `MediaPass` passes the run's token for
exactly this reason (`MediaPass.cs:306-312`).

### The passive resources have no fence

`DemuxSession`, `AudioDecoder` and `DecodingPipeline` guard their dispose with a plain bool and take
no lock (`DemuxSession.cs:353-371`, `AudioDecoder.cs:756-791`, `DecodingPipeline.cs:440-453`).
`avformat_close_input` can run while the pump is in `av_read_frame` on the same context
(`DecodingPipeline.cs:182`, `:222`). `VideoDecoder` is the exception: every native call and its
dispose take `_codecSync` (`VideoDecoder.cs:383`). The other three are safe only if their owner has
awaited every use first, and nothing checks that.

### Ten ways to dispose

The repository disposes owners of work in ten different ways:

| Mechanism | Owners | Defects it produced or allows |
| --------- | ------ | ----------------------------- |
| Plain bool, check then set | `MediaPass` (main), `PlaybackControllerCore`, `SubstrateSession`, `DecodingPipeline`, `DemuxSession`, `AudioDecoder`, the D3D converters | #696, #708, #707's frees |
| Interlocked int; the second caller returns at once | `ClockSelectVideoSink`, `ClipEncoderSink`, `MotionClipRecorder`; `PlaylistMediaPlayerCore` and `FanOutVideoSink` ignore the result | #708; latent in the pacer and recorder |
| Interlocked plus a completion source; the second caller awaits the first | `PlaylistSession`, `PreparedRuntime.DisposeRuntimeAsync`, `MediaPass` on #710 | none |
| A lock plus a flag around every native call | `VideoDecoder`, `OpenAlAudioSink`, the frame slot, `FrameFlowVideoView` | none for use after free; does not stop reordering |
| A pure "last one out" core | `PassLifecycle` (#710), `SessionUse` (#432), `SurfaceLifecycle` | none |
| Generation or binding numbers | the controller's session binding, prepared runtimes, the views | none |
| A completion source standing in for a task | `PreparedRuntime.Ready`, `MediaPass._runEnded`, `_d3d12Retired` | none |
| A bounded wait, then free, defer or abandon | `WorkerBinding` (`:139-152`), the interop view's 250 ms drain (`CompositionInteropVideoView.cs:2034`) | forgets a task after the timeout |
| Defer to a reaper, or leak rather than free | `PresenterTeardownReaper`, `D3D12YuvSharedConverter`'s fence | none |
| A hand-off to the owning thread | `SdlVideoSink` | none |

The owners that are correct all do the same three things: a second caller waits for the first, the
work is cancelled and awaited before anything it uses is freed, and a task is forgotten only once it
has completed. `PlaylistSession.DisposeAsync` (`:224-284`) and `Graph.RunAsync` (`:302-404`) are
the models.

The interop view also drops converters on its live path with untracked `Task.Run(conv.Dispose)`
(`CompositionInteropVideoView.cs:2269`, `:2271`, `:2294`, `:2320`). It does not drain presents
before them, and nothing observes their faults.

### The ADRs disagree

ADR-0020, ADR-0022, ADR-0036, ADR-0048 and ADR-0076 decision 6 all say to await the work before
freeing it. Three accepted records say the opposite:

- ADR-0013 bounds the disposal wait and then disposes native resources "regardless"
  (`:72-74`, `:87`), and rejects an unbounded wait (`:142`).
- ADR-0026's `WorkerBinding` abandons a task that does not stop in time (`:216`). Its
  compare-and-swap returns a second stop at once (`:253`), and its seek fix starts a seek and does
  not await it (`:358`).
- ADR-0044 makes a second dispose "a no-op" (`:174-175`), which in practice means it returns before
  the first has finished.

On faults, ADR-0042 skips a failed frame and continues (`:152-153`), while ADR-0069 and ADR-0077
say a mid-stream fault is reported (`ADR-0069:81-89`, `ADR-0077:222-235`). #703 did neither.

### Cancellation cannot always reach the work

No `AVIOInterruptCB` is set anywhere in `src`, so `av_read_frame` over a stalled network source
cannot be interrupted (#709). Under "cancel, then await, then free", that turns a stalled read into
a hung dispose rather than a use after free. ADR-0013 chose a bounded wait because of this
concern. The bounded wait answers a hang with a crash.

## Decision

### 1. Work is held by a scope until it completes

An owner starts its work through a `WorkScope` (an internal shell type; the name is an example). A
scope:

- starts each task under the scope's own cancellation source, linked to whatever token the work
  also needs;
- holds the task until the task completes, and only completion removes it;
- offers `StopAsync()`: cancel, then await every task the scope holds, then return the collected
  faults, excluding cancellations the stop itself caused;
- lets a caller abandon its own wait on `StopAsync` with the caller's token. The caller gets
  `OperationCanceledException`, and the scope still holds the work, so the next `StopAsync` waits
  for it.

A scope serves one run. Once stopped, it starts nothing new, and the next run gets a fresh scope.
The owner keeps the current run's scope and every older scope that still holds work, and decision 2
starts a run only when no older scope holds any. Disposal stops all of them.

Nothing that touches an owned resource is started with `_ = Task.Run(...)` or held in a bare task
field. A cancellation source is cancelled before it is disposed, and only by its scope.

### 2. The item runtime has a pure core whose state includes its work

`SubstrateSession` gets a pure Mealy core, for example `ItemRuntimeProtocol`, in
`FrameFlow.Playback.Core`, in the shape of ADR-0055 and ADR-0076: a total step function from a
state and one input to the next state and the actions the shell must perform. It owns no IO, no
`await`, no clock, no lock and no mutable state.

- **State.** The phase (Opening, Warm, Running, Stopping, Stopped, Faulted, Disposing, Disposed);
  the current run number; which of the run's work is pending (pump, graph, recovery, the empty-graph
  end of stream); which resources are alive (demux, decoders, pipeline, pacer); and a pending
  command, if one arrived while a stop was in progress.
- **Inputs.** Commands (Warm, Play, Pause, Seek with its target and policy, Rewind, Dispose) and
  completions (each piece of work ending with its run number and outcome).
- **Actions.** Start a run, cancel a run, flush the pacer, seek the demuxer, reset the decoders,
  activate or deactivate the audio sink, free a resource, report a fault, raise end of stream.
- **The rule the core exists for.** Seek the demuxer, reset the decoders, start a run, and free a
  resource are emitted only from a state in which no work of any run is pending. The exhaustive
  walk asserts it for every reachable state. The run number advances only when the old run's work
  has completed, so `RunNumber`'s remark (`SubstrateSession.cs:303-309`) becomes true by
  construction.
- **Superseding seeks coalesce.** A seek that arrives while a stop is pending replaces the pending
  target. A scrub therefore costs one unwind of the old run and one seek to the latest target, not
  one seek per superseded target. A superseded seek's caller sees it cancelled, as it does today
  (`PlaybackControllerCore.cs:651-663`).
- **A caller's cancellation is the caller's.** It abandons the caller's wait, not the runtime's
  stop: the core stays in Stopping until the completions arrive, and the next command is decided
  from there. This is ADR-0076's discipline one layer down.
- **The reposition recipe becomes transitions.** The `TaskPolicy` paths (`Cancel`, `AwaitClean`)
  and the `Reuse` and `Rebuild` choices (`SubstrateSession.cs:1280-1297`), which no ADR decided,
  become states and transitions of the core. Every one of them waits for the run's work before it
  seeks, resets or relaunches.

The shell keeps the native objects, the scope and the clocks. It performs each action and feeds
each completion back as an input. The pure cores above it, `PlaybackProtocol` and
`PlaylistSessionProtocol`, are unchanged: they still see an item runtime that reports Ok, Cancelled
or Failed.

### 3. One disposal contract for every owner of work

Every type that owns work in flight, or owns a native resource that work uses, disposes the same
way:

1. **Joined.** The first call does the teardown. Every other call, concurrent or later, awaits the
   first and sees its outcome, success or the same failure. A shared helper (for example
   `DisposeOnce`) implements it; the bool, Interlocked and compare-and-swap guards go.
2. **Cancel, then await, then free.** Cancel the owner's work and await it through its scope. Then
   free resources in reverse allocation order (ADR-0005, ADR-0020) and release claims last
   (ADR-0085 decision 6).
3. **Nothing is freed early; the caller's wait is the caller's.** No task is abandoned, and no
   resource or claim is released before the work that uses it has ended. The dispose's task
   completes when the teardown does. A caller that cannot wait that long, such as an application
   closing its window, bounds its own await (`DisposeAsync().AsTask().WaitAsync(timeout)`); the
   owner keeps the work, the resources and the claims, and frees them when the work ends, as the
   reaper does. Work the library runs is made to observe cancellation by decision 4. Work an
   application supplies, such as a sink or a graph node that ignores its token, is the case a
   caller's bound exists for. After a warning interval (for example 5 s), the owner logs once which
   work has not ended, so a stuck dispose names its cause. Where work can never be confirmed
   finished, such as a GPU fence on a device that never signals, the owner leaks the resource rather
   than freeing it and logs once, as `D3D12YuvSharedConverter` and `PresenterTeardownReaper` already
   do.
4. **Faults are returned, not swallowed.** The first call collects the failures of its own steps and
   returns them as one exception, which every caller sees. A work fault caused by the dispose's own
   cancellation is not a failure. A work fault from before the dispose was already reported
   (decision 5) and is not raised again.
5. **Passive resources state their rule.** A resource with no work of its own either fences every
   native call with the lock its dispose takes, as `VideoDecoder` does, or documents that its owner
   awaits every use before disposing it. `DemuxSession`, `AudioDecoder` and `DecodingPipeline` take
   the second rule, checked in Debug builds by a use counter that dispose asserts is zero. A lock
   around `av_read_frame` would only make the dispose wait, which decision 2 already does.

This amends ADR-0044's "no-op" second dispose, ADR-0026's abandon and compare-and-swap, and
supersedes ADR-0013's bounded disposal timeout.

### 4. Cancellation reaches native waits

- `DemuxSessionFactory` sets an `AVIOInterruptCB` tied to the scope's cancellation, so a cancelled
  run's `av_read_frame` returns `AVERROR_EXIT` promptly (#709).
- The end-of-stream flush passes the run's token, not `CancellationToken.None`
  (`SubstrateSession.cs:2050`), as `MediaPass` already does.
- A wait that the library itself performs on a bounded queue takes the run's token.

With these, decision 3's unbounded await is bounded in practice by how quickly the work observes
its token. A sink or graph node written by an application that ignores its token can still hold a
dispose, which the docs say, as #710 does for a pass.

### 5. Every loop reports its end

Every loop an owner runs ends in one of three ways: it completed, it was cancelled by its owner, or
it faulted and the fault was reported through the owner's fault path (`ReportWorkerFault`, and from
there `OnWorkerFaulted` or `OnRecoverableError`, as ADR-0069 and ADR-0077 decide). No loop ends
silently and no dispose swallows a loop's fault unreported. #703's pacer policy, which reports a
failure at a bounded rate and escalates after a run of failures, is the instance for sink failures.
The scope's collected faults are the mechanism for the rest.

### 6. One rig tests the ordering

A deterministic rig drives `SubstrateSession` through seams: a fake demuxer, decoders, pipeline and
pacer that record each native-equivalent call, count uses in flight, and hold or release on the
test's signal. Each defect above becomes a rig test:

- a seek whose wait is cancelled, then a dispose;
- a superseding seek during an unwind;
- a scrub of many seeks;
- an end of stream at the moment of a seek;
- a stop while the end-of-stream flush is parked on a full queue.

The exhaustive walk covers the core. The integration suite stays for real FFmpeg.

### Staging

Each slice carries its own BREAKING-CHANGES entry where an app can see a change.

1. **The primitives.** `WorkScope` and `DisposeOnce`, internal in `FrameFlow.Media`, with their tests. `PlaybackControllerCore` and
   `PlaylistMediaPlayerCore` move onto `DisposeOnce`, which is the fix for #708. The pacer, the OpenAL sink
   (whose overlapping disposes throw `ObjectDisposedException` today) and the interop view's second
   dispose move onto it.
2. **Native waits.** The `AVIOInterruptCB` (#709), the end-of-stream flush on the run's token, and
   the Debug use counters on `DemuxSession`, `AudioDecoder` and `DecodingPipeline`.
3. **The core.** `ItemRuntimeProtocol` with its exhaustive walk, not yet wired.
4. **The runtime.** `SubstrateSession` onto the core and a scope. `_pumpTask`, `_graphTask`,
   `_recoveryTask` and the empty-graph `Task.Run` go, the run number advances on completion, seeks
   coalesce, and the rig's seams arrive. The local #707 fix is subsumed. This is the large slice.
5. **The other owners.** `MediaPass` onto the scope and `DisposeOnce` (it is already the model after
   #710), `WorkerBinding` without its abandon, and the interop view's live-path converter disposals
   tracked through the reaper.
6. **Docs.** Amendment notes at the top of ADR-0013, ADR-0026 and ADR-0044, and a note on ADR-0056's
   rationale.

## Implementation

| Slice | Change | BREAKING-CHANGES | Shipped in |
| ----- | ------ | ---------------- | ---------- |
| 1 | `WorkScope` (decision 1), `DisposeOnce` (decision 3.1) and `SlowWorkWarning` (decision 3.3), internal in `FrameFlow.Media`, with the pure `WorkLedger`; the controller and the player on `DisposeOnce` (#708); `ClockSelectVideoSink`, `OpenAlAudioSink`, the interop view, `PlaylistSession` and its prepared runtimes on it too | 27 | #721 |
| 2 | Decision 4: an `AVIOInterruptCB` on every source, through which each demuxer call observes the token of its own operation (#709); decision 3.5's Debug use counters on `DemuxSession`, `AudioDecoder` and `DecodingPipeline` | 29 | #732 |
| 3 | Decision 2: `ItemRuntimeProtocol` in `FrameFlow.Playback.Core`, tested by a walk over every reachable state of five item shapes; not wired | none | #722 |
| 4 | Decisions 1, 2, 5 and 6: `SubstrateSession` as the core's shell, with a `WorkScope` per run; `IItemMedia` and `SubstrateMedia`, the seam the rig drives; `RunTasks<T>` and the empty-graph `Task.Run` removed; an open that fails late frees what it allocated (#725) | 31 | #740 |
| 5a | `MediaPass` on a scope and `DisposeOnce`, its first fault cancelling the other side (#713); `WorkerBinding` without its abandon; the pacer's delivery loop held by a scope; the controller's dispatch loop and seek runners on scopes, its dispose cancelling the open or play the loop waits on (#719); the player throwing what its controller's dispose threw | 30 | #733 |
| 5b | The interop view's live-path converter disposals through `PresenterTeardownReaper`; `PresenterStallWatchdog` joined to its timer through a new `DisposeAsync`; `OpenAlAudioSink`'s shutdown source never disposed (#723) | 28 | #727 |
| 6 | Amendment notes on ADR-0013, ADR-0026, ADR-0044, ADR-0056, ADR-0062, ADR-0076 and ADR-0081; notes on ADR-0048, ADR-0084 and the graph blueprint draft, which name what slice 4 removed; the comments that still described it; this section | | #741 |

Three local fixes landed first:

- #710 fixed #696. A pass's dispose cancels its run and waits for it before it frees the pipeline,
  the decoders and the demux session, and releases its claims last (BREAKING-CHANGES entry 24).
  Slice 5a put the pass on a scope and `DisposeOnce`; its pure `PassLifecycle` stays.
- #711 fixed #703. A video sink that throws costs its frame, the failures are reported at most once
  every five seconds, and more than eight in a row fault the item (entry 25). It is decision 5's
  instance for sink failures, and it stays as it is.
- #715 fixed #707 and #714. It held the pump, the graph and the lateness-recovery walk in a pure
  `RunTasks<T>` until each ended, made a cut-short wait throw rather than forget them, moved the
  end-of-stream flush onto the run's token, and made a play after a cut-short seek reposition first
  (entry 26). That is the alternative "Join the tasks and stop there", and it did more than the
  alternative says: the run number no longer advanced on an interrupted stop, and a second warm-up
  no longer started a graph beside a live one. What it left was decision 2's: the reposition paths
  still undecided, the empty-graph end of stream still a fire-and-forget, and the differences the
  core's walk found (below). Slice 4 subsumed it and removed `RunTasks<T>`. #715's nine real-media
  tests stay.

What differs from the decisions, slice by slice. None reverses one. Neutral says the public API does
not change; it gained one member, the last item under the other owners.

**The primitives (slice 1, #721).**

- Besides a cancellation the stop caused, one that the work's own token asked for is not a fault
  either. `StopAsync` returns each fault once, named, from the first stop that completes after it.
- `DisposeOnce` has two forms: a direct one, which keeps the analyzer checking what an owner's
  teardown disposes, and `RunAsync`, for a teardown that may throw before it returns its task.
- The sinks' disposal contract, ADR-0044's no-op second dispose, was amended in this slice rather
  than in slice 6, because `OpenAlAudioSink`'s second dispose could now throw the first's exception.
- `PlaylistSession` and its prepared runtimes moved too. Their second dispose already waited, but it
  succeeded after a first that had thrown.
- MotionClip's `ClipEncoderSink` and `MotionClipRecorder` keep their Interlocked guard: MotionClip is
  an application and cannot see `FrameFlow.Media`'s internals (#720).

**Native waits (slice 2, #732).**

- No owner sets the current token. Each demuxer operation installs the token it was given and
  restores the one before when it ends, so a seek after a cancelled run observes its own token, not
  the run's.
- The end-of-stream flush was already on the run's token, from #715.
- In a Debug build, a dispose with a use in flight throws `InvalidOperationException` and frees
  nothing, rather than asserting, which would end the test host.
- A read given up partway drops the part that arrived. The demuxer's position is then unreliable, so
  the session refuses reads until a seek completes, and a seek that does not complete leaves the same
  refusal. A cancelled open returns no session probed partway.
- The next operation clears the error and the end-of-file mark that an interrupt leaves on the
  format context's I/O. Nested readers, such as HLS and DASH segments and `mov` external references,
  keep theirs, as the remarks say.
- An `AVERROR_EXIT` that nothing cancelled is a fault that names it, not a fallback to the
  container's seek.

**The core (slice 3, #722).**

- The phase is two fields, not decision 2's single list: the phase (New, Opening, Open, Disposing,
  Disposed) and, while open, the run status (Idle, Running, Stopping, Stopped). Warm is a run going
  behind shut gates before the first play. There is no Faulted: a fault is reported, and the item's
  owner decides.
- The core recorded 19 places where its rule differs from `SubstrateSession` after #715. Slice 4
  shipped each of them:
  - *Stopping.* Every stop cancels a running run and waits for its work, so the `TaskPolicy` paths
    are one rule. A seek and a play rebuild the graph, and a rewind runs it again once one is built.
    The core raises each end of stream and each fault once, from the work's completion; the
    empty-graph end of stream only for a run that has not been stopped. A warm-up whose run has
    ended with no first frame starts no new run.
  - *Commands cut short.* A caller's cancellation is observed at the end of each awaited action. A
    command cut short leaves the runtime at rest: playing, held behind the gates with its clocks, or
    with its run stopped, and the next play or warm-up repositions at the clock first. A reposition
    does not clear the floor when it starts, arms the settle hold only for its own relaunch, and
    releases the hold when that relaunch ends first.
  - *The audio comes first.* A resume resumes the audio sink before it runs the clocks and opens the
    gates, and a reposition pauses the sink before it shuts them. A resume activates a sink that a
    failed reposition left inactive. A first play starts its run before its clocks.
  - *Refusals.* A disposing runtime refuses every command, a second open is refused, the core
    serialises commands, and a runtime prepared ahead refuses a play and a pause until it is
    attached.
  - *The teardown* frees the audio decoder before the video decoder, in reverse allocation order,
    and deactivates the sink of a detached runtime that activated it, which nothing reaches.

  The slice 3 audit reversed none of them. It listed what slice 4's shell had to add, and asked that
  slice 4's BREAKING-CHANGES entry carry the ones an app can see. Entry 31 does.

**The runtime (slice 4, #740).** Twelve more, from the shell. The behaviour audit found each one
acceptable.

- The #713 cancel is in the shell. When it performs the core's report of a pump's or a graph's
  fault, it cancels that run's scope first, so a pump parked on a full queue ends, and a pump's
  fault cancels the graph the same way. In the core, the cancel would break walk invariants that
  hold today.
- A command's clock inputs are read when the reader steps the command, not on the caller's thread.
  A command whose token is already cancelled when it is called does nothing.
- An attach is queued with the commands, as `AttachAsync`, which completes once the shared clocks are
  seated. A promotion whose attach fails releases the prepared runtime, and the item is opened
  instead.
- A warm-up whose run faults before its first frame completes Ok, and the fault is reported once, as
  it was before #740.
- A failure in the decoders' end-of-stream flush, which was swallowed, faults the pump.
- An immediate action that throws, such as a call to an app's clock, is logged and fails the item,
  not the command.
- The dispose throws what a stop's cancellation callbacks threw, as `MediaPass` does. A failure to
  free the provisional wallclock, once the audio sink masters the clock, is logged, not thrown.
- Run numbers can skip a value that no run launched with, when a reposition is cut short after its
  stop. An end of stream still carries its own run's number.
- The audio sink's activate, pause, resume and deactivate are called on pool threads.
- `VideoFrameBudget` and `VideoProbe` moved to `SubstrateMedia` with the video path they wire.
  ADR-0081 carries a note.

**The other owners (slices 5a and 5b, #733 and #727).**

- A pass's run starts on its scope's thread-pool task, so its audio sink's `ActivateAsync` and the
  configurators are called there (entry 30).
- Decision 3.4 also covers a pass's frees and the pacer's loop teardown, which both swallowed what
  they threw.
- `IStateBoundWorker.StopAsync` gets `CancellationToken.None`, not a token cancelled after the
  shutdown timeout. `WorkerBinding.StartAsync` completes once the worker's start has returned its
  loop, as it did before the scope moved the start onto the thread pool.
- #723 is fixed by never disposing `OpenAlAudioSink`'s shutdown source, as `WorkScope` never
  disposes its own.
- A converter retired on the interop view's live path has no caller whose wait could be bounded.
  The reaper holds it until the compositor lets it go, and names one that has waited five seconds,
  once.
- Owners left as they were, each with its reason in its pull request: `PlaylistSession`'s reader,
  its preparation of a runtime and its release of a faulted one (each joined another way), the
  inline scheduler, the interop view's logging task and interop initialisation, `SdlCommandQueue`,
  `TransportCommand.FireAndForget`, and the synchronous, UI-thread disposes of `FrameFlowPlayerView`
  and `FrameFlowVideoView`.
- `PresenterStallWatchdog` implements `IAsyncDisposable`. Its `DisposeAsync` is public, an addition
  to `FrameFlow.Media`'s API.

**The Debug CI leg (#735).** Decision 3.5's use counters exist only in Debug builds, and every CI leg
built Release (#726). Build and Test now has a `test (linux, Debug)` leg on a hosted runner. It
generates the test corpus, builds the solution in Debug, runs the Decoding, Playback, Player and
Media tests, and fails unless the three use-counter tests passed. Its first run found a racy count in
one of #715's tests, which #735 fixed. It also showed that no Release leg generates the corpus, so no
corpus-gated test runs in Release CI (#737).

**Checked on a real device.** Before #740 merged, its branch and `main` were played on a headset:
play, pause, resume, seeks, an item's end, and nine hand-offs to items prepared ahead. The two
matched, including the PRESENT GAP warning after a resume that `main` already shows (#680) and one
underrun at the end of each item.

**What a scrub costs.** Negative says the runtime slice measures it. Over 12 bursts of 15 superseding
seeks on a 1080p60 file, timed from the last seek's acceptance to the seek having settled, the median
with hardware decode was 495 to 522 ms both before and after #740. With software decode it was 941
to 1129 ms before and 740 to 806 ms after.

What is left:

- #739: an item with no audio, sought while paused after it reached its end, plays from the target
  at decode rate when it resumes, because its wallclock ran on while it was paused. `main` did the
  same before #740. The fix belongs in the core, which decides when the clocks run.
- #728: a pass whose graph completes while the demux pump waits on a full decoder queue does not
  end. It is #713's shape, reached through a completion instead of a fault; no in-box node reaches
  it.
- #729: `PresenterStallWatchdog` can run two checks at once, so a slow `Stalled` handler sees the
  stall raised again.
- #720: MotionClip's encoder sink and recorder still return at once on a second dispose.
- #737: no Release leg generates the test corpus.

## Consequences

### Positive

- Freeing a native resource, seeking the demuxer and resetting a decoder can no longer happen while
  work that uses them runs. For the item runtime, that holds by construction, checked over every
  reachable state.
- The run number, ADR-0084's timeline guarantee and ADR-0085's claim release become true by
  construction instead of by convention.
- A scrub costs one unwind and one seek, however many seeks it superseded.
- One disposal contract replaces ten mechanisms. A second dispose waits and sees the same outcome
  everywhere.
- No loop ends silently.
- Ordering defects become rig tests instead of native crashes found by reading code.

### Negative

- A dispose or a seek now waits for the old work to observe its cancellation. With decision 4 that
  is short for the library's own work. An application's node or sink that ignores its token holds
  the teardown, its resources and its claims for as long as it ignores it. The caller can bound its
  own wait, and the warning names the work, but the resources stay held until it ends.
- A seek completes after the old run has unwound, not as soon as it is accepted. Scrubbing feels
  the unwind; coalescing keeps it to one unwind per burst. The runtime slice measures it.
- Slice 4 rewrites the reposition and teardown of a 2,219-line class on the main playback path. It
  is the largest and riskiest change since the playlist protocol.
- A Debug use counter on passive resources catches a violation in tests, not in a Release build.
- More internal types: a scope, a disposal helper, and a core.

### Neutral

- The public API does not change. `PlaybackProtocol` and `PlaylistSessionProtocol` do not change.
- `ISeekResettable` stays. Only ADR-0056's reason for not having a seek plan changes.
- Resources that are reference-counted (hardware devices, Ort sessions through `SessionUse`) keep
  their counting. This record decides ordering, not lifetime by reference.

## Alternatives considered

### Keep fixing each owner

#696, #701, #703, #707 and #708 were each fixed or are being fixed in place, and each local fix is
right. The audit that found them also found more of the same shape: the overwritten task fields,
the never-awaited recovery task, the pacer and OpenAL second dispose, and the interop view's
untracked disposals. Ten mechanisms stay ten, and the next owner picks an eleventh.

### Join the tasks and stop there

Keep each task until it completes and make every stop and dispose join it, as
`PlaylistSession.DisposeAsync` and `PreparedRuntime.DisposeRuntimeAsync` do, without a core for
the item runtime. That fixes #707's use after free, and the local #707 fix does exactly this. It
lands first and is decision 1 applied to one class.

It does not fix the defects in `SubstrateSession` that are about phase rather than tasks:

- the run number advances on an interrupted stop (`:1384`);
- a resume after an interrupted reposition finds a cancelled source that is not null and starts
  nothing (`:1022`);
- nothing stops a second warm-up from starting a graph beside a live one (`:869-877`), though no
  caller is known to warm a runtime twice;
- the reposition paths (`TaskPolicy`, `Reuse`, `Rebuild`) were never decided anywhere.

Each of these follows from a phase spread over a dozen fields and flags, which each path reads and
writes in its own order. ADR-0055 and ADR-0076 met the same problem one layer up and answered it with a core,
and their exhaustive walks are what keep the decode loop and the queue correct today. Decision 2 is
that answer for the runtime.

Coalescing superseding seeks is not needed for safety. It is needed because safety makes a seek wait
for the old run to unwind, and without coalescing a scrub would pay that wait once per superseded
target.

### Bound the wait, then free

ADR-0013's choice. A bounded wait turns a hang into a use after free in native code, the class of
defect this record removes. Where work truly cannot be stopped, leaking the resource and logging is
safer than freeing it under the work.

### Reference-count the native resources

Work would hold a reference, and the last reference out would free. That is how hardware devices
and Ort sessions already work. FFmpeg's format and codec contexts are not reference-counted, so the
library would have to wrap each one. Counting also keeps a resource alive under misordered work
instead of preventing the misordering: a seek beside a running pump still corrupts the demuxer's
state, even if nothing is freed.

### Fence every native call with a lock

`VideoDecoder` does this. It prevents a use after free, but not reordering: `av_seek_frame` under a
lock still runs between two reads of a pump that should have stopped. A lock around a blocking
`av_read_frame` also makes the dispose wait for the read, which is what decision 2 does with less
contention.

### One reaper for everything

Deferring every free to a background reaper never blocks a caller. It loses the ordering ADR-0085
decision 6 needs: a claim is released only after no frame can reach the sink. The reaper stays
where it is, for GPU work that cannot be awaited on the UI thread.

### Put the tasks in the playlist protocol

`PlaylistSessionProtocol` could track each runtime's pump and graph. That couples the queue's
decisions to an item's internals, which ADR-0077 separated. The runtime's core reports Ok,
Cancelled or Failed upward, as today.

## Open questions

None remain. The draft's three were settled on acceptance:

1. **Where the scope lives.** `WorkScope` and `DisposeOnce` are internal types in `FrameFlow.Media`.
   It already shares its internals with FrameFlow.Playback, FrameFlow.Decoding,
   FrameFlow.Audio.OpenAL, FrameFlow.Avalonia and FrameFlow.Avalonia.Windows; FrameFlow.Player is
   added. FrameFlow.Graph shares its internals only with its tests, and nothing in it needs the
   types. They become public only if a sink written by an application needs them.
2. **Release-build fences.** A Debug use counter on the passive resources is enough. Decision 2 is
   what keeps a use and a dispose apart; the counter catches a regression in tests. A violation seen
   in a Release build reopens this.
3. **The views.** The interop view's 250 ms drain on the UI thread is decision 3.3 as written: it
   bounds the caller's wait, and the reaper frees only once the work that gates it has completed.
   It stays.

## Revision history

- **2026-10-14, review turn 1.** Decision 3 bounded nothing for an application's sink or node that
  ignores its token, so a dispose could hang for good. It now separates the caller's wait from the
  teardown: a caller may bound its own wait, nothing is freed or released until the work ends, and
  the owner logs which work has not ended. Decision 1 now says a scope serves one run and how older
  scopes are kept. A new alternative, "Join the tasks and stop there", says why the runtime needs a
  core and not only joined tasks.
- **2026-10-14, accepted.** Numbered ADR-0086. #713 and #714, found while the local fixes were
  made, join the motivating issues. The three open questions are settled: the scope and the disposal
  helper are internal in `FrameFlow.Media`, Debug use counters suffice, and the interop view's
  bounded drain conforms to decision 3.3.
- **2026-10-14, on implementation.** Status names the pull requests, and
  [Implementation](#implementation) lists what shipped, what differs and what is left. The
  decisions are unchanged.
