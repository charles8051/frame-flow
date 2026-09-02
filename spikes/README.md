# spikes/

Throwaway diagnostics and feasibility probes. **Not** part of `FrameFlow.slnx`,
not packed, not shipped. Each one exists to answer a specific question that was
recorded in `docs/investigations/`; once the question is closed the spike is
kept only as a way to re-check the answer on real hardware.

| Spike | Question | Verdict |
|---|---|---|
| `DmlTdrProbe` | Can DirectML GPU inference recover in-process after a Windows GPU TDR, instead of requiring a process restart? | **No** — see [2026-08-14-dml-in-process-tdr-recovery.md](../docs/investigations/2026-08-14-dml-in-process-tdr-recovery.md) |
| `package-directive-repro.cs` | Can a single-file .NET app consume FrameFlow through `#:package`, natives included, so a test-bench repro can be C# rather than a bespoke grammar? | **Yes** — see the head of Decision 6 in [command-driven-testbench-host.md](../docs/adr/command-driven-testbench-host.md) |
