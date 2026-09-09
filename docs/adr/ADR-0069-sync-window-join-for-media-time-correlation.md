# ADR-0069: Sync-window join for media-time correlation

**Status:** Proposed (2026-09-09), implemented with one of its two migrations
shipped and measured.

### What exists

| | |
| --- | --- |
| The node, both match policies, the window | [`src/FrameFlow.Graph/SyncJoin.cs`](../../src/FrameFlow.Graph/SyncJoin.cs) |
| `PumpSyncJoinAsync`, `DrainBuffered` | [`src/FrameFlow.Graph/NodePumps.cs`](../../src/FrameFlow.Graph/NodePumps.cs) |
| `ToPrimary` / `ToSecondary` chain terminators | [`src/FrameFlow.Graph/GraphChain.cs`](../../src/FrameFlow.Graph/GraphChain.cs) |
| 10 tests covering everything under *Testing* | [`tests/FrameFlow.Graph.Tests/SyncJoinTests.cs`](../../tests/FrameFlow.Graph.Tests/SyncJoinTests.cs) |
| Migration 2, the detection overlay | [`examples/.../LiveCaptioning/MainWindow.axaml.cs`](../../examples/FrameFlow.Examples.LiveCaptioning/MainWindow.axaml.cs) |

Migration 1, the caption overlay, is **not** done. The example still carries its
`ConcurrentQueue<Caption>` and `CaptionTimeline`, because that migration wants
`Within` matching and `Within` wants the ADR-0047 lookahead, which has not
landed. `MostRecentAtOrBefore` would work today and would be a second
translation of the same behaviour the timeline already implements, so it waits.

**Date:** 2026-09-09
**Supersedes:** None
**Related:**
- [ADR-0047](ADR-0047-audio-lookahead-buffer-for-captioning-sync.md) — designed
  `CaptionMatchMode.PtsInterval` against the pipeline API that preceded the
  graph substrate. This ADR is where that matching lands.
- [ADR-0054](ADR-0054-fan-out-with-explicit-cloning.md) — the precedent for
  caller-supplied delegates on the wiring surface instead of type constraints.
- [ADR-0052](ADR-0052-motion-triggered-preroll-clip-recorder.md) §4 — the
  MotionClip recorder runs motion detection inline in one sink node for want of
  this primitive.
- [ADR-0056](ADR-0056-seek-invalidation-via-iseekresettable.md) — the seek-reset
  registration this node's retained state has to join.

## Context

### Fan-out works. Rejoin does not.

`FrameFlow.Graph` fans out well. One `OutputPort<T>` takes many `Connect` calls,
each with its own `EdgeOptions` and optional cloner. A consumer can run YOLO on
a `LatestWins(1)` branch while the display branch sits at capacity 1, and the two
branches proceed at their own rates.

Nothing brings the result back. `FrameFlow.Graph` has no node with two input
ports. Every consumer that needs one writes the same correlation by hand,
outside the graph:

| Consumer | Hand-rolled form |
| --- | --- |
| `FrameFlow.Examples.LiveCaptioning`, captions | `ConcurrentQueue<Caption>` plus `CaptionTimeline`, drained and queried inside a sink body ([MainWindow.axaml.cs:338](../../examples/FrameFlow.Examples.LiveCaptioning/MainWindow.axaml.cs:338)) |
| Same, YOLO branch | `Interlocked.CompareExchange` on `_inferenceBusy`, detections pushed to an overlay control |
| `Camera.Multicast` `ObjectDetectionPreview`, `FilteredPreview` | "a manual latest-wins latch + Task.Run worker" ([DEFERRED_WORK.md:24](../DEFERRED_WORK.md:24)) |
| `FrameFlow.MotionClip` | motion detection and pre-roll taps inline in one recorder sink (ADR-0052 §4) |

Four consumers, four spellings of one shape.

### The shape they want

For each item on a fast primary stream, find the secondary that corresponds to
it **in media time**, and emit the pair. The primary sets the cadence. The
secondary is a lookup.

The variation across the four is not in the cadence. It is in what
"corresponds" means, and there are exactly two answers:

- **Captions** carry a real interval. A caption belongs to the frames whose PTS
  falls inside `[Caption.From, Caption.To]`. Showing the most recent caption
  instead is what the example does today, and [ADR-0047](ADR-0047-audio-lookahead-buffer-for-captioning-sync.md)
  was written because it reads wrong.
- **Detections, gate state, diagnostics, control parameters** carry a point.
  The answer for the current frame is the newest one computed at or before it.
  Staleness is bounded but tolerated.

A general two-input combiner does not serve either. Correlating by arrival order
desynchronises permanently the first time one side drops an item, and
correlating on "whatever arrived last" is the behaviour ADR-0047 already
rejected. The primitive has to key on media time.

## Decision

Add `SyncJoinNode<TPrimary, TSecondary, TOut>` to `FrameFlow.Graph`. Two input
ports, one output port, a primary-driven cadence, and a retention window over
the secondary keyed by media time.

The surface is one node and two match policies. It is sized to the four
consumers above and no wider.

### 1. Timestamps come from delegates, not a type constraint

