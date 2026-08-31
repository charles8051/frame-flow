# ADR-0050: Model-Shape- and Precision-Aware YOLO Detection

**Status:** Accepted — §1 (descriptor), §2 (auto-inference), §4 (class
allow-list / person-only decode), and §5 (head validation) implemented
2026-05-28. §3 Tier B (true FP16-I/O tensors) is the remaining opt-in
follow-up; §3 Tier A (FP32-I/O FP16-internal + INT8-dynamic) works on the
existing path today.
**Date:** 2026-05-28
**Related:**
- ADR-0049 (FrameFlow.Graph fork) — §3 created the EP-agnostic
  `IInferenceSession` seam and §5 consolidated detection into
  `FrameFlow.Yolo`; this ADR extends that detector without touching the
  EP seam.
- ADR-0046 (native runtime acquisition) — the DirectML EP this work is
  measured against ships per that strategy.
- ADR-0022 (deferred D3D12 resource binding) — the host-staging tensor
  path that the dtype work in §3 plugs into.
- The downstream kiosk inference pipeline whose Intel HD 620 target
  motivates this ADR.

## Context

`FrameFlow.Yolo` is the EP-agnostic YOLOv8 detector (ADR-0049 §5). It
runs any `IInferenceSession` — CUDA, DirectML, future EPs — but the
**model contract around the session is hardcoded to one shape**: the
stock `yolov8n` export at FP32, 640×640 input, 80-class COCO output.

Concretely, three hardcodes:

1. `Yolov8Preprocessor.InputSize = 640` (const), emitting a FP32
   `[1,3,640,640]` CHW tensor into a `Span<float>`.
2. `Yolov8Postprocessor` expects exactly `[1, 84, 8400]` — `84 = 4 +
   CocoClasses.Count (80)` and `AnchorCount = 8400` (both const) — and
   its decode loop scans all 80 class columns per anchor.
3. The EP sessions bind `CpuTensor<float>` only
   (`DmlInferenceSession.BindCpuTensor` → `MapDType` is dtype-general,
   but the detector only ever allocates FP32 input/output tensors).

This locks out exactly the models that make edge inference viable.

### Evidence motivating the change

The kiosk's Intel HD 620 (Gen 9, 24 EU, ~Oct-2020 driver) runs the
stock model at roughly **140 ms / inference** — near the silicon
ceiling for FP32 YOLOv8n at 640². Per-stage instrumentation added to
`Yolov8Detector.Detect` (commit on this branch:
`feat(yolo): per-stage timing in Detect`) splits the cost into
preprocess (CPU) / inference (EP device) / postprocess (CPU, the
80-class decode + NMS), so we can target the right stage.

A local frame-flow bench (RTX 3080 Ti, DML EP, p50) over the real
`DmlInferenceSession` + `Yolov8Detector` path:

| model | size | inference (run) | total |
|---|---|---|---|
| yolov8n FP32 640 | 12.2 MB | 3.04 ms | 5.58 ms |
| yolov8n FP16 640 (`keep_io_types=True`) | 6.2 MB | 2.37 ms | 4.68 ms |

FP16 is **already drop-in** (it keeps FP32 graph I/O, so the existing
`Span<float>` path feeds it unchanged) and ~20 % faster on the run
stage, with half the weights. On Gen 9 the win should be larger: FP16
has a 2× hardware rate and the iGPU is memory-bandwidth-bound, which
the halved weights directly relieve. INT8 dynamic-quant also keeps
FP32 I/O (drop-in to run) but Gen 9 has **no DP4a INT8 fast-path**, so
acceleration is hardware-dependent. Lower input resolution (320/416)
and a person-only output head both require the postprocessor to stop
assuming `8400` anchors and `80` classes.

So the cheap wins (FP16-internal, INT8-run) are blocked only by the
*hardcoded shape assumptions*, and the structural wins (smaller input,
person-only) need the detector to learn the model's actual shape.

## Decision

Make `FrameFlow.Yolo` **model-shape- and precision-aware**, driven by
the loaded model rather than compile-time constants. The
`IInferenceSession` EP seam (ADR-0049 §3) does not change.

### 1. A `YoloModelDescriptor` carries shape + precision

Introduce a descriptor:

```
record YoloModelDescriptor(
    int InputSize,            // square side, multiple of 32
    int ClassCount,           // detector classes (COCO = 80)
    IReadOnlyList<string> ClassNames,
    DType IoDtype = DType.Float32);   // graph input/output element type
```

`Yolov8Preprocessor` and `Yolov8Postprocessor` are parameterized by it
instead of by consts. Anchor count is **derived**, not stored:

```
anchors(S) = (S/8)² + (S/16)² + (S/32)²
// 640 → 8400 ; 512 → 5376 ; 416 → 3549 ; 320 → 2100
```

matching YOLOv8/v11's P3–P5 stride structure. `InputSize` must be a
multiple of 32; reject otherwise with a clear error.

### 2. Auto-infer the descriptor from the session; allow override

Extend `IInferenceSession` to expose input/output **shapes** (it
already exposes `InputNames`/`OutputNames`). `Yolov8Detector.CreateAsync`
reads:

- input `[1, 3, H, W]` → `InputSize` (and `IoDtype` from the input
  element type),
- output `[1, 4 + C, A]` → `ClassCount = C`, and asserts `A ==
  anchors(InputSize)`.

When axes are static this needs **zero caller configuration** — point
the detector at a 320² or person-only ONNX and it self-configures. A
caller-supplied `YoloModelDescriptor` overrides inference for models
with dynamic axes or non-standard class-name maps. Existing callers
that pass nothing get today's behavior (640 / 80-COCO) by default.

### 3. Reduced precision in two tiers

- **Tier A — FP32-I/O models (drop-in, ship now).** FP16-internal
  (`keep_io_types=True`) and INT8 dynamic/QDQ exports that keep FP32
  graph I/O run on the *current* tensor path unchanged. We bless this
  as the recommended quantization route and document that **EP
  acceleration is hardware-dependent** (FP16 → yes on Gen 9; INT8 →
  no DP4a on Gen 9, so expect parity-or-worse there, real gains on
  NPUs / Gen 12+ / DP4a GPUs).
- **Tier B — true FP16-I/O models (opt-in, dtype-aware tensors).**
  Add `CpuTensor<Half>` allocation in the detector and `Half` binding
  in the EP sessions (`MapDType` already maps `DType.Float16 →
  TensorElementType.Float16`; the gap is the detector only ever
  *allocates* FP32 tensors and the preprocessor only *writes* `float`).
  Gated on `descriptor.IoDtype == Float16`. Pursued only if Tier A
  FP16 proves insufficient, since it adds a `Half` write path to the
  preprocessor hot loop.

### 4. Variable class count → person-only

The postprocessor's per-anchor class loop is bounded by
`descriptor.ClassCount`. Two person-only routes, both supported:

- **1-class model** (output `[1, 5, A]`): decode collapses the
  `A × 80` argmax to `A × 1`. This is the structural win — a smaller
  output head means less data off the EP and a cheaper decode, which
  matters most if the kiosk proves postprocess-bound.
- **Class allow-list on the 80-class model** (no new model): the
  decoder skips non-allowed columns. Captures most of the decode
  saving without retraining; lets us ship a "person-only" behavior
  immediately and swap in a true 1-class model later.

### 5. Validate the head; fail loudly on incompatible models

Auto-inference asserts the transposed YOLOv8/v11 head `[1, 4+C, A]`
with `A == anchors(InputSize)`. Models that don't match (e.g. a
yolov10 NMS-free `[1, 300, 6]` head, or a segmentation/pose head) are
**rejected with a descriptive error**, never silently mis-decoded.

## Non-goals

- **NMS-free / decoupled heads** (yolov10 `[1,300,6]`, RT-DETR) —
  different decoder; a separate ADR if we adopt one.
- **Training or fine-tuning** person-only / domain models — out of
  scope. We support *loading* them; producing them is an offline
  concern.
- **Static INT8 calibration tooling** — dynamic quant only for now;
  calibration-set plumbing is future work.
- **Segmentation / pose / OBB heads** — out of scope.

## Consequences

- Unlocks the kiosk optimization path (smaller input, FP16, person-only)
  **without forking `Yolov8Detector`** — one detector, descriptor-driven.
- The anchor-count derivation is small and unit-testable against known
  shapes (640→8400, 512→5376, 416→3549, 320→2100); a wrong derivation
  fails fast in auto-inference rather than mis-decoding.
- `IInferenceSession` grows shape accessors (additive). Both EP
  implementations (`DmlInferenceSession`, `CudaInferenceSession`)
  implement them; ORT already exposes the metadata.
- `Yolov8Detector.CreateAsync` gains an optional `YoloModelDescriptor`
  parameter (additive; default preserves current behavior).
- Tier B adds a `Half` path to the preprocessor and EP tensor binding —
  additive and opt-in; no cost to Tier A users.
- Risk: variants beyond YOLOv8/v11 may share the `[1,4+C,A]` head but
  differ in box encoding or normalization; §5's validation reduces but
  does not eliminate the need to verify each new model family on real
  output before trusting detections.

## Alternatives considered

1. **Separate detector classes per shape**
   (`Yolov8Detector640`, `…320`, person-only…). Rejected — combinatorial
   duplication across {input size × class count × dtype}.
2. **Caller-supplied descriptor only, no auto-inference.** Workable but
   worse ergonomics; the session already knows its static shapes, so
   inferring them is strictly better. Chosen hybrid: auto-infer with
   override.
3. **Permanently require FP32-I/O models (forbid true FP16 I/O).**
   Simplest — Tier A alone covers FP16-internal + INT8 — but forecloses
   NPUs/EPs that want end-to-end FP16. Adopted as the *default* (Tier A)
   with Tier B as an opt-in escape hatch rather than a hard ceiling.
4. **Move preprocessing/quantization into a graph operator** rather than
   the detector. Heavier and splits the model contract across two
   places; the detector-local descriptor keeps shape knowledge in one
   spot and matches the current structure.
