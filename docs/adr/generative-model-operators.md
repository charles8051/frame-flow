# ADR-XXXX: Generative models on video, through a caller-owned chat client

## Status

**Withdrawn (2026-09-29).** Proposed the same day. Not implemented, and not to be numbered.

An application builds this from FrameFlow's public API, so FrameFlow does not need the operator:

- **A seekable source** is better served by a second decoder that reads ahead of the playhead. A
  tap on the playback graph cannot read ahead, because `ClockSelectVideoSink` keeps the decoder only
  a few frames ahead of the clock, and that is why decision 9 below has to accept late results. A
  reader that samples a few frames seconds ahead has each answer ready before its picture plays.
- **A live source**, which has nothing to read ahead, takes a `Branch(EdgeOptions.LatestWins(1))`
  ending in a `SinkNode` declared `FrameHolding.InFlight`. The sink drops the frame while the
  application's model worker is busy, and otherwise encodes it and hands the bytes over. The model
  call runs on the application's own task, so no frame is held during a call and stopping the graph
  never waits on one.

The abandonable node decision 7 names is not pursued. The record below is kept as it was proposed.

Promotes [the GenAI exploration](../explorations/generative-inference.md), which holds the evidence:
package compatibility, what ONNX Runtime GenAI's source does, and the review findings that shaped
this record.

The operator depends on two changes to `FrameFlow.Graph` that are not decided here: the fix for #489
and an abandonable node (decision 7). Each needs its own fix or record before `Describe` ships.

Related: [ADR-0038](ADR-0038-memory-domain-pipeline-operators.md) (memory-domain operators),
[ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md) (one error model),
[ADR-0072](ADR-0072-tests-do-not-depend-on-elapsed-time.md) (no elapsed time in tests),
[ADR-0078](ADR-0078-graph-chain-forks-joins-and-termination.md) (branches and termination),
[An immutable blueprint for the graph](immutable-graph-blueprint.md) (node factories).

## Context

A vision-language model (VLM) can describe a frame or answer a question about it. With constrained
decoding it answers in a schema, and the answer can decode into a typed record the way YOLO's output
decodes into `Detection`. Local servers and hosted services run such models behind an
OpenAI-compatible API, and ONNX Runtime GenAI runs them in process.

Three properties separate a generative model from the models FrameFlow runs today:

- **It takes seconds.** YOLO takes milliseconds. Graph behaviour nobody notices at 10 ms shows at
  2 s: end of stream and looping wait for the branch, seek and stop wait for the body to return, and a
  frame the node holds stays out of the decoder's pool for the whole generation.
- **Its input is an encoded image.** A server takes bytes, and GenAI cannot bind device memory. The
  GPU-resident path does not apply.
- **Its output is text of unknown length.** `IInferenceSession.Run` writes into outputs the caller
  allocated from static shapes.

## Decision

### 1. The inference interfaces stay as they are

`IInferenceSession`, `IDeviceInputSession`, `IImageModel`, `InferenceOperators.Infer` and
`InferenceSessionFactoryBuilder` do not change. Generation gets its own operator and contracts in a
new package that `FrameFlow.Inference.Abstractions` does not reference. YOLO and BlazeFace users take
no new dependency.

### 2. The model runs outside the player's process, and FrameFlow ships no model host

The caller points an `IChatClient` at a local server or a hosted service. FrameFlow ships the operator
and no `IChatClient` implementation.

In process, FrameFlow's ORT versions would follow GenAI's releases, apps on
`FrameFlow.Inference.Dml` could not use it, `OgaShutdown` would be process-wide, the model's
gigabytes would live in the player, and a GPU reset during a long prefill would take DirectML down
until the process restarts. Running in process would buy GPU-resident input, which GenAI cannot use.
Out of process, the model has its own runtime, device and failure domain, and every FrameFlow EP
package can use it, DirectML's included.

### 3. The caller owns the client through a serializer that spans graphs

The caller creates the `IChatClient`. No node constructs one, because a node factory can run once per
graph instance and a seek builds a new instance.

