# Phase 06 — Sync and Seeking

## Status

**Done.** Audio-master sync via `IClockSource` (ADR-0003, ADR-0035, ADR-0057) and seek discipline via `ISeekResettable` (ADR-0048, ADR-0056).

## Goal

Add timing, synchronization policy, and reliable seek behavior on top of the headless playback session.

## In scope

- playback clock implementation
- audio-master sync strategy
- wall-clock fallback for video-only playback
- pause/resume timing correctness
- seek reset semantics
- frame delay/drop policy

## Out of scope

- hardware acceleration
- expanded presenter matrix

## Deliverables

- production-ready `PlaybackClock`
- first real `ISyncStrategy` implementation
- seek/pause/resume integration in playback session
- timing policy for late and early frames

## Risks

- A/V drift
- audio underruns due to blocking video work
- incorrect state resets after seek

## Validation

- long-running playback tests
- repeated seek tests
- pause/resume consistency checks
- mixed media samples with and without audio

## Exit criteria

- audio/video remain acceptably synchronized
- seek works reliably enough to support a UI control
- timing logic is centralized rather than scattered through the player

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: applies (seek flag selection, flush-after-seek protocol, timestamp discontinuity handling)
- **Gate 3 — Testing Review and Implementation**: applies
