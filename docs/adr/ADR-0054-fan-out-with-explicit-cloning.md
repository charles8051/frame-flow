# ADR-0054 — Fan-out with explicit cloning for one-shot frames

Status: Accepted (2026-05-30)
Supersedes / extends: ADR-0049 §2 (substrate fork, primitives that may
be removed later) — this ADR fills a gap that analysis left open.

## Context

`FrameFlow.Graph` (forked from Crossbar per ADR-0049) supports
multi-consumer fan-out two ways today:

1. **Multi-`Connect` on an `OutputPort<T>`.** `Graph.Connect` appends a
   `ChannelWriter<T>` per downstream edge; `NodePumps.ForwardAsync`
   then duplicates the item for each branch by calling
   `(T)item.AddRef()` (except for the last branch, which inherits the
   incoming ref to save one AddRef/Dispose pair).
2. **`StorageNode<T>` as an explicit identity pump.** An older shape
   from the Crossbar era; ADR-0049 §2 flagged it as vestigial — "the
   2026-05-22 exploration analysis showed it's a vestigial identity
   pump — multi-`Connect` on an `OutputPort<T>` already supports
   fan-out without a dedicated node type. Probably removable later;
   keeping for V1."

Both paths assume the flowing item supports `AddRef`. For pooled
frame types (e.g. `Playback.CpuVideoFrame`) and `RefBox<T>` wrappers
this is fine. For **one-shot frame types** — `Media.CpuVideoFrame`
(decoder output) and every converter output produced by
`VideoOperators.{ConvertPixelFormat, Resize, ResizeAndConvert}` —
`AddRef` throws `NotSupportedException` by design. The first frame
that arrives at a multi-`Connect` fan-out point on such an output
crashes the graph.

The existing workaround, used by
`FrameFlow.Examples.Multicast{,.Dml}` and previously by
`FrameFlow.MotionClip.RecordingGate`, is to author a single
`SinkNode<VideoFrameRef>` whose body calls `frame.CloneCpu()` N
times and dispatches each clone to one downstream consumer. This
works but has costs:

- The fan-out is **not graph-native** — each branch shares one pump,
  so a slow consumer back-pressures every other branch and back up
  to the source.
- The branches can't have differentiated edge policy
  (`Buffered`/`LatestWins`/`Block`/`DropOldest`) because there are
  no actual edges — it's a sink calling N awaitables.
- The pattern is duplicated across consumers; each one re-invents
  the same per-branch clone-and-dispatch shape.

This ADR closes the gap by extending the multi-`Connect` path with
an **opt-in per-edge cloner**, so the same fan-out mechanism that
already works for AddRef-able items also works for one-shot items
with a caller-supplied clone function.

## Decision

Extend the per-edge configuration that flows into `Graph.Connect`
with an optional **typed cloner**, used by `NodePumps.ForwardAsync`
for that specific outgoing branch instead of `item.AddRef()`.

### API shape

A new typed config wrapper, paired with a `WithCloner` helper on
`EdgeOptions`:

```csharp
// FrameFlow.Graph
public readonly record struct EdgeConfig<T>(
    EdgeOptions Options,
    Func<T, T>? Cloner
)
    where T : class, IRefCounted;

public static class EdgeOptionsExtensions
{
    public static EdgeConfig<T> WithCloner<T>(
        this EdgeOptions options,
        Func<T, T> cloner
    )
        where T : class, IRefCounted
        => new(options, cloner);
}

// New Connect overload (typed):
public Graph Connect<T>(
    OutputPort<T> from,
    InputPort<T> to,
    EdgeConfig<T> config
)
    where T : class, IRefCounted;

// Existing overload preserved unchanged:
public Graph Connect<T>(
    OutputPort<T> from,
    InputPort<T> to,
    EdgeOptions? options = null
)
    where T : class, IRefCounted;
```

Call site:

```csharp
// Two consumers of the same output; the second one needs an
// explicit clone because the upstream frame is a converter output.
graph.Connect(resizeConvert.Output, gate.Input);
graph.Connect(
    resizeConvert.Output,
    previewSink.Input,
    EdgeOptions.LatestWins().WithCloner(
        input => new VideoFrameRef(input.Frame.CloneCpu())
    )
);
```

### Substrate semantics

`OutputPort<T>.Writers` becomes `List<OutputEdge<T>>` (internal),
where `OutputEdge<T>` carries the per-branch `ChannelWriter<T>` plus
the optional `Cloner`.

`NodePumps.ForwardAsync<T>` is rewritten:

1. Identify the **first edge without a cloner** (`firstNoCloner`).
   This branch will receive the incoming item ref unchanged.
2. For every other edge:
   - If the edge has a cloner, invoke `cloner(item)` to produce a
     fresh item for that branch.
   - Otherwise, call `(T)item.AddRef()`.
