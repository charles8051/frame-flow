# ADR-0051: Model Acquisition Strategy

**Status:** Accepted — establishes the framework and ships the producer
tool (`scripts/mint_yolo.py`); per-consumer path selection is a choice
within it.
**Date:** 2026-05-28
**Related:**
- ADR-0046 (native runtime acquisition strategy) — the template: a
  deliberate, license-aware decision framework for *binaries we don't
  author*. This ADR is its analogue for *model weights*.
- ADR-0050 (model-shape-aware detection) — makes the detector accept any
  compatible model from any path, which is what lets acquisition be a
  separate, pluggable decision.
- `Yolov8ModelDownloader` — the current runtime-download implementation.

## Context

After ADR-0050 the detector is **model-agnostic**: it runs any YOLOv8/v11
transposed-head model at any 32-multiple input size with FP32 graph I/O,
auto-inferring the shape. So *which* model runs, and *where it comes
from*, are now consumer decisions the library shouldn't hardcode.

Today there is exactly one acquisition path: `Yolov8ModelDownloader`
fetches `yolov8n.onnx` at first run from Hugging Face
(`cabelo/yolov8`) into `%LOCALAPPDATA%\FrameFlow.Yolo\models\`. That is
fine for dev boxes and examples but wrong for two realities of the
target deployment:

1. **The kiosk is offline / locked-down.** A first-run network fetch is
   a deployment-time failure mode on a machine that may never have egress.
2. **Edge performance needs non-stock models.** Reduced-resolution
   (320/416), FP16, and (eventually) person-only models are what bring
   inference into budget on weak iGPUs — none of which the download path
   provides, and all of which a consumer must *produce*.

Two cross-cutting concerns shape the decision:

- **Licensing is first-class.** The stock weights — and everything
  derived from them (our FP16 conversions, our reduced-resolution
  re-exports) — are **ultralytics YOLOv8/YOLO11, AGPL-3.0**. ADR-0046
  set the precedent that we choose runtime *licensing* deliberately
  (LGPL FFmpeg builds, not GPL). The same discipline applies here: a
  library that *redistributes* AGPL weights pushes copyleft onto every
  consumer, which is a non-starter for a proprietary kiosk.
- **Minting is non-trivial.** Producing a correct model has footguns
  (ONNX opset 17 vs 20 breaking the FP16 pass, stale `value_info`,
  `keep_io_types` vs FP16-I/O, INT8 needing DP4a to matter). Consumers
  must not rediscover these.

## Decision

### 1. The library stays model-agnostic; acquisition is a consumer choice

`FrameFlow.Yolo` ships **no weights**. It ships the detector, the
shape/precision contract (`YoloModelDescriptor`), the optional
convenience downloader, and the producer tooling. How a given app gets
its model is one of the four supported paths below, chosen per
deployment.

### 2. Four supported acquisition paths

| Path | Offline | Library redistributes weights? | License exposure | Use when |
|---|---|---|---|---|
| **Runtime download** (`Yolov8ModelDownloader`) | ✗ | no | low (end machine fetches) | dev boxes, examples, networked hosts |
| **Pre-seeded cache** (deploy-time copy into the cache dir) | ✓ | no | low | **the kiosk** / any offline target |
| **Consumer-supplied asset** (the app bundles its chosen model and passes the path) | ✓ | no (it's the app's asset, not the library's) | the operator's call | apps that want a pinned, app-owned model |
| **Bundled frame-flow model package** (a `.Runtime`-style nupkg) | ✓ | **yes** | **high — permissive weights only** | only for weights we may redistribute (see §4) |

The detector accepts any path via `overrideModelPath` (ADR-0050), so all
four are drop-in; nothing in the runtime changes between them.

### 3. The producer path: `scripts/mint_yolo.py`

Minting is supported tooling, co-located with the detector, mirroring
ADR-0046's `scripts/fetch-*.cs`. `scripts/mint_yolo.py` encapsulates the
hard-won recipe (opset 17, `value_info` strip, `keep_io_types=True` for
FP32-I/O drop-ins, INT8 dynamic/static, the Gen-9 DP4a caveat):

- `export` — `.pt` → ONNX at a chosen input size (needs ultralytics);
- `convert` — existing ONNX → FP16 / INT8-dynamic / INT8-static (no torch
  for FP16);
- `validate` — load and report FrameFlow compatibility.

**Mint-time validation equals load-time validation.** `validate` applies
the same anchor formula and head checks as the detector's
`YoloModelDescriptor.FromSession` / `TryDescribe`. A model that passes
`mint_yolo.py validate` is the model the detector will accept at load —
no "compiles here, fails on the kiosk" gap. `TryDescribe` (the
non-throwing C# variant) exists so any C#-side build/CI step shares the
same verdict.

### 4. License rule for bundling

The library (and any frame-flow-published NuGet package) **must not
bundle or redistribute AGPL-derived weights** — i.e. anything produced
from ultralytics `.pt` weights, including our own FP16 / resized exports.
A bundled `FrameFlow.*.Models`-style package is permitted **only** for
weights under a permissive license we may redistribute — in practice, a
model we *train ourselves* (e.g. a person-only detector on a permissively
licensed dataset). Until such weights exist, path 4 is closed.

### 5. Recommendation for a kiosk deployment

**Pre-seed the cache** (path 2): roam copies the chosen model into
`%LOCALAPPDATA%\FrameFlow.Yolo\models\` at deploy time. Offline-safe, no
library redistribution, no new package, no AGPL exposure on the
*distribution* axis. (Running AGPL weights in a product still has its own
implications; that is an operator/legal call separate from this ADR.)

## Non-goals

- **Training pipelines / datasets** — out of scope; we support *loading*
  and *converting* models, not producing weights from data.
- **A model-hosting service / registry** — the four paths cover delivery;
  we are not building infrastructure.
- **NMS-free / seg / pose heads** — gated by ADR-0050 §5; a different
  decoder, a separate decision.
- **Automated license classification** — humans decide a model's license;
  the tooling just refuses to *silently* bundle.

## Consequences

- Acquisition is explicit and license-aware; the kiosk gets an offline
  path today without waiting on permissive weights.
- The minting footguns live in one maintained place that tracks the
  detector's contract, instead of in each consumer's tribal knowledge.
- A future permissively-licensed (likely person-only) model unlocks path
  4 cleanly — the framework is already in place.
- `Yolov8ModelDownloader`'s hardcoded single URL is now clearly the
  *dev/example* default, not the kiosk path; this should be documented at
  its call sites.

## Alternatives considered

1. **Bundle everything in the library** (simplest delivery). Rejected —
   redistributes AGPL weights, the exact copyleft trap a proprietary
   consumer can't absorb.
2. **Download-only** (status quo). Rejected as the *general* answer — an
   offline kiosk can't depend on first-run egress; kept as the dev/example
   default.
3. **A model server / registry the kiosk pulls from.** Over-built for the
   current need; the four delivery paths suffice, and a server is itself a
   thing to operate and secure.
4. **A .NET minting CLI** (mirroring roam). Rejected — the producing
   toolchain (PyTorch export, onnxconverter-common FP16, ORT static
   quantization/calibration) is Python-native with no .NET equivalent, so
   a .NET tool would only shell out to Python. The .NET ecosystem
   *consumes* ONNX (ORT runs it); it does not *produce/transform* it.
   Hence the producer tool is Python while the native-fetch tools
   (`fetch-*.cs`, which only download prebuilt binaries) stay C#.
