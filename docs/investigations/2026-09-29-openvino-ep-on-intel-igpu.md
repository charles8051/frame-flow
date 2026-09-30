# ONNX Runtime's OpenVINO provider on an older Intel integrated GPU

**Date:** 2026-09-29
**Issues:** #10 (Tier B fp16 I/O), #520 (session element types). Follows
[2026-09-09-windows-ml-ep-selection.md](2026-09-09-windows-ml-ep-selection.md).

## Question

On an Intel integrated GPU, FrameFlow's GPU path is `DmlInferenceSession`. Windows ML reaches
Intel's OpenVINO provider only on Windows 11 24H2 and later. Below that floor, the only way to
OpenVINO is ONNX Runtime's standalone OpenVINO build. Does that build run on an older Intel iGPU,
and how does it compare with our DirectML session on the same machine? Separately, do models with
fp16 inputs and outputs buy anything there?

## Verdict

**It runs, and on the GPU it is about twice as fast as DirectML.** The whole graph goes to
OpenVINO, with no CPU fallback, through FrameFlow's own session base. The tests used an Intel
Gen9 integrated GPU with a 2-core, 4-thread CPU, on a Windows build below the 24H2 floor. Each
figure is the median p50 of three rounds, alternating the two builds:

| Model | DirectML | OpenVINO GPU | Speedup | CPU ms per run, DirectML → OpenVINO |
| --- | --- | --- | --- | --- |
| yolov8n 320, fp16 weights, fp32 I/O | 24.7 ms | 13.3 ms | 1.86× | 1.5 → 13.7 |
| yolov8n 320, fp32 | 29.7 ms | 14.1 ms | 2.10× | 1.6 → 13.3 |
| yolov8n 640, fp16 weights, fp32 I/O | 81.5 ms | 40.3 ms | 2.02× | 2.8 → 18.9 |
| yolov8n 640, fp32 | 113.5 ms | 41.3 ms | 2.75× | 2.8 → 22.6 |
| BlazeFace short range, fp32 | 5.2 ms | 2.9 ms | 1.8× | 0.9 → 3.5 |

The cost is CPU. Out of the box, OpenVINO holds about one CPU thread for the length of each
inference. With the GPU plugin's queue throttle at `LOW`, that drops to about a third (finding 4).
Models with fp16 I/O were no faster (finding 6).

## Method

`spikes/OpenVinoProbe` builds one source file twice. The `OpenVino/` build references
`Intel.ML.OnnxRuntime.OpenVino` 1.24.1 (ORT 1.24.1 on OpenVINO 2025.4.1). The `Dml/` build
references `FrameFlow.Inference.Dml` (ORT DirectML 1.24.4). One process can load only one
`onnxruntime.dll`, so the two cannot share a process. Every configuration runs through
`OrtInferenceSessionBase.Run` with preallocated host tensors. The DirectML rows use
`new DmlInferenceSession(path)`, as an app gets it. The OpenVINO rows are a small
`OrtInferenceSessionBase` subclass that appends the provider with `PERFORMANCE_HINT=LATENCY` and
`session.disable_cpu_ep_fallback=1`.

For each model and configuration, the probe:

- opens the session, disposes it, and opens it again
- times the first run
- runs 20 untimed warmup runs
- times up to 150 runs, or 300 in the follow-up, within a 12 s budget
- records process CPU time across the timed runs

The input is seeded noise. Each output is compared against ORT's CPU provider on the same input.
`rel` is the largest difference divided by the largest reference value. It says whether a
provider computes the same function, not how detections move.

Three rounds ran with the order of the two builds alternating. A follow-up ran three more rounds
with `GPU_QUEUE_THROTTLE` and an fp16-I/O model. **The machine was not idle.** Another
application used the GPU at about 25% 3D utilization and 20 to 70% of the CPU throughout. The
follow-up ran under heavier load than the first run, so compare within a run, not across the two.

## Findings

### 1. OpenVINO takes the whole graph on a Gen9 GPU

Every OpenVINO session opened with CPU fallback disabled, on all five models, on both the CPU and
GPU devices. The 12th-generation floor quoted in the Windows ML investigation does not apply to
this build. Whether it applies to the Windows ML catalog's OpenVINO provider was not measured.

### 2. Most of the speed is OpenVINO computing in fp16

The GPU plugin's default precision hint is f16, whatever the model file holds. An fp32 model
under the default hint ran as fast as its fp16-weight twin (14.1 ms against 13.3 ms). With the
hint forced to `f32`, the gain over DirectML shrinks to 1.2× to 1.7×:

| Model | DirectML | OpenVINO, default hint | OpenVINO, `f32` hint |
| --- | --- | --- | --- |
| yolov8n 320, fp32 | 29.7 ms | 14.1 ms | 21.4 ms |
| yolov8n 640, fp32 | 113.5 ms | 41.3 ms | 65.3 ms |