The join needs a media time from each side. `IFrame` and `IAudioBuffer` both
carry `Timestamp`, but `CaptionRef` does not; it wraps a `Caption` record whose
time lives in `From` and `To`. An `ITimestamped` constraint would force a public
type change on `FrameFlow.Whisper` and would still miss `RefBox<T>`.

Caller-supplied delegates instead, matching the ADR-0054 cloner precedent:

```csharp
public sealed record SyncJoinKeys<TPrimary, TSecondary>(
    Func<TPrimary, TimeSpan> PrimaryTime,
    Func<TSecondary, (TimeSpan From, TimeSpan To)> SecondaryInterval
)
    where TPrimary : class, IRefCounted
    where TSecondary : class, IRefCounted;
```

A point-valued secondary supplies `(t, t)`. The `Within` policy below treats a
zero-width interval as never matching, so point-valued secondaries use
`MostRecentAtOrBefore`. That is the correct pairing for them and the ADR does
not hide it behind one policy.

### 2. Two match policies

```csharp
public enum SyncMatch
{
    /// Emit the secondary whose interval contains the primary's time.
    /// Captions fed by a lookahead source (ADR-0047).
    Within,

    /// Emit the newest secondary whose interval starts at or before the
    /// primary's time, subject to MaxStaleness. Detections, gate state,
    /// diagnostics aggregates, control parameters.
    MostRecentAtOrBefore,
}
```

### 3. No match emits, it does not drop

```csharp
public delegate ValueTask<TOut?> SyncJoinOperator<in TPrimary, in TSecondary, TOut>(
    TPrimary primary,
    TSecondary? secondary,   // null when nothing matched
    CancellationToken ct
)
    where TPrimary : class, IRefCounted
    where TSecondary : class, IRefCounted
    where TOut : class, IRefCounted;
```

The body decides. A caption overlay renders the bare frame. A gate emits
`closed`.

Dropping the primary on no-match is the obvious alternative and it is wrong for
a video-primary join. Whisper's first transcription lands seconds into playback;
a join that drops until then blanks the display for those seconds. Consumers who
do want the drop return `null` from the body, which is the existing substrate
convention for "drop this input" and needs no separate policy flag.

### 4. The window is secondary retention, not primary buffering

The node retains secondaries in an ordered list, evicting entries whose `To` is
more than `Window` behind the highest primary time seen. The eviction is
`CaptionTimeline.GetActive`'s, generalised into the substrate.

**The join does not buffer the primary.** It matches against secondaries already
received and emits immediately. A primary is never held waiting for a secondary
that has not arrived.

This is a deliberate scope cut and it constrains the topology. `Within` matching
only works when the secondary leads the primary, which is exactly what
ADR-0047's lookahead buffer establishes. Making the secondary lead is the
lookahead's job. Adding primary-side reorder buffering here would put the
window's worth of latency on the display path, which no consumer wants.

Consumers whose secondary lags use `MostRecentAtOrBefore`. That covers three of
the four.

### 5. Primary EOS cancels the secondary reader

On primary EOS the pump cancels the secondary reader, drains what the channel
already holds, and completes the output. Draining an unbounded secondary to
completion would hang, and every camera-fed graph has one.

Secondary EOS does not end the join. Retained entries stay matchable until they
age out of the window.

Sibling pumps are not cancelled on clean exit. A join finishing is not a reason
to tear down branches that are still working.

### 6. Seek resets the window

Retained secondaries are pre-seek state. `FrameFlow.Graph` sits below
`FrameFlow.Decoding` and cannot implement `ISeekResettable`
([ADR-0056](ADR-0056-seek-invalidation-via-iseekresettable.md)) without
inverting the layering.

The node exposes `void ResetWindow()` and `SubstrateSession` registers a small
adapter alongside the other resettables. Moving `ISeekResettable` down into
`FrameFlow.Graph` is the cleaner answer and is left open below.

### 7. Wiring

`GraphChain` gains two terminators:

```csharp
graph.Pipeline(videoSource).ToPrimary(overlayJoin);
graph.Pipeline(captionSource).ToSecondary(overlayJoin, EdgeOptions.Buffered(16));
graph.Pipeline(overlayJoin.Output).To(presenterSink);
```

The secondary edge wants a real buffer. `LatestWins(1)`, the habit carried over
from fan-out, throws away captions a `Within` window still needs.

## Migration

This primitive ships with adapters or it does not ship.

1. **LiveCaptioning caption overlay.** *Deferred.* Deletes
   `ConcurrentQueue<Caption>`, the per-frame timeline drain, and the
   `CaptionTimeline` field from the example. Wants `Within`, which wants the
   ADR-0047 lookahead. Blocked on that, not on this node.
2. **LiveCaptioning YOLO overlay.** *Shipped.* `MostRecentAtOrBefore` over a
   2-second window. Deletes `_inferenceBusy`, `_inferencedFrameCount`,
   `_droppedWhileBusyCount`, both `Interlocked.CompareExchange` gates, and the
   two fire-and-forget inference workers — 55 lines. The detection branch's
   `LatestWins(1)` cloner edge is the skip-if-busy behaviour, as predicted at
   [DEFERRED_WORK.md:21](../DEFERRED_WORK.md:21).

