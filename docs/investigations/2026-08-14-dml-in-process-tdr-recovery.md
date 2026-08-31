# In-process DirectML recovery after a Windows GPU TDR

**Date:** 2026-08-14
**Status:** Concluded — **do not build it**. Process restart is the correct recovery.
**Scope:** `src/FrameFlow.Inference.Dml`, `src/FrameFlow.Inference.Abstractions`
**Related:** the downstream kiosk TDR reports, and the consumer-side `ReprobingInferenceSessionFactory` CPU fallback

## Question

On kiosks with an Intel HD 620 (Gen9 Kaby Lake), a single TDR
(LiveKernelEvent `0x141`, `igdkmd64.sys`, bucket `DXCOMPUTE`) poisons DirectML
EP registration for the lifetime of the process. Measured 2026-08-14 across two
production boxes: **54 re-open attempts, 0 successes**; a process restart
recovers 100% of the time on the same adapter with no driver change and no
reboot. GPU memory was 141 MB of a 7316 MB budget at failure, so this is not
exhaustion, and it reproduced identically on a 2020 driver (`27.20.100.8853`)
and a 2026 one (`31.0.101.2141`), so it is not driver-version-specific.

The failure is in EP registration, before any session object exists:

```
OnnxRuntimeException: [ErrorCode:RuntimeException]
  onnxruntime\core\providers\dml\dml_provider_factory.cc(524) ... 887A0006
   at Microsoft.ML.OnnxRuntime.SessionOptions.AppendExecutionProvider_DML(Int32)
   at FrameFlow.Inference.Dml.DmlInferenceSession.BuildSessionOptions()
```

**Proposed theory.** ORT holds process-global DXGI factory / adapter objects
that go stale after device removal (`IDXGIFactory::IsCurrent()` false), so every
subsequent `D3D12CreateDevice` off that stale enumeration fails. If we owned the
device — via the C API's `_DML1` entrypoint, which takes a caller-supplied
`IDMLDevice` and `ID3D12CommandQueue` — we could build a *fresh* factory →
adapter → device after a loss and hand ORT a live one.

## Verdict

**The theory is wrong, and the mechanism it proposes is already in place.**

`onnxruntime/core/providers/dml/dml_provider_factory.cc` at tag `v1.24.4` —
the exact native version the kiosk pins — contains no process-scoped cache of
any DXGI factory, adapter, or D3D12 device. The only file-scope `static`s are
string literals in the provider-options parsers (`"performance_preference"`,
`"device_filter"`, `"device_id"`) and the `ort_dml_api_10_to_x` function-pointer
table. Every EP registration builds the whole chain from scratch:

```cpp
// dml_provider_factory.cc:509-524 — DMLProviderFactoryCreator::CreateD3D12Device
ComPtr<IDXGIFactory4> dxgi_factory;
ORT_THROW_IF_FAILED(CreateDXGIFactory2(0, IID_GRAPHICS_PPV_ARGS(dxgi_factory.ReleaseAndGetAddressOf())));   // 510

ComPtr<IDXGIAdapter1> adapter;
ORT_THROW_IF_FAILED(dxgi_factory->EnumAdapters1(device_id, &adapter));                                      // 513
...
ComPtr<ID3D12Device> d3d12_device;
ORT_THROW_IF_FAILED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, ...));                         // 524  <-- throws
```

Line 510 creates a **brand-new** `IDXGIFactory4` on every single call. Line 524
— the line named in the production stack trace — is a plain `D3D12CreateDevice`
against an adapter enumerated from that brand-new factory, moments earlier.

So the "refresh the DXGI factory" remedy is exactly what ORT already does on
every attempt, and it fails anyway. The poisoned state lives **below** the DXGI
factory, in the process's D3D12/UMD association with the adapter, which nothing
in user mode can re-create. That is also the only explanation consistent with
the evidence that a new process on the same adapter, same driver, same kernel
succeeds instantly.

**Consequence for the `_DML1` proposal:** a caller-owned device does not sidestep
the failure, because to own a device we must first call `D3D12CreateDevice`
ourselves — the identical API on the identical adapter that is already failing
inside ORT. We would be reimplementing lines 509-524 in C# and reproducing the
`887A0006` one stack frame higher up. The interop buys us the *ownership* seam;
it does not buy us a *working device*, and the working device is the entire point.

## Secondary findings

These are worth recording because they close off adjacent "what about…" questions.

### 1. `_DML1` does not exist under that name; the real export does, and it works from .NET

There is no `OrtSessionOptionsAppendExecutionProvider_DML1` export. The
caller-owned-device entrypoint is **`OrtSessionOptionsAppendExecutionProviderEx_DML`**,
and it *is* present in the pinned binary (verified against the export-name table
of `microsoft.ml.onnxruntime.directml/1.24.4/runtimes/win-x64/native/onnxruntime.dll`;
the full DML-adjacent export set is `..._DML`, `...Ex_DML`, plus `..._DML2` and
the rest of `OrtDmlApi` reachable through `GetExecutionProviderApi("DML", …)`).

The spike (below) drives it successfully with **~40 lines of hand-rolled COM
interop and zero third-party dependencies** — no Vortice, no TerraFX, no
Silk.NET. It needs three `DllImport`s (`D3D12CreateDevice`, `DMLCreateDevice1`,
`OrtSessionOptionsAppendExecutionProviderEx_DML`), two vtable calls
(`ID3D12Device::CreateCommandQueue` slot 8, `IUnknown::Release` slot 2), and
`SessionOptions.DangerousGetHandle()` to reach the native options pointer.

