# One ownership contract for frames in every memory domain

**Status:** Proposed. Draft, pending number assignment at merge. Not implemented.

**Date:** 2026-09-24

**Supersedes on acceptance:** [ADR-0054](ADR-0054-fan-out-with-explicit-cloning.md) (fan-out with
explicit cloning).

**Amends:** [ADR-0012](ADR-0012-memory-management-for-decoded-frames.md),
[ADR-0030](ADR-0030-unify-frame-contracts-with-crossbar.md),
[ADR-0038](ADR-0038-memory-domain-pipeline-operators.md), and absorbs decisions 1, 2, 3 and 5 of
[frame-pool ownership](frame-pool-ownership.md).

**Related:**
- #41 (typed shareability), #42 (delete the pass-through wrappers), #91 (`SyncJoinNode` and
  one-shot secondaries), #93 (`LatestWins(1)` copies every frame), #90 (a joined camera
  exhausting a frame pool).
- #232 and #233 (pooled hardware frames and the D3D11 copy-out pool), #279 (`ToCpu`), #289
  (device accessors), #292 (GPU frame lifetime with no pacer), #293 (`MapToGpu`).
- #367 (frames carry no colour range).
- [ADR-0057](ADR-0057-pull-based-master-clock.md): the confirmed stall from holding a
  decode-texture lease across a clock wait.
- Periphery ADR-0035 §8b: the ref-counted camera frame contract this record adopts.

## Context

### One frame type is not ref-counted

Every item that travels the graph is reference-counted except one.

| Type | `AddRef` |
|---|---|
| `PcmAudioBuffer` | Counts, returns the same instance. Made ref-counted by ADR-0012's 2026-05-12 amendment, because audio fan-out had become routine. |
| `PooledCpuVideoFrame` (internal) | Counts, returns the same instance. |
| `GpuVideoFrame` | Counts, returns the same instance. Its doc calls this "the codebase-wide `AddRef` contract" (`src/FrameFlow.Decoding/GpuVideoFrame.cs:216`). |
| `CameraVideoFrame` | Delegates to Periphery's lease, returns the same instance (`src/FrameFlow.Camera/CameraVideoFrame.cs:79`). |
| `ITensor`, `CpuTensor<T>` | Counts (ADR-0030). |
| `RefBox<T>` | Counts, returns the same instance. |
| `VideoFrameRef`, `DetectedFaceFrameRef` | Forward to the inner frame and allocate a **new** wrapper per call (`VideoFrameRef.cs:81`, `DetectedFaceFrameRef.cs:39`). |
| `Media.CpuVideoFrame` | **Throws `NotSupportedException`** (`src/FrameFlow.Media/CpuVideoFrame.cs:85`). |

`Media.CpuVideoFrame` is produced by the decoder's CPU path (`VideoDecoder.cs:1104`), by
`SwScaleVideoConverter` (`:152`) and so by every `ConvertPixelFormat`, `Resize` and
`ResizeAndConvert` output, by `GpuFrameReadback` (`:159`), by `CloneCpu`
(`VideoFrameExtensions.cs:77`) and by `SyntheticSceneSource` (`:115`). A camera or software-decode
path passes through a converter before anything else happens to it, so this is the frame most
graphs carry.

### What that one type costs

- **Fan-out copies.** ADR-0054 added a per-edge cloner (`EdgeConfig<T>.Cloner`,
  `src/FrameFlow.Graph/EdgeAxes.cs:94`) so a one-shot frame can reach two branches.
  `ForwardAsync` (`NodePumps.cs:512`) clones the whole frame per branch. `Cloner`, `WithCloner` and
  `CloneCpu` appear 37 times in 15 files, including the MotionClip recorder and five examples.
