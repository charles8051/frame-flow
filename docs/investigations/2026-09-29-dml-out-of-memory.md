# What DirectML does when sessions outgrow the GPU

**Date:** 2026-09-29
**Issue:** #497. Read with the memory snapshot from #503.

## Question

#497 changes what `IInferenceSessionFactory` does when a provider runs out of memory opening a
model. That needs to know what the failure looks like. CUDA's is documented
(`CUDA failure 2: out of memory`); nothing in the repository recorded what DirectML raises, or
whether it raises anything.

## Method

A throwaway console harness, run on Windows 11 with a discrete NVIDIA GPU with 12 GB and 64 GB of
system memory, ONNX Runtime DirectML 1.24.4 and DirectML 1.15.4. It opened `DmlInferenceSession`s
one after another at the default optimization level, kept each open, and ran each once. Before and
after each open and after each run it read this process's budget and usage with
`GpuMemory.ReadAdapter` (#503). It stopped at the first exception, once non-local usage passed
4 GB, once local usage passed 1.5 times the local budget, or after 30 sessions, and disposed
everything.

Two synthetic models:

- **Intermediate:** input `[1, 1024]`, `Tile` to `[131072, 1024]` (512 MiB), `ReduceSum` back to
  `[1, 1024]`. Nothing is allocated for it at open; its first run allocates about 1 GiB of local
  memory, which DirectML keeps. (An `Expand` in place of `Tile` allocated nothing: DirectML
  broadcasts it without writing it out.)
- **Weights:** `MatMul` of an input `[1, 262144]` with a 1 GiB float initializer. Opening it adds
  1 GiB of local memory and 1 GiB of non-local memory, the second held until the session is
  disposed; I think that is DirectML's upload buffer for the weights. Splitting the initializer
  into 16 pieces did not change it.

The non-local upload buffers meant the weights model alone would reach the 4 GB non-local bound at
about 4 GB of local usage, well under the budget, so the second run filled local memory with the
intermediate model first.

## Results

**Run 1: intermediate sessions until the bound.** MiB, after each session's first run. The budget
fell from 11,318 to about 10,600 MiB once usage passed 7 GB.

| Session | Local used | Local budget | Non-local used | Open (ms) | First run (ms) | Later run (ms) |
| ------: | ---------: | -----------: | -------------: | --------: | -------------: | -------------: |
| 1 | 1,038 | 11,318 | 28 | 346 | 49 | 4 |
| 2–7 | 2,065 to 7,204 | 11,318 | 46 to 136 | 32 to 36 | 37 to 43 | 3 to 5 |
| 8 | 8,232 | 10,535 | 154 | 34 | 37 | 3 |
| 9 | 9,259 | 10,523 | 171 | 47 | 38 | 3 |
| 10 | 10,287 | 10,603 | 189 | 50 | 241 | 131 |
| 11 | 11,315 | 10,605 | 208 | 46 | 200 | 163 |
| 12 | 12,343 | 10,605 | 225 | 42 | 498 | 130 |
| 13 | 13,370 | 10,619 | 243 | 41 | 695 | 130 |
| 14 | 14,398 | 10,730 | 261 | 45 | 811 | 128 |
| 15 | 15,426 | 10,706 | 279 | 83 | 984 | 131 |
| 16 | 16,453 | 10,743 | 297 | 69 | 1,071 | 143 |

Stopped at session 16: local usage passed 1.5 times the budget. No exception. "Later run" is each
session run again, oldest first, once all 16 were open.

**Run 2: ten intermediate sessions, then weights sessions until the bound.** Sessions 1 to 10
matched run 1. MiB after each open.

| Session | Model | Local used | Local budget | Non-local used | Open (ms) | First run (ms) | Later run (ms) |
| ------: | ----- | ---------: | -----------: | -------------: | --------: | -------------: | -------------: |
| 10 | intermediate | 10,287 | 10,752 | 189 | 50 | 248 | 125 |
| 11 | weights | 11,315 | 10,727 | 1,229 | 979 | 48 | 44 |
| 12 | weights | 12,343 | 10,727 | 2,271 | 1,344 | 47 | 44 |
| 13 | weights | 13,372 | 10,755 | 3,313 | 1,298 | 49 | 44 |
| 14 | weights | 14,401 | 10,738 | 4,355 | 1,857 | 47 | 44 |

Stopped at session 14: non-local usage passed 4 GB. No exception. For comparison, two weights
sessions opened with nothing else on the GPU in 984 and 647 ms and ran in 2 to 3 ms after their
first run.

After every session was disposed, usage returned to 0 in both segment groups.

## What it shows

- **DirectML raised no out-of-memory**, at open or at run, with local usage at 1.5 times its budget
  and about 4 GiB more than the GPU has. Windows kept the allocations and moved some out of the
  GPU's memory.
- **The cost is time.** Sessions whose memory was allocated near or past the budget ran in 125 to
  163 ms against 3 to 5 ms for those before them, about 40 times as long. Weights opened past the
  budget ran in 44 ms against 2 to 3 ms for the same amount of weights on an otherwise empty GPU.
- **Non-local usage does not show it.** It stayed under 300 MiB in run 1. Local usage above the
  local budget is the sign, which is why the #503 docs point there.
- **The budget moves.** It dropped by about 800 MiB during run 1, at session 8.

## Failure texts for the #497 classifier

Observed on this machine, with the ONNX Runtime DirectML build:

| Failure | Raised as | Text |
| ------- | --------- | ---- |
| Device lost, opening a DirectML session | `OnnxRuntimeException` | `... dml_provider_factory.cc(596) ... 887A0005 The GPU device instance has been suspended. ...` |
| Device lost, the next session in the process | `OnnxRuntimeException` | `... dml_provider_factory.cc(524) ... 887A0006 The GPU will not respond to more commands, ...` |
| Device lost, a D3D12 call | `COMException`, HResult `0x887A0005` | `The GPU device instance has been suspended. ... (0x887A0005)` |
| No adapter at the index | `OnnxRuntimeException` | `... dml_provider_factory.cc(513) ... 887A0002 The object was not found. ...` |
| CUDA provider in the DirectML build | `EntryPointNotFoundException` | `Unable to find an entry point named 'OrtSessionOptionsAppendExecutionProvider_CUDA' ...` |
| Model file missing | `OnnxRuntimeException` | `[ErrorCode:NoSuchFile] Load model from ... failed ...` |
| Bytes that are not a model | `OnnxRuntimeException` | `[ErrorCode:InvalidProtobuf] Failed to load model because protobuf parsing failed.` |

The device-lost texts came from GPU resets on the same machine during test runs, not from the
harness.

Not observed, taken from ONNX Runtime's source and its reported failures: CUDA's
`CUDA failure 2: out of memory`, the arena's `Failed to allocate memory for requested buffer`,
cuBLAS and cuDNN's `..._STATUS_ALLOC_FAILED`, ONNX Runtime failing to load a provider's library
(`LoadLibrary failed with error 126`), and `E_OUTOFMEMORY` (`8007000E`) in DirectML's message
format. If DirectML does raise an allocation failure, it would carry `8007000E` in the same
position as the device-lost codes above; none was seen here.
