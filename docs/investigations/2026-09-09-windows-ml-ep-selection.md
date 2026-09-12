# Windows ML as an inference path alongside DirectML

**Date:** 2026-09-09 (measurements revised 2026-09-12 — see Secondary finding 1)
**Status:** Concluded — **build it, additively.** A `FrameFlow.Inference.WinML`
package, for the vendor execution providers Windows ML can reach. DirectML
stays; this is not a migration.
**Scope:** `src/FrameFlow.Inference.Dml`, `src/FrameFlow.Inference.Ort`,
`src/FrameFlow.Inference.Abstractions`
**Related:**
- ADR-0049 §3 (host-memory binding uniform across EP wrappers) — the layering
  this would slot into
- ADR-0050 (model-shape-aware detection), ADR-0051 (model acquisition) — the
  model side, unaffected
- [2026-08-14 DML in-process TDR recovery](2026-08-14-dml-in-process-tdr-recovery.md)
  — the other open question about the DirectML EP

## Question

Microsoft moved DirectML to sustained engineering and named Windows ML as the
successor for Windows ONNX Runtime deployments. Two things follow, and only one
of them is obvious:

1. Does DirectML stop working? (No.)
2. Does Windows ML give FrameFlow something DirectML cannot? (This document.)

The pressure is real but indirect. `FrameFlow.Inference.Dml` references
`Microsoft.ML.OnnxRuntime.DirectML` as `Version="1.*"` — a floating reference,
which restores today to **1.24.4**. That is not a stale pin we forgot to bump;
it is the newest build of that package. Mainline `Microsoft.ML.OnnxRuntime.Managed`
is at 1.29.0, and the runtime Windows ML ships is 1.27.1. A floating reference
that cannot float is what sustained engineering looks like from inside a csproj.

## Verdict

**Windows ML is worth an additive EP package, because it reaches vendor
execution providers DirectML cannot. Roughly 1.9× on this model and hardware.**

yolov8n (12 MB, 1×3×640×640, FP32 I/O) on a discrete NVIDIA GPU, on a Windows 11
build new enough for the EP catalog. 50 timed iterations after warmup, **one
process per configuration**, with the TensorRT engine cache already populated
(see Secondary finding 1 — that qualifier is not optional):

| configuration | open ms | warmup ms | median ms | p95 ms |
|---|---:|---:|---:|---:|
| `NvTensorRTRTX`, appended explicitly by device | 3731 | 91.8 | **1.73** | 2.31 |
| `SetEpSelectionPolicy(MAX_PERFORMANCE)` | 3558 | 97.6 | **1.72** | 2.30 |
| `SetEpSelectionPolicy(PREFER_GPU)` | 3620 | 90.7 | **1.72** | 2.33 |
| DirectML, `DmlInferenceSession` recipe | 291 | 9.4 | 3.28 | 3.74 |
| DirectML, ORT defaults | 307 | 8.7 | 3.38 | 4.03 |
| CPU | 45 | 44.4 | 28.52 | 30.99 |

The gain is attributable to the vendor EP rather than to Windows ML generally:
re-run either policy with the vendor EP left unregistered and it lands on
DirectML's number, because DirectML is then the only GPU EP present.

| configuration | median ms | p95 ms |
|---|---:|---:|
| `PREFER_GPU`, `--no-register` | 3.35 | 3.78 |
| `MAX_PERFORMANCE`, `--no-register` | 3.29 | 3.86 |

## Secondary findings

### 1. Retracted: "hand-picking the EP device is slower than the policy"

The first version of this document led with a finding that is **wrong**, and the
way it was wrong is worth more than the finding was.

It reported `AppendExecutionProvider(env, [nvTensorRtRtxDevice], {})` at
**8.60 ms**, 2.6× slower than DirectML, against 1.72 ms for the selection
policy — and concluded that EP choice must be delegated to Windows, which in
turn implied `IInferenceSessionFactory`'s preferred-EP-plus-fallback-chain model
was the wrong shape for this platform. That was a large design claim resting on
one number.

The number did not reproduce. Re-measured, alternating the two configurations
across separate processes:

| run | explicit device | selection policy |
|---|---:|---:|
| 1 | 2.19 | 1.91 |
| 2 | 1.72 | 1.93 |
| 3 | 1.73 | 1.72 |

They are the same within run-to-run spread. The original 8.60 ms was taken on a
machine that had never compiled a TensorRT engine for this model: the cost of
that first compile is not confined to session open, and it did not recur once
the on-disk engine cache existed.

