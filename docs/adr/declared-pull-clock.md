# Declared pull: the master clock as a graph-visible dependency

## Status

Draft, pending number assignment at merge. **Proposed** (2026-09-16). Not implemented.

This record started as a proposal to register the master clock on the `Graph` so the substrate
could see which nodes are clocked by what. Drafting it against the wiring that exists changed the
shape. The registry validates nothing, because the clock's author is legitimately outside the
graph, and the one in-graph clock reader has no call site left. What the evidence supports is a
written rule and a deletion. The registry is recorded here as deferred, with the condition that
would revive it.

**Related:**
- [ADR-0057](ADR-0057-pull-based-master-clock.md) — made `IClockSource` pull-based and, in its
  Stage 2, moved the video clock wait out of the graph and into `ClockSelectVideoSink`. This
  record states the rule that change was following.
- [ADR-0003](ADR-0003-audio-master-sync-policy.md) — audio masters the clock when audio is present.
- [ADR-0035](ADR-0035-master-clock-interface-split.md) — `IMasterClock` as a focused read surface.
- [ADR-0049](ADR-0049-frameflow-graph-fork-from-crossbar.md) §2 — the precedent for dropping an
  axis the runner never reads, rather than carrying it because the fork brought it.
- [ADR-0071](ADR-0071-what-the-audio-clock-is-allowed-to-believe.md) — what the audio clock may
  report, which is a separate question from where it is declared.

## Context

### The substrate has two kinds of thing in it

Every edge is a bounded `Channel<T>` built from a capacity and one of three full modes
([`Graph.cs:295`](../../src/FrameFlow.Graph/Graph.cs)). Every node implements `IPumpableNode` and
gets one `Task` running a read-invoke-write loop
([`Graph.cs:259`](../../src/FrameFlow.Graph/Graph.cs)). There is no unbounded edge and no direct
node-to-node call.

`GraphTopology.Validate` is a total function of exactly those two things, `(nodes, edges)`
([`GraphTopology.cs`](../../src/FrameFlow.Graph/GraphTopology.cs)). It is pure by design, so the
wiring rules are testable without starting a pump. Anything the substrate is to reason about has
to be visible as a node or an edge.

### The clock is a third thing

`IClockSource` is an ordinary interface reference ([`IClockSource.cs`](../../src/FrameFlow.Graph/IClockSource.cs)).
A consumer captures one in a closure or a field and calls `Latest` or `WaitUntilAsync` whenever it
wants. Nothing registers it, so `Validate` cannot see it, a graph dump cannot name it, and a node
that depends on a clock looks identical to one that does not.

### What is actually wired today

| Role | Type | A node? |
| --- | --- | --- |
| Clock author, audio present | `OpenAlAudioSink` | Yes, a `SinkNode<PcmAudioBufferRef>` via `AsSinkNode` |
| Clock author, no audio | `WallClockSource` | No |
| Clock selection, per item | `SubstrateSession` ([`:534`](../../src/FrameFlow.Playback/SubstrateSession.cs)) | No, it is the type that builds the graph |
| Clock reader, video | `ClockSelectVideoSink` ([`:92`](../../src/FrameFlow.Playback/ClockSelectVideoSink.cs)) | No, an `IVideoSink` the video `SinkNode` delegates to |
| Clock reader, in graph | `PaceUntil` ([`:80`](../../src/FrameFlow.Playback/PaceUntil.cs)) | Yes, an `OperatorNode<T, T>` |

Two facts follow from the table, and both cut against the registry.

The author is outside the graph on the no-audio path. `WallClockSource` is constructed by the
session and is not a node ([`SubstrateSession.cs:215`](../../src/FrameFlow.Playback/SubstrateSession.cs)).
A registry could therefore only record readers, and a reader-only registry cannot assert that an
author exists. The rule "a graph with a clock reader and no clock author will hang" is the rule
worth having, and it is not expressible.

The reader is outside the graph on the video path. ADR-0057 Stage 2 moved pacing into
`ClockSelectVideoSink`, a decorator the video `SinkNode` hands frames to. The substrate sees a
`SinkNode<VideoFrameRef>` and nothing else.

### The in-graph pacer has no call site

`PaceUntil.Create` is not called anywhere in `src/`, `tests/`, or `examples/`. Every remaining
mention is a comment describing what the code used to do. The two examples that the
`SubstrateSession` comment block still names as retaining it
([`:1437`](../../src/FrameFlow.Playback/SubstrateSession.cs)) do not: the Camera.Multicast example
says it deliberately has no pacer, and the LiveCaptioning example's reference is a stale class doc
comment. The code twenty lines below that block says so directly, that there is no second shape any
more, because the chain contract removed the configurator-terminated path that ADR-0057 carved out.

`PaceUntil` sits in [`PublicAPI.Unshipped.txt:215`](../../src/FrameFlow.Playback/PublicAPI.Unshipped.txt),
not the shipped baseline.

### The rule nothing writes down

ADR-0057 Stage 2 moved the wait out of the graph for a measured reason. Awaiting the clock inside
an operator holds the frame inside the operator, and the graph's edges are capacity-1, so on the
zero-copy path a single long wait pinned a D3D11VA decode-texture slice with no slack and drained
the FFmpeg-default hwframe pool. That is recorded as a change to one call path. It is not recorded
as a constraint on what a pump body may do, so the next operator that awaits a clock will
reintroduce it.

