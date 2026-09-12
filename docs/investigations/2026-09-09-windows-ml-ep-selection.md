# Windows ML as an inference path alongside DirectML

**Date:** 2026-09-09
**Status:** Concluded — **build it, additively.** A `FrameFlow.Inference.WinML`
package whose value is Windows ML's *EP selection policy*, not its EP list.
DirectML stays; this is not a migration.
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

**Windows ML is worth an additive EP package, and the reason is the selection
policy rather than any single execution provider.**

yolov8n (12 MB, 1×3×640×640, FP32 I/O) on a discrete NVIDIA GPU, on a Windows 11
build new enough for the EP catalog. 50 timed iterations after warmup, **one
process per configuration** (see Secondary finding 4 for why that qualifier is
not optional):

| configuration | open ms | warmup ms | median ms | p95 ms |
|---|---:|---:|---:|---:|
| `SetEpSelectionPolicy(PREFER_GPU)` | 3896 | 80.7 | **1.72** | 2.77 |
| `SetEpSelectionPolicy(MAX_PERFORMANCE)` | 4813 | 82.5 | **1.72** | 1.93 |
| DirectML, ORT defaults | 302 | 8.2 | 3.17 | 3.32 |
| DirectML, `DmlInferenceSession` recipe | 354 | 8.9 | 3.30 | 3.58 |
| `NvTensorRTRTX`, appended explicitly by device | 7007 | 172.9 | 8.60 | 9.33 |
| CPU | 43 | 29.8 | 26.55 | 30.34 |

The policy is **1.9× DirectML**. The win is attributable rather than assumed:
re-run the same policy configuration with the vendor EP left unregistered and it
lands on 3.20 ms / 3.19 ms — DirectML's number, to two significant figures,
because DirectML is then the only GPU EP present.

| configuration | median ms | p95 ms |
|---|---:|---:|
| `PREFER_GPU`, `--no-register` | 3.20 | 3.42 |
| `MAX_PERFORMANCE`, `--no-register` | 3.19 | 4.54 |

## Secondary findings

### 1. Hand-picking the EP device is worse than doing nothing

The row that matters most is the one that looks like a mistake:
`AppendExecutionProvider(env, [nvTensorRtRtxDevice], {})` measures **8.60 ms**,
2.6× *slower* than DirectML, while the selection policy reaching the same EP on
the same adapter measures 1.72 ms. Both were measured in isolated processes, so
this is not ordering.

The two paths are not "the same thing with different syntax". Asking for a
device by hand yields a session that performs worse than the fallback it was
meant to beat, and nothing in the API shape warns about it.

**This is the design consequence, and it points away from how FrameFlow selects
EPs today.** `IInferenceSessionFactory` is built around a caller-declared
preferred EP plus an ordered fallback chain — the caller names DirectML or CUDA
and the factory probes down the list. That model is exactly the hand-picking
that loses here. A WinML session should hand EP choice to Windows and report
back what was chosen, which is a different contract, not a new entry in the
`ExecutionProvider` enum.

Untested, and the first thing to check before designing that contract: whether
the policy's advantage survives models other than yolov8n, and whether a session
can report which EP a policy actually selected without turning on ORT profiling.

### 2. `FrameFlow.Inference.WinML` can inherit `OrtInferenceSessionBase` unchanged

`Microsoft.Windows.AI.MachineLearning` ships its own managed
`Microsoft.ML.OnnxRuntime.dll` — same simple name and same public key token
(`f27f157f0a5b7bb6`) as the `Microsoft.ML.OnnxRuntime.Managed` package that
`FrameFlow.Inference.Ort` references. That is the shape of an assembly conflict,
so it was tested rather than assumed: a `net10.0-windows10.0.18362.0` project
referencing both `FrameFlow.Inference.Ort` and `Microsoft.Windows.AI.MachineLearning`

- builds with **0 warnings** on a clean rebuild, no `MSB3277`,
- emits exactly one `Microsoft.ML.OnnxRuntime.dll`,
- resolves `OrtInferenceSessionBase` and `ExecutionProviderCatalog` together,
- and reports `OrtEnv.GetVersionString()` = 1.27.1 at runtime, i.e. the Windows
  ML copy won the unification.

So the ~515-line host→ORT staging body in `OrtInferenceSessionBase` carries over
with no changes, and a WinML EP wrapper is the same shape as
`DmlInferenceSession`: session-options configuration and nothing else. ADR-0049
§3's layering holds without amendment.

