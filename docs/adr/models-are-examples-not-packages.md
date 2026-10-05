# ADR-XXXX: Models are examples, not packages

## Status

**Proposed (2026-10-05).** Not implemented. Assign the number at merge.

Related: [ADR-0049](ADR-0049-frameflow-graph-fork-from-crossbar.md) (the EP split that made the detector
take an `IInferenceSession`),
[ADR-0038](ADR-0038-memory-domain-pipeline-operators.md) (memory-domain operators),
[the GenAI exploration](../explorations/generative-inference.md) (a model family FrameFlow does not ship).

## Context

FrameFlow ships two model-family packages, `FrameFlow.Yolo` and `FrameFlow.Face` (BlazeFace). Each holds
the code that surrounds one family of ONNX models:

- a preprocessor that builds an `ImageToTensorOptions`,
- a postprocessor that decodes the output and runs non-maximum suppression (NMS),
- a descriptor, a class table or anchor generator, and result types (`Detection`, `FaceDetection`),
- for YOLO, `Yolov8ModelDownloader`, which fetches `yolov8n.onnx` on first run.

Neither package contains weights. Neither has an entry in `PublicAPI.Shipped.txt`; both surfaces are
still in `PublicAPI.Unshipped.txt`.

The contract these packages consume lives in `FrameFlow.Inference.Abstractions`: `IInferenceSession`,
`IDeviceInputSession`, `IImageModel`, `ImageToTensor` and `InferenceOperators.Infer`. The pixel work,
including `ImageFit.Letterbox`, `ImageBorder`, `ImageSampling` and the D3D12 device path, is already
there. Both preprocessors delegate to `ImageToTensor`. `BlazeFacePreprocessor` selects `Letterbox` and
returns a `TensorTransform`. `Yolov8Preprocessor` selects a stretched resize and returns two scale
factors.

Shipping a model family costs FrameFlow three things the contract does not:

- **A public surface per family.** A family's decode rules change with the model, not with FrameFlow.
  BlazeFace has already produced two behaviour changes in `BREAKING-CHANGES.md`.
- **A choice of weights.** The downloader names a URL and a cache path for every consumer. The licence of
  those weights has not been checked for this record.
- **A reason to grow.** Each new model asks for another package, and the packages have no shared
  vocabulary beyond the contract.

Two copies of greedy NMS already exist, in `Yolov8Postprocessor` and `BlazeFacePostprocessor`. They differ
in IoU threshold (0.45 and 0.3), in class handling (per class and single class), and in how a suppressed
box is treated (BlazeFace merges by weighted average).

## Decision

### 1. The contract ships. Model families do not.

`FrameFlow.Inference.Abstractions`, the execution-provider packages and `InferenceOperators.Infer` remain
packages. `FrameFlow.Yolo` and `FrameFlow.Face` stop being packages.

### 2. Their source moves under `examples/`

A new project, `examples/FrameFlow.Examples.Models`, holds a `Yolo/` and a `Face/` folder. It is not
packable. The examples that reference `FrameFlow.Yolo` or `FrameFlow.Face` today reference it instead.
The spikes that reference them do the same.

The namespaces are kept, so a consumer who copies the folder changes no `using`.

### 3. The tests move with the code and stay in `tests/`

`FrameFlow.Yolo.Tests`, `FrameFlow.Face.Tests` and the two model-specific tests in
`FrameFlow.Inference.Cpu.Tests` reference the examples project. They are the witness that a real model
runs through the contract: `Infer`, fp16 input, device input and the non-finite-output rules.

A test that needs access to `internal` members, such as the preprocessors' `Options`, gets
`InternalsVisibleTo` on the examples project.

### 4. The downloader is deleted

An example that needs weights fetches them itself and states where they came from. The licence of each
set of weights is recorded in the example's README, not in a package.

### 5. NMS and other post-processing are not shared

FrameFlow does not ship an NMS helper, and does not depend on a vision library for one. Each model's
example code keeps its own. Letterboxing stays where it is, in `ImageToTensor`.

A preprocessor in an example selects the fit, border, sampling, normalization and layout through
`ImageToTensorOptions` and does no pixel math of its own.

### 6. A new model family is added as an example

A request for a model family is answered with an example folder, not a package. The bar for adding a
type to `FrameFlow.Inference.Abstractions` is that two families need it.

## Consequences

- The packable surface shrinks to the contract and the execution providers. `PublicAPI.Unshipped.txt` for
  Yolo (85 lines) and Face (149 lines) leaves the analyzer's scope.
- Consumers who referenced `FrameFlow.Yolo` or `FrameFlow.Face` copy the folder. This needs a
  `BREAKING-CHANGES.md` entry under the next minor.
- The examples become the supported reference for a family's decode rules. They are built and tested in
  CI, so they cannot go stale unnoticed.
- Comments in `src/Directory.Build.props`, `FrameFlow.Inference.Abstractions.csproj` and
  `FrameFlow.Inference.Ort.csproj` name `FrameFlow.Yolo` as a consumer and need rewording.
- Nothing about the contract changes. `IInferenceSession`, `IImageModel` and `Infer` are as they were.

## Alternatives

- **Keep the packages.** Keeps the one-line install. Keeps the per-family surface, the downloader and the
  open licence question.
- **One `FrameFlow.Models` package.** Moves the surface into a single package without removing it, and
  invites a catalogue.
- **A shared vision-utilities package for NMS and box math.** Pulls in the options a general NMS needs
  (per class, soft, rotated, merge by average) for a loop of a few dozen lines over a few hundred boxes.
- **A third-party vision library for NMS.** Brings its own image and tensor types into a pipeline whose
  frames stay on the device.

## Follow-ups

- `Yolov8Preprocessor` stretches the frame and its comment defers letterboxing. When it moves, switch it
  to `ImageFit.Letterbox` and return a `TensorTransform` as Face does. That changes detection results on
  non-square frames, so it is a separate change with its own entry in `BREAKING-CHANGES.md`.
- Check the licence of the weights the downloader fetches before the downloader's replacement in the
  examples is written.
- Check whether any version of `FrameFlow.Yolo` or `FrameFlow.Face` has been pushed to a feed. If one
  has, the packages need a final release that points to the examples.
