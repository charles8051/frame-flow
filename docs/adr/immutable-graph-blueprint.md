# An immutable blueprint for the graph

## Status

Draft, pending number assignment at merge. **Proposed** (2026-09-16). Not implemented.

This record proposes splitting `FrameFlow.Graph.Graph` into a description that is a value and an
instance that runs. The description would be immutable, validated at construction, and
instantiable more than once; the instance would own the channels, the pumps, and the cancellation.

**Related:**
- [ADR-0078](ADR-0078-graph-chain-forks-joins-and-termination.md) — the chain contract, which
  rejected a junction-stage DSL with reusable blueprints, on the grounds that nodes are single-use
  instances and "a blueprint would need a factory layer that does not exist". This record proposes
  that layer, so it reopens that alternative rather than contradicting it.
- [ADR-0049](ADR-0049-frameflow-graph-fork-from-crossbar.md) — the fork that made this substrate
  in-tree and changeable, and §2's precedent for dropping what the runner does not read.
- [ADR-0055](ADR-0055-decode-protocol-as-a-pure-mealy-core.md) — the in-repo precedent for a pure
  core with a shell around it. `ClockSelectBuffer` under `ClockSelectVideoSink` is the second.
- [ADR-0009](ADR-0009-threading-and-concurrency-model.md) — the threading model the instance owns.
- [ADR-0020](ADR-0020-lifecycle-decoupled-from-processing-logic.md) — the same separation applied
  one layer up.

## Context

### `Graph` is a description and a runner in one type

`Graph` holds the topology as `_nodes` and `_edges`. It also holds `_wireUps`, `_resets`,
`_beforeRun`, and a `_running` flag ([`Graph.cs`](../../src/FrameFlow.Graph/Graph.cs)). `RunAsync`
validates, runs the before-run actions, runs the resets, runs the wire-ups, starts one `Task` per
node, and awaits them.

The ports carry both kinds of state too. `InputPort` has `IsConnected`, a build-time fact, and
`Reader`, which is null until the graph runs ([`Ports.cs`](../../src/FrameFlow.Graph/Ports.cs)).
`OutputPort` has `Writers`, appended at wire-up. `Connect` sets `to.IsConnected = true`, so
describing an edge mutates the node the edge lands on.

The tests already name the type for what it does. Eight files in `tests/FrameFlow.Graph.Tests` open
with `using GraphRunner = FrameFlow.Graph.Graph;`.

### The description is already a value, and the core already consumes it

`GraphTopology.Validate(nodes, edges)` is a total function over exactly the topology, pure by
design so the wiring rules are testable without starting a pump
([`GraphTopology.cs`](../../src/FrameFlow.Graph/GraphTopology.cs)). `EdgeSpec` exists already, as
"one wired edge, reduced to the facts the rules are about".

The functional core is written. The value it is a function of has no type of its own, is assembled
by side effect, and is reachable only from inside the runner.

### Three mechanisms exist because the two are fused

**`_resets`.** A second `RunAsync` would append a second writer to each output port, so a source
would fan a frame into an orphaned channel. `_resets` clears `from.Writers` and `to.Reader` before
each run to prevent it. The comment says so directly.

**`BeforeEachRun`.** Node bodies are delegates that survive the run, so a loop that re-runs the
same graph shows an operator the same closure with the timeline back at zero. `BeforeEachRun` asks
the caller to drop that state. It is a convention with no enforcement: an operator that forgets is
a silent wrong answer on the second run, not a failure.

**`GraphPolicy`.** `SubstrateSession` carries `Rebuild` and `Reuse`
([`SubstrateSession.cs:1012`](../../src/FrameFlow.Playback/SubstrateSession.cs)). `Reuse` re-runs
the retained graph to skip the per-loop teardown on a 24/7 attract loop; `Rebuild` builds a fresh
topology because a seek follows a cancel and a new topology is the safe shape. Two policies over
one object, chosen by how much of its state the caller trusts after the last run.

