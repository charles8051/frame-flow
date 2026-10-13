# ADR-0084: Frames carry the timeline they belong to

## Status

**Accepted (2026-10-13).** Not yet implemented. Proposed on 2026-10-12, and revised on acceptance
for items prepared ahead (#674): ids identify a timeline but carry no order, and order comes from
presentation. [Revision history](#revision-history) lists what changed.

Motivating issues: #621 (`PresentedResults` has no item boundary), #641 (LiveCaptioning shows
captions for audio from before a seek or loop) and #624 (a PRESENT GAP warning when a new item
starts after `Ended`). It would also replace the inference rules that #620 and #619 added to the
Whisper window and to `CaptionTimeline`, and the example machinery in #670. Not covered: #669 (an
audio-only file leaves the previous file's caption on screen) and #671 (audio that ends before its
video stalls the picture). [What it does not fix](#what-it-does-not-fix) says why.

Line numbers are on `main` at 2452a17, except where the revision added them, which are at 45acf7e.

**Related:**

- [ADR-0056](ADR-0056-seek-invalidation-via-iseekresettable.md): `ISeekResettable`, the reset the
  session runs on its own components at a reposition. This record keeps it and covers what it
  cannot reach (decision 6).
- [ADR-0073](ADR-0073-sync-window-join.md): the sync-window join, whose backward-step rule is one
  of the inferences this replaces for tracked sources. Its open question on moving
  `ISeekResettable` into `FrameFlow.Graph` is declined here.
- [ADR-0076](ADR-0076-playlist-session-protocol.md) decision 5 and
  [the playlist end-of-queue draft](playlist-end-of-queue-replay-and-faults.md): the session numbers
  each run of an item. This record turns that number into a value that leaves the session.
- [Playlist events name their item](playlist-events-name-their-item.md): identity on transport
  events. This record puts identity on the data, because events are not ordered against frames.
- [ADR-0080](ADR-0080-one-ownership-contract-for-graph-items.md): the ownership contract for graph
  items. Decision 3 follows its rules for a field that new items copy from their inputs.
- [ADR-0036](ADR-0036-decoded-media-stream-decoupled-from-playback.md) and
  [ADR-0022](ADR-0022-long-lived-workers-with-pause-gate.md) use "epoch" for a cancellation scope.
  This record says "timeline" so the two are not confused.

## Context

### The session knows when the timeline breaks

A timeline breaks whenever the next frame is not the continuation of the last one: a load, a seek,
a loop, the next item, a `ReplaceAsync`, a clip that opens at a start position. The session decides
and carries out every one of them, and two code paths cover them all.

- **A reposition.** `SubstrateSession.RepositionAsync` (`SubstrateSession.cs:1281-1462`) serves a
  seek, a loop, a hand-off to another item of the same source, and a clip's start. In order, it
  closes the gates, stops the run, increments the session's run number (`:1337`), flushes the pacer
  (`:1347`), seeks the demuxer, resets the registered components (`:1368-1371`), reseats both clocks
  (`:1376`, `:1393`) and, if playing, launches the next run. A seek rebuilds the graph
  (`:1121-1128`); a loop and a same-source hand-off run the same graph again (`:1174-1208`).
- **A new session.** The next item from a different source disposes the old runtime and opens a
  new `SubstrateSession` (`PlaylistSessionProtocol.cs:916-1025`), with a new graph and pacer and the
  same warm sinks.

Counters for these breaks already exist: the controller's session generation
(`PlaybackControllerCore.cs:121-130`), the playlist's item generation
(`PlaylistSessionProtocol.cs:703`), the session's run number (`SubstrateSession.cs:194`), the
pacer's run id (`ClockSelectVideoSink.cs:458`). None of them reaches a frame.

### An item prepared ahead decodes before it plays

An item can be prepared while another plays (#674). Its runtime opens detached, positions at its
start and warms up without touching the shared sinks or clock (`SubstrateSession.cs:119-120`,
`:199`, `:835`), so its run starts, and its decoders produce items, before it is promoted.

Those items wait behind its closed gates. Every graph is wired source, gate, configurator, sink
(`SubstrateSession.cs:1735-1760`, `:1899-1911`), so no configured operator, tap or sink sees them
until the runtime is promoted and its gates open. The outgoing item is disposed, and its graph
stopped, before the next item is promoted or opened (`PlaylistSessionProtocol.cs:927-948`). So
inside the player's graphs and at its sinks, items arrive in presentation order, whichever runtime
made them. A consumer the player feeds through a path of its own, such as a channel read by another
task or a second graph, has no such guarantee: an item delayed on that path can arrive after the
next timeline's first item. And the order in which runs start does not match presentation order
at all: a prepared run starts before the playing item's later loops and seeks.

### Nothing downstream is told

A video frame carries its presentation time, duration, format, memory domain, sample aspect ratio
and rotation (`IVideoFrame.cs:36-95`). `PcmAudioBuffer` carries its presentation time
(`PcmAudioBuffer.cs:62-137`). `InferenceResult<T>` carries a timestamp (`InferenceResult.cs:24-40`),
a `Caption` its `From` and `To`, and `FramePresentedInfo` the presentation time and the wall-clock
time it was shown (`IFramePresentedSource.cs:18`). Nothing says which run produced it.

`ISeekResettable` reaches exactly the three components the session registers at open
(`SubstrateSession.cs:736-743`): the two decoders and the decoding pipeline. It does not reach an
operator that a configurator wired in, a sink, or anything on the far side of an asynchronous
boundary, and ADR-0056 made discovery a non-goal. `Graph.BeforeEachRun` (`Graph.cs:100-105`) tells
an operator inside a graph that runs again, but has no caller in `src/`.

### So every stateful consumer guesses

| Consumer | How it detects a break | What it misses |
| -------- | ---------------------- | -------------- |
| Whisper window (`WindowClock.cs:68-70`, #638) | a buffer more than 50 ms from where the held audio ends | a jump under 50 ms |
| `CaptionTimeline` (`CaptionTimelineState.cs:69-72`, #640) | a presentation time below its clock | a forward jump, which needs the caller's `Reset()`; captions still in flight |
| `PresentedResults` (`PresentedResults.cs:83-88`) | a result earlier than the last bumps a private counter | a new item whose first timestamps are not earlier than the old item's pending results (#621) |
| `SyncJoinNode` (`SyncJoin.cs:486-496`) | a primary that steps back past its window | a smaller step back |
| PRESENT GAP tracker (`AvaloniaVideoSink.cs:59`, `:209`) | none | the pause before a new item reads as a stall (#624) |
| LiveCaptioning (`MainWindow.axaml.cs:225-229`, `:269`, `:319-326`) | its own count of graphs, and a presented time below the last | a loop, which keeps the graph; captions in flight (#641) |

#670 fixes the last row by adding a wrapper type and a run counter to the example.

One consumer does not guess. `ClockSelectVideoSink` is told: the session calls `Flush` and
`BeginRun` at every reposition (`ClockSelectVideoSink.cs:809-827`, `:903-931`), and it keys its
presentation lag on a run id so it never compares two timelines (`:655-683`). It is the only
stateful consumer here with no open issue of this kind.

Each rule is local, reasonable and reviewed. Together they are five answers to a question the
session has already answered, and each misses cases another catches. The last two batches of fixes
added two of them.

### Events cannot carry it

The transport raises events at these breaks, but not in order with the frames:

- After a seek, the first frame of the new position very likely presents before `SeekStateChanged`
  returns to `NotSeeking` (`PlaybackControllerCore.cs:668`, `:1367-1370`).
- After a loop, `SourceTransitioned(Loop)` and `LoopRestarted` are raised after the relaunch
  (`PlaylistSessionProtocol.cs:1128-1146`).
- After a different-source item, `SourceTransitioned` is raised after Play's outcome, with the
  gates already open (`PlaylistSessionProtocol.cs:1049-1057`).

ADR-0075 decision 5 states the rule: "It does not wait for a frame to be presented." A consumer
keyed on events sees some frames of the new timeline before it hears of it, and still has to sort
the items already in flight into before and after.

## Decision

### 1. A timeline is a value

`TimelineId` is a `readonly record struct` over a `long`, in `FrameFlow.Graph` beside `IFrame` and
`IAudioBuffer`. `TimelineId.None` (0) means unknown.

- **Identity, not order.** Ids come from one process-wide counter, so no two runs share one, but
  an id minted later is not a later timeline. An item prepared ahead mints its id at warm-up,
  before the ids the playing item mints at its later loops and seeks. Order comes from
  presentation (decision 5).
- **Promise.** Within one id, presentation times lie on one continuous media timeline. Across ids,
  nothing about presentation times is promised.
- **Scope.** Ids are opaque, process-local and not persisted.
- **Lineage.** Ids are compared only within one lineage: the items that reach one player's sinks,
  whichever of its runtimes made them. A run's video and audio carry the same id, and so does
  anything derived from them, such as an inference result or a caption. Items from independent
  sources, such as two players or a camera and a file, are never compared.

### 2. The session mints one per run, and the decoders stamp it

`SubstrateSession` allocates a new id each time it launches a run: the first play of an item, every
relaunch after `RepositionAsync`, and every new session. `MediaPass` allocates one for its run.

The decoders put the current id on every frame and buffer they create. The session installs a new
one on a fixed point of the reposition order: after the run's tasks have stopped
(`SubstrateSession.cs:1321-1332`), and after `ResetForSeek` has replaced each decoder's packet
queue, flushed its codec and dropped the pipeline's retained packet (`IVideoDecoder.cs:33-44`,
`SubstrateSession.cs:1368-1371`). The next run launches after that. No packet read before the break
is left to decode, and no codec state built from one is left to drain, so nothing from before the
break can come out under the new id. A new session has new decoders and no earlier work. A paused
seek launches no run until Play, so the first frame after it is produced by the new run and carries
the new id.

A prepared runtime (#674) mints its id when its run launches at warm-up. Its items wait behind its
closed gates until it is promoted (see Context), so no consumer downstream of the gates sees them
before the playing item's last item.

The run number (`SubstrateSession.cs:194`) and the pacer's run id stay as they are for now. Slice 4
may derive them from the id.

### 3. Frames and buffers carry it, and derived items copy it

- `IFrame.Timeline` and `IAudioBuffer.Timeline` are default interface members returning
  `TimelineId.None`, so external implementations keep compiling. Sample aspect ratio and rotation
  took the same route (`IVideoFrame.cs:58`, `:61`).
- `CpuVideoFrame`, `GpuVideoFrame` and `PcmAudioBuffer` store it. Each `Create` factory gains an
  overload that takes it (ADR-0080's fill pattern). The existing signatures stay and produce
  `None`, so a binary built against an earlier release keeps running. A trailing optional parameter
  would keep source compatibility but remove the old signature, and a binary that calls it would
  fail with `MissingMethodException`.
- A node that builds a new item from an input copies the input's id, as the converters copy sample
  aspect ratio and rotation today (`SwScaleVideoConverter.cs:78-87`). That covers the converters,
  GPU readback and upload, `CloneCpu`, `TensorToImage`, the resampler, `InferenceResult<T>` (from
  its frame) and `CaptionRef` (from the audio it was transcribed from).
- Under ADR-0080 a forwarded item is the same object and fan-out is `AddRef`, so every branch sees
  the same id with no further work.

### 4. Presentation reports it

`FramePresentedInfo` gains `Timeline`, the presented frame's id. This is how a consumer outside the
graph (an overlay, a caption display, `PresentedResults`) learns which timeline is on screen, in
order with the frames, without listening to transport events.

### 5. Stateful consumers take order from presentation

Two pure cores in `FrameFlow.Graph.Core` answer the question for the two kinds of consumer. Both
keep the current id and the few ids they left most recently, so an item that arrives late from a
timeline already left is recognised as **past** and dropped, whatever path delayed it. They differ
only in what an id they have not seen means:

- **`TimelineFollower`, for a stream.** An unseen id starts a **new** timeline: the consumer
  resets, then takes the item, and the id it was on becomes past. A consumer inside the player's
  graphs, or at its sinks, sees items in presentation order (see Context), so a new id there is the
  next timeline. A consumer behind a path of its own relies on the past rule for items that path
  delays.
- **`PresentedTimeline`, for derived items matched against what is shown.** It is fed the
  presented frame's id (decision 4). A derived item, such as an inference result or a caption, is
  **current** when its id is on screen and **past** when its id has been left. An unseen id is
  **upcoming**: the consumer holds the item, within its existing bounds, until its id is presented.
  A derived item can arrive before its frame presents, which is why an unseen id is not a new
  timeline here.

A follower tracks one lineage (decision 1). A consumer with two independent inputs keeps one
follower per input. An item or a follower on `None` is treated as current, so a consumer fed by
an untracked source keeps its present behaviour, including any presentation-time rule.

The consumers in the table above adopt them:

| Consumer | Follows | On a new timeline | On a past item |
| -------- | ------- | ----------------- | -------------- |
| Whisper window | its input stream | drops the partial window | drops the buffer |
| `SyncJoinNode` | each input's stream, separately | on the primary, clears the secondary window | drops the item on that input |
| PRESENT GAP tracker | the presented frames | starts over (#624) | does not arise (frames present in order) |
| `PresentedResults` | the presented timeline | clears results of the timeline it left (#621) | drops the result |
| `CaptionTimeline` | the presented timeline | starts empty | drops the caption (#641) |

A join's inputs need not come from the same player, so it never compares one input's id with
the other's. The Whisper window keeps its 50 ms rule and `SyncJoinNode` its backward-step rule for
`None` only. `CaptionTimeline` keeps `Reset()` for callers with no tracked source.

### 6. `ISeekResettable` stays for what the session constructs

`ISeekResettable` remains the reset for components the session constructs and registers. The
timeline id serves everything else. This declines ADR-0073's open question about moving
`ISeekResettable` into `FrameFlow.Graph`, and the `ISessionResettable` entry in
`docs/DEFERRED_WORK.md`. A reset callback cannot reach a consumer past an asynchronous boundary, and
it cannot tell an item already in flight from one produced after it. An id on the item can.

### Staging

1. **Carry it.** `TimelineId`, the interface members, storage, minting, stamping, copying, and
   `FramePresentedInfo.Timeline`. Tests:
   - For each break (load, seek while playing, seek while paused, loop, same-source hand-off,
     different-source item, `ReplaceAsync`, clip start), the first video frame and the first audio
     buffer after it carry an id no earlier item carries, and the items between two breaks
     share one id.
   - A reposition while the decoders hold queued packets and codec delay: no item stamped with the
     new id has a presentation time from the old position.
   - An item prepared ahead (#674) and promoted after the playing item has looped: its items start
     a new timeline for every follower, although its id was minted first.
   - Pure tests for both followers, including an item of a left timeline that arrives after the
     next timeline's first item, through a delayed path.
   - A conformance test over every in-repo producer of `IVideoFrame` and `IAudioBuffer`: a derived
     item has its input's id. Sample aspect ratio and rotation should be added to the same test.
2. **Follow it.** The two followers and the adopters in decision 5. Closes #621 and #624; moves
   the Whisper window and `CaptionTimeline` off presentation-time rules for tracked sources.
3. **Use it in the example.** LiveCaptioning drops its graph counter and its presentation-time
   checks, and keys captions on the presented timeline. Closes #641; #670 is closed as superseded.
4. **Optional.** Derive the session's run number and the pacer's run id from the timeline id.

## Consequences

### Positive

- One fact, decided once, by the code that causes the break.
- Exact where the rules guess: jumps under 50 ms, forward seeks, a new item whose timestamps do
  not go back, items in flight.
- It reaches consumers outside the graph and past asynchronous boundaries, because it travels with
  the data.
- It does not depend on the order of transport events.
- Presentation gives the order, so a consumer can drop stale items as well as reset on new ones,
  and an item prepared ahead needs no special case.
- Forwarding and fan-out need nothing (ADR-0080).

### Negative

- Every node that builds a new frame or buffer must copy the id. A missed copy turns it into
  `None` downstream, which consumers treat as continuous: today's behaviour, without a warning. The
  conformance test in slice 1 is the guard, and it has to grow with every new producer.
- Each `Create` factory has two overloads, one with the timeline and one without.
- External `IVideoFrame` and `IAudioBuffer` implementations report `None` until they store an id,
  and their consumers keep today's behaviour.
- Each frame and buffer carries eight more bytes.
- A follower keeps a short history of the timelines it has left. A late item from a timeline older
  than that history is unseen: a stream follower would reset to it, and a presented-timeline
  follower would hold it until the consumer's bounds evict it. The history has to be longer than
  the number of timelines a delayed path can fall behind by, which a few entries cover.
- Two mechanisms remain, `ISeekResettable` for the session's components and the id for everything
  else. Decision 6 says which is which.

### Neutral

- Presentation-time semantics do not change.
- Two players in one process interleave ids. A consumer follows one lineage and compares ids only
  for equality, so the interleaving does not matter.

### What it does not fix

- **#669.** An audio-only item presents no frames, so nothing tells an overlay keyed on
  presentation that the timeline changed. The audio buffers do carry the id, so a consumer that
  reads the audio side can act on it, but a display driven by presented frames cannot. That needs a
  presentation signal for audio-only playback, which is a separate question.
- **#671.** Audio that ends before its video is not a break in the timeline. The audio sink is the
  master clock, and nothing takes over timing when its stream ends. That is a clock-ownership
  question for its own record.

## Alternatives considered

### Keep the per-consumer rules

This is the status quo, and it is what the last two batches extended. Each rule is cheap, but each
misses cases, and every new stateful consumer has to invent another. The table under
[So every stateful consumer guesses](#so-every-stateful-consumer-guesses) is the cost so far.

### Extend `ISeekResettable` to operators and sinks

ADR-0073 left this open, and `docs/DEFERRED_WORK.md` lists an `ISessionResettable` with a
discontinuity kind. A callback has three problems here. It cannot reach a consumer the session does
not know about, such as a caption graph fed through a channel. It arrives at one instant, while
items from before the break may still be queued, so the consumer still cannot tell them apart.
And a loop re-runs the graph without re-running the configurator, so a callback would need
registration that ADR-0056 chose not to provide.

### An in-band discontinuity marker

A marker item in the stream would be exact and ordered. But graph items are the frames themselves
(ADR-0080), so a marker needs either a union item type on every edge or a sentinel frame. Every
operator would have to forward it, and a fan-out or a join would have to decide what a marker on
one branch means for the other. An id on each item gives the same ordering without a new kind of
item.

### Identity on events only

The [events draft](playlist-events-name-their-item.md) names the item on transport events, and
that stays useful. It cannot replace an id on the data, because events are not ordered against
frames ([Events cannot carry it](#events-cannot-carry-it)).

### Reuse the session's run number

`SubstrateSession` already numbers its runs. The number restarts with each new session, so it is
not unique across items, and it does not leave the session. Making it process-wide and putting it
on the items is this decision.

### Order ids by when they are minted

The draft did this, and it fails once an item is prepared ahead (#674). The prepared item mints its
id at warm-up, so after the playing item loops, the next timeline on screen has the older id, and a
drop-older rule discards it. Minting at promotion instead does not work: the prepared item's frames
are decoded, and stamped, before promotion, and a frame is immutable once published (ADR-0080).

### Call it an epoch

"Epoch" is the usual word, but ADR-0036 and ADR-0022 already use it for a cancellation scope.
"Timeline" also names what the id promises: one continuous run of presentation times.

## Open questions

1. **Live sources.** Should a camera capture mint one id per capture start, and a new one on a
   camera switch, or leave `None`? The proposal is one per start.
2. **Captions.** Does the id go on `CaptionRef` only, or on the public `Caption` record too? Adding
   a positional member to `Caption` breaks code that constructs it.
3. **The latest-wins slot.** Reading the code suggests a frame held in a sink's latest-wins slot
   before a seek can present after it. That has not been seen at run time. If it happens, slice 2
   should drop an older-timeline frame from the slot.
4. **A preview on a paused seek.** If any path presents the destination frame before Play, that
   frame must come from a run that minted the new id. The code read for this record found no such
   path.
5. **Audio-only display.** Should the transport also expose the current timeline id, for example
   on `PlaylistTransition`, so a consumer with no presented frames (#669) can follow it?

## Revision history

- **2026-10-13, on acceptance.** #674 landed while the draft was in review: an item prepared ahead
  starts its run, and mints its id, while another item still plays. The draft's ordered ids would
  have made a prepared item look older than the loop it follows. Decision 1 now makes ids identity
  only. Decision 5 takes order from presentation, with a follower for in-order streams and one for
  derived items matched against what is shown. Context and decision 2 say why a prepared item's
  items still reach consumers in presentation order. Staging gains the test, and Alternatives the
  minted-order design. Review then found that order holds only inside the player's graphs and at
  its sinks, not on a consumer's own delayed path, so both followers remember the timelines they
  left and drop late items from them.
