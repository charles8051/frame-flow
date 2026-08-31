# ADR-0004: V1 Platform and Backend Matrix

## Status

Accepted

## Context

FrameFlow is intended to grow into a reusable media core with multiple presenters and potentially hardware-accelerated paths.

However, trying to support too many presenters, audio backends, and acceleration models in v1 would create a large coordination burden before the software path is proven.

The project needs a constrained and realistic v1 scope.

## Decision

FrameFlow v1 will target:

- software decode
- software pixel conversion
- a headless core
- one initial UI presenter: Avalonia
- one initial audio backend: OpenAL
- Windows, macOS, and Linux as supported environments where FFmpeg can be loaded and the chosen backend is available

FrameFlow v1 will explicitly not require:

- hardware decode
- GPU-first rendering
- multiple audio backends
- a large presenter matrix

Additional presenters and acceleration work are Phase 09 concerns, not v1 foundation requirements.

## Consequences

### Positive

- scope remains tractable
- architecture stays centered on the software path
- v1 can focus on correctness, lifecycle, and API quality

### Negative

- some consumers may want backends or presenters earlier
- presenter and backend abstractions must still be designed carefully enough to support later expansion

## Alternatives Considered

### Build multi-presenter and multi-backend support from the start

Rejected because it would greatly increase early complexity.

### Commit to a hardware-accelerated path in v1

Rejected because it would distort the foundational architecture around optional complexity.

### Single-presenter, single-backend, software-first v1

Accepted as the cleanest way to establish a strong foundation.