- **Joins throw.** `SyncJoinNode` calls `AddRef` on its retained secondary at a match, which throws
  for a one-shot frame and kills the graph under `Propagate` (#91). Detections have to be wrapped
  frame-free in a `RefBox` to be joined at all.
- **`LatestWins` edges copy.** A `LatestWins(1)` branch deep-copies every frame and then drops most
  of them (#93).
- **Retaining nodes clone.** `PreRollBuffer` (`PreRollBuffer.cs:61`) and `RecordingGate`
  (`:179`, `:193`, `:208`) clone every frame they keep.
- **The interface promises what the type may not do.** `AddRef` is on `IVideoFrame`, and whether it
  works is a runtime fact of the concrete type (#41).
- **Two `AddRef` semantics.** Most types return the same instance; the wrappers allocate. The
  comments in `ForwardAsync`'s failure path handle both.
- **The wrappers are public.** `VideoFrameRef` and `PcmAudioBufferRef` exist to reshape
  `IVideoFrame` and `PcmAudioBuffer` into `IRefCounted`, and they are in the signature of
  `PlaybackController.Create`'s `configureVideo` and `configureAudio` (#42).

### Why one-shot was kept, and what that reason misses

ADR-0054 considered making converter outputs ref-counted and deferred it as "much larger scope;
touches the frame-pool design across `FrameFlow.Media`, `FrameFlow.Decoding`, `FrameFlow.Video`."
The code churn is real. The pool consequence is not: every one-shot producer rents from
`MemoryPool<byte>.Shared` (`SwScaleVideoConverter.cs:84`, `SharedMemoryFramePool`,
`VideoFrameExtensions.cs:74`), which grows on demand. Holding one of these frames longer costs
memory. It never stalls a producer.

### Where holding a frame does stall: fixed pools

Two sources hand out frames whose storage belongs to a fixed pool.

- **Hardware decode slices.** A `GpuVideoFrame` pins one slice of the D3D11VA or DXVA2 decode
  array until its last `Dispose`. [Frame-pool ownership](frame-pool-ownership.md) derives the
  spare count from FFmpeg's pool sizing: as few as two or three slices for H.264 using its full
  reference set. ADR-0057 records a confirmed stall from `PaceUntil` holding a frame across a clock
  wait, and #292 records the same shape for any operator that holds a GPU frame on a pass with no
  pacer.
- **Camera leases.** Periphery's `LeasedCameraFrame` returns its buffer to a pool of
  `BufferCount + QueueDepth + 1`, and active leases are never revoked. `CameraVideoFrame.AddRef`
  delegates to the lease, so a FrameFlow node that retains camera frames drains Periphery's pool.
  `SyncJoinNode`'s own documentation records the case: a live camera joined as the secondary of a
  paused primary pins every frame it captures (#90).

Every fixed pool is protected today by consumers remembering not to hold. The camera path is safe
mostly by accident, because its first converter copies into growable memory. `PreRollBuffer` is
safe on purpose, by cloning.

### Periphery already settled the contract

Periphery.Camera (its ADR-0035 §8b) makes every frame ref-counted, including copies it owns
outright, for interface uniformity. The library never revokes, relocates or mutates backing memory
while any reference is live. It offers two escape valves: `AddRef` shares the frame and keeps the
pool slot, and `Copy()` detaches an `OwnedCameraFrame` that pays bytes for pool independence.

This record adopts that contract for FrameFlow, and decides the part Periphery leaves to its
caller: which of the two a source with a fixed pool does by default.

## Decision

### 1. `AddRef` always works and returns the same instance

Every `IRefCounted` implementation counts references on itself, returns `this`, and releases at
zero. `AddRef` throws only after the final release, as `ObjectDisposedException`, which is a
use-after-release bug in the caller. No type implements `AddRef` by throwing
`NotSupportedException`. A composite item, a frame plus a payload such as `DetectedFaceFrameRef`,
keeps its own count and releases the frame once, at zero.

### 2. One CPU frame type over ref-counted storage

`Media.CpuVideoFrame` and `PooledCpuVideoFrame` become one type, and every current producer emits
it. `CloneCpu` stays, as an explicit copy for a consumer that wants a private buffer. It stops
being a fan-out mechanism.

### 3. Frames are immutable once published

A producer writes through a builder that owns writable memory. Publishing yields a read-only frame.
`CpuFrameData` already exposes `ReadOnlyMemory<byte>`; the public, writable
`CpuVideoFrame.PixelData` (an `IMemoryOwner<byte>`) goes. A search of `src/` and `examples/` found
no write into a frame after it was published, so this states current practice. It is also the
condition under which sharing by `AddRef` is safe.

### 4. A frame references storage, and storage carries its domain and pool model

A frame is metadata plus a reference to storage. The metadata is width, height, format, PTS,
duration, and colour range and matrix. No frame carries colour today: `H264VideoEncoder`
hard-codes limited range (`H264VideoEncoder.cs:205`) and a JPEG encoder would have to as well
(#367).

Storage records its memory domain (CPU, D3D11 texture, CUDA, and so on), its origin, and its pool
model: growable, or fixed with a spare count. This extends decision 1 of
[frame-pool ownership](frame-pool-ownership.md), pool model as data per decoder backend, to every
kind of storage.

### 5. Fixed-pool storage does not enter the graph by default

A source whose storage comes from a fixed pool copies each frame into growable storage before
emitting it:

- The camera source copies out of Periphery's lease, with the semantics of
  `LeasedCameraFrame.Copy()`.
- The D3D11 decoder copies out of its decode slice into a FrameFlow-owned texture pool, which is
  decision 2 of [frame-pool ownership](frame-pool-ownership.md).

A consumer that releases every frame within one frame period may opt in to receiving the
fixed-pool frame directly. A presenter is that consumer, and so is a converter placed directly
after the source, which already copies into growable memory as part of converting. On the common
camera path the copy is therefore the conversion that happens today, and on the D3D11 path it is
the staging copy the presenter already makes (`D3D11Nv12SharedConverter`), moved to decode time.

With this default, no node's retention can stall a producer, and memory held is bounded by each
retaining node's own bound (decision 6).

### 6. Retention is declared

A node that holds frames past its own call declares its bound as a count or a duration. The
existing ones are edge capacities, `SyncJoinNode.Window`, `ClockSelectVideoSink`'s capacity and
`PreRollBuffer`'s duration. `SyncJoinNode.MaxLead` is optional today, and `null` means a
secondary arriving ahead of the primary is retained without limit; a join whose secondary is a
frame must set it.

This is what makes the memory bound in decision 5 computable, and it is the input any budget
would need.

### 7. Domain changes are explicit, and hardware storage kinds share one GPU frame type

`ToCpu` (#279) and `MapToGpu` (#293) produce new frames, as ADR-0038 already requires.
`GpuVideoFrame.ToCpu()` already throws and redirects to the explicit readback.

A FrameFlow-owned texture (#232, decision 5 of frame-pool ownership) becomes a storage kind of the
GPU frame type, not a second frame type. Consumers branch on storage kind through accessors
(#289), not on the concrete class. `CompositionInteropVideoView.cs:790` tests
`frame is GpuVideoFrame gpu && gpu.TryGetD3D11Texture(...)`, and it is the site that shows a black
screen for any other GPU frame shape today.

### 8. Graph items are the frames themselves

`IVideoFrame` and `PcmAudioBuffer` implement `IRefCounted`. `VideoFrameRef` and
`PcmAudioBufferRef` are deleted (#42). `GraphChain<VideoFrameRef>` becomes
`GraphChain<IVideoFrame>` everywhere, including `PlaybackController.Create`'s hooks.

### 9. Fan-out is `AddRef`

`EdgeConfig<T>.Cloner` and `WithCloner` are removed. `ForwardAsync` keeps its inherit-then-`AddRef`
rule without the clone branch. ADR-0054 is superseded.

## The fixed-pool default: the alternative this record rejects

**Budgeted holding.** Let fixed-pool frames into the graph and copy out only when outstanding
leases approach the pool's spare count. It keeps zero-copy whenever the graph holds little.

Rejected, for three reasons:

- The spare count moves. It depends on how many reference frames the stream is using, so a fixed
  threshold is a guess unless the decoder reports live usage.
- The decision arrives too late. Pressure is visible only once some node already holds the frame,
  and copying a held frame means replacing it under that node, which decision 3 forbids. A budget
  therefore has to decide at admission, from predicted retention, which means summing the bounds
  of every retaining node downstream of the source.
- Decision 6 makes that sum computable. Nothing computes it today.

This is the question the review of this record is asked to test.

## Other alternatives considered

### A. Make shareability visible in the type (#41)

`TryAddRef`, or a narrower `IRefCountedFrame`. One-shot becomes a compile-time fact instead of a
runtime throw, but it stays. Every fan-out, join and `LatestWins` site still branches on it, and
the cloner machinery stays. Rejected.

### B. Keep ADR-0054

A full copy per branch per frame, and joins and `LatestWins` edges stay broken for the most common
frame. Rejected.

### C. Native ref counting through `AVFrame` and `AVBufferRef`

It would give decoder frames zero-copy sharing. Converter outputs, camera frames and synthetic
frames are not `AVFrame`s, so it cannot be the contract. It can be a storage kind later.

### D. Larger fixed pools (`extra_hw_frames`)

Alternative A of frame-pool ownership. A per-backend fallback, not a contract.

## Consequences

### Positive

- #41, #91 and #93 close by construction. #42 lands. #232 becomes a storage kind. #292 applies only
  to consumers that opt in to fixed-pool frames.
- Fan-out stops copying. MotionClip's pre-roll and recording gate hold references instead of
  clones.
- FrameFlow and Periphery share one frame contract.

### Negative

- **A public break.** The `IVideoFrame` hierarchy, the wrappers, the type argument of every
  `GraphChain` over video or audio, `PlaybackController.Create`'s hooks, `CpuVideoFrame`'s
  constructor and `PixelData`, and `EdgeConfig<T>.Cloner`. The wrappers appear in 55 files across
  `src/`, `examples/` and `tests/`.
- **Large buffers stay out longer.** A held frame keeps its `MemoryPool<byte>.Shared` buffer. A
  1080p Bgra32 frame is 8.3 MB, and `ArrayPool<T>.Shared` retains only a few arrays per size, so a
  graph that holds many frames allocates fresh large arrays and leaves them to the large-object
  heap. A dedicated growable frame-storage pool with metrics, which ADR-0012 anticipated, may be
  needed. A soak decides.
- **A copy where there was none.** A camera source whose first node is not a converter now copies:
  1080p NV12 is 3.1 MB per frame, about 93 MB/s at 30 fps.

### Neutral

- `IVideoSink.FramePool` and `CpuFramePool` are untouched. `RentAsync` has no production caller
  (frame-pool ownership's amendment), and this record does not need it.
- `ITensor` already follows the contract (ADR-0030).

## Order of work

1. The ownership core: storage, same-instance `AddRef`, immutable publish, one CPU frame type.
   Tests are pure: reference counting and storage release against a fake storage. The #91 and #93
   scenarios become tests that fail on the current tree for the right reason before the change.
2. `IVideoFrame` and `PcmAudioBuffer` implement `IRefCounted`, and the wrappers are deleted (#42).
   This is the public break.
3. Delete `Cloner`, supersede ADR-0054, and move MotionClip and the examples to references.
4. Declared retention, including a required `MaxLead` on joins whose secondary is a frame.
5. Pool model on storage, and copy-out at fixed-pool sources: the camera source first, then D3D11
   (#233, with #232 as a storage kind).
6. Colour metadata on frames.

## Open questions

- The fixed-pool default: decision 5, or budgeted holding.
- Whether a dedicated storage pool replaces `MemoryPool<byte>.Shared` for large frames.
- Whether the fixed-pool opt-in is declared by the consumer's type or by an edge option.
- Whether `IVideoSink.FramePool` survives.

## Validation

None has run. Before step 5 lands, a soak on the camera and D3D11 paths reports memory and
pool-occupancy numbers; `examples/FrameFlow.Examples.ZeroCopyInterop --soak` is the instrument
frame-pool ownership already names. Every test added in step 1 is shown to fail with the fix
reverted.
