# Phase 08 — Polish and Diagnostics

## Status

**Done.** Structured logging via source-generated `LoggerMessage` partials, diagnostics surfaces (ADR-0034), 19 test projects under `tests/`, and packaging turned on repo-wide (`src/Directory.Build.props`, ADR-0041).

## Goal

Improve reliability, debuggability, usability, and packaging readiness after the software playback path is working.

## In scope

- richer logging and diagnostic surfaces
- clearer exception and error models
- options and DI ergonomics
- test strategy hardening
- repository/documentation cleanup
- package-readiness work

## Out of scope

- major new rendering architectures
- large platform expansion efforts

## Deliverables

- logging/diagnostic improvements
- stronger options surfaces
- better examples and setup docs
- stronger validation around disposal and state transitions

## Risks

- trying to polish too early and obscuring architectural issues
- package ergonomics driving core compromises

## Validation

- build and smoke-test across the existing software path
- verify errors are actionable
- verify docs align with actual APIs

## Exit criteria

- the software path is stable enough for wider iteration
- diagnostics are good enough to support future optimization work

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: does not apply (no direct FFmpeg interaction)
- **Gate 3 — Testing Review and Implementation**: applies
