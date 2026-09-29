# ADR-XXXX: Generative models on video, through a caller-owned chat client

## Status

Proposed (2026-09-29). Draft pending number assignment. Nothing here is implemented.

Promotes [the GenAI exploration](../explorations/generative-inference.md), which holds the evidence:
package compatibility, what ONNX Runtime GenAI's source does, and the review findings that shaped
this record.

The operator depends on three changes to `FrameFlow.Graph` that are not decided here: the fix for
#489, an abandonable node (decision 7) and a demand edge (decision 8). Each needs its own record or
fix before `Describe` ships.

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
  frame held by the node pins a decoder surface for the whole generation.
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

### 3. The client is owned by the caller

The caller creates the `IChatClient` and disposes it. No node constructs one, because a node factory
can run once per graph instance and a seek builds a new instance.

`IChatClient` does not say how a client behaves under concurrent calls, dispose during a call, or
cancellation. The operator makes one call at a time per operator, passes the graph's token, and
documents that a client which ignores the token delays seek and stop by the rest of its call.

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

### 5. Two nodes, joined by a demand edge

`Describe` builds two nodes on a branch whose edge is `LatestWins(1)`:

- **The snapshot node** runs when the generate node asks for work. It takes the newest frame, asks
  the cadence core (decision 6) whether to fire, and drops the frame if not. For a frame it fires on,
  it reads back, crops, resizes and encodes, and emits a snapshot: the image bytes, the `FrameInfo`
  and the transform. It declares `FrameHolding.Boundary`, so no frame is held during a generation.
- **The generate node** sends the snapshot to the client, emits the outcome, and then asks for the
  next snapshot.

Between them is a demand edge (decision 8), so the snapshot node reads back and encodes only frames
the generate node will take, and only when it is free. The frame it takes is at most one frame
interval older than the newest.

The caller passes the readback function and the image encoder at the call site. The readback stays as
explicit as ADR-0038 asks of `ToCpu`, and the operator package takes no reference to FFmpeg.
FrameFlow provides a managed PNG encoder, since PNG is one of the formats OpenAI's image input
accepts. A caller that wants smaller payloads for a hosted service passes a JPEG encoder.

### 6. A pure core decides when to call the model

`(state, frameTimestamp, now, triggered) -> Fire | Skip`, with the clock owned by the shell. It covers
an interval, a trigger from a detector's results or a motion event, and backoff after a failure:
`(state, outcome, now) -> (state', notBefore)`. Without it the operator would call the model back to
back, which on a hosted service is a call every few seconds per stream.

### 7. The generate node is abandonable

At end of stream the generate node drops its call in flight and the snapshot waiting for it, so end
of stream and a loop's restart do not wait on a generation. `FrameFlow.Graph` gains a per-node option
for this: a node marked abandonable has its body cancelled, and its buffered input dropped, when its
input completes. Other nodes finish their work as they do today, so a pass that wants its last result
still gets it. Its semantics go in their own record.

### 8. The graph gains a demand edge

An edge mode in which the upstream node runs only when the downstream node asks for an item. It is a
general graph feature with its own record. It also answers the stale input that `ToCpu` in front of
any slow node delivers today, `Infer`'s included: with the default blocking edge, `ToCpu` converts a
frame and waits, and from the third run the slow node reads a frame two runs old.

### 9. A failure is a result

A client failure, a timeout or an unparseable answer becomes an outcome on the output, not an
exception. `FailureResponse.Propagate` would stop playback on one rate-limit response, and `Discard`
would hide it. An exception from the operator's own code remains a fault.

The operator does not ship until #489 is fixed. Until then an HTTP client's timeout, which surfaces as
`TaskCanceledException`, ends the graph as a clean finish.

### 10. Results are late annotations of a past frame

A new result type carries the frame's timestamp and the time the answer completed. It is not
`InferenceResult<TResult>`, whose `Path` means nothing here. A result arrives seconds after its frame,
and a consumer that draws it over the picture draws it over a later frame. `PresentedResults` can
still order results; it cannot make them current.

### 11. Scope

Vision-language and text models. Speech recognition, whose streaming form keeps state between calls,
needs its own record. GPU-resident input is out of scope.

## Consequences

- A new operator package over `Microsoft.Extensions.AI.Abstractions`, whose release pace then affects
  FrameFlow's public surface. It stays out of `FrameFlow.Inference.Abstractions`.
- Three pure cores: the prompt, the cadence and the backoff. Each is testable without a model or a
  clock. Operator tests use a fake client that completes through `TaskCompletionSource` (ADR-0072).
- An end-to-end test runs against a local OpenAI-compatible server with greedy decoding, asserts that
  the answer matches its schema rather than its text, and is skipped when no server is configured.
- Per-request cost is the server's. A server built on GenAI prefills the whole prompt on every
  request, because GenAI's C# API gives a VLM no prefix reuse.
- Order of work: fix #489; the abandonable node and the demand edge, each with its record; the cores
  and the operator against a fake client; an example against a local server.

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
- **A busy flag shared by the two nodes.** It works without a graph change, but the coupling is
  invisible to the topology and to validation, and it fixes only this operator.
- **An operator that releases its input before its body completes.** It changes the `Operator`
  contract for one caller, and a node that snapshots on every frame still reads back frames nobody
  takes.
- **A FrameFlow-owned session interface instead of `IChatClient`.** It gains nothing in tests, and as a
  synchronous enumerable it would hold a pool thread for seconds and block on a remote client.
- **The vision encoder on FrameFlow's device path.** `InferenceRunner` refuses the encoder's dynamic
  patch count, and GenAI accepting precomputed image features is unverified.