`IChatClient` does not say how a client behaves under concurrent calls or cancellation, and decision
7 stops waiting for a call without ending it. The caller therefore hands the client to a FrameFlow
serializer, which owns it from then on, and passes the serializer to `Describe`. The serializer lives
across loops, seeks and graph instances. It starts a call only after the previous call has returned,
whether that call finished, failed or was abandoned. A client that ignores cancellation then delays
the next call, not the graph, and two generations never overlap on one client. The serializer cannot
stop a server finishing a request the client failed to cancel; that cost stays with the client.

The caller disposes the serializer, not the client. `DisposeAsync` refuses new calls, cancels the call
in flight, waits for it to return, and then disposes the client. A call abandoned by a graph that has
already stopped is still the serializer's, so no call outlives the client it runs on. A client that
ignores cancellation makes `DisposeAsync` wait for the rest of its call.

### 4. A frame prompt is a pure value, written for one model family

```csharp
public interface IFramePrompt<TResult>
{
    RotatedRect CropFor(FrameInfo frame);
    ImageSize ImageFor(ImageSize crop);
    PromptText PromptFor(FrameInfo frame);
    GenerationOptions Options { get; }
    GenerationOutcome<TResult> Decode(GeneratedText text, TensorTransform transform, FrameInfo frame);
}
```

It never sees pixels and carries no `Microsoft.Extensions.AI` type; the operator maps it to a
`ChatMessage` at the edge. `FrameInfo` is a frame's timestamp and size. `Decode` is total:
`GenerationOutcome<TResult>` is a result, or Truncated, Refused or Unparseable.

A prompt belongs to a model family because answers locate things differently by family. Qwen2.5-VL
answers in pixels of the image its processor resized; Qwen3-VL answers in coordinates normalized to 0
to 1000. `ImageFor` returns the size that family's processor resizes to, the operator sends the image
at that size, and the transform maps an answer back to the frame once. Image placeholders and the chat
template are the server's concern.

### 5. One node on a `LatestWins(1)` branch, holding one frame

`Describe` puts one node on a branch whose edge is `LatestWins(1)`. While the node is busy, the edge
keeps only the newest frame, so the node always takes a frame at most one frame interval old. For
each frame it asks the cadence core (decision 6) whether to fire, and drops the frame if not. For a
frame it fires on, it reads back, crops, resizes and encodes, sends the image to the client, and emits
the outcome. It reads back only the frames it fires on.

The node holds its frame until the call returns, and declares it: `FrameHolding.AtMost(1,
forwardsStorage: false)`, as `Infer` does. The fixed-pool budget counts frames, not time
([ADR-0081](ADR-0081-fixed-pool-budget.md)), so the decoder's pool gets one more frame for it: a
decoder surface of about 3 MB at 1080p.

The caller passes the readback function and the image encoder at the call site. The readback stays as
explicit as ADR-0038 asks of `ToCpu`, and the operator package takes no reference to FFmpeg.
FrameFlow provides a managed PNG encoder, since PNG is one of the formats OpenAI's image input
accepts. A caller that wants smaller payloads for a hosted service passes a JPEG encoder.

### 6. A pure core decides when to call the model

`(state, frameTimestamp, now, triggered) -> Fire | Skip`, with the clock owned by the shell. It covers
an interval, a trigger from a detector's results or a motion event, and backoff after a failure:
`(state, outcome, now) -> (state', notBefore)`. Without it the operator would call the model back to
back, which on a hosted service is a call every few seconds per stream.

### 7. The node is abandonable

At end of stream the node drops its call in flight and the frame waiting in its edge, so end of
stream and a loop's restart do not wait on a generation. `FrameFlow.Graph` gains a per-node option
for this: a node marked abandonable has its body cancelled, and its buffered input dropped, when its
input completes. When the graph is cancelled for a seek or a stop, its pump does not wait for the
cancelled body either. Other nodes finish their work as they do today, so a pass that wants its last
result still gets it. Its semantics go in their own record.

Abandoning ends the graph's wait, not the call. The serializer in decision 3 holds the next call on
the same client until the abandoned one returns.

### 8. A failure is a result

