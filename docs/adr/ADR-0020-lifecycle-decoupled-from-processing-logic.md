# ADR-0020: Lifecycle Decoupled from Processing Logic

## Status

Proposed

## Context

ARCHITECTURE.md section 9 establishes a design goal: initialization, startup, and teardown of resources should be separated from the processing logic that uses them. The rationale is to avoid mixing four distinct concerns inside a single class:

- resource acquisition
- environment validation
- runtime processing
- disposal policy

When these concerns coalesce, the resulting class becomes hard to test in isolation, hard to reason about during debugging, and fragile during teardown because cancellation and disposal paths interleave with in-flight processing state.

FrameFlow already applies this principle well in several places. The bootstrapper (`FrameFlowBootstrapper`) is strictly a lifecycle manager — it resolves FFmpeg paths, probes availability, and caches the result, but never participates in demuxing or decoding. Factories like `DemuxSessionFactory` and `PlaybackSessionFactory` encapsulate construction sequences and hand off fully-formed objects without retaining ownership of processing state. `PlaybackClock` is a focused state machine whose only job is time tracking.

However, parts of the codebase still exhibit coupling between lifecycle management and processing logic. This ADR codifies the principle, defines the boundaries, and identifies the patterns to follow.

## Decision

FrameFlow will treat lifecycle separation as a structural constraint, not a guideline. The following rules apply:

### 1. Constructors must not perform resource acquisition

Constructors should accept pre-allocated dependencies and store them. They must not open devices, allocate native contexts, call FFmpeg functions, or perform I/O. If a component requires multi-step initialization that can fail, that work belongs in a dedicated factory method, static `Open()` method, or factory class.

**Rationale:** A constructor that performs allocation forces callers to handle partial construction failures. It also prevents the object from being created in a testable state without real resources.

### 2. Factories own the allocation-and-handoff sequence

Factory methods and factory classes are the designated owners of resource acquisition sequences. They allocate resources, validate them, and either return a fully-initialized object or clean up on failure. Once the handoff is complete, the factory retains no references to the created object's internal state.

**Rationale:** This keeps the "open" sequence in one place, makes error handling for partial allocation explicit, and ensures the created object starts in a known-good state.

### 3. Lazy initialization during processing must be justified

Deferring resource initialization into the first processing call (e.g., creating a pixel format converter on the first decoded frame) is sometimes necessary when upstream metadata is not available until processing begins. When this pattern is used:

- it must be documented with a comment explaining why eager initialization is not possible
- the lazy initialization must be idempotent and thread-safe within the component's threading model
- the cost of first-call initialization should be noted in any public API documentation

**Rationale:** Lazy initialization during processing couples a lifecycle event (resource creation) with a processing event (first frame). This is acceptable when FFmpeg does not populate format metadata until decode time, but it should not be used as a convenience shortcut when eager initialization is feasible.

### 4. Worker task lifecycle must be explicit and sequenced

Starting and stopping background worker tasks is a lifecycle operation, not a processing operation. When a component manages multiple workers:

- startup order dependencies must be enforced by synchronization primitives (e.g., `TaskCompletionSource`, barriers), not by relying on `Task.Run` call ordering
- shutdown must cancel the source (demux pump) before draining consumers (decoders)
- shutdown must await all workers with a bounded timeout before proceeding to disposal
- the worker management logic should be separable from the processing logic those workers execute

**Rationale:** Implicit ordering based on call sequence is fragile under refactoring. Explicit sequencing makes the dependency visible and testable.

### 5. Disposal must not assume processing state

`Dispose` / `DisposeAsync` must be safe to call regardless of whether processing completed normally, was cancelled, or never started. Disposal should:

- cancel any in-flight work
- await worker completion with a timeout
- release resources in reverse allocation order
- never throw from the disposal path (log and continue)

Disposal must not contain processing logic such as flushing remaining frames to a presenter or draining a queue for correctness. If such finalization is needed, it belongs in an explicit `StopAsync()` or `CompleteAsync()` method called before disposal.

### 6. Start/Stop symmetry in stateful adapters

Components with an explicit start/stop lifecycle (audio sinks, device adapters) should follow symmetric pairing:

- `StartAsync()` acquires runtime resources and transitions to an active state
- `StopAsync()` halts processing, flushes buffers, and transitions to an idle state
- `DisposeAsync()` releases all resources regardless of current state

`StopAsync()` followed by `StartAsync()` must be safe (restart). `DisposeAsync()` without `StopAsync()` must also be safe (the component handles its own cleanup). A three-step lifecycle where Stop de-initializes but does not free, leaving cleanup to Dispose, is acceptable only when the intermediate state is documented and the transitions are enforced.

## Consequences

### Positive

- components become testable in isolation because lifecycle can be controlled independently of processing
- teardown paths are predictable because disposal does not depend on processing state
- refactoring worker startup order does not silently break sequencing invariants
- lazy initialization is permitted but visible and intentional

### Negative

- some components require additional factory methods or coordinator types
- enforcing startup ordering with synchronization primitives adds a small amount of complexity
- the constraint may feel heavy for simple components that would naturally combine a small amount of initialization with processing

### Neutral

- existing components that already follow these rules (bootstrapper, factories, clock, presenter) require no changes
- the deferred SWS/SWR initialization pattern in VideoDecoder and AudioDecoder is compliant because it is justified by FFmpeg behavior, but should be documented more explicitly

## Alternatives Considered

### Treat lifecycle separation as advisory only

Rejected. Advisory guidance drifts over time. Structural constraints are enforced during review and prevent regression.

### Require eager initialization for all native contexts

Rejected. FFmpeg does not always populate frame format metadata before the first decode call. Requiring eager initialization would force incorrect assumptions about pixel format and sample layout.

### Extract all worker management into a dedicated WorkerCoordinator

Considered but deferred. A dedicated coordinator would improve PlaybackSession's separation, but the current worker count (3-4 tasks) does not yet justify a new abstraction. If worker complexity grows or additional playback modes are added, this should be revisited. For now, enforcing explicit synchronization at startup and shutdown within PlaybackSession is sufficient.

## Compliance Checklist

When reviewing code against this ADR, check:

- [ ] Constructor performs no I/O, no native allocation, no device opening
- [ ] Multi-step initialization lives in a factory or static `Open()` method
- [ ] Lazy initialization during processing has a justifying comment
- [ ] Worker startup ordering is enforced by synchronization, not call order
- [ ] Worker shutdown cancels producers before consumers
- [ ] `DisposeAsync()` is safe regardless of processing state
- [ ] Start/Stop pairs are symmetric in stateful adapters