### 3. The `DmlInferenceSession` session-options overrides buy nothing at 1.27.1

`DmlInferenceSession.BuildSessionOptions()` sets
`GraphOptimizationLevel.ORT_ENABLE_BASIC` and `EnableMemoryPattern = false`, with
comments asserting the DirectML EP requires both. Measured against ORT defaults,
in isolated processes:

| configuration | median ms | p95 ms |
|---|---:|---:|
| DirectML, ORT defaults | 3.17 | 3.32 |
| DirectML, `ORT_ENABLE_BASIC` only | 3.21 | 4.01 |
| DirectML, `EnableMemoryPattern = false` only | 3.18 | 4.09 |
| DirectML, both (shipping recipe) | 3.30 | 3.58 |

All four are inside each other's spread. Whatever the overrides did when they
were written, at ORT 1.27.1 on this adapter they cost nothing and gain nothing.

This is **not** a recommendation to delete them. The claim they encode is about
correctness on some adapter, not throughput on this one, and a single machine
cannot retire a compatibility workaround. It is recorded so the next person to
read those comments knows the performance half of the claim has been measured
and came back flat.

### 4. Measurement hazard: sessions in one process are not independent

The first version of this comparison ran every configuration in a single process
and produced two false results, in opposite directions:

- Whichever configuration went first absorbed GPU clock ramp, driver shader
  cache, and ORT native init. The `DmlInferenceSession` recipe measured
  **14.62 ms median / 51.75 ms p95** as row one, and **3.41 / 4.81** — the same
  code, same model — once anything had run before it. Read naively, that was a
  4:1 win for Windows ML that did not exist.
- The first TensorRT-RTX session builds engines that later sessions in the same
  process reuse. So the explicit `NvTensorRTRTX` row (early) looked slow at
  6.62 ms while the policy rows (late) looked fast at 1.93 ms, inflating a real
  gap into a bigger one.

Both are fixed in the probe: it primes on DirectML before the table, and takes
`--only` so each configuration can own a process. **Every number in this
document comes from a `--only` run.** Anyone re-running this should do the same
before quoting a figure.

## Cost/benefit

**What it costs.**

- A Windows App SDK dependency (`Microsoft.Windows.AI.MachineLearning`,
  ~41 MB self-contained) and a `net10.0-windows10.0.18362.0` TFM, in a library
  whose core is deliberately cross-platform. This is containable the same way
  CUDA and DirectML already are — one EP package, chosen at the executable
  boundary, never referenced by `FrameFlow.Inference.Abstractions`.
- First-open cost goes from ~0.3 s to ~4 s, which is TensorRT engine build.
  ORT's compile API (`OrtModelCompilationOptions`) is designed to cache exactly
  this, and it is **untested here**. An app that opens a session per run, rather
  than once per process, would feel this before it felt the 1.9×.
- A second EP-selection model to maintain next to the existing fallback chain,
  per Secondary finding 1.

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
2. **Design the WinML session contract around policy, not preference**, per
   Secondary finding 1. It does not fit `ExecutionProvider` + fallback chain, and
   forcing it into that enum would encode the 8.60 ms path as the intended one.
3. **Measure engine-compilation caching before committing to the 1.9×.** A 4 s
   open that cannot be cached changes which deployments benefit.
4. **Re-run the comparison on a second GPU vendor** before generalising. Every
   number here is one NVIDIA adapter; the OpenVINO and QNN paths are unmeasured,
   and OpenVINO's floor (12th-gen Core for GPU) excludes older Intel integrated
   graphics entirely.
5. **Leave the `DmlInferenceSession` overrides alone**, with Secondary finding 3
   as the record that their performance justification is now known to be flat.

## Reproducing / re-checking this

`spikes/WinMlProbe` is a throwaway console app, deliberately **not** in
`FrameFlow.slnx` and not shipped. It runs six steps: runtime load, EP devices
before registration, catalog inventory, registration, EP devices after, and the
benchmark table.

The default run is read-only with respect to the machine — it calls
`RegisterCertifiedAsync()`, which registers what is already installed and
downloads nothing:

```bash
dotnet run --project spikes/WinMlProbe
```

Baseline before any EP was acquired:

```
--- 1  Windows ML runtime ---
  PASS  OrtEnv.Instance()  ORT 1.27.1

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
tens of seconds; that figure is network-bound and will differ.

```bash
dotnet run --project spikes/WinMlProbe -- --acquire
```

For any number you intend to quote, one process per configuration:

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
