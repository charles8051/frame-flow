# Investigation: Fan-out Cloner (ADR-0054) Substrate Review — Error-Path Refcount Leak

**Date:** 2026-05-30
**Trigger:** Post-merge architectural review of `8573798`
("feat(graph): per-edge cloner for one-shot frame fan-out (ADR-0054)").
**Scope:** `FrameFlow.Graph` (`NodePumps.ForwardAsync`, `Ports`, `Graph`,
`EdgeAxes`), `FrameFlow.MotionClip` consumer wiring, ADR-0054.

---

## Summary

`8573798` adds an opt-in per-edge cloner so multi-`Connect` fan-out works
over one-shot frame types (`Media.CpuVideoFrame`, converter outputs) whose
`AddRef` throws by design. The change is architecturally sound and aligned
with ADR-0049 §2's "multi-`Connect` IS fan-out" direction: it extends the
existing primitive rather than adding a parallel `BroadcastNode<T>`, the
typed `EdgeConfig<T>` / `WithCloner` API keeps the failure mode at compile
time, and the `RecordingGate` consumer becomes a pure state machine with
preview promoted to a graph-native sibling sink. The full solution builds
green (0 warnings).

The review surfaced **one real correctness bug** (error-path refcount leak
in the rewritten `ForwardAsync`), **one coverage gap** (no
`FrameFlow.Graph.Tests` project), and **one doc-accuracy nuance**.

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | Error-path refcount leak in `ForwardAsync` when a cloner throws | Minor (exceptional path, teardown) — but it breaks the substrate's core "every ref is disposed" invariant | **Fixed** in this change (code + ADR-0054) |
| 2 | No `FrameFlow.Graph.Tests` project; fan-out ownership matrix verified only by smoke | Medium (foundation layer, untested error paths) | Open |
| 3 | "Slow UI can't backpressure motion detection" slightly overstated | Cosmetic (doc) | Noted; design unchanged |

---

## Ownership contract (recap)

The substrate's invariant: **every reference is disposed exactly once.**
`IRefCounted.AddRef()` increments the count and returns a reference the
caller must dispose independently. Two implementation flavors coexist in
this codebase, and they differ in a way that matters for the bug below:

- **`RefBox<T>.AddRef()` returns `this`** (`src/FrameFlow.Graph/RefBox.cs`).
  An AddRef'd ref is *reference-equal* to the original.
- **`VideoFrameRef.AddRef()` returns a *new* wrapper**
  (`src/FrameFlow.Media/VideoFrameRef.cs`) around an AddRef'd inner frame.
  An AddRef'd ref is a *distinct* object.

`NodePumps.ForwardAsync<T>` is generic over `T : IRefCounted` and therefore
must be correct for **both** flavors.

---

## Finding 1: error-path refcount leak in `ForwardAsync` (fixed)

**File:** `src/FrameFlow.Graph/NodePumps.cs` (the `ForwardAsync<T>` rewrite).

### Symptom

When a per-edge cloner throws **and at least one cloner-less (inheriting)
branch is present**, the incoming `item` reference is never disposed. Its
backing buffer (for converter outputs, a `MemoryPool<byte>` rental inside a
`CpuVideoFrame`) is leaked. The graph then faults and tears down, so it is
one leaked frame per occurrence — but it is a straight violation of the
substrate's refcount invariant, on the foundation layer.

### Reproduction (the live MotionClip topology)

`RecorderPipeline.BuildGraph` fans `resizeConvert.Output` out to two
siblings, in this order:

1. `gate` — cloner-less (inherits the ref)
2. `preview` — `LatestWins().WithCloner(input => new VideoFrameRef(input.Frame.CloneCpu()))`

So `outputs = [gate(no cloner), preview(cloner)]`, `firstNoCloner = 0`.
`CloneCpu` can throw — most realistically `MemoryPool<byte>.Shared.Rent`
under memory pressure on a long-running kiosk. Trace:

1. `i=0`: `i == firstNoCloner` → `branchItems[0] = item`
2. `i=1`: `clone(item)` throws (OOM in `CloneCpu`)
3. `catch`:
   - `j=0`: `bi = item`; guard `!ReferenceEquals(bi, item)` is **false** → skipped
   - `j=1`: `bi = null` → skipped
   - `if (firstNoCloner < 0)` is **false** (it's `0`) → `item` **not** disposed
   - `throw`

`item` (the `resizeConvert` output `VideoFrameRef`) is now owned by nobody.
No write occurred, so the inheriting `gate` branch never received it, and
nothing else holds it. Leak.

### Root cause

Two distinct reasoning errors, both in the `catch`:

1. **"Destined to inherit" ≠ "received."** The pre-rewrite design disposed
   the incoming ref only when no branch was *destined to inherit* it
   (`firstNoCloner < 0`). That guard is correct on the **success** path
   (after `Task.WhenAll`, the inheriting branch's write transferred the
   ref). It is **wrong in the `catch`**: materialization runs entirely
   *before* any write, so on throw the inheriting branch has not received
   anything — the incoming ref is always still owned by `ForwardAsync` and
   must be released.

2. **`!ReferenceEquals(bi, item)` also leaks AddRef'd siblings for
   return-`this` types.** The guard was meant to avoid double-disposing the
   inherited slot. But for `RefBox` (and any `AddRef`-returns-`this`
   implementation), an AddRef'd sibling slot *is* reference-equal to
   `item`, so it is skipped too — leaking each increment produced before
   the throw. (`VideoFrameRef` happens to be safe because its `AddRef`
   returns a new wrapper, but the substrate is generic and cannot assume
   that.)

### Fix

In the `catch`, no branch has been written yet, so `ForwardAsync` still owns
the incoming ref **plus** every per-branch ref it produced. Dispose them all
unconditionally, then release the incoming ref once more iff the inheriting
slot was never assigned:

```csharp
catch
{
    // No branch has been written yet: ForwardAsync still owns the incoming
    // ref plus every per-branch ref it produced. Dispose them all. For
    // AddRef-returns-this types (RefBox) the AddRef'd slots ARE `item`, so
    // disposing each balances its increment; for new-wrapper types
    // (VideoFrameRef) they dispose independently.
    for (int j = 0; j < branchItems.Length; j++)
        branchItems[j]?.Dispose();

    // If the inheriting slot was never assigned, the incoming ref hasn't
    // been released yet. (If it was, the loop above already released it
    // exactly once via that slot.)
    if (firstNoCloner < 0 || branchItems[firstNoCloner] is null)
        item.Dispose();
    throw;
}
```

### Why the fix balances for both AddRef flavors

`item`'s refcount entering the `catch` = `1` (incoming) + (number of AddRef
siblings produced before the throw). The loop disposes every produced slot:

