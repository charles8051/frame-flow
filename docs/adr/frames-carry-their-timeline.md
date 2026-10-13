# Frames carry the timeline they belong to

## Status

Draft, pending number assignment at merge. **Proposed** (2026-10-12). Not implemented.

Motivating issues: #621 (`PresentedResults` has no item boundary), #641 (LiveCaptioning shows
captions for audio from before a seek or loop) and #624 (a PRESENT GAP warning when a new item
starts after `Ended`). It would also replace the inference rules that #620 and #619 added to the
Whisper window and to `CaptionTimeline`, and the example machinery in #670. Not covered: #669 (an
audio-only file leaves the previous file's caption on screen) and #671 (audio that ends before its
video stalls the picture). [What it does not fix](#what-it-does-not-fix) says why.

Line numbers are on `main` at 2452a17.

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

- **Order.** Ids come from one process-wide counter and only increase, so a larger id is a later
  timeline. Equality says "same timeline"; order says "older" or "newer".
- **Promise.** Within one id, presentation times lie on one continuous media timeline. Across ids,
  nothing about presentation times is promised.
- **Scope.** Ids are opaque, process-local and not persisted.

### 2. The session mints one per run, and the decoders stamp it

`SubstrateSession` allocates a new id each time it launches a run: the first play of an item, every
relaunch after `RepositionAsync`, and every new session. `MediaPass` allocates one for its run.

The session hands the id to the decoders in the same pass that runs `ResetForSeek`, before the
run's first packet is decoded. The decoders put it on every frame and buffer they create. So every
item a run produces carries that run's id, and nothing decoded before the reposition does. A paused
seek launches no run until Play, so the first frame after it is produced by the new run and carries
the new id.

The run number (`SubstrateSession.cs:194`) and the pacer's run id stay as they are for now. Slice 4
may derive them from the id.

### 3. Frames and buffers carry it, and derived items copy it

- `IFrame.Timeline` and `IAudioBuffer.Timeline` are default interface members returning
  `TimelineId.None`, so external implementations keep compiling. Sample aspect ratio and rotation
  took the same route (`IVideoFrame.cs:58`, `:61`).
- `CpuVideoFrame`, `GpuVideoFrame` and `PcmAudioBuffer` store it. Their `Create` factories take it
  as a trailing optional parameter (ADR-0080's fill pattern).
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

### 5. Stateful consumers follow one rule

A pure core, `TimelineFollower` in `FrameFlow.Graph.Core`, answers one question: given the timeline
a consumer is on and the timeline of an arriving item, is the item **current**, **newer** (reset,
then take it) or **older** (drop it)? An item or a consumer on `None` is treated as current, so a
consumer fed by an untracked source keeps its present behaviour, including any presentation-time
rule.

The consumers in the table above adopt it:

| Consumer | Newer | Older |
| -------- | ----- | ----- |
| Whisper window | drops the partial window | drops the buffer |
| `CaptionTimeline` (fed the presented frame's timeline, and each caption's) | starts empty | drops the caption (#641) |
| `PresentedResults` | clears results of older timelines (#621) | drops the result |
| `SyncJoinNode` | clears the secondary window | drops the item |
| PRESENT GAP tracker | starts over (#624) | not applicable |

The Whisper window keeps its 50 ms rule and `SyncJoinNode` its backward-step rule for `None`
only. `CaptionTimeline` keeps `Reset()` for callers with no tracked source.

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
     buffer after it carry a larger id than everything before it, and the items between two breaks
     share one id.
   - A conformance test over every in-repo producer of `IVideoFrame` and `IAudioBuffer`: a derived
     item has its input's id. Sample aspect ratio and rotation should be added to the same test.
2. **Follow it.** `TimelineFollower` and the adopters in decision 5. Closes #621 and #624; moves
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
- Because ids are ordered, a consumer can drop stale items as well as reset on new ones.
- Forwarding and fan-out need nothing (ADR-0080).

### Negative

- Every node that builds a new frame or buffer must copy the id. A missed copy turns it into
  `None` downstream, which consumers treat as continuous: today's behaviour, without a warning. The
  conformance test in slice 1 is the guard, and it has to grow with every new producer.
- The `Create` factories change binary signature. Source that calls them compiles unchanged.
- External `IVideoFrame` and `IAudioBuffer` implementations report `None` until they store an id,
  and their consumers keep today's behaviour.
- Each frame and buffer carries eight more bytes.
- Two mechanisms remain, `ISeekResettable` for the session's components and the id for everything
  else. Decision 6 says which is which.

### Neutral

- Presentation-time semantics do not change.
- Two players in one process interleave ids. A consumer follows one stream, so only equality and
  order within that stream matter.

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