All three are the same fact: there is one object where there are two things, so a second run is
implemented by resetting rather than by instantiating.

### Validation happens at run time

`Validate` is called inside `RunOnceAsync`, so a malformed graph throws from `RunAsync` rather
than from the call that made it malformed. The `InvalidOperationException` names the broken rule,
which is good, but it arrives after the caller has built, configured, and started the graph.

### Nodes are single-use

An input port rejects a second connection, and a node's ports are created in its constructor. A
node instance therefore belongs to exactly one graph and one position in it. This is what ADR-0078
cited when it rejected reusable blueprints.

### The surface has not shipped

`src/FrameFlow.Graph/PublicAPI.Shipped.txt` is empty. All 227 entries of the substrate's public
surface sit in `PublicAPI.Unshipped.txt`. Every breaking change proposed here is free in the
versioning sense, and stops being free at the first release that ships `FrameFlow.Graph`.

In-tree there are 80 `new Graph()` sites across 13 directories, concentrated in
`tests/FrameFlow.Graph.Tests` (8 files).

## Decision

### 1. The blueprint is a value

```csharp
public readonly record struct PortRef(string NodeId, string PortName);

public readonly record struct EdgeSpec(
    PortRef From,
    PortRef To,
    EdgeOptions Options,
    bool Inherit);

public sealed record GraphBlueprint(
    ImmutableArray<NodeSpec> Nodes,
    ImmutableArray<EdgeSpec> Edges);
```

An edge names its ports rather than holding them. A port instance belongs to a node instance, and
a blueprint exists before any node instance does.

### 2. Validation moves to construction

`GraphBlueprint` is produced by a builder that validates before it returns one. A blueprint that
exists is a blueprint that passed `GraphTopology.Validate`, so an instance cannot be asked to run
a graph that cannot work. The builder returns a `Result` per
[ADR-0008](ADR-0008-result-types-and-exception-boundaries.md) rather than throwing.

`Validate` keeps its signature and its purity. It gains a blueprint-shaped overload and loses
nothing.

### 3. Instantiation is a separate step

```csharp
public sealed class GraphInstance : IAsyncDisposable
{
    public static GraphInstance Instantiate(GraphBlueprint blueprint);
    public Task RunAsync(CancellationToken ct = default);
}
```

The instance owns the channels, the node instances, the pumps, and the linked cancellation source.
Running the same blueprint twice means instantiating it twice. Two instances of one blueprint may
run concurrently and share nothing.

### 4. A node spec is a factory

```csharp
public sealed record NodeSpec(string Id, FailureResponse OnError, Func<INode> Create);
```

This is the factory layer ADR-0078 named as missing. It is what makes Decision 3 real: an
instance's nodes are its own, so `_resets` has nothing to reset and a second instance cannot
disturb the first.

It is also the decision with the largest reach, because it changes what a caller writes. Today a
caller constructs `new OperatorNode<T, T>("id", body)` and hands over the instance. Under this
decision the caller hands over a function that constructs it.

### 5. `_resets` and `BeforeEachRun` are removed

`_resets` becomes unreachable: an instance wires its own fresh ports once, and there is no second
run to reset for.

`BeforeEachRun` becomes unnecessary for state a node owns, because Decision 4 builds a fresh node
per instance. It does not become unnecessary for state the caller owns outside the graph. A body
that closes over a field on the caller's class still sees that field across instantiations, and no
substrate change can reach it. The convention is narrowed, not eliminated, and the record should
say which cases it still covers rather than claim the problem is solved.

### Staging

Decisions 1 and 2 are additive: `Graph` gains a `ToBlueprint()` projection and the builder
validates eagerly, while `RunAsync` keeps working. Decisions 3, 4 and 5 are the break, and land
together because a factory without a separate instance buys nothing.

## Consequences

### Positive

- The core stops being a function of a value that does not exist. `Validate` gets the type it has
  been destructuring by hand.