**So the path is reachable and cheap.** It just doesn't solve this problem.
Recording it here means the next person who needs caller-owned D3D12 device
ownership — the ADR-0022 zero-copy iGPU path is the obvious candidate — starts
from a working reference instead of re-deriving it.

### 2. There is a second, DXCore-based device-creation path reachable with zero interop

`AppendExecutionProvider_DML(deviceId)` is not ORT's only route. Passing
provider options **without** a `device_id` routes through a completely different
adapter-enumeration stack — DXCore rather than DXGI:

```
provider_registration.cc:203  CreateFromProviderOptions
  -> dml_provider_factory.cc:494  CreateFromDeviceOptions   (no device_id given)
  -> :322  LoadLibraryExW("dxcore.dll") -> DXCoreCreateAdapterFactory
  -> :375  CreateFromAdapterList -> :645  D3D12CreateDevice(IDXCoreAdapter, ...)
```

and the managed 1.28.0 binding reaches it with no interop at all, because
`SessionOptions.AppendExecutionProvider(string, Dictionary<string,string>)`
forwards the name verbatim and `provider_registration.cc` accepts `"DML"`:

```csharp
options.AppendExecutionProvider("DML", new Dictionary<string, string>
{
    ["performance_preference"] = "high_performance",
});
```

This is a genuinely different enumeration stack, so it is *conceivable* it is
poisoned differently. It is also a two-line change with no new dependencies, so
it costs nothing to test. **But note it still bottoms out in `D3D12CreateDevice`
at line 645**, so the same reasoning predicts it fails identically. Probe path
`D` exists to check this cheaply if anyone wants the confirmation; do not build
anything on the assumption that it works.

## Cost/benefit

| | In-process recovery via caller-owned device | Process restart |
|---|---|---|
| Recovers GPU after TDR | **No** (per the analysis above) | **Yes, 54/54 observed** |
| New dependencies | D3D12/DXGI/DML COM interop in a shipped assembly | none |
| New failure surface | device/queue lifetime, native leaks on the failure path, an EP that ORT will not tear down for us | none |
| Ongoing cost | interop pinned against an ORT export marked `[[deprecated]]` in-source | none |

Even if the mechanism worked, we would be adding native device-lifetime
ownership to `FrameFlow.Inference.Dml` — currently a ~100-line class whose whole
virtue is that DirectML needs no bootstrap — in exchange for something a restart
already does perfectly. Since the mechanism does *not* work, there is no case at
all.

**No FrameFlow API change is proposed.** `DmlInferenceSession(string)`,
`IInferenceSessionFactory`, and `InferenceSessionFactoryBuilder` stay as they
are. The device-provider seam that was sketched for this — an
`IDmlDeviceProvider` defaulting to today's behaviour — would be a seam onto a
dead end, and adding it would imply to future readers that in-process recovery
is a supported direction. It isn't.

## What to do instead

1. **Keep the CPU fallback** (`ReprobingInferenceSessionFactory`, consumer-side).
   It is the correct load-bearing mitigation: the kiosk keeps serving through a
   TDR, degraded but alive. Note that it re-probes on every open specifically to
   defeat `LazyResolvingFactory`'s cached-EP shortcut — that pairing is doing
   real work and should not be "simplified" away.
2. **Recover the GPU by restarting the process**, on a supervisor-driven policy
   (e.g. after N consecutive DML registration failures, when the box is idle).
   This is not a workaround for a missing feature; per the analysis above it is
   the only user-mode recovery that exists.
3. **Treat the TDR itself as the bug worth fixing.** Gen9
   `DXCOMPUTE` hangs are provoked by the workload. Shrinking or throttling what
   is dispatched, or raising `TdrDelay`, attacks the cause; everything in this
   document is about the aftermath.

## Reproducing / re-checking this

`spikes/DmlTdrProbe` is a throwaway console app that exercises all five
device-creation paths in one process and reports which survive. It is
deliberately **not** in `FrameFlow.slnx` and is not shipped.

```bash
dotnet run --project spikes/DmlTdrProbe -- --repeat 20 --delay 30
```

Baseline on healthy hardware (verified 2026-08-14, this workstation):

```
PASS  A  D3D12CreateDevice(default adapter)
PASS  B  DMLCreateDevice1(FL 5_0)
PASS  C  ORT AppendExecutionProvider_DML()
PASS  D  ORT AppendExecutionProvider("DML", perf=high_performance)
PASS  E1 ID3D12Device::CreateCommandQueue
PASS  E2 ORT ...AppendExecutionProviderEx_DML(caller-owned)
```

Run it on a **dev/test box that has just taken a real TDR** — never on a
production kiosk. The analysis predicts `A` fails with `887A0006`, which settles
the question empirically: `E2` cannot succeed when `A` fails, because `E2`
consumes `A`'s output.

The probe deliberately does **not** provoke a TDR via `ID3D12Device5::RemoveDevice`.
That call poisons only the single device object it targets and the next
`D3D12CreateDevice` succeeds cleanly, which would produce a false "in-process
recovery works" result and send us straight back down this path.