**So selection route does not change steady-state throughput, and no design
conclusion follows from it.** How a `FrameFlow.Inference.WinML` session should
choose an EP is still open — `SetEpSelectionPolicy` is less code and tracks
whatever Windows learns about new silicon, while explicit device selection is
predictable and matches the existing factory — but it is now a design
preference, not something the measurements decide.

### 2. `FrameFlow.Inference.WinML` can inherit `OrtInferenceSessionBase` unchanged

`Microsoft.Windows.AI.MachineLearning` ships its own managed
`Microsoft.ML.OnnxRuntime.dll` — same simple name and same public key token
(`f27f157f0a5b7bb6`) as the `Microsoft.ML.OnnxRuntime.Managed` package that
`FrameFlow.Inference.Ort` references. That is the shape of an assembly conflict,
and whether a WinML EP wrapper could reuse the existing staging body turns on it.

`spikes/WinMlProbe` references `FrameFlow.Inference.Ort` for exactly this reason,
and step 1 exercises the collision rather than asserting it is benign: it opens
yolov8n through FrameFlow's own `CpuInferenceSession` — driving the base class's
session construction and name/shape reflection — against whichever runtime won
unification. It builds with 0 warnings and no `MSB3277`, emits one
`Microsoft.ML.OnnxRuntime.dll`, and reports:

```
--- 1  Windows ML runtime + FrameFlow.Inference.Ort coexistence ---
  PASS  OrtEnv.Instance()  ORT 1.27.1
  Microsoft.ML.OnnxRuntime resolved from ...\win-x64\Microsoft.ML.OnnxRuntime.dll
  PASS  FrameFlow FfCpuSession opened the model: 1 input(s), 1 output(s), input shape [1,3,640,640]
```

So the ~515-line host→ORT staging body carries over with no changes, and a WinML
EP wrapper is the same shape as `DmlInferenceSession`: session-options
configuration and nothing else. ADR-0049 §3's layering holds without amendment.
Because the probe is pinned to one WinML package version, a future version that
breaks this stops the spike building — or fails step 1 — instead of the breakage
surfacing inside a new EP package. The check exits non-zero when FrameFlow's
wrapper fails against the runtime, so it is usable from automation; a missing
model is a skip rather than a failure.

One naming note for that package: `FrameFlow.Inference.ExecutionProvider` (the
EP enum) and `Microsoft.Windows.AI.MachineLearning.ExecutionProvider` (a catalog
entry) collide, so any file touching both needs aliases.

### 3. The `DmlInferenceSession` session-options overrides buy nothing at 1.27.1

`DmlInferenceSession.BuildSessionOptions()` sets
`GraphOptimizationLevel.ORT_ENABLE_BASIC` and `EnableMemoryPattern = false`, with
comments asserting the DirectML EP requires both. Measured against ORT defaults,
in isolated processes:

| configuration | median ms | p95 ms |
|---|---:|---:|
| DirectML, ORT defaults | 3.38 | 4.03 |
| DirectML, `ORT_ENABLE_BASIC` only | 3.45 | 4.01 |
| DirectML, `EnableMemoryPattern = false` only | 3.41 | 4.11 |
| DirectML, both (shipping recipe) | 3.28 | 3.74 |

All four are inside each other's spread. Whatever the overrides did when they
were written, at ORT 1.27.1 on this adapter they cost nothing and gain nothing.

This is **not** a recommendation to delete them. The claim they encode is about
correctness on some adapter, not throughput on this one, and a single machine
cannot retire a compatibility workaround. It is recorded so the next person to
read those comments knows the performance half of the claim has been measured
and came back flat.

### 4. Measurement hazard: three ways this benchmark lied

Every wrong number in this investigation came from treating one measurement as
independent when it was not. In order of how much they cost:

1. **The on-disk TensorRT engine cache**, across *machine* lifetime. This
   produced the retracted finding in Secondary finding 1 and survived every
   in-process precaution, because the state lives outside the process. The first
   run on a machine that has never compiled engines for a given model is not a
   steady-state measurement of anything. Discard it and re-run.
2. **Process-wide warm-up.** Whichever configuration went first absorbed GPU
   clock ramp, driver shader cache and ORT native init. The `DmlInferenceSession`
   recipe measured **14.62 ms median / 51.75 ms p95** as row one and **3.41 /
   4.81** — same code, same model — once anything had run before it.
3. **In-process engine reuse.** The first TensorRT session in a process builds
   engines that later sessions in that process reuse, so an early explicit row
   looked slow next to late policy rows.

The probe addresses 2 by priming on DirectML before the table, and 3 via `--only`
so each configuration owns a process. It cannot address 1, which is machine
state rather than program state — that one is a rule for the operator, not code.
**Every number in this document comes from a `--only` run on a warm engine
cache.**

## Cost/benefit

**What it costs.**