3. If `firstNoCloner < 0` (every edge has a cloner), dispose the
   incoming item ref after all clones are produced — no branch
   inherited it.

Edge cases preserved or newly specified:

- `Writers.Count == 0`: dispose the incoming ref (unchanged).
- `Writers.Count == 1`: behaves as before when no cloner; with a
  cloner, the cloner runs and the original ref is disposed.
- **All branches have cloners**: the incoming ref is disposed once
  all clones are produced and written. Useful for "always clone for
  every consumer" topologies.
- **Cloner throws**: materialization runs entirely *before* any
  branch is written, so on throw nothing has been handed downstream.
  Every per-branch ref already produced (clones and AddRefs) is
  disposed, and the incoming ref is disposed too — no branch
  inherited it, because no write happened. The exception then
  propagates, matching the substrate's existing "operator threw →
  pump faults" semantics in `ForwardAsync`. (Note: "a branch was
  *destined* to inherit the ref" is not "a branch *received* it" —
  the inheriting write never ran, so the incoming ref must still be
  released on this path.)

### Relationship to ADR-0049 §2

This ADR is consistent with ADR-0049's direction: multi-`Connect`
on an `OutputPort` remains *the* fan-out primitive. We're not
adding a parallel `BroadcastNode<T>` primitive; we're patching the
one place the existing primitive falls short. `StorageNode<T>`
remains vestigial — once consumers settle on multi-`Connect` for
both AddRef and cloned fan-out, a future ADR can retire it as
ADR-0049 §2 already anticipated.

### Why not `Func<object, object>` on the non-generic `EdgeOptions`?

Considered and rejected. It pushes the cast to every call site, and
silently fails when the wrong T is passed. The typed `EdgeConfig<T>`
wrapper costs one extra type but the failure mode becomes
"won't compile" instead of "throws at runtime."

### Why not a new `BroadcastNode<T>(int n, Func<T,T> clone)` primitive?

Considered. It would be more discoverable in `Nodes.cs` and the
authoring story would say "use `BroadcastNode` for clone-required
fan-out, multi-`Connect` for AddRef fan-out." But it duplicates the
fan-out story — two ways to do the same thing depending on item
type — which ADR-0049 §2 specifically pushes against (the whole
reason `StorageNode<T>` is flagged for retirement is *because* it
duplicates multi-`Connect`'s job). Extending the existing primitive
keeps one fan-out story.

## Consequences

### Immediate

- `FrameFlow.MotionClip.RecordingGate` loses its preview side-effect
  and its `_preview` field; the recorder's preview becomes a true
  sibling sink in the graph, fed by a fan-out edge with a
  `CloneCpu` cloner. Slow UI rendering on `Avalonia` no longer
  back-pressures motion detection.
- `FrameFlow.MotionClip.RecorderPipeline.BuildGraph` accepts
  `IVideoSink? preview` and wires the fan-out itself. Headless
  callers pass `null`.
- The recorder's encoder branch is unaffected; it's still a
  `Buffered(cap=1)` edge between gate and `ClipEncoderSink`.

### Deferred

- **`Examples.Multicast{,.Dml}` migration.** Same inline-clone-sink
  workaround as the recorder used; can move to multi-`Connect` +
  cloner in a follow-up PR. Not blocked on this ADR.
- **`StorageNode<T>` retirement.** Already anticipated by ADR-0049
  §2; the cloner extension brings us closer by unifying both
  fan-out modes under multi-`Connect`. Out of scope for this ADR.
- **Pooled / refcountable converter outputs.** A different way to
  close the same gap (make `Media.CpuVideoFrame` and converter
  outputs AddRef-able). Much larger scope; touches the frame-pool
  design across `FrameFlow.Media`, `FrameFlow.Decoding`,
  `FrameFlow.Video`. Not pursued here.

### Risks

- **Cloner cost is invisible at the wireup site.** Authors who add
  `CloneCpu` to a fan-out without realising it's a full pixel copy
  may be surprised. Mitigation: `WithCloner` is opt-in and the
  cloner delegate is right there in the wireup code; it's visible
  in code review. Documentation in `VideoFrameExtensions.CloneCpu`
  already calls out the cost.
- **Two ways to fan out (AddRef vs cloner)**, until consumers
  converge. Both flow through the same `multi-Connect` mechanism
  so the mental model stays single; the only branch is in
  `ForwardAsync`.

## Verification

- Existing `Graph.Connect` callers (no cloner) compile and run
  unchanged. The non-cloner code path through `ForwardAsync` is
  preserved verbatim (just rewritten to use the `OutputEdge<T>`
  shape).
- `FrameFlow.MotionClip` synthetic smoke: motion fires, clip is
  saved to disk, `"Stopped. Clips saved: N"` lands in the log. No
  AddRef exception on the converter-output fan-out branch.
- Kiosk camera deploy: unplug/replug cycle still works, preview
  stays live across the gate's motion-detect work.
