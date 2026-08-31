# ADR-0049: FrameFlow.Graph — Fork of the Crossbar Substrate

**Status:** Accepted
**Date:** 2026-05-22

> **Licensing note, 2026-08-28.** `FrameFlow.Graph` contains a verbatim one-time
> copy of the Crossbar substrate, so a reader may reasonably ask how it can ship
> under a different license from the code it was copied from.
>
> The facts, as they can be checked from the repositories: Crossbar is a
> first-party library, released under the MIT license with `Copyright (c) 2026
> Charles Lee` on it; git attributes all 74 of its commits to that same name, with
> no other human author and no vendored third-party source in its tree; and
> Crossbar ADR-0024 is the companion record authorizing this fork. `FrameFlow.Graph`
> ships under the PolyForm Small Business License 1.0.0 (`LICENSE.md`) along with
> the rest of this repository.
>
> Whether any MIT notice obligation survives that relicensing is a question for the
> maintainer and their counsel, not one this ADR settles. It is recorded here
> because the fork is the most obvious licensing question a reader can raise, and
> the answer should be findable rather than absent.
>
> The Crossbar repository is not published, which is why the `crossbar` ADRs cited
> below cannot be opened. They are cited as the record of the decision, not as
> reachable documents.
**Supersedes:** None directly; restructures the consumption side of
several prior ADRs (see §"Consequences for prior ADRs").
**Related:**
- `crossbar` ADR-0014 (primitive-set substrate) — the substrate being
  forked from
- `crossbar` ADR-0024 (substrate fork — FrameFlow.Graph divergence) —
  the companion ADR on the Crossbar side that authorizes this fork
- `periphery` ADR-0045 (substrate independence from Crossbar) — the
  Periphery-side ADR that makes `Periphery.Camera` substrate-agnostic
  so this ADR can absorb it via dependency

## Context

FrameFlow has historically depended on Crossbar's substrate
(`Crossbar` core + `Crossbar.Cuda` + `Crossbar.Onnx`) for graph
primitives, refcounted item ownership, and CUDA-EP inference. The
2026-05-21 / 2026-05-22 design conversation surfaced a charter
tension in Crossbar: it has been trying to be both a *Holoscan-class
substrate* (real-time multi-sensor with first-class GPU primitives)
and an *ergonomic everyday-inference graph runtime* (FrameFlow's
actual need). Those ambitions pull the primitive set in opposite
directions.

Crossbar ADR-0024 resolves the tension by reaffirming Crossbar's
Holoscan-class charter and **authorizing FrameFlow to fork the
substrate** for its own narrower needs. This ADR is the FrameFlow
side of that fork: what we take, what we slim, where it lives, and
the consolidation work that lands at the same time.

## Decision

### 1. Create `FrameFlow.Graph` by forking the Crossbar substrate

A one-time copy of the relevant files from
`crossbar/src/Crossbar/` (at the commit on which this ADR lands)
into `frame-flow/src/FrameFlow.Graph/`. Namespace becomes
`FrameFlow.Graph`. **No ongoing sync.** Per Crossbar ADR-0024, the
two codebases diverge from this point forward.

Files taken (substrate primitives that media pipelines need):

