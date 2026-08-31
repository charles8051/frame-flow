# ADR-0001: API-First Foundation Sequencing

## Status

Accepted

## Context

FrameFlow is a greenfield media playback project with a large amount of likely complexity in FFmpeg interop, playback orchestration, synchronization, and UI/backend adaptation.

If lower-level implementation starts before the top-level API, lifecycle model, and public contracts are stable enough, the project risks:

- shaping the API around incidental implementation details
- introducing coupling that becomes expensive to unwind
- forcing consumer-facing surfaces to evolve reactively instead of intentionally

The project also explicitly values:

- consumer-oriented API cleanliness
- composition over inheritance
- hosted DI and options friendliness
- lifecycle separation from processing logic

## Decision

FrameFlow will use an API-first sequencing model.

This means:

1. **Phase 00 is API and Foundation Design**
2. usage samples and consumer workflows are designed before lower-level implementation accelerates
3. public contracts, lifecycle boundaries, options surfaces, and skeleton signatures are defined early
4. lower-level phases implement against that reviewed shape instead of inventing the shape during implementation

The API Steward is the primary owner of Phase 00, with Architecture Hawk review and Master Coordinator gating.

## Consequences

### Positive

- public API quality becomes a first-class concern
- architectural boundaries can be seen earlier
- later implementation has a clearer target
- DI/options/host integration can be intentional instead of retrofitted

### Negative

- some early API decisions may need revision after implementation learning
- there is a risk of over-designing abstractions too early
- implementation may feel slower at the beginning

## Alternatives Considered

### Implement bootstrap and decoding first, shape API later

Rejected because it tends to let low-level details dictate the public model.

### Design everything in abstract before any scaffolding

Rejected because it increases the risk of speculative architecture disconnected from real code.

### API-first with immediate skeleton scaffolding

Accepted because it balances intentional design with practical forward movement.
