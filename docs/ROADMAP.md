# FrameFlow Roadmap

This roadmap organizes the project into implementation phases that can be executed, reviewed, and refined independently.

The roadmap is intentionally sequential at the top level, but each phase should still be implemented with testable seams so later work does not require redesigning earlier foundations.

## Planning model

FrameFlow planning is split across these documents:

- `ARCHITECTURE.md` for long-lived architectural intent
- `ROADMAP.md` for phase ordering and delivery boundaries
- `phases/` for execution-level phase plans
- `adr/` for major decisions that should be durable and discoverable

## Phase summary

| Phase | Name | Status | Goal | Depends On |
|---|---|---|---|---|
| 00 | API and Foundation Design | Done | Lock the consumer API shape, lifecycle model, contracts, and skeleton | none |
| 00b | DI Registration and Host Integration | Done | Deliver IServiceCollection surface, lifetimes, and hosted-service integration | 00 |
| 00c | Test Corpus Generation | Done | Generate synthetic test media files for validation across all phases | 00 |
| 01 | Bootstrap and Probe | Done | Load and validate FFmpeg binaries | 00 |
| 01a | Runtime Download Script | Done | Download script for develop-time priming of runtimes directory DLL cache | 01 |
| 02 | Demux and Metadata | Done | Open media and inspect streams | 00, 01 |
| 03 | Video Decode | Done | Decode and materialize video frames | 00, 01, 02 |
| 04 | Audio Decode | Done | Decode and materialize PCM audio blocks | 00, 01, 02 |
| 05 | Playback Session | Done | Build the first headless playback orchestration | 00, 01, 02, 03, 04 |
| 06 | Sync and Seeking | Done | Add clocks, sync policy, seek, pause, resume | 05 |
| 07 | Avalonia Adapter | Done | Add the first UI presenter and control surface | 05, 06 |
| 08 | Polish and Diagnostics | Done | Improve logging, errors, testability, packaging readiness | 05, 06, 07 |
| 09 | Acceleration and Presenters | Done | Explore GPU paths and additional presenters | 08 |

## Where the work is tracked now

All thirteen numbered phases are complete. The phase model got FrameFlow from
an empty solution to a working player; the work since then — the processing
graph, the encoder terminal, capture sources, inference backends, and the
zero-copy Windows presenter — has been driven by ADRs rather than by new
phases, and this table has not been extended to cover it.

For current work:

- `docs/adr/` is the authority on what was decided and why (the series runs
  through ADR-0067)
- deferred items live in `docs/DEFERRED_WORK.md`, not in this file
- `docs/issues/README.md` tracks open review findings

Treat the phase table below as the delivery history, not as a live plan.

## Status tracking

Every phase is **Done**, so there are no transitions left to record. The **Status** column and the `## Status` section in each phase doc are now a finished record, not a field to maintain.

Edit them only to correct an error in the history — a phase marked Done whose deliverable turns out never to have shipped, or a status line that names a type that was never built. Do not reopen a phase to carry new work; new work goes in an ADR.

## Exit criteria model

Every phase doc should define:

- explicit in-scope deliverables
- out-of-scope work
- technical risks
- test and validation strategy
- exit criteria

No phase should be considered complete just because some code exists. It should satisfy its exit criteria and produce useful artifacts for the next phase.

## Short descriptions

### Phase 00 — API and Foundation Design

Lock the top-level consumer API, usage samples, lifecycle model, options surface, and empty skeleton signatures before deeper implementation begins.

### Phase 00b — DI Registration and Host Integration

Deliver the `IServiceCollection` integration surface, service lifetime decisions, and hosted-service integration so that FrameFlow works naturally in both standalone builder and DI-hosted scenarios.

### Phase 00c — Test Corpus Generation

Use FFmpeg to generate a baseline corpus of synthetic test media files so that bootstrapping, decoding, and playback phases can be validated with real media from the start.

### Phase 01 — Bootstrap and Probe

Deliver a real FFmpeg bootstrap path that can resolve binaries, initialize bindings, and prove that the native environment is usable.

### Phase 01a — Runtime Download Script

Write a download script that primes the `runtimes/` directory cache with the correct FFmpeg native DLLs for develop-time use.

### Phase 02 — Demux and Metadata

Open inputs, enumerate streams, surface media metadata, and establish source abstractions plus seek-capable demux session behavior.

### Phase 03 — Video Decode

Decode compressed video into presentation-ready frames with correct timestamps and a stable managed frame contract.

### Phase 04 — Audio Decode

Decode audio into a stable PCM format suitable for downstream audio sinks.

### Phase 05 — Playback Session

Compose demux, decode, buffering, and outputs into a headless playback session with a clear state model.

### Phase 06 — Sync and Seeking

Add clocking, audio-master timing, frame delay/drop policy, and reliable seek/reset behavior.

### Phase 07 — Avalonia Adapter

Build the first full presenter and UI control without polluting the playback core with Avalonia concerns.

### Phase 08 — Polish and Diagnostics

Improve reliability, observability, documentation, and package ergonomics.

### Phase 09 — Acceleration and Presenters

Explore hardware decode/render and additional presenter targets after the software path is stable.

## Cross-cutting workstreams

Some concerns should be addressed continuously across multiple phases:

- native resource ownership
- cancellation and disposal behavior
- logging and diagnostics
- options and DI registration
- test harnesses and validation assets

These should not become hidden side work. If they materially affect phase scope, update the relevant phase doc.

## Standard sub-phase gates

Every phase must pass through standard review gates before completion. See `phases/SUB_PHASE_GATES.md` for details:

1. **Architectural Scrutiny** — Architecture Hawk reviews structural integrity (all phases)
2. **FFmpeg Domain Scrutiny** — FFmpeg Expert Agent reviews API correctness (phases 00c, 01, 01a, 02, 03, 04, 05, 06, 09)
3. **Testing Review and Implementation** — Testing / Validation Agent designs and implements test suite (all phases)

## Revision guidance

This file is closed to new planning. Do not add phases, re-sequence them, or move a Done phase back to In Progress.

Edit it only to correct the historical record. Architectural changes go in an ADR; deferred work goes in `docs/DEFERRED_WORK.md`; review findings go in `docs/issues/`.
