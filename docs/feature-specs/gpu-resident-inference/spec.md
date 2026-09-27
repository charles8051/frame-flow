# GPU-resident inference

**Status:** Draft. Requirements 1 and 7 are met; the rest are open. Tracked by #288; every
requirement below names the issue that carries it. Living document, rewritten as the feature
changes.

**Date:** 2026-09-18. Measurements re-taken in a Release build 2026-09-26.

**Decision record:**
[ADR-0038](../../adr/ADR-0038-memory-domain-pipeline-operators.md) owns the memory-domain
operators and the `GpuVideoFrame` contract, and its 2026-09-18 amendment says which parts of it
are not in the tree. [ADR-0057](../../adr/ADR-0057-pull-based-master-clock.md) records the
held-lease coupling this feature has to answer for a path with no pacer.
[ADR-0079](../../adr/ADR-0079-the-pass-and-the-player.md) defers the pass-side option on a
trigger this feature fires. No `adr.md` here yet: the two decisions that would fill it (#291,
#292) are open, and an append-only record is the wrong place to think out loud.

## What

Hardware decode leaves a frame on the GPU. An inference model runs on the GPU. Today every frame
travels between them through system memory, twice.

The path a frame takes now, and what each leg costs. Measured on an RTX 3080 Ti, D3D11VA decode,
DirectML EP, 1080p H.264, Release build, two runs of ~1200 frames, p50 per frame:

| leg | p50 | where |
| --- | --- | --- |
| `av_hwframe_transfer_data` downloads NV12 | 4.5 ms | `VideoDecoder.BuildManagedFrame` |
| `sws_scale` converts NV12 to BGRA | 2.2 ms | `VideoDecoder.BuildManagedFrameFromCpu` |
| resize, normalize, HWC to CHW | 0.7 ms | `Yolov8Preprocessor.Preprocess`, through `ImageToTensor` (#363) |
| the model | 6.5 ms | the EP, including its host-to-device staging |
| 80-class decode and NMS | 0.8 ms | `Yolov8Postprocessor` |

7.3 ms of the ~14.6 ms is spent moving and reshaping pixels the GPU already had. The model is
6.5 ms.

**The download and the conversion are the cost.** Together they are 6.7 ms of the 7.3 ms.
Preprocessing was 1.1 to 1.5 ms before #363 vectorised it. The first version of this table put
preprocessing at 5.3 ms and called it the largest leg; that was a Debug build (see *Open
questions*). Instrumentation is `DecodeStageMetrics` and the Multicast.Dml example's
`--exit-after` (#282).

The end state: the decoder yields in the domain it decoded in, an operator preprocesses where the
pixels already are, the session binds a device pointer, and the only thing crossing the bus is the
output tensor.

## Requirements

1. **A GPU-yielded frame can reach a CPU operator.** (#279) Met: `VideoOperators.ToCpu` is the
   node ADR-0038 §4 specifies. Before it, all three `FrameFlow.Video` operators routed through
   `SwScaleVideoConverter.Process`, which threw on a `GpuVideoFrame`, so turning the decoder flag
   on made a graph unrunnable rather than faster.

2. **A GPU frame can surface its backend handle.** (#289) `TryGetD3D11Texture` is the only
   accessor. `Cuda` is a supported decode backend and `CudaInferenceSession` documents a
   device-pointer binding with no PCIe staging, naming FFmpeg's NVDEC output as the kind of thing
   that supplies one. Both ends are built; nothing joins them.

3. **An operator can consume a `GpuVideoFrame`, and survives one that is not.** (#290) The
   decoder chooses per frame — `ReceiveFrame` branches on `YieldHardwareFrames && onHardware`, and
   `TrackHardwareEngagement` documents that FFmpeg re-runs `get_format` on a coded-format or
   dimension change and can decide differently. So the operator's own input type varies within a
   run. `VideoFormatInfo` is `(Width, Height, Format)` and carries no memory domain, so there is no
   event to react to: it is a per-frame type test, and the CPU branch is a correctness requirement
   rather than a fallback.

4. **Preprocessing happens where the pixels are.** (#291) Resize, BGRA-to-RGB normalize and
   HWC-to-CHW transpose run on the device for a GPU-resident frame. Whether that is a per-backend
   kernel, work folded into the model graph, or something else is open. That it must move is not:
   a GPU-resident frame has no CPU pixels to preprocess, and downloading them is the cost this
   feature removes. The CPU branch is `ImageToTensor` (#363).

5. **A GPU frame's lifetime is bounded on a path with no pacer.** (#292) Each live
   `GpuVideoFrame` pins a slice of a default-sized hwframe pool. ADR-0057 records the resulting
   stall as confirmed, and the player's two answers — moving the wait into `ClockSelectVideoSink`,
   and the `_readbackEveryN` shedding rung the playback layer sets — are both unavailable to a
   `MediaPass`. An operator holding a frame across a model run is the same shape as the pacing hold
   that caused it. D3D12VA is the exception: its pool grows, so on that backend a held frame costs
   memory rather than a slice of a fixed pool (ADR-0081, 2026-09-27 revisions).

6. **A pass can ask for hardware frames.** (#277) `IPassBuilder.WithHardwareFrames`, one option
   and one assignment in `PassBuilder.BuildAsync`. It lands with requirement 3, not before: a flag
   whose only reachable consumer is a CPU operator is a flag that breaks the run.
   `PassBuilderTests.ThePassBuilderHasNoTransportOptions` currently asserts the option is absent
   and gives a reason, so this reverses a recorded decision rather than adding to one.

7. **Every claim above fails honestly on a machine without a GPU.** (#295) Met by #354:
   `RequiresHardwareDecodeFact` skips, naming what is missing, unless the probe initialised a
   backend for the codec under test. CI has no GPU on either leg, so skipping loudly there is the
   intended behaviour.

## Affected layers

| Layer | Change |
| --- | --- |
| `FrameFlow.Decoding` | Backend device accessors on `GpuVideoFrame`; whatever bounds in-flight leases; the `InternalsVisibleTo` grant to `FrameFlow.Video` finally has the consumer it was written for |
| `FrameFlow.Video` | The `ToCpu` node factory; `MapToGpu` later (#293) |
| `FrameFlow.Yolo` | The preprocessor splits by memory domain; `Yolov8Detector` stops assuming a CPU tensor |
| `FrameFlow.Inference.Cuda` | The `OrtValue` device-binding path gets its first caller |
| `FrameFlow.Inference.Dml` | No device-resident path exists — see *Open questions* |
| `FrameFlow.Player` | `WithHardwareFrames` on `IPassBuilder`, and the test that asserts its absence |
| `FrameFlow.Graph` | Only if the in-flight bound for requirement 5 belongs at the edge rather than in the operator contract |
| Tests | The hardware gate, and the end-to-end assertion that nothing was downloaded |

## Open questions

- **Which backend pairing goes first, and whether the measured one can go at all.** Zero-staging
  inference exists on `CudaInferenceSession` only. `DmlInferenceSession` documents that it has no
  device-resident escape hatch and stages host-to-device through D3D12 upload buffers. So an
  end-to-end GPU-resident path today means CUDA decode into CUDA inference — while the measurement
  above was taken on D3D11VA decode into DirectML, which is the pairing that cannot close without
  D3D12 resource binding as well. Decoding with D3D12VA, which the decoder already supports as a
  backend, would leave the frame as a D3D12 resource with a fence and remove the D3D11-to-D3D12
  sharing step. The session would still need a D3D12 device of its own to bind it (#298).
  Requirement 2 is written for the CUDA side because that is the side with a consumer. Whether the
  DML side is in scope is the first thing to settle, because it decides whether the numbers above
  describe a path this feature can deliver on that hardware.

- **Whether the output tensor is worth moving too.** The model's output is 84 × 8400 floats,
  about 2.8 MB per frame, against 4.9 MB for the input. Postprocess is 0.8 ms of CPU work on it.
  If the input upload goes away and the output download does not, the round trip is halved rather
  than removed.

- **What the CPU-only path costs after requirement 4.** Every consumer shares one
  `Yolov8Preprocessor`. If preprocessing splits by memory domain, the CPU branch has to stay at
  least as fast as it is now. Since #363 the CPU branch is `ImageToTensor`, shared by the YOLO and
  BlazeFace preprocessors and vectorised for an unrotated crop. On a random 1080p frame in a
  Release build, one consumer, the YOLO preprocess is 0.39 ms p50 against 1.15 ms for the loop it
  replaced.

- **How much of the table is the example's own overhead.** The Multicast.Dml example clones each
  frame to three panes with `CloneCpu()`, so the CPU legs compete with two extra full-frame copies.
  A single-consumer pass would show lower sws and preprocess figures.

- **Why the model and the download are slower in Release.** The first version of the table was a
  Debug build: re-running the commit before #363 in Debug reproduces it (download 3.6 to 3.7 ms,
  conversion 2.2 to 2.3, preprocess 5.3, model 5.0 to 5.1, postprocess 3.0). In Release the managed
  stages shrink about fourfold, but the model run and the download each come out about 1 ms slower
  than in Debug, in all four Release runs. The cause is not known. More GPU contention once the CPU
  stages stop spacing the frames out is one guess, and it is untested.

- **Where the inference ADR citations point.** `DmlInferenceSession` attributes its deferral to
  "ADR-0022", and `CudaInferenceSession` and ADR-0050 cite "ADR-0049 §3" for the `IInferenceSession`
  EP seam. In this repository ADR-0022 is long-lived workers with a pause gate and ADR-0049 is the
  graph fork. The numbers appear to predate that fork, so the rationale for the DML deferral is not
  reachable from the tree, and the first open question above cannot be answered from the record
  alone.
