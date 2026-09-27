# spikes/

Throwaway diagnostics and feasibility probes. **Not** part of `FrameFlow.slnx`,
not packed, not shipped. Each one exists to answer a specific question that was
recorded in `docs/investigations/`; once the question is closed the spike is
kept only as a way to re-check the answer on real hardware.

| Spike | Question | Verdict |
|---|---|---|
| `DmlTdrProbe` | Can DirectML GPU inference recover in-process after a Windows GPU TDR, instead of requiring a process restart? | **No** — see [2026-08-14-dml-in-process-tdr-recovery.md](../docs/investigations/2026-08-14-dml-in-process-tdr-recovery.md) |
| `package-directive-repro.cs` | Can a single-file .NET app consume FrameFlow through `#:package`, natives included, so a test-bench repro can be C# rather than a bespoke grammar? | **Yes** — see the head of Decision 6 in [ADR-0068](../docs/adr/ADR-0068-command-driven-test-bench-host.md) |
| `D3D12DmlProbe` | Can a D3D12VA-decoded frame reach a DirectML session with no CPU copy: session on the decoder's device, GPU wait on the decoder's fence, a compute shader writing the input, the buffer bound as the tensor? | **Yes.** About 3.0 ms per 1080p frame against 9.2 ms through the CPU; the binding is exact. See [2026-09-27-d3d12-dml-zero-copy.md](../docs/investigations/2026-09-27-d3d12-dml-zero-copy.md) |
| `D3D12WinMlProbe` | Can the D3D12 tensor `D3D12DmlProbe` builds reach Windows ML's TensorRT-RTX with no CPU copy, through a CUDA import of the buffer? | **Yes.** About 2.2 ms per 1080p frame against 8.0 ms through the CPU; the binding is exact. Teardown order can crash the process unless the decoder borrows a `HardwareDevice` (#428). See [2026-09-27-d3d12-winml-tensorrt.md](../docs/investigations/2026-09-27-d3d12-winml-tensorrt.md) |
| `WinMlProbe` | DirectML is in sustained engineering. Does Windows ML give FrameFlow anything DirectML cannot, enough to justify a `FrameFlow.Inference.WinML` package? | **Yes — for the vendor EPs it can reach.** TensorRT-RTX is ~1.9× DirectML on yolov8n; both selection routes reach it and measure the same. See [2026-09-09-windows-ml-ep-selection.md](../docs/investigations/2026-09-09-windows-ml-ep-selection.md), including the retracted finding in §1 and the cold-engine-cache hazard that produced it |
| `LookaheadOverlayProbe` | Does a deeper video ring help an overlay line up with the picture, or does keying it off the presented frame decide it? | **Keying decides it.** At depth 3, 93 to 98% of frames show their own result; depths 12 and 24 add nothing, and an overlay drawn from what was posted leads the picture by the ring. See [2026-09-27-lookahead-overlay.md](../docs/investigations/2026-09-27-lookahead-overlay.md) |