The default hint moves the output about as much as DirectML does on an fp16-weight model: `rel`
0.021 to 0.023 against DirectML's 0.016 at 320. Under the `f32` hint, `rel` is about 2e-6.

### 3. OpenVINO's tail is tighter

On the 320 model, p90 was 16.4 ms against DirectML's 33.7 ms, and p99 was 31 ms against 47 ms.

### 4. Out of the box it spends a CPU thread per inference; the queue throttle cuts that

Unthrottled, OpenVINO on the GPU used 13 to 23 ms of process CPU per inference. That is close to
its wall time. DirectML used 1 to 3 ms. With `GPU_QUEUE_THROTTLE=LOW` in `load_config`, the
follow-up measured this on the 320 model:

| | p50 | p90 | CPU ms per run |
| --- | --- | --- | --- |
| DirectML | 28.9 ms | 46.5 ms | 1.5 |
| OpenVINO GPU | 20.0 ms | 28.7 ms | 13.0 |
| OpenVINO GPU, throttle `LOW` | 15.4 ms | 19.8 ms | 5.1 |

The throttled session was not slower. I think the unthrottled cost is the host waiting on the GPU
queue, since the throttle hint is what changes it. I have not confirmed that in a profiler.

### 5. Opening: DirectML is slow on this GPU; OpenVINO needs its cache

DirectML's first open of a YOLO model took a median of 12 to 24 s per model, and one open took
95 s. Reopening the same model in the same process took 11 to 23 s. BlazeFace took about 1.4 s. OpenVINO's GPU device took 16 s cold per YOLO
model. With `CACHE_DIR` set, it took about 4 s to reopen.

### 6. fp16 I/O gains nothing measurable

An fp16-I/O copy of the 320 model was made by removing the fp16-weight model's two boundary
`Cast` nodes. Its output is within 0.25 of the original on values up to 325. The same session
path ran it with `Half` tensors:

| yolov8n 320 | fp32 I/O | fp16 I/O |
| --- | --- | --- |
| DirectML | 28.9 ms | 27.5 ms |
| OpenVINO GPU, throttle `LOW` | 15.4 ms | 15.5 ms |

The model's I/O type does not change OpenVINO's compute precision. That is set by the precision
hint (finding 2). On DirectML the difference is 1.4 ms. On this hardware that does not justify
Tier B (#10), which would add a `Half` path to both detectors. #520 stands on its own.

### 7. For a small model, the CPU wins

BlazeFace ran in 1.0 ms on OpenVINO's CPU device, against 5.2 ms on DirectML and 3.6 ms on ORT's
CPU provider. That is the first run's medians; the follow-up measured 2.6, 4.0 and 3.1 ms. At
this size, the GPU dispatch costs more than the model. For the YOLO models, OpenVINO's CPU device
was level with DirectML at best and had a wide tail.

## What to do

1. **A `FrameFlow.Inference.OpenVino` package is worth building** for Intel GPUs below the
   Windows ML floor. It would join the pick-exactly-one set beside `.Cpu`, `.Dml`, `.Cuda` and
   `.WinML`. The Intel package needs managed ORT 1.24.1 or later. In the probe,
   `FrameFlow.Inference.Ort`'s managed 1.24.4 ran against its 1.24.1 native. The findings give
   it these defaults:
   - a `CACHE_DIR`, since without one every open pays the cold compile
   - `GPU_QUEUE_THROTTLE=LOW` on the GPU
   - OpenVINO's default precision hint, with `f32` as an opt-out
2. **Leave Tier B (#10) unbuilt** unless other hardware shows a gain.
3. **Choose the provider per model, not per app.** On this machine the detector belongs on the
   GPU and the face model on the CPU.
4. **Still unmeasured:**
   - detection accuracy on real images under the f16 hint
   - several sessions sharing the GPU
   - power and thermals under sustained load

## Reproducing

```bash
dotnet publish spikes/OpenVinoProbe/OpenVino -c Release -o out/ov
dotnet publish spikes/OpenVinoProbe/Dml -c Release -o out/dml
out/dml/OpenVinoProbe.exe --runs 150 --budget 12 --csv results.csv --tag r1 yolov8n-320-fp16.onnx
out/ov/OpenVinoProbe.exe --runs 150 --budget 12 --csv results.csv --tag r1 yolov8n-320-fp16.onnx
```

These are the first run's limits; the follow-up used `--runs 300 --budget 12`. The probe's own
defaults (200 runs, 20 s) are not what the tables above used. Alternate the two builds over
several rounds. `--configs` picks configurations; the OpenVINO build
has `ort-cpu`, `ov-cpu`, `ov-gpu`, `ov-gpu-f32` and `ov-gpu-low`. The run holds the CPU and GPU
flat out for its length, so don't run it on a machine doing other work that matters.