| Source (Crossbar) | Destination (FrameFlow) | Notes |
|---|---|---|
| `Graph.cs`, `GraphChain.cs` | `FrameFlow.Graph/Graph.cs`, `GraphChain.cs` | Verbatim port |
| `Nodes.cs` | `FrameFlow.Graph/Nodes.cs` | Verbatim port (includes `MultiOperatorNode`, `StorageNode` — see §2) |
| `NodePumps.cs` | `FrameFlow.Graph/NodePumps.cs` | Verbatim port |
| `Ports.cs` | `FrameFlow.Graph/Ports.cs` | Verbatim port |
| `EdgeAxes.cs` | `FrameFlow.Graph/EdgeAxes.cs` | Verbatim port (Push/Pull, Producer/Consumer-Paced, all overflow / underflow modes) |
| `NodeAxes.cs` | `FrameFlow.Graph/NodeAxes.cs` | Verbatim port (all `FailureResponse` and `JoinFiringRule` variants) |
| `Operator.cs` | `FrameFlow.Graph/Operator.cs` | Verbatim port (delegate signatures + `IOperatorContext` per Crossbar ADR-0021) |
| `IRefCounted.cs`, `RefBox.cs` | `FrameFlow.Graph/IRefCounted.cs`, `RefBox.cs` | Unchanged |
| `IFrame.cs`, `FrameMemoryDomain.cs` | `FrameFlow.Graph/IFrame.cs`, `FrameMemoryDomain.cs` | Unchanged |
| `CpuTensor.cs`, `CpuTensorPool.cs`, `ICpuTensor.cs`, `ITensor.cs`, `TensorShape.cs`, `DType.cs` | `FrameFlow.Graph/Tensors/` | Unchanged |
| `IAudioBuffer.cs`, `AudioSampleFormat.cs` | `FrameFlow.Graph/Audio/` | Unchanged |
| `IClockSource.cs`, `ClockSubject.cs` | `FrameFlow.Graph/Clock/` | Unchanged |

