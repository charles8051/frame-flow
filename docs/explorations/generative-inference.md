# Exploration: generative models through ONNX Runtime GenAI

**Status:** Exploration. Nothing here is decided.

**Date:** 2026-09-29

**Related:** #489, a graph bug this exploration found.

**Sources:** [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai) (GenAI below) was
read from source at tag `v0.17.0`. The 0.17.1 NuGet packages are `v0.17.0` plus one unrelated fix,
built from a branch with no tag. A result marked *(tested)* comes from loading the win-x64 binaries
of 0.17.1; everything else is read from source, package metadata or documentation.

## Question

Can FrameFlow run generative models through GenAI, and what would change in FrameFlow's inference
interfaces?

## Verdict

It is feasible, and `IInferenceSession`, `IDeviceInputSession` and `IImageModel` stay as they are.
Three questions come before any code:

1. Whether the model runs in the player's process or in another one
   ([In process or out of process](#in-process-or-out-of-process)).
2. Whether the pieces work together. No multimodal generation has run beside a FrameFlow EP package
   yet ([Order of work](#order-of-work) describes the spike).
3. How the graph treats a node that runs for seconds
   ([Graph behaviour for a slow node](#graph-behaviour-for-a-slow-node)).

## What it would enable

This exploration covers vision-language models (VLMs) and text models.

- **Questions about frames.** Qwen2.5-VL, Qwen3-VL, Phi-3.5-vision and Gemma 3 can describe a frame
  or answer a question about it.
- **Typed answers.** GenAI constrains output to a JSON schema, a regular expression or a Lark
  grammar. An answer can then decode into a record, as YOLO's output decodes into `Detection`.
- **Chosen frames.** A detector result or a motion event can decide which frames a VLM sees. The
  operator needs a trigger input for this ([Cadence and trigger](#cadence-and-trigger)).
- **Text over captions.** A language model can translate or summarize what `TranscribeWithWhisper`
  produces.

GenAI also runs speech models. Moonshine and Nemotron stream through its `StreamingProcessor`;
Parakeet, Whisper and Phi-4-multimodal take a whole utterance. Streaming recognition takes
continuous input and keeps state between calls. The request-and-response shape below does not cover
it, and speech is left to its own exploration. GenAI's Whisper ignores `suppress_tokens` and
`begin_suppress_tokens` ([onnxruntime-genai#2201](https://github.com/microsoft/onnxruntime-genai/issues/2201)),
and its output differs from the reference implementation's.

## What GenAI provides

GenAI wraps ONNX Runtime (ORT) with what a generative model needs around its sessions: the
tokenizer and chat template, the KV cache, the token loop with search and sampling, and processors
that turn images and audio into model inputs. A model is a folder holding `genai_config.json`, the
ONNX files, the tokenizer files, and `processor_config.json` for images or
`audio_processor_config.json` for speech ([config reference](https://onnxruntime.ai/docs/genai/reference/config.html)).

The C# surface is `Model`, `Config`, `Tokenizer`, `TokenizerStream`, `GeneratorParams`, `Generator`,
`MultiModalProcessor`, `StreamingProcessor`, `Images`, `Audios` and `Adapters`
([src/csharp](https://github.com/microsoft/onnxruntime-genai/tree/v0.17.0/src/csharp)). It also
ships `OnnxRuntimeGenAIChatClient`, an implementation of `IChatClient` from Microsoft.Extensions.AI.

## Packaging

FrameFlow ships one package per execution provider (EP): `FrameFlow.Inference.Cpu`, `.Cuda`, `.Dml`
and `.WinML`. Each brings its own native `onnxruntime.dll`, and an app references exactly one.

GenAI's native library imports `onnxruntime.dll` by name: statically in the CPU, CUDA and Foundry
builds, and delay-loaded in the WinML build. Loaded into a win-x64 process after ORT 1.30, GenAI
0.17.1 bound to that module and loaded no second copy *(tested)*. Binding on Linux was not tested.

A pairing needs two things. The NuGet packages must not bring a second native ORT, and the GenAI
library must accept the loaded ORT's API version.

| FrameFlow package | Its native ORT | GenAI package and what it requires | Status |
|---|---|---|---|
| `Inference.Cpu` | `Microsoft.ML.OnnxRuntime` 1.30.0 | `Microsoft.ML.OnnxRuntimeGenAI`: `Microsoft.ML.OnnxRuntime` 1.30.0 or later | Loads *(tested)*. No model run. |
| `Inference.WinML` | ORT 1.27.1, inside `Microsoft.Windows.AI.MachineLearning` 2.3.42 | `.WinML`: `Microsoft.Windows.AI.MachineLearning` 2.1.1 or later, no ORT package | Initializes against 1.27.1 *(tested)*. No model run. |
| `Inference.Cuda` | `Microsoft.ML.OnnxRuntime.Gpu`, pinned to `1.26.*` for CUDA 12 | `.Cuda`: `.Gpu` 1.30.0 or later, a CUDA 13 build | Conflicts. Needs the pin at 1.30 and `scripts/fetch-cuda.cs` at CUDA 13. |
| `Inference.Dml` | `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 | None built for it | Blocked. |

DirectML is in [sustained engineering](https://learn.microsoft.com/en-us/windows/ai/directml/dml),
and GenAI no longer publishes a DirectML package:

- `Microsoft.ML.OnnxRuntimeGenAI.DirectML` stopped at 0.14.1. The packaging pipeline builds the CPU,
  CUDA, WinML and Foundry packages
  ([packaging.yml](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/.pipelines/packaging.yml)).
- The CPU, CUDA and Foundry libraries are built against ORT 1.26 headers and request API version 26
  with no fallback
  ([onnxruntime_api.h](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/models/onnxruntime_api.h)).
  Beside ORT 1.24.4 the CPU library fails to initialize *(tested)*. It has no DML: it imports
  neither `d3d12` nor `dxgi`.
- The WinML library is built against API version 24 and contains DML. It initialized beside the
  `onnxruntime.dll` from `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 *(tested)*. Its dependency
  `Microsoft.Windows.AI.MachineLearning` ships its own `onnxruntime.dll`, which collides with the
  DirectML package's. Excluding that copy is an untested route.

Every GenAI native package depends on a native ORT. The CPU package added beside `Inference.WinML`
or `Inference.Dml` puts a second `onnxruntime.dll` in the build output, and that failure comes
before any API version check. A `buildTransitive` target that fails the build when two native ORT
packages resolve would catch it.

Other facts:

- **Native size.** The GenAI library is 8.6 MB on win-x64 and 37.4 MB on linux-x64, on top of ORT.
- **Platforms.** The CPU package covers win-x64, win-arm64, linux-x64, linux-arm64 and osx-arm64.
  The CUDA package has no linux-arm64, and no package has osx-x64.
- **Native AOT.** A .NET 10 win-x64 Native AOT publish with GenAI 0.17.1 and ORT 1.30.0 built with
  no warnings and ran *(tested)*. It was not tested against a model.
- **Telemetry.** The native libraries send telemetry. Setting `ORT_DISABLE_TELEMETRY=1` before
  initialization turns it off; `Utils.DisableTelemetryEvents()` suppresses only non-essential events
  ([Privacy.md](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/docs/Privacy.md)).
- **Shutdown.** Disposing `OgaHandle` calls `OgaShutdown` for the process. Every other GenAI object
  must be disposed first.

## GPU-resident input

GenAI creates its own D3D12 device, command queue and DML device
([dml_helpers.cpp](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/ep/dml/dml_helpers.cpp),
[dml/interface.cpp](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/ep/dml/interface.cpp)).
It replaces any CUDA stream the caller passes with its own
([cuda/session_options.cpp](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/ep/cuda/session_options.cpp)).
The C# `Tensor` constructor wraps its buffer as CPU memory, and there is no way to bind a device
buffer as an input. Images go in through `Images.Load`, as file paths or one image's encoded bytes,
and are decoded on the CPU. On Windows the decoder is WIC.

A frame for a generative model is therefore read back and encoded on the CPU, and the device path of
[GPU-resident inference](../feature-specs/gpu-resident-inference/spec.md) does not apply. I expect the
readback and encode to be small next to a generation. Neither has been measured for this path.

## Fit with the existing interfaces

`IInferenceSession.Run` runs the model once and writes into outputs the caller allocated from the
model's static shapes. `InferenceRunner` refuses an output with a dynamic dimension
([InferenceOperators.cs:181](../../src/FrameFlow.Inference.Abstractions/InferenceOperators.cs)).
Generation is a loop of runs with an output length unknown in advance, a KV cache carried between
steps, and cancellation between tokens.

`IImageModel` fills a float tensor through `ImageToTensorOptions`. A GenAI model takes an encoded
image and preprocesses it itself.

Generation on raw ORT sessions would leave FrameFlow writing the tokenizer, the KV cache, the token
loop and each model family's image processor. Changing `IInferenceSession` to support it would also
touch `OrtInferenceSessionBase`, the four EP sessions built on it, and ten test sessions across four
test projects.

## In process or out of process

The one thing the player's process could offer GenAI is GPU-resident input, and GenAI cannot use it.
Running there costs:

- FrameFlow's ORT versions follow GenAI's releases ([Packaging](#packaging)).
- Apps on `FrameFlow.Inference.Dml` cannot use it.
- `OgaShutdown` is process-wide.
- A model of several gigabytes lives in the player's memory.
- A GPU reset during a long prefill leaves DirectML unusable until the process restarts
  ([DML TDR recovery](../investigations/2026-08-14-dml-in-process-tdr-recovery.md)).

In another process, the model has its own ORT, device and failure domain, and it restarts without
the player. The image is encoded either way. The added cost is one local round trip per request.
Candidates are a sidecar FrameFlow ships over GenAI, Foundry Local, or any OpenAI-compatible local
server. Whether Foundry Local serves VLMs is not verified.

An operator written against `IChatClient` works with both. For an app on `Inference.Dml`, the
out-of-process route is the only one.

## Graph behaviour for a slow node

Four behaviours of `FrameFlow.Graph` go unnoticed while a model runs for milliseconds and show once
it runs for seconds.

1. **End of stream waits for the branch.** A graph finishes when every pump finishes, and no pump
   cancels on a clean exit ([Graph.cs](../../src/FrameFlow.Graph/Graph.cs)). At end of stream the
   branch still finishes its generation in flight, then the frame waiting in its `LatestWins(1)`
   edge. The player raises end of stream after the graph returns
   ([SubstrateSession.cs:1716](../../src/FrameFlow.Playback/SubstrateSession.cs)). A looping clip
   holds its last frame until the branch is done. A branch node that drops its in-flight and
   buffered work when its upstream completes would fix it. That is a graph feature.
2. **Seek and stop wait for the branch.** Both cancel the graph and wait for every pump. Checking
   the token between tokens does not interrupt a prefill, which is one native call. GenAI's
   `Generator.SetRuntimeOption("terminate_session", "1")` sets ORT's terminate flag on the run
   ([model.cpp](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/models/model.cpp)).
   Whether it interrupts a prefill when called from another thread is not tested.
3. **A body's `OperationCanceledException` ends the graph as a clean finish** (#489). An HTTP
   timeout surfaces as `TaskCanceledException`. Until #489 is fixed, a hosted backend's timeout ends
   playback silently.
4. **The error model has two responses.** `FailureResponse.Propagate` stops playback on one
   unparseable answer or one rate-limit response. `Discard` drops the failure silently, and the next
   frame calls the backend again at once. A generative operator reports failures as results, and a
   pure core decides when to call again: `(state, outcome, now) -> (state', notBefore)`.

## Proposed shape

### Unchanged

`IInferenceSession`, `IDeviceInputSession`, `IImageModel`, `InferenceOperators.Infer`, the EP
sessions and `InferenceSessionFactoryBuilder`.

### Backend

`IChatClient` from `Microsoft.Extensions.AI.Abstractions` is the seam to the model, at the shell edge
only. It carries images as `DataContent`, a JSON schema through `ChatResponseFormat.ForJsonSchema`,
streaming through `GetStreamingResponseAsync`, and a cancellation token. It works in process and out
of process alike.

It leaves gaps FrameFlow fills:

- Regular-expression and Lark constraints have no typed option. They pass through
  `ChatOptions.AdditionalProperties`.
- It says nothing about concurrent calls, dispose during a call, or cancellation latency. FrameFlow's
  backends promise one call at a time per model, and dispose cancels the call in progress.

GenAI's `OnnxRuntimeGenAIChatClient` is text only in 0.17.0. Its default formatter reads
`message.Text`, and a custom `PromptFormatter` returns a string for the tokenizer
([OnnxRuntimeGenAIChatClient.cs](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/csharp/OnnxRuntimeGenAIChatClient.cs)).
The in-process backend is FrameFlow's own `IChatClient` over `MultiModalProcessor` and `Generator`.

A FrameFlow-owned `IGenerativeSession` was the alternative. It gains nothing in tests, where both
take a fake. As a synchronous `IEnumerable<string>` it would hold a thread-pool thread for seconds,
and it would be synchronous over asynchronous for a remote backend.

### Frame prompt

The counterpart to `IImageModel`: which part of the frame the model sees, at what size, what to ask,
and how the answer becomes a result. It is a pure value. It carries no Microsoft.Extensions.AI type
and never sees the frame's pixels. A sketch:

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

`FrameInfo` is a frame's timestamp and size. `GeneratedText` is the text and the reason generation
stopped. `GenerationOutcome<TResult>` is a result or one of Truncated, Refused and Unparseable, so
`Decode` is total.

A prompt is written for one model family:

- **Image placeholders.** Phi-3.5-vision and Phi-4-multimodal need `<|image_1|>` in the prompt;
  Qwen2.5-VL and Qwen3-VL need `<|vision_start|><|image_pad|><|vision_end|>`. Each image processor
  throws when the count does not match the images, and `ApplyChatTemplate` does not insert them for
  Phi-3.5-vision. GenAI's own examples insert them per model type
  ([Common.cs](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/examples/csharp/Common/Common.cs)).
- **Coordinates.** Qwen2.5-VL answers in pixels of the image its processor resized
  ([Qwen2.5-VL](https://qwenlm.github.io/blog/qwen2.5-vl/),
  [model discussion](https://huggingface.co/Qwen/Qwen2.5-VL-7B-Instruct/discussions/13)). Qwen3-VL
  answers in coordinates normalized to 0 to 1000. `ImageFor` returns the processor's target size,
  computed from `processor_config.json`. The operator resizes to that size, the processor's own
  resize then changes nothing, and the image the operator sent is the image the model answers about.
  The transform maps the answer back to the frame once.

### Operator

Two nodes on a branch with a `LatestWins(1)` edge:

1. **A gate** that drops frames while a generation is in flight or the cadence says skip. For a
   frame it passes, it reads back, crops, resizes and encodes, and emits a snapshot: the encoded
   bytes, the `FrameInfo` and the transform. It declares `FrameHolding.Boundary`. No frame or decoder
   surface is held during a generation.
2. **A generate node** that sends the snapshot to the backend and emits the outcome.

The two nodes share a busy flag, which is shell state.

The snapshot is at most one frame interval older than the newest frame when the generation starts.
With `VideoOperators.ToCpu` in front of a slow node instead, the default edge (capacity 1, blocking)
leaves a converted frame in the edge and another in `ToCpu`. From the third run on, the node reads a
frame two runs old. A `LatestWins(1)` edge after `ToCpu` keeps it fresh, but reads back every frame.

The gate reads back only the frames it passes. This departs from `Infer`, which refuses a GPU frame
without a device stage and points the caller to `ToCpu`, and from
[ADR-0038](../adr/ADR-0038-memory-domain-pipeline-operators.md), which makes `ToCpu` the explicit
readback. `GpuVideoFrame.ReadbackToCpuBgra32()` lives in `FrameFlow.Decoding`, with the FFmpeg bindings. The
gate takes the readback as an injected function and declares the memory domains it can read.

The image format depends on the backend. GenAI on Windows decodes through WIC, which reads BMP. A BMP
writer in managed code is enough there. A hosted backend needs PNG or JPEG. FFmpeg's MJPEG encoder is
built in; its PNG encoder needs zlib, which the macOS FFmpeg build leaves out.

### Cadence and trigger

Without a cadence the gate passes a frame whenever the backend is free, keeping the GPU busy beside
decode and presentation, or calling a hosted API every few seconds per stream. A pure core decides:
`(state, frameTimestamp, now, triggered) -> Fire | Skip`, with the clock owned by the shell. A
trigger comes in through a predicate or a join with a detector's results. The motion detector in
`FrameFlow.MotionClip` is internal to that tool.

### Results

`InferenceResult<TResult>.Path` (host or device) means nothing for a generation, and the type has no
field for a result's age. A result arrives seconds after its frame, and `PresentedResults` shows it
on the frame presented when it arrives. A generation's result is a late annotation of a past frame,
not an overlay for the frame on screen. A result type carrying the frame's timestamp and the
completion time says so directly.

### Disposal and threading

`SessionUse`, the pure core under `OrtInferenceSessionBase`, lets `Dispose` return at once and
defers the native release to the end of a run in progress. For a generation that would leave several
gigabytes allocated until the answer finishes, and an `OgaShutdown` after it would run under a live
generation. The GenAI backend needs its own core, whose dispose transition cancels the generation
through `terminate_session` and releases once it stops.

GenAI's C API is not thread-safe. The backend runs each model on a dedicated thread fed from a queue.

### Prompt prefix

Every request repeats the same system prompt, instructions and schema, and only the image changes.
Reusing the KV cache for that prefix is not available to a VLM through GenAI's C# API:

- The `Engine` caches prefixes by default
  ([config.h](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/config.h#L789)). It
  has no C# binding, and it rejects multimodal position layouts
  ([simple_decoder.cpp](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/engine/decoders/simple_decoder.cpp#L34)).
- `Generator.RewindTo` throws for `phi3v` and `lfm2_vl`
  ([generators.cpp](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/generator/generators.cpp#L973)).
- For the other VLM types, `MultiModalPipelineState` does not override `RewindTo`, and the base
  `State::RewindTo` does nothing
  ([model.h](https://github.com/microsoft/onnxruntime-genai/blob/v0.17.0/src/models/model.h#L32)).
  A rewind on Gemma 3 or Qwen-VL would shorten the token sequence and leave the decoder's KV cache
  as it was, producing wrong output rather than an error. This is read from source, not run.

Each generation prefills its whole prompt. The backend does not call `RewindTo` on a VLM until the
spike settles what it does.

### Provider selection

`InferenceSessionFactoryBuilder` falls back from the preferred EP to the others, ending on CPU. That
suits YOLO, where CPU is slower but usable. For a VLM, CPU is a different product. A failed load of
several gigabytes also costs seconds and memory, and the factory holds its lock across construction
([InferenceSessionFactoryBuilder.cs](../../src/FrameFlow.Inference.Abstractions/InferenceSessionFactoryBuilder.cs)).
Generation opens on the EP the app names, with no fallback unless the app asks for one. If fallback
is wanted later, the chain-and-cache logic moves into a pure core that both shells share.

A model exported per EP lives in one folder per EP, which the app's open delegate chooses. GenAI's
model packages, which list a variant per EP in one manifest, are compiled out of the 0.17.1
libraries and have no C# binding. `Config.AppendProvider` picks the EP for one folder.

### GenAI package

`FrameFlow.GenAI` references `Microsoft.ML.OnnxRuntimeGenAI.Managed`, which carries no native code.
The app adds the native GenAI package that matches its FrameFlow EP package. `FrameFlow.GenAI`:

- holds the in-process `IChatClient`, its thread, and its lifecycle core;
- fails the build when two native ORT packages resolve;
- leaves `OgaHandle` and the telemetry setting to the app, since both are process-wide;
- maps `ExecutionProvider` to GenAI provider names. `ExecutionProvider.WindowsML` names no single
  GenAI provider. Its mapping depends on the package the app chose.

## Alternatives

- **The vision encoder on FrameFlow's device path.** The encoder would run as an `IDeviceInputSession`
  on the decoder's GPU, and only the language model through GenAI. `InferenceRunner` refuses the
  encoder's dynamic patch count, and whether GenAI accepts precomputed image features is not verified.
- **Preprocessing in FrameFlow.** `Generator.SetModelInput` can take the processor's outputs
  directly. The caller must then supply `pixel_values` in the model's layout (Qwen takes flattened
  patches), `num_image_tokens` (missing, the vision model is skipped without an error), the
  per-family inputs `image_sizes`, `image_grid_thw` or `token_type_ids`, and `input_ids` with the
  placeholders expanded. Only Gemma 3's fixed-size input is close to what `ImageToTensor` produces.
- **llama.cpp through LLamaSharp.** No ORT coupling, one GGUF file across backends, and Vulkan on
  integrated GPUs. Not evaluated.

## Order of work

| Step | Where | Complexity |
|---|---|---|
| Fix #489 | `FrameFlow.Graph` | Small: one exception filter in five pumps, two regression tests |
| Spike: a VLM on GenAI.WinML beside `Inference.WinML`, in both load orders; an image through placeholders and the processor; `terminate_session` during a prefill; the processor's target size; prefill time for the intended prompt, measured apart from decode; `RewindTo` on a Qwen-VL generator, compared with a fresh generator given the same prompt | Outside `src` | Small to medium |
| Decide in process or out of process | This document | |
| A branch node that drops its work when upstream completes | `FrameFlow.Graph` | Medium |
| Pure cores: prompt, cadence, retry, generation lifecycle | New operator package | Small each |
| Gate and generate nodes, tested against a fake `IChatClient` | New operator package | Medium |
| Backend: `FrameFlow.GenAI`, or a client for another process | New package | Medium |
| Example on `Inference.WinML` or `Inference.Cpu` | `examples/` | Small |

Tests on the fake client complete through `TaskCompletionSource`, not delays
([ADR-0072](../adr/ADR-0072-tests-do-not-depend-on-elapsed-time.md)). A real-model test uses greedy
decoding, asserts that the answer matches its schema rather than its text, and is skipped when the
model has not been downloaded.

## Decisions to make

1. **In process or out of process.** Decides whether `FrameFlow.GenAI` exists.
2. **How a branch gives up its work** when its upstream completes: a node option, an edge option, or
   a graph-wide policy for branches.
3. **Readback in the gate** against `ToCpu` as the one explicit readback (ADR-0038).
4. **The result type:** a new one, or `InferenceResult<TResult>` with an age.
5. **The operator package's name,** and whether it references `FrameFlow.Video` for the branch
   helpers.

## Out of scope

- Binding GPU-resident input.
- Running generative models on raw ORT sessions.
- Exporting models. GenAI's model builder is a Python tool.
- Speech recognition.