- A malformed graph fails where it was made malformed.
- Three mechanisms collapse into one concept. `_resets`, `GraphPolicy`, and most of
  `BeforeEachRun` are all answers to "how do I run this twice", which becomes "instantiate it
  twice".
- A blueprint is printable, comparable, and testable without a pump, a channel, or a task. A test
  that today builds a graph and asserts on the exception from `RunAsync` can assert on a value.
- The junction DSL the chain record rejected becomes expressible, because nodes stop being
  single-use.
- `_running` goes away. Concurrency between runs is a property of how many instances exist rather
  than a flag that refuses.

### Negative

- **The typed `Connect` loses its compile-time proof.** Today
  `Connect<T>(OutputPort<T>, InputPort<T>)` cannot connect mismatched item types, because the ports
  carry `T`. A blueprint that names ports by string checks that at instantiation instead. Keeping
  the proof means a typed handle the builder hands out, which is the open question below.
- **It is a break across every consumer.** 80 construction sites, `GraphChain`, `Pipeline`,
  `Connect`, `Add`, and `SubstrateSession.BuildGraph`. Free in versioning terms today, not free in
  work.
- **The caller writes a factory.** `Func<INode>` is more to write than a constructor call, and a
  caller who closes over mutable state inside the factory reintroduces the problem the factory
  exists to remove, with no diagnostic.
- **Nothing in tree needs concurrent instantiation.** `SubstrateSession` runs one graph at a time.
  The benefit today is the removal of the reset machinery and the unblocking of a rejected design,
  not a capability a consumer is waiting for.
- **The instantiation plan stays opaque.** The blueprint carries the topology as data; turning it
  into wired channels still needs per-edge typed work, which is what `_wireUps` is. That work moves
  and gets parameterized by the instance's nodes. It does not become inspectable.

### Neutral

- No change to edge semantics, ownership, fan-out, refcounting, or pump behaviour. `EdgeOptions`,
  the cloner rule, the trunk rule, and `NodePumps` are untouched.

## Alternatives considered

**Keep one type and fix the symptoms.** Make `_resets` complete, document `BeforeEachRun` harder,
and leave `GraphPolicy` alone. Rejected as the option that spends the unshipped window. Each
mechanism is small; the reason there are three is structural, and a fourth arrives with the next
thing that outlives a run.

**Blueprint as description only, without factories.** Decisions 1, 2 and 3 with node instances
still supplied by the caller. Rejected: two instances would then share node instances and therefore
ports, which is the corruption `_resets` was written to prevent, now unguarded. A separate instance
without a factory is a worse version of today.

**Typed `PortRef<T>` phantom handles.** The builder hands out `PortRef<T>` and `Connect` keeps its
compile-time check while the stored blueprint is type-erased. Not rejected. Held as the open
question below, because it changes how much of the break is felt at call sites and the answer wants
a spike rather than a paragraph.

**Make `Graph` immutable without splitting it.** Every `Connect` returns a new `Graph`. Rejected:
it makes the topology persistent while leaving `RunAsync` mutating ports on shared node instances,
so the value is immutable and what it describes is not.

## Open questions

- **How the typed connect survives.** Whether `PortRef<T>` preserves the compile-time check at
  acceptable cost, or whether the check moves to instantiation and the builder's fluent surface
  carries it instead. This decides how much of the break reaches call sites, and it should be
  spiked against `SubstrateSession.BuildGraph` and one fan-out example before the record is
  accepted.
- **What a `SyncJoinNode` spec looks like.** It has two typed inputs and the keys and the window,
  so its factory is the largest one and the test of whether `Func<INode>` is the right shape.
- **Whether `GraphPolicy` survives in a narrowed form.** `Reuse` becomes "instantiate the retained
  blueprint again", which is cheap but not free, and the attract loop is the case that cared.
  Measuring instantiation cost against the retained-graph re-run is a precondition for removing the
  enum.
- **Whether this lands before `FrameFlow.Graph` ships.** The versioning argument in Context is the
  whole reason to decide now rather than later, and it expires.