Files **not taken** (Crossbar-specific that FrameFlow doesn't need):

- Anything in `src/Crossbar.Cuda/` — stays in Crossbar.
- Anything in `src/Crossbar.Onnx/` — see §3, moves separately to
  `FrameFlow.Inference.Cuda`.

### 2. V1 is a verbatim port; slimming is a future, opt-in decision

The initial fork takes the substrate **verbatim**. No primitives are
removed in V1. The goal of the first pass is to get
`FrameFlow.Graph` building, the existing FrameFlow consumers
retargeted, and the inference + camera integration working
end-to-end. Slimming decisions follow once the consumer's actual
needs are observable rather than predicted.

Primitives that *might* be reasonable to remove in a future ADR
once the fork is stable — flagged here so the option stays visible,
not as commitments:

- **`MultiOperatorNode<TIn, TOut>` (1→N via `IAsyncEnumerable<TOut>`).**
  Media pipelines are predominantly 1→1 chains with fan-out at the
  edge level, not 1→N at the operator boundary. Rare cases like
  B-frame batching tend to live inside the operator. Probably
  removable later; keeping for V1.
- **`StorageNode<T>`.** The 2026-05-22 exploration analysis showed
  it's a vestigial identity pump — multi-`Connect` on an
  `OutputPort<T>` already supports fan-out without a dedicated
  node type. Probably removable later; keeping for V1.

Primitives **explicitly kept and unlikely to be reconsidered**:

- **`IOperatorContext`** (Crossbar ADR-0021) — operator-body
  context for substrate-shipped logging, drop-reason callbacks,
  operator-id introspection. Closure-capture as an alternative
  has known failure modes (the FrameFlow logger-plumbing audit;
  the `frame-flow@d03e4b0` silent-logger bug). Keep.
- **All three `JoinFiringRule` variants** — `WhenBoth`,
  `LatestWins`, `PrimaryDriven`. Audio+video sync and captions-
  overlay use cases reach for non-`WhenBoth` modes. Keep all.
- **`FrameMemoryDomain` enum** — Cpu/Gpu tagging on items. Even
  if FrameFlow.Graph runs primarily on host memory, adapter code
  that wraps device pointers via the ORT `OrtValue` escape hatch
  benefits from the tag. Keep.
- **Push and Pull `Shape` axis on `EdgeOptions`.** Media is mostly
  producer-paced + push, but pull-shape consumers exist (some
  Avalonia render-thread patterns, future request-response style
  HTTP integration). Keep both shapes.
- **Producer-Paced and Consumer-Paced `Cadence` axis.** Same
  rationale as Push/Pull — the second-order patterns benefit from
  having the option. Keep both.
- **`IStreamItem<T>` opt-in wrapper (Crossbar ADR-0018).** Media
  items need metadata beyond `PresentationTime` —
  `SequenceIndex` (for Zip-style ordered joins), `SourceId` (for
  multi-source mixing), and lineage tags for debugging. The
  opt-in wrapper is how that metadata is carried at the substrate
  level. Keep.
- **All `FailureResponse` variants** (`Propagate`, `Discard`,
  `Retry`). Per-operator error policy is useful for non-critical
  branches (a logging operator that fails shouldn't kill the
  whole graph). Keep all. *(Correction: the forked code shipped the
  variant as `Discard`, not `Continue` — an earlier draft of this list
  misnamed it. The later substrate-slimming pass removed the
  never-implemented `Retry` variant once it was confirmed dead; the
  surviving policy axis is `Propagate` + `Discard`.)*
- **All `Underflow` variants** (`Block`, `Fail`, `Default`).
  Polling-style consumers and bootstrap-with-default pipelines
  may emerge. Keep all. *(The later substrate-slimming pass removed the
  whole `Underflow` axis — the runner never read it — along with the
  unread `Shape` and `Cadence` axes.)*

The substrate forked here is therefore a **near-1:1 port** of the
Crossbar substrate as of this commit. The fork's value comes from
*divergence after the port*, not from up-front trimming. Future
ADRs in FrameFlow's series can revisit any primitive — but each
removal is its own decision, with its own consequences, written
up at the time the removal happens.

Per the holoscan-sol gap analysis in
`crossbar/docs/comparisons/holoscan-sol-api-shapes.md`, FrameFlow's
substrate also has *additions* on the horizon (declared rates on
ports, timer-source primitives, sync-window joins, per-operator
deadlines). Those will land as their own ADRs as the consumer
demand surfaces.

### 3. Consolidate ORT inference wrappers into FrameFlow

The ORT-EP wrapper packages move from Crossbar into FrameFlow. The
shape across EPs is now uniform: every wrapper binds **host memory**
via `ICpuTensor`; per-EP differences are confined to bootstrap and
session-options configuration.

| Source (Crossbar) | Destination (FrameFlow) | Notes |
|---|---|---|
| `src/Crossbar.Onnx/OnnxInferenceSession.cs` | `src/FrameFlow.Inference.Cuda/CudaInferenceSession.cs` | **Rebound to host memory.** The `ICudaTensor` device-binding path is removed; inputs/outputs are `ICpuTensor`. ORT-CUDA EP stages internally. Optional escape-hatch overload accepts pre-built `OrtValue`s for advanced consumers who bring their own device pointers (per the 2026-05-22 conversation on resident NVDEC → inference) |
| `src/Crossbar.Onnx/OnnxProbe.cs` | `src/FrameFlow.Inference.Cuda/CudaProbe.cs` | Unchanged |
| `src/Crossbar.Onnx/CudaBootstrapper.cs`, `CudaDllResolver.cs`, `CudaBootstrapResult.cs`, `ICudaInstallInstructionProvider.cs`, `WindowsCudaInstallInstructionProvider.cs` | `src/FrameFlow.Inference.Cuda/Bootstrap/` | Native DLL resolution stays paired with the EP that needs it |
| `scripts/fetch-cuda.cs` (Crossbar) | `scripts/fetch-cuda.cs` (FrameFlow) | Dev-time CUDA redist fetcher |
| (Proposed `Crossbar.Onnx.Dml` per crossbar ADR-0022) | `src/FrameFlow.Inference.Dml/DmlInferenceSession.cs` + `DmlProbe.cs` | Ships as part of this fork rather than separately |

A new `FrameFlow.Inference.Abstractions` package (or namespace inside
`FrameFlow.Graph`) defines the common `IInferenceSession` interface
the EP-specific packages implement. Consumer code (the YOLO
detector, future inference operators) references the abstraction; the
EP-specific package is the runtime dependency.

### 4. Camera integration via `FrameFlow.Camera`

A new `FrameFlow.Camera` sub-package owns the integration between
`Periphery.Camera` (capture) and `FrameFlow.Graph` (graph runtime).
Its responsibilities:

- **Adapter types.** `CameraFrameAdapter` (or similar) — a wrapper
  that implements `FrameFlow.Graph.IFrame` and
  `FrameFlow.Graph.IRefCounted` around a `Periphery.Camera.ICameraFrame`.
  Per `periphery` ADR-0045, the Periphery types are *substrate-
  agnostic*; the adapter is what gives them substrate identity for
  FrameFlow.Graph operators.
- **Graph adapters.** `CameraSourceNode` — converts a
  `Periphery.Camera.ICameraSession`'s frame stream into a
  `FrameFlow.Graph.SourceNode<CameraFrameAdapter>`. This is roughly
  what `periphery:src/Periphery.Camera.Pipelines/CameraSourceAdapters.cs`
  does today, rewritten against the new substrate.
- **Sink adapters.** `CameraFrameSinkNode` and friends, mirroring the
  current `Periphery.Camera.Pipelines/CameraFrameSinkAdapters.cs`.
- **Avalonia integration.** If `Periphery.Camera.Avalonia`'s content
  is camera-pipeline-shaped, it moves here too as
  `FrameFlow.Camera.Avalonia` (or folds into `FrameFlow.Avalonia` if
  the cross-cutting concerns warrant). If it's pure capture-side
  UI, it stays in Periphery.

Dependencies: `FrameFlow.Camera` references `Periphery.Camera` (as a
NuGet package via the local feed, the existing workspace convention)
and `FrameFlow.Graph`. Periphery does *not* reference FrameFlow.

### 5. Collapse `FrameFlow.Yolo.Cuda` + `FrameFlow.Yolo.Dml` into a single `FrameFlow.Yolo`

With both EP wrappers behind a unified `IInferenceSession`
abstraction, the two detector packages collapse:

- A single `FrameFlow.Yolo` package contains the model loading,
  preprocessing, postprocessing, and detection-extraction logic.
- The EP choice happens at composition time — consumer picks
  `FrameFlow.Inference.Cuda` or `FrameFlow.Inference.Dml` (or
  future `FrameFlow.Inference.TensorRT`) and passes the
  `IInferenceSession` to `FrameFlow.Yolo` at construction.
- The old `FrameFlow.Yolo.Cuda` and `FrameFlow.Yolo.Dml` packages
  are deleted; their differences were entirely in which inference
  session they used.

### 6. Existing FrameFlow packages migrate to `FrameFlow.Graph`

Every existing FrameFlow project that depends on Crossbar
re-targets:

| Project | Today's dependency | After |
|---|---|---|
| `FrameFlow.Media` | `Crossbar` (for `IFrame`, refcount) | `FrameFlow.Graph` |
| `FrameFlow.Decoding` | `Crossbar` | `FrameFlow.Graph` |
| `FrameFlow.Playback` | `Crossbar` | `FrameFlow.Graph` |
| `FrameFlow.Audio` | `Crossbar` (for audio buffer primitive) | `FrameFlow.Graph` |
| `FrameFlow.Audio.OpenAL` | (transitively via FrameFlow.Audio) | unchanged |
| `FrameFlow.Avalonia` | `Crossbar` (probably) | `FrameFlow.Graph` |
| `FrameFlow.Yolo.{Cuda,Dml}` | `Crossbar.Cuda` (for ICudaTensor), `Crossbar.Onnx` | Collapsed into `FrameFlow.Yolo` against `FrameFlow.Inference.Abstractions` |

`using Crossbar;` becomes `using FrameFlow.Graph;` across the
codebase. Mechanical sed-style change, plus removing the Crossbar
PackageReference from each `.csproj`.

### 7. Divergence policy

After the fork, `FrameFlow.Graph` is **its own codebase under
FrameFlow's evolution discipline.** Specifically:

- FrameFlow is free to add, remove, or restructure primitives in
  `FrameFlow.Graph` without consulting Crossbar.
- Crossbar's substrate may evolve independently; FrameFlow does not
  track those changes.
- If a future improvement in either codebase looks valuable for the
  other, the receiving side may *deliberately* port it (writing an
  ADR that documents the choice). This is *not* automatic.
- If a primitive in `FrameFlow.Graph` and Crossbar drift in
  incompatible ways, neither side owes the other a fix.

## Consequences

### What this enables

- **Media-centric API ergonomics.** FrameFlow.Graph can ship the
  primitives that media pipelines actually want — declared
  rates per port, timer-source primitives, sync-window joins,
  per-operator deadlines — without negotiating with Crossbar's
  Holoscan-class roadmap. The gap analysis in
  `crossbar/docs/comparisons/holoscan-sol-api-shapes.md`'s
  "Gaps Crossbar would need to fill" section becomes the
  FrameFlow.Graph roadmap.
- **Single-package inference story.** A consumer wanting
  inference picks exactly one of `FrameFlow.Inference.Cuda`,
  `FrameFlow.Inference.Dml`, or future `FrameFlow.Inference.TensorRT`.
  The model code lives in `FrameFlow.Yolo` and is EP-agnostic.
  Switching EPs is a PackageReference change.
- **Periphery integration via clean dependency edge.** FrameFlow
  consumes `Periphery.Camera` as a normal NuGet dependency.
  Periphery's standalone identity (per its ADR-0045) is preserved.
- **Sibling-repo coordination shrinks.** With ORT wrappers in
  FrameFlow and camera integration in FrameFlow, the cross-repo
  surface area between FrameFlow and Crossbar drops to zero. The
  cross-repo surface to Periphery is one direction only
  (FrameFlow → Periphery.Camera).

### What this rules out (until reopened)

- **Cross-substrate operator portability.** A FrameFlow.Graph
  `OperatorNode<T,U>` is not a Crossbar `OperatorNode<T,U>`; the
  types live in different namespaces and packages. An operator
  authored for one does not run on the other without porting.
- **Free reuse of Crossbar's future GPU primitives.** When
  Crossbar ships `cuMemPoolCreate`-backed pools, IPC events,
  green-context partitioning, etc., FrameFlow.Graph does not
  automatically benefit. Those primitives are designed for the
  Holoscan-class consumer, not for FrameFlow.
- **Direct device-tensor inference binding.** The "I bring a
  device pointer; ORT runs inference on it without staging" path
  remains *available* through the `Run(IReadOnlyDictionary<string, OrtValue>)`
  escape hatch on `CudaInferenceSession`, but FrameFlow does not
  ship a `Crossbar.Cuda`-style allocator to produce those device
  pointers. Consumers either bring pointers from a third party
  (FFmpeg's NVDEC outputs) or accept host-staging.

### Consequences for prior FrameFlow ADRs

- **ADR-0030** (unify frame contracts with Crossbar) — superseded
  in spirit. `IVideoFrame` no longer extends `Crossbar.IFrame`; it
  extends `FrameFlow.Graph.IFrame`. The unification stays — both
  the video and camera sides now extend `FrameFlow.Graph.IFrame`
  (camera via the adapter in `FrameFlow.Camera`).
- **ADR-0046** (native runtime acquisition strategy) — extended.
  CUDA-EP and DirectML-EP bootstrap join the existing native-
  runtime story. ADR-0011 from Crossbar (native bootstrapping)
  moves with the code.

### Migration sequence

A workable order for executing the fork:

1. **Copy substrate code from Crossbar into `src/FrameFlow.Graph/`**
   verbatim, with namespace rename (`Crossbar.*` → `FrameFlow.Graph.*`).
   Verify the new project builds in isolation against the renamed
   namespaces.
2. **Retarget existing FrameFlow projects** (Media, Decoding,
   Playback, Audio, Avalonia) to `FrameFlow.Graph`. Remove
   `Crossbar` PackageReferences from `.csproj` files. Switch
   `using Crossbar;` to `using FrameFlow.Graph;` across the
   codebase.
3. **Move ORT wrappers** from Crossbar to
   `src/FrameFlow.Inference.{Cuda,Dml}/`. Apply the
   `ICpuTensor`-binding rewrite. Create the
   `FrameFlow.Inference.Abstractions` boundary.
4. **Wait for Periphery ADR-0045 to land.** Periphery drops
   Crossbar dependencies from `ICameraFrame`.
5. **Create `src/FrameFlow.Camera/`** with the
   `CameraFrameAdapter`, `CameraSourceNode`, `CameraFrameSinkNode`.
   Migrate or rewrite content from
   `periphery:src/Periphery.Camera.Pipelines/` (now deleted on
   the Periphery side).
6. **Collapse YOLO packages.** Build the new `FrameFlow.Yolo`
   against `FrameFlow.Inference.Abstractions`. Delete
   `FrameFlow.Yolo.Cuda` and `FrameFlow.Yolo.Dml`.
7. **Update examples.** Every example that referenced
   `Crossbar.*` updates its `using` clauses and PackageReferences.
8. **Build the full solution; fix the inevitable mechanical issues.**
9. **Update `README.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`** with
   the new architecture. The "FrameFlow depends on Crossbar"
   framing is gone.
10. **Defer slimming.** Per §2, no primitives are removed during
    the fork. Future ADRs revisit specific primitives
    (`MultiOperatorNode`, `StorageNode`) once the V1 substrate is
    settled and consumer usage patterns are observable.

### API stability

Per FrameFlow's "no consumers yet" stance, the migration
is unconstrained. Every consumer is in this workspace; every
consumer follows.

## Alternatives considered

### A. Move *Crossbar* into FrameFlow as `FrameFlow.Graph` sub-package

Considered. Rejected because Crossbar's Holoscan-class charter is
preserved in its own repo per Crossbar ADR-0024. A fork is the
mechanism that lets both ambitions coexist; moving Crossbar would
extinguish one of them.

### B. Keep depending on Crossbar; just rewrite ORT wrappers in FrameFlow

The minimum-change version: FrameFlow keeps its Crossbar
PackageReference; only the inference wrappers move; nothing forks.

Rejected because the Crossbar substrate's *charter* points toward
Holoscan-class primitives (streams, events, green contexts) that
FrameFlow will not benefit from but will pay for in API surface
area and primitive sprawl. The fork lets FrameFlow's substrate
evolve toward media ergonomics (rate declarations, timer sources,
sync-window joins) that Crossbar would not prioritize. Coordinating
two divergent roadmaps in one substrate is the original problem.

### C. Single combined mega-package

Roll Crossbar, FrameFlow, Periphery into one library. Discussed
during the 2026-05-22 conversation and explicitly stepped back from
— the three libraries have distinct identities (Holoscan-class
substrate / media library / hardware peripherals library) that the
mega-package would muddle.

### D. Fork via git history rewrite rather than copy

Use `git subtree split` or a similar tool to preserve Crossbar's
substrate history in FrameFlow.Graph. Considered for the audit
trail value, rejected because the "no consumers yet" stance makes
the history-preservation work cost more than the benefit. The
forked code's history starts fresh in FrameFlow with this ADR as
the audit trail; the old history remains in Crossbar's repo if
anyone ever needs to spelunk.

## References

- `crossbar` ADR-0024: Substrate fork — FrameFlow.Graph divergence
  (the authorizing ADR on the Crossbar side).
- `crossbar` ADR-0014: Primitive-set substrate (the substrate being
  forked from).
- `crossbar` ADR-0022: DirectML backend (re-scoped — the DML
  inference wrapper lands here as `FrameFlow.Inference.Dml`).
- `periphery` ADR-0045: Substrate independence (the Periphery-side
  change that lets `FrameFlow.Camera` consume Periphery without
  Crossbar in the dependency chain).
- `crossbar/docs/comparisons/holoscan-sol-api-shapes.md`: API gap
  analysis whose "gaps Crossbar would need to fill" section
  becomes the post-fork FrameFlow.Graph roadmap.
- The "no consumers yet" stance held by all three libraries, which is
  what authorised a restructure spanning them.
