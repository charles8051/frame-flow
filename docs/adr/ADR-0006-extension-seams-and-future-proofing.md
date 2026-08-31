# ADR-0006: Extension Seams and Future-Proofing

## Status

Accepted

## Context

FrameFlow is intentionally keeping v1 narrow so the playback core can be built cleanly before the project expands into more presenters, diagnostics, or customization features.

At the same time, some future consumer scenarios are already plausible:

- frame overlays and annotations
- alternate video presenters
- audio processing hooks
- custom media source providers
- diagnostics and telemetry listeners
- timeline annotations such as chapters, bookmarks, or markers

If the architecture ignores these possibilities completely, later additions may require cross-layer rewrites. If the architecture overreacts, the project may accumulate a speculative plugin system before the core even exists.

## Decision

FrameFlow will future-proof the design by defining a small set of narrow extension seams during Phase 00, without building a broad plugin platform in v1.

The preferred extension seam categories are:

1. source providers
2. video frame processors, including overlays and annotations
3. audio processors
4. presenters
5. diagnostics listeners
6. timeline annotation providers

These seams should be expressed as focused contracts near the relevant layer boundaries.

They must not:

- leak FFmpeg-native details into top-level APIs
- require hidden cross-layer knowledge
- force all playback paths through a generalized plugin pipeline
- imply that every seam must be implemented in v1

The Architecture Hawk is the default review owner for future-proofing concerns. Its job is to protect optionality where it matters while rejecting speculative generalization.

## Consequences

### Positive

- likely future consumer needs have believable paths forward
- extension concerns are discussed while API and boundary design are still cheap to change
- the project can stay narrow in v1 without closing important doors

### Negative

- Phase 00 must spend some effort on extension design before concrete implementation
- some provisional contracts may exist before their first production use

## Alternatives Considered

### Ignore future extension concerns until a feature is requested

Rejected because some seams are expensive to retrofit after the playback core and public API are entrenched.

### Build a general plugin framework in v1

Rejected because it would add speculation, complexity, and governance overhead too early.

### Introduce ad hoc hooks feature by feature later

Rejected because it would likely produce inconsistent extension shapes and cross-layer coupling.