- **`VideoFrameRef`** — AddRef'd slots are distinct wrappers, disposed
  directly; the inherited slot (`== item`) disposes the incoming ref once;
  the `firstNoCloner` guard then suppresses a second dispose. Net: each ref
  released once.
- **`RefBox`** — inherited slot and every AddRef'd slot are all `== item`;
  the loop disposes `item` exactly `1 + #AddRef` times → refcount reaches
  zero and `onLastRelease` fires once. The guard suppresses the extra
  dispose because the inherited slot was assigned.
- **Throw before the inheriting slot** (a cloner earlier in the list throws;
  `firstNoCloner` not yet reached, or `firstNoCloner < 0`) — only cloner
  objects were produced (distinct), disposed by the loop; the guard then
  disposes the still-unconsumed incoming ref once.

No double-dispose, no leak, in every ordering.

### Second leak in the same method (all-cloner write path)

Verifying the fix surfaced a sibling leak in the **success** path. The
all-cloner dispose (`if (firstNoCloner < 0) item.Dispose();`) originally sat
*after* `Task.WhenAll(writeTasks)`. In an all-cloner topology, if a write
throws on cancellation (`WriteOrDisposeAsync` rethrows `OperationCanceledException`),
that line is skipped and the incoming ref — which no branch inherited —
leaks. Narrower than the primary finding (needs an all-cloner topology,
which no current consumer uses, *plus* mid-write cancellation), but the same
teardown-leak class introduced by the same change. Fixed by releasing the
incoming ref **before** the writes: in the all-cloner case every branch
already holds an independent clone, so the incoming ref is dead weight at
that point, and moving the dispose earlier means a throwing write only has
to account for its own branch item (which `WriteOrDisposeAsync` already
disposes). The post-`WhenAll` dispose is removed.

### ADR correction

ADR-0054's *Decision → "Cloner throws"* bullet encoded the same flawed spec
("the incoming ref is disposed if no branch was destined to inherit it").
Corrected to state that on throw no write has occurred, so the incoming ref
is always disposed (alongside every per-branch ref already produced).

### Recommended regression test (depends on Finding 2)

Wire a two-branch fan-out over a `RefBox`-backed source — branch 0
cloner-less, branch 1 with a cloner that throws — run the graph, and assert
the source `RefBox.RefCount == 0` after the run faults. With the old code
the count stays `>= 1`; with the fix it reaches `0`.

---

## Finding 2: no `FrameFlow.Graph.Tests` project (open)

`FrameFlow.Graph` is the substrate every other package builds on, yet it is
the **only** core subsystem without a test project (~14 siblings under
`tests/` have one). `RefBox` deliberately exposes `RefCount` "for
diagnostics / tests", but nothing exercises it. Per ADR-0054's Verification
section the cloner path was validated only by synthetic smoke — which is why
Finding 1 (an error path) slipped through.

Recommended: a focused `ForwardAsync` ownership suite covering the full
matrix — `Writers.Count == 0`; single branch (with / without cloner);
multi-branch all-AddRef; mixed inherit + cloner; all-cloner; and the
throwing-cloner error path from Finding 1 — each asserting final
`RefBox.RefCount == 0`.

---

## Finding 3: "slow UI can't backpressure motion detection" overstated (noted)

The preview edge's `LatestWins` policy means the preview's *consumption*
cannot backpressure the gate — the real and intended win. But the cloner
(`CloneCpu`) runs **synchronously on `resizeConvert`'s pump**, upstream of
the fan-out, on the path shared with the gate. So the clone *cost* is not
fully isolated from motion detection. This is inherent (a one-shot frame
must be copied at the fan-out point, which is the producer pump) and the
cost is negligible (~1.2 MB memcpy/frame at 640×480), so the design is fine
as-is; the prose just slightly overstates the isolation. No code change.

---

## What's right (for the record)

- **Extends the right primitive.** Multi-`Connect` stays *the* fan-out
  mechanism; no parallel `BroadcastNode<T>`. Honors ADR-0049 §2 and keeps
  one fan-out story. ADR-0054's "Why not BroadcastNode / Why not
  `Func<object,object>`" sections engage the alternatives honestly.
- **Type-safe API.** `EdgeConfig<T>` + fluent `WithCloner` → "won't
  compile", not "throws at runtime". The non-cloner `Connect` overload is
  preserved and delegates to the typed one (single code path).
- **Consumer refactor is a genuine improvement.** `RecordingGate` drops
  `_preview` and becomes a pure `Idle → Building → Idle` state machine;
  preview becomes a graph-native sibling on a `LatestWins` edge; the
  previously-flagged side-effect shortcut is removed and its XML docs
  updated to match (no stragglers).
- **Refcount discipline is otherwise meticulous** and the success path is
  correct for every fan-out shape.