`Camera.Multicast`'s two preview controls and MotionClip's inline gate follow
separately. They are listed so the primitive's reach is on record, not to widen
this change.

### Measured, 1080p25 with people on screen

Two runs of the migrated example, probe instrumentation reverted afterwards.

| | normal | inference slowed to 150ms |
| --- | --- | --- |
| Frames joined | 1140 | 690 |
| Detections emitted | 1139 | 153 |
| Frames matched | 1139 | 685 |
| Frames unmatched | 1 | 5 |
| Frame-to-detection lag | 40ms | 200–400ms |
| Audio underruns | 0 | 0 |

**The lag is the claim.** 40 ms is exactly one frame at 25fps: the tightest
pairing `MostRecentAtOrBefore` can produce, held across the whole run. Before
this change the overlay was posted from whenever inference happened to finish,
with no relationship to the picture on screen.

**The unmatched frames are the null path.** One in the first run, five in the
second — the frames displayed before the first detection landed, scaling with
how long that took. Each was emitted with `secondary: null`, not dropped. Under
the alternative in §3 the display would have been blank for that span.

**The second run is the drop path.** `yolov8n` runs in ~16ms against a 40ms
frame interval, so at normal speed the `LatestWins` edge almost never has to
drop and the run says nothing about skip-while-busy. Slowed past the frame
interval, the edge discarded roughly 78% of the inference branch while every
frame still reached the display and 685 of 690 still carried a detection.
`underruns=0` in both: the drop absorbs the rate mismatch rather than
back-pressuring the shared demux pump into starving audio. That is what
`_inferenceBusy` was for, now a property of the edge.

## Testing

All of the below are covered by the 10 tests in
[`SyncJoinTests.cs`](../../tests/FrameFlow.Graph.Tests/SyncJoinTests.cs); the
project is green at 24 tests.

- One end-to-end graph test per match policy.
- The no-match path: body receives `null`, output is emitted, primary is not
  dropped.
- Secondary EOS with the primary still running, matches continuing from the
  retained window until they age out.
- Primary EOS against an unbounded secondary, asserting the pump terminates.
- Refcount balance across all of the above, following
  [the 2026-05-30 fan-out review](../investigations/2026-05-30-graph-fanout-cloner-refcount-review.md):
  every retained, evicted, matched and unmatched secondary disposed exactly once.
- `ResetWindow()` mid-stream, asserting no pre-reset secondary matches after.

## Consequences

### Positive

- One correlation primitive replaces four hand-rolled spellings.
- `CaptionTimeline`'s eviction becomes substrate behaviour with substrate tests
  instead of domain code in `FrameFlow.Whisper`.
- Branch faults isolate. Today's fan-out-into-one-sink pattern shares a pump, so
  a failing preview takes the presenter with it.
- ADR-0047's `PtsInterval` matching gets a home. Its display-side half has been
  unimplemented since 2026-05.

### Negative

- **`AnimatedReveal` gets harder to justify.** ADR-0047 already argued the
  reveal is an illusion of liveness that real interval matching makes obsolete.
  Under `Within` it is actively wrong: it emits sub-captions with synthesised
  intervals that no longer describe when the words were spoken. Removing it is a
  separate decision this ADR does not take.
- **A second timestamp vocabulary.** The substrate already has `IClockSource`
  for pacing. This adds media-time correlation. They are different concerns,
  when to present versus what belongs together, but a reader meeting both at
  once has two time concepts to hold.
- **The delegates are unvalidated.** A `PrimaryTime` that returns non-monotonic
  values silently degrades matching. The node cannot detect the mistake.
- **Secondary edges need thought.** The `LatestWins(1)` habit from fan-out is
  wrong on a `Within` secondary. Documented on the port, still a foot-gun.

### Not settled here

- **Whether `ISeekResettable` moves into `FrameFlow.Graph`.** The adapter in §6
  works and keeps the layering. It also means a substrate node with pre-seek
  state relies on a registration living two layers up, which is the shape
  ADR-0056 was written to eliminate.
- **The shipped migration does not register that adapter.** `ResetWindow()` is
  implemented and unit-tested, and has no caller: the example builds its join
  inside `configureVideo`, where `SubstrateSession` cannot see it. Seek
  therefore degrades rather than resets — post-seek frames find pre-seek
  detections outside the staleness bound, match nothing, and the overlay clears
  until fresh detections arrive. Acceptable for a demo overlay, and not
  acceptable for a consumer whose join body has side effects. Wiring it needs a
  way for a configurator-built node to reach the session's resettable
  registration, which is the §6 question with a concrete forcing case attached.
- **Index-paired joins.** Stereo rigs and encode-verify comparisons need pairing
  by sequence index, not by time. That needs a per-item sequence number the
  substrate does not carry. Its own ADR, and its own decision about whether
  items grow metadata.
- **Three-or-more inputs.** Frame plus detections plus captions is two chained
  joins. Whether that stays acceptable is a question for the second consumer
  that wants it.
