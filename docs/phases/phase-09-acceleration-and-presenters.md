# Phase 09 — Acceleration and Presenters

## Status

**Done.** Hardware decode selection (ADR-0033), the zero-copy Windows presenter (`FrameFlow.Avalonia.Windows`, ADR-0061/0063/0064), the SDL presenter (`FrameFlow.Sdl`, ADR-0018/0019), and DirectML/CUDA inference backends all shipped.

## Goal

Explore optional hardware acceleration and additional presenter targets after the core software path is stable and understandable.

## In scope

- hardware decode/render investigation
- presenter abstraction review based on real usage
- additional presenter targets beyond Avalonia
- platform-specific adapter spikes

## Out of scope

- rewriting the core around acceleration-first assumptions

## Deliverables

- decision material for hardware acceleration strategy
- initial acceleration spike(s) or prototype adapters
- additional presenter roadmap or first implementation(s)

## Risks

- overfitting abstractions too early
- platform-specific complexity overwhelming the clean core
- mixing acceleration concerns back into headless layers

## Validation

- compare software and accelerated paths where available
- verify fallback behavior remains intact
- document platform-specific constraints clearly

## Exit criteria

- acceleration is optional, isolated, and justified
- presenter expansion does not damage the core architecture

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: applies (hardware acceleration context, frame transfer, format negotiation)
- **Gate 3 — Testing Review and Implementation**: applies