## Decision

### 1. The clock stays a pull, and stays out of band

`IClockSource` is not modelled as an edge, and a clock value is not modelled as an item. The value
is computed on read from the underlying time source; it is not produced, queued, or delivered.

This is a restatement of ADR-0057 rather than a new choice, recorded here because the substrate's
"every edge is a buffer, every node is a pump" shape invites the question and does not answer it.
Making the clock an edge reinstates the publish link ADR-0057 deleted, with a channel added: a
reader waiting for `t` could only unblock once a tick past `t` had been produced *and* delivered,
which is the starvation failure with an extra hop.

### 2. A pump body does not await the clock

A node's operator, producer, or consumer delegate must not call `IClockSource.WaitUntilAsync`. A
clock wait belongs in a shell the pump hands off to, which is what `ClockSelectVideoSink` is.
Reading `Latest` is permitted; it never blocks.

This is not machine-checkable. The wait happens inside a delegate the substrate cannot inspect, so
the rule is a written one, enforced at review and by removing the node that breaks it.

### 3. `PaceUntil` is removed

It is the only in-graph clock reader, it violates Decision 2 by construction, it has no call site,
and it is unshipped public API. Removing it makes the rule true of the tree rather than aspirational.

The `ClockSelectVideoSink` path already covers every shape the session builds. A consumer that
needs a second sink wires it on a `Branch` edge and returns its trunk open, which is the chain
contract's shape, and the trunk still terminates at the clock-selecting pacer.

### 4. The reader registry is deferred

The proposal was:

```csharp
// FrameFlow.Graph
public sealed class Graph
{
    /// <summary>Records that <paramref name="node"/> reads <paramref name="clock"/>.</summary>
    public Graph ClockedBy(INode node, IClockSource clock);
}

internal readonly record struct ClockSpec(INode Node, IClockSource Clock);
```

`Graph.BeforeEachRun` is the precedent for a graph-level registration that is not a node or an edge.

It is deferred because of what it would buy today. `Validate` gains no rule, for the author reason
above. Diagnostics gain one string naming a dependency that `SubstrateSession` already knows,
selects, and could log itself. Against that, the substrate gains a public method and a third
concept alongside nodes and edges.

Revive it when a second clock reader exists inside a graph the session does not build, so that the
dependency is no longer knowable from one place. A graph with two clocks, or a consumer-authored
clock reader, is that condition.

## Consequences

### Positive

- The reason the video pacer sits outside the graph becomes a rule instead of an artefact of one
  bug fix.
- The substrate keeps two concepts. A reader of `Graph` does not have to learn a third that carries
  one registration.
- One unused public type leaves `FrameFlow.Playback` before it ships.

### Negative

- Decision 2 is unenforced. A pump that awaits a clock compiles, runs, and reproduces the hwframe
  starvation on the zero-copy path only.
- A consumer wiring its own clocked node gets no help from the substrate, and no diagnostic names
  the dependency. This is the state today; the record does not improve it.
- Removing `PaceUntil` removes the only worked example of clock-paced flow inside a graph. Anyone
  who wants that shape now reads `ClockSelectVideoSink`, which is a larger type with a ring buffer
  and a delivery loop in it.

### Neutral

- No runtime behaviour changes. `WaitUntilAsync`, `Latest`, and `SeekBaseline` keep their contracts.

## Alternatives considered

**The clock as a `LatestWins` edge.** A `ClockNode` emitting `RefBox<TimeSpan>` into a capacity-1
`DropOldest` edge per reader. Rejected: it reinstates the deleted publish ticker behind a channel,
per Decision 1. Secondary costs are that one scalar fans out to N channels with N drop policies,
and that a pump reads one input per iteration, so a reader holding an item while sampling time
needs a second input and therefore a join.

**The clock as a join secondary.** `SyncJoinNode<VideoFrameRef, ClockTick, VideoFrameRef>` with
`MostRecentAtOrBefore` is expressible today. Rejected: the join matches a primary against the
newest secondary and emits on primary arrival. Pacing is a sleep, not a match, so the join
describes the wrong operation.

**Register the clock and add the author rule anyway,** by requiring `WallClockSource` to be a node.
Rejected: it makes a wallclock a pump with nothing to pump, in order to satisfy a validator. The
session's ownership of the clock lifecycle (`Start`, `Pause`, `Resume`, `Seek`, `DisposeAsync`,
serialised by the single-owner rule in ADR-0028) does not fit a node's lifetime.

## Open questions

- **The `ISeekableClock` cast.** `SubstrateSession` reseats the clock baseline through
  `(_clockSource as ISeekableClock)?.SeekBaseline(...)` at two call sites
  ([`:741`](../../src/FrameFlow.Playback/SubstrateSession.cs) and
  [`:1188`](../../src/FrameFlow.Playback/SubstrateSession.cs)). Both clock authors implement it, so
  the null branch is unreachable today and a seek against a clock that did not implement it would
  silently do nothing. Whether the session should require `ISeekableClock` outright is a playback
  question, not a substrate one, and is out of scope here.
- **The stale comment block.** `SubstrateSession.cs:1437` describes a configurator-terminated path
  that the chain contract removed. It should be corrected with Decision 3 or ahead of it.
