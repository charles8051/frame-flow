# ADR-0007: Testing and Validation Strategy

## Status

Accepted

## Context

FrameFlow is a media playback system with native interop, timing-sensitive behavior, multiple subsystem boundaries, and future presenter and backend expansion.

If testing is treated as an afterthought, the project is likely to accumulate several kinds of pain:

- logic that depends on real wall-clock timing and cannot be validated deterministically
- playback behavior that can only be checked manually through UI-driven workflows
- native and decoding failures that are difficult to isolate
- format regressions that go unnoticed because there is no stable sample corpus
- architecture that looks clean in docs but is hard to validate in practice

At the same time, building a full test suite before the core exists would be wasteful.

## Decision

FrameFlow will design its testing and validation strategy early, while implementing most of the concrete harnesses and coverage incrementally as phases mature.

The test strategy will be organized around these layers:

1. unit tests for isolated logic and policy
2. contract tests for shared models and boundary expectations
3. integration tests for subsystem composition
4. playback smoke tests for end-to-end software-path validation
5. sample media corpus validation for format and failure coverage

Early architectural requirements include:

- deterministic seams where timing and synchronization behavior matter
- fakeable presenters, audio sinks, and related output dependencies
- headless validation paths that do not require a UI adapter
- harnesses for probe, metadata, first-frame, audio decode, and playback smoke scenarios

FrameFlow will add a dedicated Testing / Validation Agent to own validation infrastructure and test strategy execution.

The Architecture Hawk remains responsible for reviewing whether the architecture is testable, but not for implementing the validation system as its primary role.

## Consequences

### Positive

- testability becomes part of the architecture instead of a retrofit
- validation work can be phased in alongside implementation rather than postponed indefinitely
- future debugging and regression work become easier because harnesses and doubles exist

### Negative

- early phases must reserve some design effort for determinism and harness seams
- some validation infrastructure will appear before full production implementations exist

## Alternatives Considered

### Defer test planning until after the playback core works

Rejected because important seams such as clocks, output doubles, and headless validation paths are harder to add later.

### Make the Architecture Hawk the default testing owner

Rejected because architectural review and validation implementation are related but distinct responsibilities.

### Build a comprehensive end-to-end test suite immediately

Rejected because it would front-load too much concrete testing work before the core APIs and subsystem behaviors have stabilized.
