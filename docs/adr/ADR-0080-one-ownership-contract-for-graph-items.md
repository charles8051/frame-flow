# ADR-0080: One ownership contract for graph items

**Status:** Accepted (2026-09-24), ahead of its implementation, which #372 tracks. Proposed and
revised the same day; see [Revision history](#revision-history). Not implemented.

**Date:** 2026-09-24

**Supersedes:** [ADR-0054](ADR-0054-fan-out-with-explicit-cloning.md) (fan-out with explicit
cloning).

**Amends:** ADR-0005 rule 3 and ADR-0009 (which thread frees), ADR-0012 (single ownership of
video frames), ADR-0030 (the frame contract), ADR-0052 §3 (pre-roll by cloning), ADR-0066 (the
`Detach`-based sink adapter), ADR-0073 decision 8 (`maxLead`), ADR-0078 decision 1 (the inherit
marker).

**Not in this record:**
- What a source with a fixed pool does with its frames: [ADR-0081](ADR-0081-fixed-pool-budget.md).
- Storage kinds and the D3D11 copy-out pool: [frame-pool ownership](frame-pool-ownership.md),
  which stays provisional on #231.
- Colour range and matrix on frames (#367): additive, no break needed.

**Related:** #41, #42, #90, #91, #93, #369, #370. Tracked in #372.

## Context

### Every item on the graph counts references, except one, and they count differently

| Type | `AddRef` | A `Dispose` past zero |
|---|---|---|
| `PcmAudioBuffer` | Same instance | Clamps silently (`PcmAudioBuffer.cs:196`) |
| `PooledCpuVideoFrame` (internal) | Same instance | Clamps silently (`:158-163`) |
| `GpuVideoFrame` | Same instance | Clamps silently (`GpuVideoFrame.cs:381`) |
| `RefBox<T>` | Same instance | Throws (`RefBox.cs:57`) |
| `ClipSegment` | Same instance, and revives a released segment (`ClipSegment.cs:67-70`) | Ignored |
| `EncodedPacket` | Same instance | Not checked; the count frees nothing (`EncodedPacket.cs:93-100`) |
| `CameraFrameAdapter` | Same instance, forwards every `Dispose` to the lease | Per the lease |
| `CameraVideoFrame` | Same instance, but its `Dispose` acts once per instance, so a shared frame dies under its second holder and leaks a lease (#369) | Ignored |
| `VideoFrameRef`, `PcmAudioBufferRef`, `DetectedVideoFrameRef`, `DetectedFaceFrameRef` | A new wrapper per call | Forwards to the inner item |
| `CaptionRef` | A new instance with its own count (`CaptionRef.cs:42`) | Not checked (`:48`) |
| `Media.CpuVideoFrame` | **Throws `NotSupportedException`** (`CpuVideoFrame.cs:85`) | Disposes the buffer owner again |

`ITensor` is `IDisposable`, not `IRefCounted` (`ITensor.cs:42`), and does not travel the graph.

`Media.CpuVideoFrame` is what the decoder's CPU path (`VideoDecoder.cs:1104`), every
`VideoOperators` converter (`SwScaleVideoConverter.cs:152`), `GpuFrameReadback` (`:159`),
`CloneCpu` (`VideoFrameExtensions.cs:77`) and `SyntheticSceneSource` (`:115`) produce. A camera
or software-decode path passes through a converter first, so it is the frame most graphs carry.

### What the one-shot frame costs

- **Fan-out copies.** ADR-0054's per-edge cloner (`EdgeConfig<T>.Cloner`, `EdgeAxes.cs:94`) copies
  the whole frame per branch. `Cloner`, `WithCloner` and `CloneCpu` have 29 code references in 14
  files across `src/` and `examples/`, or 37 in 16 files with `tests/`. LiveCaptioning is the one
  example on `WithCloner`; four others call `CloneCpu` to feed preview panes.
- **Joins throw.** `SyncJoinNode` calls `AddRef` on its retained secondary at a match, which throws
  and kills the graph under `Propagate` (#91).
- **`LatestWins` edges copy.** A `LatestWins(1)` branch copies every frame and drops most (#93).
- **Retaining nodes clone.** `PreRollBuffer` (`:61`) and `RecordingGate` (`:179`, `:193`, `:208`)
  clone every frame they keep.
- **The interface promises what the type may not do** (#41), and **the wrappers are public**, in
  `PlaybackController.Create`'s hooks and `IPlayerBuilder`/`IPassBuilder.ConfigureVideo` (#42).

Estimated from frame sizes and rates, not measured, the copies that sharing would remove:

| Example | Copies per frame | Rate |
|---|---|---|
| Multicast, Multicast.Dml | 3 × 8 MiB at 1080p | about 750 MB/s at 30 fps |
| Camera.Multicast | 3 × 4 MiB at the 720p cap | about 380 MB/s |
| LiveCaptioning, CPU mode | 8 MiB, about 78% then dropped (#93) | about 210 MB/s at 25 fps |
| MotionClip | 2 × 2 MiB | about 126 MB/s |

### Why one-shot was kept, and what that reason missed

ADR-0054 deferred ref-counted converter outputs as "much larger scope; touches the frame-pool
design across `FrameFlow.Media`, `FrameFlow.Decoding`, `FrameFlow.Video`." The churn is real. A
pool consequence is not: every one-shot producer rents from `MemoryPool<byte>.Shared`
(`SwScaleVideoConverter.cs:84`, `SharedMemoryFramePool`, `VideoFrameExtensions.cs:74`), which
grows. Holding one of these frames longer costs memory and never stalls a producer.

On .NET 10 the shared array pool keeps up to 32 arrays per size class in each of its partitions,
one partition per core. A review probe on a 24-thread machine returned 200 arrays of 8 MiB, forced
two compacting gen-2 collections, and got all 200 back. In every fan-out in the tree, sharing holds
no more buffers than cloning does, so memory falls. What remains: the pool rounds up to a power of
two (1080p NV12 is 35% over its 4 MiB class, 640x480 Bgra32 71% over 2 MiB), keeps its high-water
mark until a gen-2 trim, is shared with the rest of the process, and has no gauge.

### The pools that do stall are a separate problem

Hardware decode slices and camera leases come from fixed pools, and holding them longer can exhaust
them (#90, #370). This record makes counting uniform and leaves what a fixed-pool source does to
[ADR-0081](ADR-0081-fixed-pool-budget.md). Periphery's camera frames already follow the counting
contract below (Periphery ADR-0035 §8b), and Periphery leaves copying versus sharing to the holder.

## Decision

### 1. Counting

Every `IRefCounted` item keeps an atomic count on itself, updated with `Interlocked`. `AddRef`
returns `this`. `AddRef` after the final release throws `ObjectDisposedException` and never
revives the item. A composite item, a frame plus a payload such as `DetectedFaceFrameRef`, keeps its
own count and releases the frame once, at zero. `ClipSegment`, `CaptionRef` and `EncodedPacket`
change to match.

### 2. Disposing

Each `Dispose` releases exactly one reference. A `Dispose` that acts once per instance, as
`CameraVideoFrame`'s does, is a defect on any shareable item (#369). A wrapper forwards every call.

Releasing below zero calls `Debug.Fail` and increments an over-release counter. It never throws:
the pumps dispose inside `catch` blocks, and `DrainUntilCompletedAsync` abandons the rest of its
drain at the first exception (`NodePumps.cs:628-645`). This replaces the five behaviours in the
table.

A counting bug now damages every branch rather than one. An extra `Dispose` while the count is two
or more returns a buffer that other branches are still reading to any renter in the process,
including native `sws_scale` and encoder pointers. The debug checks here and in decision 5 exist
for that.

### 3. Forwarding an input

A body forwards an input by returning that same object. The pumps treat "the result is the input"
as moving the reference (`NodePumps.cs:124`, `:425-428`), so a body that returns `input.AddRef()`
leaks one reference per item. `SyncJoin`'s guidance that any other return value "must be freshly
built or `AddRef`'d" (`SyncJoin.cs:72-77`) stays true for other values.

The join pump releases every input reference except the one it forwards. Today, when the primary
and the secondary are the same instance (two branches of one source) and the body returns it,
neither reference is released and one leaks. `ForwardAsync` (`:559`) and `AdvanceAndMatch` call
`AddRef` and keep using the reference they already hold.

### 4. The CPU frame counts, then becomes one type

`Media.CpuVideoFrame` counts references first, with its public surface unchanged. It then merges
with `PooledCpuVideoFrame` into one CPU frame type that carries up to three planes.
`PooledCpuVideoFrame` already has three strides; `Media.CpuVideoFrame` has one buffer, and
`CloneCpu` copies only the Y plane (`VideoFrameExtensions.cs:40-48`). `CloneCpu` stays, as an
explicit private copy that handles planar formats, and stops being a fan-out mechanism.

### 5. Frames are immutable once published

A frame is created by one call that rents storage, runs a fill callback over writable `Span`
planes, and returns the read-only frame:

```csharp
var frame = CpuVideoFrame.Create(format, width, height, pts, state,
    static (planes, state) => { /* write planes.Y, planes.U, planes.V */ });
```

The spans exist only inside the callback. They cannot be stored on the heap, the callback cannot
return them, and no builder object exists for a caller to hold across publication, so no writable
alias outlives the fill. A `static` lambda with an explicit state argument allocates nothing. Every
current producer fills its buffer synchronously, including through a pointer for `sws_scale`
(`fixed` inside the callback), so the shape fits all of them. The storage object replaces today's
per-rent `IMemoryOwner` wrapper rather than adding an allocation. `PcmAudioBuffer` gets the same
factory.

If the fill callback throws, the factory returns the storage to its pool and rethrows the
original exception unchanged. No frame is published, so nothing else can hold the storage.

This removes `CpuVideoFrame.PixelData` and `PcmAudioBuffer.SampleData` (`:64`), both public
`IMemoryOwner`s through which any holder can free shared storage, and the unused
`PooledCpuVideoFrame.WriteData` (`:186`).

A search of `src/` and `examples/` found no write into a published frame, so this states current
practice. The compiler enforces the callback's scope for safe code. A producer that keeps a pointer
past the callback, through `fixed`, `MemoryMarshal` or pinning, can still write after publication;
not doing so is the producer's obligation, and the runtime does not check it. The `ReadOnlyMemory` views from `AsCpu()` are not covered by the count, so a
use-after-release reads stale data only sometimes. Debug builds fill released CPU storage with a
known pattern so it fails every time.

### 6. Graph items are the frames themselves

`IVideoFrame : IFrame, IRefCounted`, with `new IVideoFrame AddRef()` and a default
`IRefCounted.AddRef()` bridge, the pattern `IVideoFrame` already uses for `Timestamp`
(`IVideoFrame.cs:58`). `IAudioBuffer` follows the same shape. Video chains are
`GraphChain<IVideoFrame>`. Audio chains are `GraphChain<PcmAudioBuffer>`, because Whisper, the
resampler and the OpenAL sink all use the concrete type. `VideoFrameRef` and `PcmAudioBufferRef`
are deleted (#42), and `CameraFrameAdapter` becomes redundant once #369 is fixed.

The three uses of `VideoFrameRef.Detach` are replaced:

- **Sink adapters** (`SinkAdapters.cs:79`, `:102`) hand the item itself to `PresentAsync`, and the
  sink disposes it. ADR-0066's ownership rule is unchanged.
- **`VideoOperators.ToCpu`** (`:181`) forwards a CPU frame by returning it (decision 3).
- **LiveCaptioning's early release of a decode slice** (`MainWindow.axaml.cs:605`) becomes a
  `ToCpu` node ahead of the detector, which releases the GPU frame when its body returns.

### 7. Fan-out is `AddRef`

`EdgeConfig<T>.Cloner` and `WithCloner` are removed. `EdgeConfig<T>` then holds only its options
and is deleted. `ForwardAsync` keeps its inherit rule without the clone branch. ADR-0078 decision
1's inherit marker only mattered when `AddRef` returned a fresh wrapper, so it and
`GraphChainForkTests`' tagged-wrapper case go. ADR-0054 is superseded.

### 8. A join whose secondary is a frame sets `MaxLead`

Once #91 is fixed, a join no longer throws on a frame secondary. Without a lead bound it then
retains every secondary that arrives ahead of the primary (#90, and `SyncJoin.cs:171`). ADR-0073
decision 8 made `maxLead` optional; for frame secondaries it becomes required, checked when the
join is constructed.

That is a compatibility break for a join whose secondary is a frame type that already counts, such
as `GpuVideoFrame`: it works today without `MaxLead` and will fail at construction. No in-tree join
has that shape; the one `SyncJoinNode` in the tree, in LiveCaptioning, joins a
`RefBox<DetectionSet>`. The break is intended. Such a join retains every secondary ahead of the
primary, and for `GpuVideoFrame` each one pins a decode slice (#90, #370). A join over
`Media.CpuVideoFrame` secondaries throws at its first match today, so it has no working form to
break.

## Alternatives considered

### A. Make shareability visible in the type (#41)

`TryAddRef`, or a narrower `IRefCountedFrame`. One-shot becomes a compile-time fact but stays, so
every fan-out, join and `LatestWins` site still branches on it and the cloner stays. Rejected.

### B. Keep ADR-0054

A full copy per branch per frame, with joins and `LatestWins` edges still broken for the most
common frame. Rejected.

### C. Native counting through `AVFrame` and `AVBufferRef`

It would give decoder frames zero-copy sharing, but converter outputs, camera frames and synthetic
frames are not `AVFrame`s, so it cannot be the contract. It can back a storage kind later.

### D. Copy-on-write instead of immutability

A writer holding the only reference could write in place. Nothing writes into a published frame
today, so nothing needs it. It stays compatible with decision 5 if a writer appears.

## Consequences

### Positive

- #41, #91 and #93 close by construction, #42 lands, and #369 is fixed along the way.
- Fan-out, pre-roll and recording hold references instead of copies.
- One counting rule and one disposing rule for every item.

### Negative

- **A public break.** About 77 of the 2,112 entries across 12 projects' `PublicAPI.Unshipped.txt`:
  43 typed with the wrappers, 14 on the `Detected*` types, 10 on `EdgeConfig`, `Cloner`, `Connect`
  and `Branch`, 6 `AddRef` signatures, 2 on `CpuVideoFrame`'s constructor and `PixelData`, and
  `PcmAudioBuffer.SampleData`. The surfaces most consumers touch are `PlaybackController.Create`,
  `IPlayerBuilder`/`IPassBuilder.ConfigureVideo` and `ConfigureAudio`,
  `Mp4VideoWriter.WriteAsync`/`RecordAsync`, the decoder and camera source adapters, and
  `VideoOperators`/`AudioOperators`. Every `PublicAPI.Shipped.txt` is empty, so the analyzer will
  not flag the removals; the break goes in `docs/BREAKING-CHANGES.md`, as ADR-0078's did.
- **Tests that change.** `CpuVideoFrameTests` (`PixelData`, `Dispose_CalledTwice_ForwardsToPixelDataTwice`),
  `VideoConverterTests`, 6 of 9 `ForwardAsyncFanOutTests`, the cloner cases in
  `GraphChainForkTests`, `SinkAdaptersTests`, `ToCpuOperatorTests`, and five test fakes whose
  `AddRef` throws.
- **The final release runs on whichever thread drops the last reference.** That is already true of
  `GpuVideoFrame`. It amends ADR-0009's rule that the allocating thread frees, and ADR-0005 rule 3:
  a managed handle over a native resource may be shared by count, while the native pointer still
  never escapes.

## Order of work

**1a. Non-breaking.** `Media.CpuVideoFrame` counts references. The counting rule and the disposing
rule land on every type, including #369 and `ClipSegment`, `CaptionRef` and `EncodedPacket`. The
pumps get the forwarding fix. Joins with a frame secondary require `MaxLead`. This closes #91 and
unblocks #93.

Tests, pure and on fake storage, each shown to fail with the fix reverted:

- One test run over every counted type: `AddRef` returns the same instance; N `AddRef`s and N+1
  `Dispose`s release the storage once, on the last; `AddRef` after the final release throws and
  does not revive; an extra `Dispose` never releases twice.
- Every exit path of every pump ends balanced, barriered on `RunAsync` completing, never on a delay.
- #91: a second primary matching a CPU-frame secondary (fails today at `SyncJoin.cs:404`).
- #93: a `LatestWins(1)` branch with no cloner (fails today at `NodePumps.cs:559`).
- A join whose primary and secondary are the same instance ends balanced.

No multi-threaded stress test of the counter: it would test `Interlocked`, and it would flake.

**1b. Breaking**, in one minor release with step 2: storage, the fill factory, the merged planar CPU
type, and the removal of `PixelData`, `SampleData` and `WriteData`.

**2. Graph items are frames.** `IVideoFrame` and `IAudioBuffer` implement `IRefCounted`, the
wrappers and `Detach` go, and the sink adapters, `ToCpu` and LiveCaptioning change as decision 6
describes. This is the riskiest step: it touches every dispose path in 55 files, and a mistake
there fails silently.

**3. Delete the cloner** and `EdgeConfig<T>`, supersede ADR-0054, and move MotionClip and the
examples to references.

## Open questions

- Whether a FrameFlow-owned CPU frame pool, with exact size classes and an occupancy gauge,
  replaces `MemoryPool<byte>.Shared`. A soak with a byte gauge decides; the rounding waste above is
  the case for it.
- Whether `IVideoSink.FramePool` and `CpuFramePool` survive. `RentAsync` has no production caller,
  and the pool's frames are written after construction, which decision 5 forbids.

## Validation

None has run. Step 1a's tests are the gate for the counting and forwarding rules. Before step 1b,
a soak on the Multicast and camera examples reports memory with a byte gauge, before and after.

## Revision history

**2026-09-24, first review.** Four independent reviews and the automated panel on #368 read the
first draft. That draft also decided storage kinds, a copy-out default for fixed-pool sources, an
opt-in for consumers that release within a frame period, and colour metadata. The reviews found
the opt-in contradicted by the player's own presenter path (`ClockSelectVideoSink` holds three
frames, without a time limit while paused), the D3D11 half dependent on work frame-pool ownership
gates on #231, and the storage abstraction read by nothing the draft decided. Those parts moved to
[ADR-0081](ADR-0081-fixed-pool-budget.md) and frame-pool ownership. The reviews also added the
disposing rule, the forwarding rule, the `MaxLead` requirement and the split of step 1; corrected
the inventory, the counts and the memory analysis; and found #369.

**2026-09-24, second automated review.** A `ref struct` builder consumed by `Publish` did not stop
writes through a `Span` obtained before `Publish`, which stays usable in the same scope. Decision 5
now creates a frame with one call and a fill callback, so the spans never outlive the fill, and says
plainly that a producer keeping a pointer past it is on its own. Decision 8 now names the
compatibility break for joins over already-counted frame secondaries, which the first revision
denied.

**2026-09-24, third automated review.** Decision 5 now says what the fill factory does when the
callback throws: it returns the storage and rethrows the original exception.
