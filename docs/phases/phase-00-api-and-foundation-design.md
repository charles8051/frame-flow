# Phase 00 — API and Foundation Design

## Status

**Done.** Consumer API shape, lifecycle model, contracts, and skeleton signatures landed. The surface has since evolved past the original skeleton — see `FrameFlow.Player` (`MediaPlayer.CreateAsync`, `FrameFlowPlayer.Open`) and ADR-0024/ADR-0027/ADR-0032 for the shape as built.

## Goal

Lock the top-level consumer API shape, lifecycle model, options surface, usage samples, and foundational skeleton before lower-level implementation expands.

## In scope

- consumer-facing usage samples
- builder and fluent configuration shape
- DI registration surface
- options model shape
- lifecycle and disposal model
- state, event, and error model
- public interfaces and empty method skeletons
- extension-point API sketch for plausible future consumer customization
- rules for what counts as API-stable enough to build on
- example projects under `examples/` with aspirational "dream code" that compiles against skeleton interfaces and validates the intended consumer experience:
  - `FrameFlow.Examples.AvaloniaPlayer` — full Avalonia desktop player with video surface and transport controls
  - `FrameFlow.Examples.ConsoleMediaInspector` — headless metadata probing, no audio or video output
  - `FrameFlow.Examples.AudioOnlyPlayer` — console audio player demonstrating decoupled audio pipeline
  - `FrameFlow.Examples.HostedServicePlayer` — Generic Host integration with config-driven DI and `BackgroundService`

## Out of scope

- real FFmpeg binding implementation
- real demuxing
- real decoding
- real playback orchestration
- UI rendering implementation

## Deliverables

- top-level API sketch that feels good from a consumer perspective
- sample code for plain library usage, DI usage, and host-based usage
- reviewed contract set across `FrameFlow.Media`, `FrameFlow.Playback`, and registration surfaces
- empty skeleton signatures where deeper phases will implement behavior
- extension seam sketch covering future hooks such as frame overlays/annotations, audio processing, presenters, source providers, diagnostics listeners, and timeline annotations
- explicit lifecycle, ownership, and error-handling guidance
- four example projects covering Avalonia desktop, headless inspection, audio-only console, and Generic Host scenarios (all compile as dream code today; progressively functional as phases ship)

## Extension-point API sketch

Phase 00 should define narrow extension seams without committing to a broad plugin system.

The initial sketch should explore contracts shaped like:

- source provider extension points
- video frame processor or overlay/annotation extension points
- audio processor extension points
- presenter extension points
- diagnostics listener extension points
- timeline annotation provider extension points

The goal is not to implement these features in Phase 00.

The goal is to make sure the public API and orchestration boundaries leave room for them later without forcing architectural rewrites.

## Risks

- over-designing abstractions too early
- making the public API feel nice while leaving internal ownership unclear
- locking in naming or lifecycle choices that conflict with FFmpeg realities
- introducing a speculative plugin system instead of a few believable seams

## Validation

- review usage examples before implementation
- ensure DI and options surfaces are coherent
- ensure lifecycle and disposal responsibilities are understandable from sample code
- ensure the Architecture Hawk can trace clean subsystem boundaries from the API surface inward
- ensure likely future customization cases can be expressed through narrow seams rather than cross-layer hacks

## Exit criteria

- the API Steward is satisfied with consumer ergonomics
- the Architecture Hawk is satisfied with boundary and lifecycle shape
- the Master Coordinator considers the API stable enough to support bootstrap and demux work

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: does not apply (no FFmpeg interaction)
- **Gate 3 — Testing Review and Implementation**: applies
