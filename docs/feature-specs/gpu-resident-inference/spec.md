# GPU-resident inference

**Status:** Draft. Nothing implemented. Tracked by #288; every requirement below names the issue
that carries it. Living document, rewritten as the feature changes.

**Date:** 2026-09-18

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
DirectML EP, 1080p H.264, four runs of ~1200 frames, p50 per frame:

| leg | p50 | where |
| --- | --- | --- |
| `av_hwframe_transfer_data` downloads NV12 | 3.6 ms | `VideoDecoder.BuildManagedFrame` |
| `sws_scale` converts NV12 to BGRA | 2.2 ms | `VideoDecoder.BuildManagedFrameFromCpu` |
| resize, normalize, HWC to CHW | 5.3 ms | `Yolov8Preprocessor.Preprocess`, scalar, no SIMD |
| the model | 4.7 ms | the EP, including its host-to-device staging |
| 80-class decode and NMS | 3.1 ms | `Yolov8Postprocessor` |

11.3 ms of the ~18.5 ms is spent moving and reshaping pixels the GPU already had. The model is
4.7 ms.

**The largest leg is the preprocessor, not the download.** That is the finding that shapes this
feature. A GPU path that only skips the readback recovers 3.6 ms and leaves 5.3 ms untouched, so
"stop downloading the frame" is necessary and nowhere near sufficient. Instrumentation is
`DecodeStageMetrics` and the Multicast.Dml example's `--exit-after` (#282); the measurement
carries a caveat recorded under *Open questions*.

The end state: the decoder yields in the domain it decoded in, an operator preprocesses where the
pixels already are, the session binds a device pointer, and the only thing crossing the bus is the
output tensor.

## Requirements

1. **A GPU-yielded frame can reach a CPU operator.** (#279) Today it cannot: all three
   `FrameFlow.Video` operators route through `SwScaleVideoConverter.Process`, which calls
   `source.ToCpu()`, which throws on a `GpuVideoFrame`. The exception names a `pipeline.ToCpu()`
   operator that ADR-0038 §4 specifies and the tree does not contain. Until a `ToCpu` node exists,
   turning the decoder flag on makes a graph unrunnable rather than faster, so this is the first
   requirement and not an optimisation.

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
   kernel, work folded into the model graph, or something else is open; that it must move is not,
   because it is the largest leg.

5. **A GPU frame's lifetime is bounded on a path with no pacer.** (#292) Each live
   `GpuVideoFrame` pins a slice of a default-sized hwframe pool. ADR-0057 records the resulting
   stall as confirmed, and the player's two answers — moving the wait into `ClockSelectVideoSink`,
   and the `_readbackEveryN` shedding rung the playback layer sets — are both unavailable to a
   `MediaPass`. An operator holding a frame across a model run is the same shape as the pacing hold
   that caused it.

6. **A pass can ask for hardware frames.** (#277) `IPassBuilder.WithHardwareFrames`, one option
   and one assignment in `PassBuilder.BuildAsync`. It lands with requirement 3, not before: a flag
   whose only reachable consumer is a CPU operator is a flag that breaks the run.
   `PassBuilderTests.ThePassBuilderHasNoTransportOptions` currently asserts the option is absent
   and gives a reason, so this reverses a recorded decision rather than adding to one.

7. **Every claim above fails honestly on a machine without a GPU.** (#295) There is no
   hardware-conditional test gate. `HardwareDecodeIntegrationTests` asserts only that playback
   completed, which passes identically when the software path served every frame. Nothing here is
   verifiable until a gate exists, and CI has no GPU on either leg, so skipping loudly is the
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
  D3D12 resource binding as well. Requirement 2 is written for the CUDA side because that is the
  side with a consumer. Whether the DML side is in scope is the first thing to settle, because it
  decides whether the numbers above describe a path this feature can deliver on that hardware.

- **Whether the output tensor is worth moving too.** The model's output is 84 × 8400 floats,
  about 2.8 MB per frame, against 4.9 MB for the input. Postprocess is 3.1 ms of CPU work on it.
  If the input upload goes away and the output download does not, the round trip is halved rather
  than removed.

- **What the CPU-only path costs after requirement 4.** Every consumer shares one
  `Yolov8Preprocessor`. If preprocessing splits by memory domain, the CPU branch has to stay at
  least as fast as it is now, and vectorising that loop is worth doing on its own merits whatever
  this feature decides.

- **How much of the 5.3 ms is the example's own overhead.** The Multicast.Dml example clones each
  frame to three panes with `CloneCpu()`, so the CPU legs compete with two extra full-frame copies.
  The decoder-thread legs are less affected. A single-consumer pass would show lower preprocess,
  sws and postprocess figures, and re-measuring on one is the cheap way to tighten the estimate
  before committing to requirement 4's shape.

- **Where the inference ADR citations point.** `DmlInferenceSession` attributes its deferral to
  "ADR-0022", and `CudaInferenceSession` and ADR-0050 cite "ADR-0049 §3" for the `IInferenceSession`
  EP seam. In this repository ADR-0022 is long-lived workers with a pause gate and ADR-0049 is the
  graph fork. The numbers appear to predate that fork, so the rationale for the DML deferral is not
  reachable from the tree, and the first open question above cannot be answered from the record
  alone.
