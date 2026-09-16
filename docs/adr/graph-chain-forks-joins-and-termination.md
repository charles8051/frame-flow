# ADR-XXXX: The chain declares its forks and joins, and the builder always terminates it

## Status

Proposed (2026-09-15). Draft pending number assignment.

This record decides that a fork's ref inheritance is declared on the edge rather than derived from
wiring order, that a fork and a join are expressible in `GraphChain<T>`, and that a configurator has
one contract: it returns an open chain and the builder terminates it. The configurator-terminated
mode goes away, so every configured graph is a single-sink graph whose configured segment sits
upstream of the pacer, at decode rate.

It supersedes:

- **[ADR-0054](ADR-0054-fan-out-with-explicit-cloning.md) "Substrate semantics" step 1**, which
  makes the first cloner-less edge the inheritor of the incoming ref. That rule stays as the
  fallback; it stops being the only way to say which branch inherits.
- **[ADR-0045](ADR-0045-unified-pipeline-termination.md) "Termination"**, which says the pump runs
  whatever the configurator returns and that "the configurator always says how to terminate." The
  code stopped doing that when the substrate changed; this record says what replaces it.
- **[ADR-0057](ADR-0057-pull-based-master-clock.md) "Scope: configurator-terminated paths retain
  `PaceUntil`"**, which carves out fan-out and inference chains because they have no single sink to
  decorate. Decision 4 removes the path that carve-out describes.

Related: [ADR-0043](ADR-0043-consumer-configurable-pipeline-operators.md),
[ADR-0049](ADR-0049-frameflow-graph-fork-from-crossbar.md),
[ADR-0057](ADR-0057-pull-based-master-clock.md),
[ADR-0059](ADR-0059-discard-streams-with-no-consumer.md),
[the frame-pool record](frame-pool-ownership.md).
Issues #8, #9, #91, #93, #122, #125, #216, #218, #227, #231, #237.

## Context

### The chain stops where the topology stops being linear

`GraphChain<T>` covers a linear segment: `graph.Pipeline(src).Then(a).To(sink)`. Its `Then`, `To`,
`ToPrimary` and `ToSecondary` all take an `EdgeOptions?`. The per-edge cloner that
[ADR-0054](ADR-0054-fan-out-with-explicit-cloning.md) introduced lives on `EdgeConfig<T>`, which
only `Graph.Connect` accepts (`GraphChain.cs:45-113`, `Graph.cs:69-126`).

So a consumer whose video path forks to an inference branch and rejoins cannot stay in the chain.
Both in-tree consumers of that shape drop to port-level wiring:

- LiveCaptioning wires three `Connect` calls and one `Pipeline().To`
  (`examples/FrameFlow.Examples.LiveCaptioning/MainWindow.axaml.cs:488-495`).
- `RecorderPipeline` is written entirely in `Connect` (`src/FrameFlow.MotionClip/RecorderPipeline.cs:67-87`).

