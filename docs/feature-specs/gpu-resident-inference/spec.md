# GPU-resident inference

**Status:** Draft. Requirements 1, 3, 5, 6 and 7 are met, 2 is met for D3D12, and 4 is decided and
built. Tracked by #288; every
requirement below names the issue that carries it. Living document, rewritten as the feature
changes.

**Date:** 2026-09-18. Measurements re-taken in a Release build 2026-09-26.

**Decision record:**
[ADR-0038](../../adr/ADR-0038-memory-domain-pipeline-operators.md) owns the memory-domain
operators and the `GpuVideoFrame` contract, and its 2026-09-18 amendment says which parts of it
are not in the tree. [ADR-0057](../../adr/ADR-0057-pull-based-master-clock.md) records the
held-lease coupling this feature has to answer for a path with no pacer.
[ADR-0079](../../adr/ADR-0079-the-pass-and-the-player.md) defers the pass-side option on a
trigger this feature fires. This feature's own decisions are in [adr.md](adr.md): where
preprocessing runs (decision 1, #291) and the shape of the frame's backend handles (decision 2,
#289).

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

2. **A GPU frame can surface its backend handle.** (#289) Met for D3D12:
   `TryGetD3D12Texture` gives a D3D12VA frame's texture and the fence, with its value, that the
   decoder signals once the frame is written. That is what both inference spikes read by
   reflection (#420, #421). The CUDA device pointer of an NVDEC frame is deferred until a path
   decodes with CUDA directly ([decision 2](adr.md)): TensorRT-RTX gets CUDA memory from a
   D3D12VA frame by importing the preprocessed buffer.

3. **An operator can consume a `GpuVideoFrame`, and survives one that is not.** (#290) Built as
   `InferenceOperators.Infer` (#436): it chooses a route per frame, the device stage for a frame it
   reads and `ImageToTensor` for a frame in system memory, and refuses a GPU frame with neither. The
   decoder chooses per frame — `ReceiveFrame` branches on `YieldHardwareFrames && onHardware`, and
   `TrackHardwareEngagement` documents that FFmpeg re-runs `get_format` on a coded-format or
   dimension change and can decide differently. So the operator's own input type varies within a
   run. `VideoFormatInfo` is `(Width, Height, Format)` and carries no memory domain, so there is no
   event to react to: it is a per-frame type test, and the CPU branch is a correctness requirement
   rather than a fallback.

4. **Preprocessing happens where the pixels are.** (#291) Decided ([decision 1](adr.md)): one
   D3D12 compute shader, the device side of `ImageToTensor` and configured by the same
   `ImageToTensorOptions`, reads the D3D12VA texture after a GPU wait on the frame's fence and
   writes the model input into a D3D12 buffer. DirectML reads the buffer directly, through
   `DmlInferenceSession.OnDevice` and `IDeviceInputSession` ([decision 4](adr.md), #427); TensorRT-RTX
   reads it through a CUDA import. CPU frames keep the CPU `ImageToTensor`. Built in
   `FrameFlow.Inference.D3D12` as `D3D12ImageToTensor` (#425).

5. **A GPU frame's lifetime is bounded on a path with no pacer.** (#292) Each live
   `GpuVideoFrame` pins a slice of a default-sized hwframe pool. ADR-0057 records the resulting
   stall as confirmed, and the player's two answers — moving the wait into `ClockSelectVideoSink`,
   and the `_readbackEveryN` shedding rung the playback layer sets — are both unavailable to a
   `MediaPass`. An operator holding a frame across a model run is the same shape as the pacing hold
   that caused it. D3D12VA is the exception: its pool grows, so on that backend a held frame costs
   memory rather than a slice of a fixed pool (ADR-0081, 2026-09-27 revisions).

   Met as ADR-0081 answers it for the player: a pass that yields hardware frames computes its video
   path's budget before the decoder opens and sizes a fixed pool for it, and refuses at build a
   path with a holder that declares no bound. A pool that grows is guarded at the same budget, so
   a holder that keeps more than it declared parks the decoder rather than taking a surface for
   every frame (#416).

6. **A pass can ask for hardware frames.** (#277) Met: `IPassBuilder.WithHardwareFrames`. A pass on
   D3D12VA with `Infer` and a device stage on the decoder's `HardwareDevice` runs the model on the
   frames where they are: the sink receives GPU frames and every result takes the device route
   (`PassHardwareFramesTests`).

7. **Every claim above fails honestly on a machine without a GPU.** (#295) Met by #354:
   `RequiresHardwareDecodeFact` skips, naming what is missing, unless the probe initialised a
   backend for the codec under test. CI has no GPU on either leg, so skipping loudly there is the
   intended behaviour.

## Affected layers

| Layer | Change |
| --- | --- |
| `FrameFlow.Decoding` | `HardwareDevice`, which decoders borrow so the device outlives them ([borrowed device](../../adr/borrowed-hardware-device.md), #428); backend device accessors on `GpuVideoFrame`; whatever bounds in-flight leases; the `InternalsVisibleTo` grant to `FrameFlow.Video` finally has the consumer it was written for |
| `FrameFlow.Video` | The `ToCpu` node factory; `MapToGpu` later (#293) |
| `FrameFlow.Yolo` | The preprocessor splits by memory domain; `Yolov8Detector` stops assuming a CPU tensor |
| `FrameFlow.Inference.Cuda` | The `OrtValue` device-binding path gets its first caller |
| `FrameFlow.Inference.Abstractions` | `DeviceTensor` and `IDeviceInputSession` ([decision 4](adr.md)); `IImageModel`, `IDeviceImageToTensor`, `InferenceOperators.Infer` and `PresentedResults` (#436) |
| `FrameFlow.Inference.Dml` | `DmlInferenceSession.OnDevice` runs on the caller's device and binds a `DeviceTensor` in place (#427) |
| `FrameFlow.Player` | `WithHardwareDevice` on both builders (#428); `WithHardwareFrames` on `IPassBuilder`, and the test that asserts its absence |
| `FrameFlow.Graph` | Only if the in-flight bound for requirement 5 belongs at the edge rather than in the operator contract |
| Tests | The hardware gate, and the end-to-end assertion that nothing was downloaded |

## Open questions

- **Which backend pairing goes first.** Settled: D3D12VA decode, `D3D12ImageToTensor`, and DirectML
  on the decoder's device ([decisions 1](adr.md) and [4](adr.md)). The #420 spike measured it at
  about 3.0 ms per 1080p frame against 9.2 ms on the CPU path. CUDA decode into CUDA inference waits
  for a CUDA decode path ([decision 2](adr.md)).

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
