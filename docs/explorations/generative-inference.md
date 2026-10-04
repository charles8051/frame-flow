# Exploration: generative models through ONNX Runtime GenAI

**Status:** Exploration, concluded. Its proposals went into
[Generative models on video, through a caller-owned chat client](../adr/generative-model-operators.md),
which is withdrawn: an application builds generative-model features from FrameFlow's public API, and
FrameFlow adds no operator for them. This document keeps the evidence.

**Date:** 2026-09-29. [TensorSharp](#tensorsharp) added 2026-10-04.

**Related:** #489, a graph bug this exploration found.

**Sources:** [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai) (GenAI below) was
read from source at tag `v0.17.0`. The 0.17.1 NuGet packages are `v0.17.0` plus one unrelated fix,
built from a branch with no tag. A result marked *(tested)* comes from loading the win-x64 binaries
of 0.17.1; everything else is read from source, package metadata or documentation. TensorSharp was
read from source at commit
[`2cef684`](https://github.com/zhongkaifu/TensorSharp/tree/2cef684fb70585fda22cc83679119b77bc41289a),
the 2026.10.03 release, and nothing of it was run.

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
- **TensorSharp.** A .NET engine for GGUF models, evaluated in [TensorSharp](#tensorsharp).

## TensorSharp

[TensorSharp](https://github.com/zhongkaifu/TensorSharp) is a .NET 10 inference engine for GGUF
models. It runs on managed CPU kernels, its own CUDA kernels, and GGML's CPU, CUDA, Vulkan and Metal
backends. The question here is whether a VLM could run in the player's process, on frames from the
graph, through TensorSharp instead of GenAI.

Links in this section point into commit `2cef684`.

### Against GenAI

- **No ORT.** GGML is linked statically into TensorSharp's own `GgmlOps` library
  ([CMakeLists.txt](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.GGML.Native/CMakeLists.txt#L84)).
  It can sit beside every FrameFlow EP package, `Inference.Dml` included. `FrameFlow.Whisper` ships
  its ggml as `ggml-whisper.dll` and siblings, so the native names do not collide.
- **Prefix reuse for a VLM.** The engine's radix cache keys each image by a content id at its
  position, and reuse stops at the first image that differs
  ([PromptMediaSpan.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Runtime/Scheduling/PromptMediaSpan.cs#L28)).
  The leading system messages are reused across requests from different cache scopes
  ([paged attention](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/docs/PAGED_ATTENTION_AND_CONTINUOUS_BATCHING.md#L451-L455)).
  Instructions in the system message are prefilled once. GenAI prefills them on every request
  ([Prompt prefix](#prompt-prefix)).
- **Constrained output.** `SamplingConfig.Grammar` takes a constraint built from
  `GrammarLibrary.ForJsonSchema`, `ForJsonObject` or `ForGbnf`
  ([GrammarLibrary.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Runtime/Grammar/GrammarLibrary.cs#L69)).
  Each sequence needs its own constraint instance.
- **An asynchronous engine.** `InferenceEngine.SubmitRequest(SequenceState, CancellationToken)`
  returns a handle with a `ChannelReader<int>` of tokens and a `Completion` task
  ([InferenceEngine.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Runtime/Scheduling/InferenceEngine.cs#L166)).
  Several threads may call it, and the engine batches sequences itself without the HTTP server.

### Image input

- Images go in as file paths, in `ChatMessage.ImagePaths`
  ([ChatTemplate.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Runtime/ChatTemplate.cs#L26)).
  The model's image processor reads the file and decodes it on the CPU: its own codec for PNG,
  StbImageSharp for JPEG, Magick.NET for anything else
  ([ImageProcessorUtils.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Models/ImageProcessorUtils.cs#L29-L37)).
  Resizing and normalizing run in managed code.
- `Qwen35VisionEncoder.Encode(float[] pixelValues, int resizedH, int resizedW)` is public and takes
  preprocessed pixels
  ([Qwen35VisionEncoder.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Models/Models/Qwen35/Qwen35VisionEncoder.cs#L169)).
  The injector that places an encoder's output in a prompt is internal, and it identifies media by
  hashing the file
  ([ModelMultimodalInjector.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Models/ModelMultimodalInjector.cs#L1294)).
  Calling `Encode` directly gives up the engine and its prefix cache.
- No API takes a device buffer. The direct CUDA backend retains the device's primary context and
  creates its own streams
  ([CudaContext.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Backends.Cuda/CudaContext.cs#L31)).
  GGML creates its own CUDA and Vulkan devices. Nothing in the tree imports external memory or
  interoperates with D3D11 or D3D12.

A frame for TensorSharp is read back, encoded and written to a file. As with GenAI, the
[GPU-resident path](#gpu-resident-input) does not apply. PNG keeps Magick.NET off the decode.

### Costs in the player's process

- **Process-wide state.** One GGML backend type per process
  ([ggml_ops_core.cpp](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.GGML.Native/ggml_ops_core.cpp#L721-L727)).
  The CUDA resolver prepends CUDA directories to `PATH`
  ([CudaLibraryResolver.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Backends.Cuda/Interop/CudaLibraryResolver.cs#L64-L82)).
  A module initializer installs the desktop media codecs, GGML's static constructor can set native
  environment variables, and model creation writes to the console.
- **Cancellation.** The token aborts a request between engine steps
  ([InferenceEngine.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Runtime/Scheduling/InferenceEngine.cs#L297-L301)).
  A step is one decode, or one prefill chunk of 256 tokens when batching and 8192 when running alone.
  The vision encode runs before submission, under the model's `GpuComputeLock`, and takes no token
  ([ChatGenerationPipeline.cs](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Chat/ChatGenerationPipeline.cs#L474-L479)).
  Only Gemma 4's encoders release that lock mid-encode. The same comment puts an image encode at
  100 ms to 2 s.
- **Disposal.** `InferenceEngine.Dispose` waits up to 60 s for the worker to leave its step, then
  fails pending requests with `ObjectDisposedException`.
- **Dependencies.** `TensorSharp.Models` references the GGML, CUDA and MLX backend assemblies,
  Magick.NET, OpenCvSharp4 with native runtimes, PdfPig, NLayer and NVorbis
  ([TensorSharp.Models.csproj](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/TensorSharp.Models/TensorSharp.Models.csproj#L35-L61)).
  Its OpenCV runtimes cover win-x64, Ubuntu 24.04 x64 and arm64, and macOS, with no win-arm64. The
  NuGet packages carry no native code
  ([publish-nuget.yml](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/.github/workflows/publish-nuget.yml#L11-L13)).
  An app builds `GgmlOps` or takes it from TensorSharp's release archives.
- **Windows GPUs.** The release archives are CPU builds and CUDA 12.6 builds. Vulkan is forced off
  in them
  ([release-binaries.yml](https://github.com/zhongkaifu/TensorSharp/blob/2cef684fb70585fda22cc83679119b77bc41289a/.github/workflows/release-binaries.yml#L204-L210)),
  so an AMD or Intel GPU needs a source build. There is no DirectML or D3D12 backend. The CUDA build
  bundles `cublas64_12.dll`, a name `FrameFlow.Inference.Cuda` also loads. Whether the two copies
  coexist in one process is untested.
- **Native AOT.** No project carries trimming or AOT annotations, and the op registry is built by
  reflection.
- **Maintenance.** One author wrote about 860 of roughly 1,000 commits (GitHub contributors API,
  2026-10-04). nuget.org lists three date-versioned releases in five weeks: 2026.9.1, 2026.9.29 and
  2026.10.3. No API-stability policy is stated.

### Video

TensorSharp decodes video through OpenCvSharp's `VideoCapture`, samples one frame per second by
default, writes each sampled frame to a temporary PNG, and passes the paths with `IsVideo` and
`ImageTimestamps` set. The Qwen-VL family pairs frames into temporal patches labelled with their
time. Gemma 4 takes one image per frame, stamped mm:ss. An application on FrameFlow can build the
same request from frames FrameFlow already decoded, and ask one question over a window of frames
rather than one frame per call.

### In the graph

TensorSharp needs no FrameFlow operator. For a live source an application uses the shape the
withdrawn record names: a `Branch(EdgeOptions.LatestWins(1))` ending in a `SinkNode` declared
`FrameHolding.InFlight`. The sink drops a frame while the model is busy. Otherwise it encodes the
frame, writes the file and hands the path to the application's worker, which submits it to
`InferenceEngine` on its own task. Stopping the graph then never waits on a vision encode it cannot
cancel.

`TensorSharp.Server.Host` serves the same engine behind OpenAI- and Ollama-compatible APIs, so it
also fits the out-of-process route. That keeps the process-wide state, OpenCV and Magick.NET out of
the player. It listens on all interfaces with no authentication.

### Verdict

An application can run TensorSharp in process with no change to FrameFlow. TensorSharp has no ORT
version coupling, runs beside `Inference.Dml`, and reuses a VLM prompt's prefix. It does not give
the player's process GPU-resident input, the one thing in-process running could offer. That needs a
change in TensorSharp: a public engine input that takes decoded pixels or a tensor with a
caller-supplied content id. The withdrawn record's conclusion stands.

## Order of work

| Step | Where | Complexity |
|---|---|---|
| Fix #489 | `FrameFlow.Graph` | Small: one exception filter in five pumps, two regression tests |
| Spike: a VLM on GenAI.WinML beside `Inference.WinML`, in both load orders; an image through placeholders and the processor; `terminate_session` during a prefill; the processor's target size; prefill time for the intended prompt, measured apart from decode; on a Qwen-VL generator, a generation on one image, `RewindTo` the shared prefix, and a second request with a different image, compared with a fresh generator given that second request | Outside `src` | Small to medium |
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