- A Windows App SDK dependency (`Microsoft.Windows.AI.MachineLearning`,
  ~41 MB self-contained) and a `net10.0-windows10.0.18362.0` TFM, in a library
  whose core is deliberately cross-platform. This is containable the same way
  CUDA and DirectML already are — one EP package, chosen at the executable
  boundary, never referenced by `FrameFlow.Inference.Abstractions`.
- First-open cost goes from ~0.3 s to ~3.5-5 s, which is TensorRT engine
  preparation, and it is paid per process even with a warm on-disk cache. ORT's
  compile API (`OrtModelCompilationOptions`) is designed to cut this and is
  **untested here**. An app that opens a session per run, rather than once per
  process, would feel this before it felt the 1.9×.

**What it does not cost.** Not a migration. Windows ML ships the DirectML EP
in-box, labelled legacy, so `FrameFlow.Inference.Dml` keeps working and stays
the right answer wherever the catalog is unavailable.

**Where it buys nothing at all.** The vendor EP catalog requires Windows 11
24H2 (build 26100) or greater. Below that floor Windows ML offers CPU and
DirectML — precisely what `FrameFlow.Inference.Dml` already provides — in
exchange for a new dependency. That is a property of the platform, not something
a package design can route around, so a consumer below the floor should keep
using the DirectML package directly.

## What to do

1. **Do not migrate `FrameFlow.Inference.Dml`.** It is correct, it is the only
   GPU path below the 24H2 floor, and it is what Windows ML falls back to anyway.
2. **Decide the WinML session's EP-selection shape on design grounds**, not on
   these numbers — Secondary finding 1 retracted the measurement that appeared to
   settle it. Both routes reach the same steady state.
3. **Measure engine-compilation caching before committing to the 1.9×.** A
   multi-second open that cannot be amortised changes which deployments benefit.
4. **Re-run the comparison on a second GPU vendor** before generalising. Every
   number here is one NVIDIA adapter; the OpenVINO and QNN paths are unmeasured,
   and OpenVINO's floor (12th-gen Core for GPU) excludes older Intel integrated
   graphics entirely.
5. **Leave the `DmlInferenceSession` overrides alone**, with Secondary finding 3
   as the record that their performance justification is now known to be flat.

## Reproducing / re-checking this

`spikes/WinMlProbe` is a throwaway console app, deliberately **not** in
`FrameFlow.slnx` and not shipped. It runs six steps: runtime load and coexistence,
EP devices before registration, catalog inventory, registration, EP devices after,
and the benchmark table.

The default run is read-only with respect to the machine — it calls
`RegisterCertifiedAsync()`, which registers what is already installed and
downloads nothing:

```bash
dotnet run --project spikes/WinMlProbe
```

Baseline before any EP was acquired:

```
--- 2  EP devices before registration ---
  CPUExecutionProvider             CPU  Microsoft (device 0)
  DmlExecutionProvider             GPU  Microsoft (device 8712)

--- 3  EP catalog inventory ---
  NvTensorRTRTXExecutionProvider   NotPresent   Certified
  WebGpuExecutionProvider          NotPresent   Uncertified
```

`--acquire` switches to `EnsureAndRegisterCertifiedAsync()`, which **downloads
and installs** every compatible EP system-wide from Windows Update. It is opt-in
for that reason; run the default first, because step 3 tells you what `--acquire`
would fetch before you let it fetch anything. Acquiring `NvTensorRTRTX` took
tens of seconds; that figure is network-bound and will differ. Neither
registration API is transactional, so a failure can leave providers installed —
the probe stops and returns non-zero rather than benchmarking the partial state.

```bash
dotnet run --project spikes/WinMlProbe -- --acquire
```

For any number you intend to quote, one process per configuration, and **discard
the first TensorRT run on a machine that has not compiled engines for this model
before**:

```powershell
foreach ($c in 'recipe','defaults','CPU','NvTensorRTRTX','MAX_PERFORMANCE','PREFER_GPU') {
    dotnet run --project spikes/WinMlProbe -- --runs 50 --only $c
}
```

And to attribute a policy result to the vendor EP rather than to DirectML, run
the same policy with `--no-register` and compare:

```bash
dotnet run --project spikes/WinMlProbe -- --runs 50 --no-register --only PREFER_GPU
```

The probe reads its model from ADR-0051's pre-seeded cache path
(`%LOCALAPPDATA%\FrameFlow.Yolo\models\yolov8n.onnx`) and takes `--model` to
point elsewhere. It infers the input shape from the loaded model the way
`YoloModelDescriptor.FromSession` does (ADR-0050 §2), so a minted 320 or 416
model benchmarks without a code change.
