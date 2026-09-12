# spikes/

Throwaway diagnostics and feasibility probes. **Not** part of `FrameFlow.slnx`,
not packed, not shipped. Each one exists to answer a specific question that was
recorded in `docs/investigations/`; once the question is closed the spike is
kept only as a way to re-check the answer on real hardware.

| Spike | Question | Verdict |
|---|---|---|
| `DmlTdrProbe` | Can DirectML GPU inference recover in-process after a Windows GPU TDR, instead of requiring a process restart? | **No** — see [2026-08-14-dml-in-process-tdr-recovery.md](../docs/investigations/2026-08-14-dml-in-process-tdr-recovery.md) |
| `package-directive-repro.cs` | Can a single-file .NET app consume FrameFlow through `#:package`, natives included, so a test-bench repro can be C# rather than a bespoke grammar? | **Yes** — see the head of Decision 6 in [ADR-0068](../docs/adr/ADR-0068-command-driven-test-bench-host.md) |
| `WinMlProbe` | DirectML is in sustained engineering. Does Windows ML give FrameFlow anything DirectML cannot, enough to justify a `FrameFlow.Inference.WinML` package? | **Yes — for its EP selection policy, not its EP list.** 1.9× DirectML on yolov8n; appending the same vendor EP by hand is 2.6× *slower* than DirectML. See [2026-09-09-windows-ml-ep-selection.md](../docs/investigations/2026-09-09-windows-ml-ep-selection.md) |
