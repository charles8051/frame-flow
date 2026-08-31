# Phase 07 — Avalonia Adapter

## Status

**Done.** `FrameFlow.Avalonia` ships `AvaloniaVideoSink`, `FrameFlowVideoView`, and the `FrameFlowPlayerView` chrome. The presenter contract is `IVideoSink`, not the originally sketched `IVideoFramePresenter`.

## Goal

Build the first presenter and UI control on top of the headless playback core without leaking Avalonia concerns into the lower layers.

## In scope

- `AvaloniaVideoPresenter`
- UI control surface
- input commands for open/play/pause/seek/stop
- property/event mapping between playback session and control
- basic visual presentation using CPU-backed rendering

## Out of scope

- GPU acceleration
- alternate presenters

## Deliverables

- usable Avalonia presenter
- thin Avalonia control
- wiring for current position, duration, and state updates

## Risks

- accidental reintroduction of player/UI coupling
- UI thread marshalling complexity
- frame copy costs becoming hidden inside the control layer

## Validation

- run a sample Avalonia app
- verify first-frame display
- verify play/pause/seek/stop behavior
- verify UI-thread-safe frame presentation

## Exit criteria

- Avalonia becomes a clean adapter, not the architecture center
- playback works in a UI app without infecting the core with UI types

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: does not apply (no direct FFmpeg interaction)
- **Gate 3 — Testing Review and Implementation**: applies