That is the affordance `docs/DEFERRED_WORK.md` asks for, and #8, #9 and #93 are all blocked behind
it. #231's second measurement arm needs LiveCaptioning on the single-sink path, so the lookahead
epic (#227) is behind it too.

### Which branch inherits the ref is a wiring-order fact

`NodePumps.ForwardAsync` scans for the first cloner-less edge and gives it the incoming ref; other
cloner-less edges `AddRef`, cloner edges clone, and if every edge has a cloner the incoming ref is
disposed (`NodePumps.cs:525-584`).

This is not only an allocation detail. `Media.CpuVideoFrame.AddRef` throws by design
(`src/FrameFlow.Media/CpuVideoFrame.cs:85-88`, reached through `VideoFrameRef.cs:89`), so for a
one-shot frame the inheriting branch is the only one that can exist without a cloner.
`RecorderPipeline`'s gate edge depends on being that branch.

Both call sites carry a comment saying the cloner-less branch has to be wired first. Neither needs
to: each has exactly one cloner-less edge, and the scan inspects every edge before any branch item
is produced. The rule is correct, invisible at the call site, and already being restated wrongly in
the two places that depend on it.

### The configurator has three shapes, and its return value means different things in each

| Entry point | Shape | The return value |
|---|---|---|
| `PlayerSession.BuildAsync` | `source → configurator → sink` | Terminated by the builder. No-sink builds throw (`PlayerSession.cs:125-131`) |
| `SubstrateSession`, single sink | `source → gate → configurator → pacer` | Terminated by the builder (`SubstrateSession.cs:1464-1467`) |
| `SubstrateSession`, configurator only | `source → PaceUntil → gate → configurator` | Ignored; the configurator must terminate (`SubstrateSession.cs:1485-1486`, `:1509`) |

#125 records the three shapes. The interface documents only the first two
(`src/FrameFlow.Player/IPlayerBuilder.cs:68-86`), and the three examples that rely on the third
return an untouched chain as a placeholder (LiveCaptioning `:467`, `:496`, Multicast `:419`, `:476`,
Multicast.Dml `:327`).

ADR-0045 decided this differently: `WithVideoSink` composed `.ToSink(sink)` into the configurator, so
there was one contract and the configurator always terminated. That mechanism belonged to the
pipeline surface that predates the substrate fork (ADR-0049). After the fork `WithVideoSink` only
records the sink (`PlayerBuilder.cs:54-58`) and the session composes the terminal. No record covers
the shape that replaced it.

Two decisions elsewhere depend on the mode being known before the configurator runs. ADR-0057 keeps
the in-graph `PaceUntil` on configurator-terminated paths, and the session picks that shape at
`SubstrateSession.cs:1478-1486`. ADR-0059 decides decode-or-discard from sink-or-configurator
presence at `SubstrateSession.cs:449-450`. A contract read out of the return value arrives after
both.

## Decision

### 1. Inheritance is declared on the edge

`EdgeConfig<T>` gains an init-only `Inherit` flag, default unset, plus a `WithInherit()` helper
alongside `WithCloner`. An init-only member rather than a third positional parameter, so existing
positional construction keeps compiling.

`ForwardAsync` uses the declared inheritor when a port has one, and falls back to the
first-cloner-less scan when none of a port's edges declares. `Graph` rejects at wiring time a port
with more than one declared inheritor, and an inheritor that also carries a cloner. The all-cloner
case is unchanged: no inheritor, and the incoming ref is disposed once the branch items exist
(`NodePumps.cs:583-584`).

Keeping the scan matters for correctness, not compatibility. A plain `Connect` fan-out that declares
nothing must keep working on one-shot frames, and `RecorderPipeline` is exactly that shape.

### 2. `Branch` declares a fork

```csharp
public GraphChain<T> Branch(EdgeConfig<T> config);
```

`Branch` returns a chain over the same output port whose next hop uses `config`. The trunk, meaning
the chain `Branch` was called on, is the inheritor. The edge config is required rather than
defaulted: an omitted one is `EdgeOptions.Default`, a capacity-1 blocking edge, which is the wrong
default for a sibling branch and the mistake `GraphChain.cs:92-95` already warns about on join
secondaries.

A fan-out built only from `Branch` calls has no trunk and therefore no inheritor. That is the
all-cloner case above, and it is legal.

### 3. `Join` returns a chain

```csharp
public GraphChain<TOut> Join<TSecondary, TOut>(
    GraphChain<TSecondary> secondary,
    SyncJoinNode<T, TSecondary, TOut> join,
    EdgeOptions primaryOptions,
    EdgeOptions secondaryOptions);
```

Both edge options are required, for the reason in decision 2. `ToPrimary` and `ToSecondary` stay:
they wire a join whose other side is built elsewhere.

Fork-then-rejoin is the "one producer feeds both sides" shape `SyncJoin.cs:183-189` warns about. The
warning is not lifted by this record. A dropping branch edge, which is what LiveCaptioning uses, is
safe; a blocking branch edge under a tight `MaxLead` is the case the warning describes.

### 4. There is one configurator contract: it returns an open chain, and the builder terminates it

`ConfigureVideo` keeps `Func<GraphChain<T>, GraphChain<T>>` and it means what it has always
documented: return the chain, the builder terminates it at the registered sink. The
configurator-terminated mode is removed rather than given a second registration method. A
configurator registered without a sink is rejected at build time.

Decisions 2 and 3 are what make one contract sufficient. A consumer that needs extra sinks wires
them on `Branch` edges and returns the trunk open. LiveCaptioning's shape becomes:

```csharp
.WithVideoSink(new CaptionOverlaySink(viewSink, captionQueue, captionTimeline))
.ConfigureVideo(chain =>
{
    var head = chain.Then(convert);
    var detections = head
        .Branch(EdgeOptions.LatestWins(1).WithCloner<VideoFrameRef>(CloneForInference))
        .Then(detect);
    return head.Join(detections, CreateDetectionJoin(), EdgeOptions.Default, EdgeOptions.Buffered(4));
})
```

The terminal that the configurator used to wire itself becomes an `IVideoSink` the builder owns.
That is a decorator over the view sink in all three cases that need it, since `IVideoSink` is
`PresentAsync` plus a frame pool and a format hook (`src/FrameFlow.Media/IVideoSink.cs:66-91`).

**Every configured graph is then a single-sink graph, which is the point.** The pacer decorates the
registered sink, so the chain a consumer configures sits upstream of the pacer's ring and runs at
decode rate. That is the attachment point the lookahead epic needs, and it is why ADR-0057's
carve-out goes: there is no longer a configured path without a ring to deepen. #227's deferred
question, whether the configurator-terminated path converges onto the pacer or a lead stays a
single-sink feature, is answered here as convergence.

ADR-0059's decode-or-discard rule keeps its input and gets simpler: a stream has a consumer when it
has a sink (`SubstrateSession.cs:449-450`).

### 5. `Graph` holds an edge list, and validates it before the run

`Connect` appends an immutable edge record — from, to, options, cloner, inherit — instead of
appending two closures. `RunAsync` derives the per-edge reset and wire-up from that list, keeping
the order it has today: the `_beforeRun` hooks #239 added, then the resets, then the wire-ups
(`Graph.cs:202-208`).

A pure `Validate(edges)` runs first and reports declared-inheritor conflicts, an inheritor carrying
a cloner, and a join input that was never wired. The workspace's functional-core rule is the reason:
the topology is a value, so the rules over it are a total function of that value, testable without
running a graph.

## Consequences

- **Additive on the graph surface, a behaviour break on the player surface.** `Branch`, `Join` and
  `WithInherit` are new members, and `EdgeConfig<T>` gains an init-only member rather than a
  positional one. Removing the configurator-terminated mode is the break: a configurator registered
  without a sink used to run and now fails the build.
- **The break is not a compile error.** The three examples that terminate inside the configurator and
  return a placeholder chain keep compiling. They present twice, once through their own terminal and
  once through the builder's, until they move that terminal into an `IVideoSink`. That is the class
  `docs/BREAKING-CHANGES.md` calls out at the top, and it needs an entry naming the three call sites.
- **The lookahead epic gets its attachment point.** A configured chain is upstream of the pacer on
  every path, so a deeper ring is a property of the sink decorator rather than of which shape the
  consumer happened to build. #227 can drop the convergence question from its deferred list.
- **#216 stays independent.** It adds a build context to the configurator and is the signature break.
  This record adds a method beside the existing one, so the two can land in either order.
- **The examples move off `Connect`.** #8, #9 and #93 become expressible, and #231's second arm gets
  the single-sink LiveCaptioning it needs.
- **`Graph.Connect` stays public.** It is the port-level escape hatch for topologies the chain does
  not express, including multi-input nodes beyond `SyncJoinNode`.

## Alternatives considered

**A junction-stage graph DSL: `Broadcast`, `Merge` and `Zip` stages with reusable blueprints.**
Rejected. Nodes here are single-use instances whose input ports reject a second connection
(`Ports.cs:51-56`), so a blueprint would need a factory layer that does not exist. `SyncJoinNode` is
the only junction, and ADR-0054 already rejected a second fan-out mechanism on the grounds that it
splits the authoring story in two.

**`To` returns a closed-chain marker, and the builder reads the contract off the return type.**
Rejected. Pacing (ADR-0057) and decode-or-discard (ADR-0059) are both decided before the
configurator is invoked, so the answer arrives too late to be acted on, and it is a binary break on
a member three examples call.

**Two registration methods, one per contract.** Rejected, and this is the alternative that shaped
decision 4. It is the smaller change: nothing is removed, no example has to move its terminal into a
sink, and the builder still knows the mode before init. What it buys is permanence for the split.
Two contracts stay two contracts, #125's three shapes stay three, and the configurator-terminated
path keeps its in-graph `PaceUntil` and its missing ring, which leaves the lookahead epic holding
the convergence question it deferred. The split exists because the chain could not express a fork;
once decisions 2 and 3 land, keeping it would be preserving a workaround past its cause.

**Replace the first-cloner-less scan with the declared marker outright.** Rejected. A plain `Connect`
fan-out would then have no inheritor and would `AddRef` every cloner-less edge, which throws for
one-shot frames and breaks `RecorderPipeline` on its first frame. The existing fan-out tests would
not catch it: they use `RefBox`, whose `AddRef` returns `this` (`RefBox.cs:40-51`).

**Leave it and keep dropping to `Connect`.** Rejected. It is what blocks #8, #9, #93 and #231's
second arm, and the ordering claim in the frame-pool record assumes this contract lands first.

## Open questions

- Whether `Branch` should take a params list for an N-way fan-out, or whether repeated calls read
  better at the two call sites that exist.
- Whether `Validate` runs on every `RunAsync` or once per topology mutation. A re-run graph
  revalidates an unchanged edge list today.
- Whether any consumer legitimately has no builder-owned sink. A detect-only pipeline would register
  its detection terminal as the video sink, which reads oddly for something that presents nothing.
  A discard sink would answer it; none of the three examples needs one.
- What `PlayerSession` does with a configured chain. It terminates at the sink and has no pacer, so
  one contract holds there, but the missing pacer is the part of #125 this record does not answer.
- #91 is adjacent and not answered here: `SyncJoinNode.AdvanceAndMatch` `AddRef`s the retained
  secondary, which throws for one-shot frame types. A join reached through `Join` hits it the same
  way it does through `ToSecondary`.
