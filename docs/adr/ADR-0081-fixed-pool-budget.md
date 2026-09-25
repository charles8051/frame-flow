# ADR-0081: Fixed-pool frames are budgeted when the graph is built

**Status:** Accepted (2026-09-24), ahead of its implementation, which #373 tracks. Proposed and
revised the same day; see [Revision history](#revision-history). Not implemented.

> **Amended 2026-09-25.** #370 is reproduced ([the reproduction](../investigations/2026-09-25-d3d11va-pool-exhaustion.md)). An exhausted
> pool fails `avcodec_send_packet`, so the decode enumeration throws and the player faults; it does
> not drop pictures silently. The *What exhaustion does* paragraph below records the source reading
> that predicted a drop. The decisions are unchanged: the guard now replaces a fatal error with
> back-pressure.

**Date:** 2026-09-24

**Depends on:** [ADR-0080](ADR-0080-one-ownership-contract-for-graph-items.md), for same-instance
counting and the disposing rule.

**Amends:** [Frame-pool ownership](frame-pool-ownership.md): decision 1 (pool model as data) is
what this record reads; decision 2 (the D3D11 copy-out pool) stops being the way fixed pools are
protected; decision 7 (an observable ceiling) becomes per decoder. Also corrects how ADR-0057 and
`DecodePoolMetrics` describe pool exhaustion (#370).

**Related:** #90, #227, #231, #232, #233, #292, #294, #370. Periphery ADR-0035 §8b. Tracked in
#373.

## Context

### Two sources hand out frames from fixed pools

- **Hardware decode slices.** A `GpuVideoFrame` pins one slice of the decoder's pool until its
  final release. D3D11VA and DXVA2 pools are fixed; VAAPI, NVDEC and Vulkan are uncharacterised and
  treated as fixed (frame-pool ownership, decision 1); VideoToolbox and software decode grow. For
  H.264, FFmpeg sizes the D3D11VA pool at 20 slices, and a stream using its full 16 references
  leaves 3 spare, nearer 2 once the frame just emitted is held. Nothing in `src/` sets
  `extra_hw_frames`. Hardware frames reach the graph only when `YieldHardwareFrames` is set, and
  #294 proposes making that the default.
- **Camera leases.** Periphery's pool is `BufferCount + QueueDepth + 1` buffers (defaults 3 and 1),
  and active leases are never revoked (`CameraFramePool.cs:10`). `CameraVideoFrame` and
  `CameraFramePushBridge` put the lease itself on the graph. An open `CameraSession` exposes
  `Options.BufferCount` and `Metrics.OutstandingLeases`. `CameraPushSource` takes a session the
  caller has already opened.

### What exhaustion does

- **D3D11VA (#370).** In FFmpeg 9.0, `d3d11va_pool_alloc` logs "Static surface pool size
  exceeded." and returns NULL (`libavutil/hwcontext_d3d11va.c:263-266`). The H.264 decoder drops
  the slice and carries on (`libavcodec/h264dec.c:652-656`), and nothing in `src/` sets
  `err_recognition`. While the pool stays exhausted no frame comes out and FrameFlow sees no
  error. After it recovers, a frame that references a dropped picture can decode against a missing
  reference. ADR-0057 and `DecodePoolMetrics` call this a stall; the output stops, but the decoder
  is not waiting. Read from source, not reproduced.
- **Camera.** Under `LatestWins`, queued frames are evicted. Under `StallProducer`, the producer
  waits. Holding more than `BufferCount` downstream is, in Periphery's words, the one loss no policy
  prevents.

### Who holds frames, counted

| Holder | Frames held | For how long |
|---|---|---|
| An edge | Its capacity (default 1) | Until read |
| An operator body | 1 per pump | The call |
| A fan-out | Each branch holds independently | Per branch |
| `ClockSelectVideoSink` | 3 (`DefaultCapacity`, `:89`) | No limit while paused |
| `LatestWinsFrameSlot` in the Avalonia, CompositionInterop and SDL sinks | 1 | Until replaced |
| `PausableGate` | 1 | No limit while paused |
| `SyncJoinNode` | Secondaries within `Window` behind and `MaxLead` ahead | Durations, not counts |
| `PreRollBuffer` | `PreRollFrames` | Rolling |
| `RecordingGate` | Up to `MaxFramesPerClip` (`GateCore.cs:47`) | One clip |
| `ClipEncoderSink` | 4 segments, each up to `MaxFramesPerClip` frames | Until encoded |
| `CameraFramePushBridge` | Its channel capacity (default 1), before the source | Until read |

On the D3D11 playback path, `ClockSelectVideoSink`'s ring and the presenter's slot alone hold
four frames, more than the worst-case H.264 floor. Most streams use fewer references, which is
why that has not surfaced.

### Why not copy out at the source by default

The first draft of the ownership record (#368) proposed exactly that, with an opt-in for consumers
that release within one frame period. Review found four problems:

- The time rule is false for the player's own presenter path, which holds three frames and holds
  them without a time limit while paused.
- On D3D11 the copy needs the FrameFlow-owned texture pool (#232, #233). Frame-pool ownership gates
  that pool on #231, and it has open questions about device ownership (ADR-0064), seek and device
  loss.
- Deciding whether an opt-in is safe needs the same per-source sum a budget uses.
- A fixed-pool source that fans out to some branches that opted in and some that did not must copy
  per branch, which is the per-edge cloner the ownership record deletes.

## Decision

### 1. Holders declare a count

Every node or sink that can hold a frame past its own call declares the most frames it holds, as a
count. Time does not matter to a pool, so `ClockSelectVideoSink` declares 3 and `PausableGate`
declares 1, although both hold without a time limit while paused.

A duration does not bound a count: a variable-frame-rate source can put any number of frames into
a window. A holder bounded only by a duration therefore also declares a count cap, or counts as
unbounded. `SyncJoinNode` gains a retained-count limit next to `Window` and `MaxLead`.

At the limit, the join first releases every retained secondary that can no longer match. Between
window resets the primary's time does not go backwards, so under `MostRecentAtOrBefore` only the
newest secondary at or before the primary can match now or later, and every older one is released.
Under `Within`, a secondary whose interval ends at or before the primary is released. What
remains are candidates: under `MostRecentAtOrBefore`, the newest secondary at or before the
primary and any ahead of it; under `Within`, intervals that contain the primary's time or start
after it.

If the candidates alone reach the limit, the join stops reading its secondary edge until the
primary advances past one of them. That is the back-pressure ADR-0073 gives `MaxLead`. The primary
edge is never paused, and the join's primary loop never waits on its secondary: the secondary is
read by a separate loop (`NodePumps.cs:311`), and each primary is resolved at once against what is
retained, taking the newest match or none (`NodePumps.cs:375-381`, `SyncJoin.cs:386-388`). So a
paused secondary edge cannot stop primaries from being emitted.

What a full limit costs is match quality. A newer secondary still on the paused edge is not seen
until the primary passes the end of a retained one and releases it. Under `Within`, when every
candidate contains the primary's time, primaries keep matching the newest of them until then.
Choosing the limit by the rule below keeps that from happening in steady state.

It inherits `MaxLead`'s deadlock as well: if one branch feeds both inputs and blocks when full, the
paused secondary edge stops that branch and the primary never arrives (ADR-0073, "Why opt-in rather
than measured against `Window`"). A count limit therefore sets `IHoldsItsSecondary.StopsReadingSecondary`,
so `GraphTopology.DeadlockedForkRejoins`, which already refuses that shape for a lead, refuses it
for a count limit too. ADR-0073's rule for choosing a lead applies: set the limit above the most
candidates the join can hold at once, which under `Within` is the most overlapping intervals plus
those within the lead, or give the secondary edge a dropping policy.

An item that carries frames, `ClipSegment`, declares frames, not items. An operator declares 1 for the call in
flight, and an edge declares its capacity.

`IVideoSink` gains the declaration, because sinks hold frames outside the graph. A holder that
declares nothing counts as unbounded.

### 2. Nodes declare whether they forward their input's storage

A node that emits a new frame and releases its input, such as a converter or a readback, is a
storage boundary. A node that can forward its input's storage is not: `ToCpu` forwarding a CPU
frame, a pass-through, a view such as a crop. The node declares which it is. Nothing infers it from
the node's kind.

### 3. The budget is computed when the graph is built

For each fixed-pool source, the budget is the sum of the declared counts on every path from the
source to the first storage boundary on that path, with each branch of a fan-out added. It is a
pure function of the topology, the declarations and the source's pool model, computed where the
graph is validated: the blueprint validation proposed in
[an immutable blueprint for the graph](immutable-graph-blueprint.md), and `RunAsync` until then.

A path with an unbounded holder has no budget. A camera source then copies (decision 4). A hardware
decode source refuses to build and names the holder, because it has no cheap fallback: a readback
per frame is what the GPU path exists to avoid, and the copy-out pool is not built.

### 4. The source sizes its pool to the budget, or copies

- **Hardware decode.** When the decoder opens, `extra_hw_frames = max(0, budget - floor)`, where
  the floor is the backend's spare count from frame-pool ownership's pool model and zero for an
  uncharacterised backend. The decoder opens per playlist item, so the budget is applied per item.
  A growable backend ignores it.
- **Camera.** A session that FrameFlow opens gets `BufferCount` of at least the budget. A session
  the caller opened is checked: if `Options.BufferCount` is below the budget, the source copies
  each frame into FrameFlow's CPU storage and logs the shortfall once. It does not use
  `LeasedCameraFrame.Copy()`, which allocates a fresh array per frame.

Zero-copy is what happens whenever the budget fits. There is no per-consumer opt-in.

### 5. A guard per source backs the declarations

Each fixed-pool source counts its own outstanding frames against its budget.

- **At the budget,** the camera source copies the next frame, and the decoder waits for a release
  before its next decode. The wait makes the pool pace the decoder instead of dropping pictures
  (#370).
- **Over the budget** means a holder declared too little. The source logs itself, the budget and
  the observed count.
- **At the budget with no release for a set interval** is reported as a probable deadlock: a holder
  that will never release, rather than a slow one.

The decoder's wait observes the source's cancellation token. Stop, seek and graph teardown cancel
the source before they drain the holders, so a source parked at its budget exits, and teardown then
releases what the holders kept. While playback is paused, a decoder parked at its budget is the
intended state: it has nothing to decode ahead into.

The wait counts only frames the source has handed downstream, so it does not block lateness
recovery. `ClockSelectVideoSink` drops a late frame and its slice returns at once, and the
decoder's discard levels (lateness-driven decode skip) drop frames before they are handed on. A
source at its budget while the pipeline is late has a holder that is not releasing, and waiting is
then the right response, because decoding into an exhausted pool drops pictures (#370).

The guard's policy is a pure transition from outstanding count, budget and event to an action. The
shell owns the wait, its cancellation and the watchdog's clock. `DecodePoolMetrics` becomes per decoder and carries
the ceiling, replacing the process-wide gauge.

### 6. The D3D11 copy-out pool is not how fixed pools are protected

It stays frame-pool ownership's answer for a lookahead deeper than a decode pool can be sized for,
gated on #231.

## Alternatives considered

### A. Copy out at the source by default, with an opt-in

The first draft on #368. Rejected for the reasons in the Context.

### B. Copy out unconditionally

The simplest rule, suggested by the automated review of #368. For the camera it is one memcpy per
frame, and decision 4 does exactly this when the budget does not fit. For hardware decode it needs
the unbuilt copy-out pool or a readback per frame, which frame-pool ownership's alternative C
already rejects. Rejected as the rule for hardware frames.

### C. A runtime guard alone, with no declarations

It cannot size a pool before the decoder opens, and it finds an under-declared holder only after
the breach. Kept as decision 5, as the backstop rather than the mechanism.

### D. A fixed `extra_hw_frames` allowance, with no declarations

Suggested by the automated review of this record as proportionate to the one known path. It is
proportionate, and it is the first phase of the order of work below. As the end state it is
rejected: an allowance sized for the player is wrong for any other graph that holds hardware
frames, and without declarations nothing says which graphs those are until the guard reports a
breach.

## Consequences

### Positive

- Pools are sized so they do not run out, and the guard makes a breach visible instead of
  producing wrong pictures silently (#370).
- Zero-copy stays wherever retention fits.
- #292's case, an inference operator holding a GPU frame on a pass with no pacer, is covered: the
  operator's hold is declared and the pool is sized for it.

### Negative

- Every holder carries a declaration, including `IVideoSink`, which breaks third-party sinks. It
  only bites on paths that carry fixed-pool frames.
- The player has to know the graph before it opens a decoder, so `SubstrateSession` must validate
  the graph for an item before `VideoDecoder.Open` for that item.
- The decoder's wait is a new pacing path. It has to be reconciled with ADR-0060's rule that the
  pump is paced by a consumed stream's wait queue, and with lateness-driven decode skip.
- Each extra slice costs one decoded surface of VRAM, about 3 MB at 1080p NV12.

## Order of work

1. **The guard and a pool sized for the player.** The per-source guard (decision 5) with its
   cancellable decoder wait, per-decoder metrics with the ceiling, and an `extra_hw_frames` that
   covers the player's own path: the ring's 3 frames, the presenter slot's 1 and 1 in flight,
   less the backend's spare count. The camera source gets the same guard against `BufferCount`. No
   declarations. This covers the one path that carries hardware frames today and makes a breach
   anywhere visible.
2. **Declarations and the build-time budget** (decisions 1 to 4), once a graph outside the player
   carries fixed-pool frames by default. #294, yielding hardware frames by default, is that
   trigger. LiveCaptioning's GPU mode and #292's inference operator are the known cases waiting on
   it.

## Open questions

- Spare counts for VAAPI, NVDEC and Vulkan.
- Whether operators declare explicitly or default to 1.
- The watchdog interval.
- Whether #294, yielding hardware frames by default, waits for this record.

## Validation

None has run.

- The budget computation and the guard's transition are pure and get table tests over topologies
  from the tree: the player's D3D11 path, LiveCaptioning in GPU mode, Camera.Multicast and the
  MotionClip recorder, each with its expected per-source sum.
- #370's mechanism is reproduced before decision 5's decoder wait is built: hold frames until the
  pool-exhausted log line appears, then compare the rest of the GOP against a software decode.
  Done 2026-09-25 ([the reproduction](../investigations/2026-09-25-d3d11va-pool-exhaustion.md)): the decoder faults at 20 held frames on an H.264 clip and at 5 on an
  HEVC clip.
- With `extra_hw_frames` set from the budget, the same hold no longer produces the log line.
- A decoder parked at its budget exits when its source is cancelled. The test barriers on the
  source's own exit signal, not on a delay.

## Revision history

**2026-09-25, #370 reproduced.** Exhaustion fails the decode call rather than dropping the
picture, and HEVC's spare count on the test clip equals what the player's own path holds. See the
amendment under Status and [the reproduction](../investigations/2026-09-25-d3d11va-pool-exhaustion.md).

**2026-09-24, first automated review (#371).** Four findings. A duration bound does not bound a
count on a variable-frame-rate source, so decision 1 now requires a count cap next to any duration
bound, and `SyncJoinNode` gains one. The decoder's wait had no stated exit, so decision 5 now says
it observes cancellation and that teardown cancels sources before draining holders. The wait was
said to be able to block lateness recovery; decision 5 now says why it does not, since it counts
only frames handed downstream. Graph-wide declarations were called disproportionate to the one
known path; the new order of work lands the guard and a pool sized for the player first, and the
declarations when #294 makes hardware frames the default.

**2026-09-24, second automated review.** A join at its count limit could stop reading while it
held only secondaries that can no longer match, and stall. Decision 1 now releases those first and
pauses the secondary edge only when every retained secondary lies ahead of the primary.

**2026-09-24, third automated review.** Under `Within` an interval can contain the primary's time,
so the candidates left at the limit are not all ahead of the primary. Decision 1 now says the pause
waits on the primary advancing, inherits `MaxLead`'s fork-rejoin deadlock, and is refused by the
same topology check.

**2026-09-24, fourth automated review.** A `Within` join whose candidates all contain the
primary's time was said to stall at its limit. It does not: the join's primary loop never waits on
the secondary. Decision 1 now cites the pump and says the cost of a full limit is match quality.
