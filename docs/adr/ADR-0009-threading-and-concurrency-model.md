# ADR-0009: Threading and Concurrency Model

## Status

Accepted

## Context

The playback pipeline is built around three concurrent worker loops described in ARCHITECTURE.md:

- **Demux loop** — reads packets from the container and routes them to stream queues.
- **Video decode/present loop** — decodes video packets, converts frames, applies sync timing, and hands frames to the presenter.
- **Audio decode/output loop** — decodes audio packets, resamples, and queues PCM into the audio sink.

These loops communicate through bounded queues with explicit backpressure policies. When a queue is full the demux loop blocks; when video frames are stale they may be dropped; audio buffers must never run unbounded.

The project targets .NET latest with async-first public APIs and uses CancellationToken throughout. ADR-0005 establishes that the thread which allocates a native resource is the thread that frees it. The Avalonia presenter must marshal decoded frames to the UI thread, but the playback core is designed to be headless and must not depend on any UI framework.

A clear threading model is needed so that every loop, queue, cancellation path, and resource boundary behaves predictably under normal playback and during teardown.

## Decision

### Queue primitive

All inter-loop communication uses System.Threading.Channels.Channel<T> configured with a bounded capacity and BoundedChannelFullMode.Wait. This is the single queue abstraction for the pipeline. It is async-native, integrates with CancellationToken, and enforces backpressure by design.

### Worker loop execution

Each of the three worker loops runs via Task.Run with a long-running async delegate. Dedicated Thread instances are not used. This keeps the model compatible with sync/wait, allows structured cancellation, and avoids manual thread lifecycle management.

### Cancellation hierarchy

Each worker loop creates its own CancellationTokenSource linked to the parent session token via CancellationTokenSource.CreateLinkedTokenSource. This gives the playback session a single top-level token that tears down all loops, while still allowing an individual loop to be cancelled independently during graceful shutdown sequencing.

### UI thread marshalling

Marshalling decoded frames to a UI thread is exclusively the presenter's responsibility. The playback core must not reference any dispatcher, SynchronizationContext, or UI-framework type. Presenters such as the Avalonia presenter accept managed frame contracts from the channel and handle their own thread affinity internally.

### Native resource thread affinity

Per ADR-0005, native resources must not cross thread boundaries. The thread that allocates a native handle is the thread that frees it. What crosses the bounded channels are managed contracts — DecodedVideoFrame, PcmAudioBuffer, and similar value-oriented types — never raw native pointers or the wrappers that own them.

## Consequences

### Positive

- Bounded channels give every queue measurable depth and deterministic backpressure without custom synchronization code.
- Task.Run with async loops composes naturally with the rest of the async-first API surface and with CancellationToken.
- Linked cancellation tokens provide a clean teardown hierarchy: cancel the session token and all loops observe it, or cancel one loop in isolation.
- Keeping dispatcher awareness out of the core means the core remains fully headless and testable without a UI host.
- Restricting channel payloads to managed contracts enforces the native-ownership boundary from ADR-0005 at the type level.

### Negative

- Bounded channels with Wait mode mean the demux loop can stall when downstream consumers are slow; tuning queue capacities will require profiling.
- Task.Run loops rely on the thread pool, which may introduce scheduling jitter compared to dedicated high-priority threads; this is acceptable for v1 software-decoded playback but may need revisiting for real-time guarantees.
- Each presenter must independently implement its own marshalling strategy, which adds work for every new presenter target.

## Alternatives Considered

### Dedicated Thread instances

Rejected. Dedicated threads do not compose well with sync/wait, make structured cancellation harder, and require manual lifecycle management that Task.Run with linked tokens already handles.

### BlockingCollection<T>

Rejected. BlockingCollection<T> is synchronous-first. It does not expose async read/write APIs and would force the pipeline into blocking waits that conflict with the async-first design and CancellationToken integration.

### System.Threading.Channels.UnboundedChannel<T>

Rejected. Unbounded queues violate the backpressure design constraint. A slow consumer would allow memory to grow without limit, which is unacceptable for continuous media playback.

### Core-level dispatcher awareness

Rejected. Embedding dispatcher or SynchronizationContext knowledge into the playback core would couple the headless library to a specific UI framework and break the separation between core and presenter described in ARCHITECTURE.md.

### TPL Dataflow

Rejected. TPL Dataflow adds an external dependency and a more complex programming model for what are straightforward single-producer, single-consumer loops. Channel<T> provides the needed functionality with less abstraction overhead.