A client failure, a timeout or an unparseable answer becomes an outcome on the output, not an
exception. `FailureResponse.Propagate` would stop playback on one rate-limit response, and `Discard`
would hide it. An exception from the operator's own code remains a fault.

The operator does not ship until #489 is fixed. Until then an HTTP client's timeout, which surfaces as
`TaskCanceledException`, ends the graph as a clean finish.

### 9. Results are late annotations of a past frame

A new result type carries the frame's timestamp and the time the answer completed. It is not
`InferenceResult<TResult>`, whose `Path` means nothing here. A result arrives seconds after its frame,
and a consumer that draws it over the picture draws it over a later frame. `PresentedResults` can
still order results; it cannot make them current.

### 10. Scope

Vision-language and text models. Speech recognition, whose streaming form keeps state between calls,
needs its own record. GPU-resident input is out of scope.

## Consequences

- A new operator package over `Microsoft.Extensions.AI.Abstractions`, whose release pace then affects
  FrameFlow's public surface. It stays out of `FrameFlow.Inference.Abstractions`.
- The serializer is the one piece of the operator's state that outlives a graph instance. The caller
  owns it, and through it the client.
- Three pure cores: the prompt, the cadence and the backoff. Each is testable without a model or a
  clock. Operator tests use a fake client that completes through `TaskCompletionSource` (ADR-0072).
- An end-to-end test runs against a local OpenAI-compatible server with greedy decoding, asserts that
  the answer matches its schema rather than its text, and is skipped when no server is configured.
- Per-request cost is the server's. A server built on GenAI prefills the whole prompt on every
  request, because GenAI's C# API gives a VLM no prefix reuse.
- Order of work: fix #489; the abandonable node, with its record; the cores and the operator against
  a fake client; an example against a local server.

## Alternatives considered

- **Extending `IInferenceSession` to generate.** It would touch the ORT base class, the four EP
  sessions and ten test sessions, and leave FrameFlow writing the tokenizer, KV cache and token loop.
- **An in-process `FrameFlow.GenAI` client beside the operator.** It carries the costs in decision 2.
  It can be added later without changing the operator, if a measurement shows the local round trip
  matters.
- **A FrameFlow sidecar over GenAI.** It isolates the model, but FrameFlow would own a server's
  lifecycle, IPC and deployment when existing local servers already do that.
- **Abandoning everything on a branch when the trunk completes.** Every tap would lose its last
  result, YOLO's included.
- **Raising end of stream when the presentation sink completes and letting branches finish.** A loop
  would start its next iteration while the previous one's generation still runs on the same client.
- **Two nodes: one that snapshots and releases the frame, one that generates.** The split exists
  only to avoid holding a frame during a generation, and the pool budget already covers that hold.
  The snapshot node would also need to know when the generate node is free, which the graph cannot
  express without one of the next two changes.
- **A demand edge, where the upstream node runs only when the downstream node asks.** It is the
  general primitive for "prepare work only when the consumer can take it", but FrameFlow's graph is
  push-based. Adding it means a consumer-to-producer signal, gating in the pumps, a new class of
  blocking edge for the fork-rejoin deadlock check, wake-ups at termination, and new `EdgeOptions`
  surface. The one other place it would apply, a `ToCpu` node in front of a fast detector, has
  staleness of two runs of a few milliseconds, and demand would stop readback and inference
  overlapping there.
- **A busy flag shared by two nodes, or an operator that releases its input early.** Either lets the
  two-node split work. Neither is needed once one node holds the frame.
- **`ToCpu` in front of the node.** With the default blocking edge, `ToCpu` converts a frame and
  waits, so from the third run the node reads a frame two runs, here several seconds, old. A
  `LatestWins(1)` edge after it keeps the frame fresh but reads back every frame.
- **A FrameFlow-owned session interface instead of `IChatClient`.** It gains nothing in tests, and as a
  synchronous enumerable it would hold a pool thread for seconds and block on a remote client.
- **The vision encoder on FrameFlow's device path.** `InferenceRunner` refuses the encoder's dynamic
  patch count, and GenAI accepting precomputed image features is unverified.
