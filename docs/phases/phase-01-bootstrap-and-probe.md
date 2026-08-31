# Phase 01 — Bootstrap and Probe

## Status

**Done.** `FrameFlow.Native` resolves and loads the FFmpeg libraries (`FrameFlowBootstrapper`, `FFmpegLibraryResolver`, `FfmpegNativeLibraryLoader`) and probes hardware-decode capability (`HardwareDecodeProbe`, ADR-0033).

## Goal

Implement a real FFmpeg bootstrap path in `FrameFlow.Native` that can resolve native binaries, initialize bindings, and prove that the process can call FFmpeg successfully.

## In scope

- native options model refinement
- runtime identifier detection
- bundled/custom/system binary resolution strategy
- binding initialization
- version/probe API
- diagnostic result model for success/failure

## Out of scope

- demuxing
- decoding
- playback
- UI integration

## Deliverables

- concrete `FrameFlowBootstrapper`
- probe result including binary source and version information
- deterministic failure messages for missing or invalid FFmpeg environments
- a small executable or test harness path that validates bootstrap behavior

## Risks

- platform-specific library search behavior
- dependency loading differences across Windows, macOS, and Linux
- confusion between "path resolved" and "bindings actually usable"

## Validation

- initialize from a custom path
- initialize from bundled binaries when present
- initialize from system binaries when configured
- fail clearly when FFmpeg is unavailable
- call at least one FFmpeg function successfully

## Exit criteria

- `FrameFlow.Native` can reliably initialize FFmpeg
- probe output is actionable and testable
- later phases can depend on a stable bootstrap contract

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: applies (library loading, version detection, function resolution)
- **Gate 3 — Testing Review and Implementation**: applies
