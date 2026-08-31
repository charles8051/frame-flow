# ADR-0005: Native Resource Ownership Rules

## Status

Accepted

## Context

FFmpeg interop depends on explicit ownership and disposal of native resources such as:

- format contexts
- codec contexts
- frames
- packets
- scaling contexts
- resampling contexts

The easiest way for this kind of codebase to become fragile is to let raw native pointers leak across subsystem boundaries or to make ownership ambiguous.

## Decision

FrameFlow will adopt these ownership rules:

1. the component that allocates a native resource is responsible for freeing it
2. native pointers must not leak outside native-owning layers except through tightly controlled abstractions
3. shared ownership of FFmpeg pointers is prohibited
4. native-owning classes should be small, explicit, and focused
5. playback and UI layers should consume managed contracts, not raw FFmpeg objects

In practice:

- `FrameFlow.Native` and `FrameFlow.Decoding` will own FFmpeg-native resources
- `FrameFlow.Media`, `FrameFlow.Playback`, and adapters will consume managed models and stable contracts

## Consequences

### Positive

- disposal responsibilities remain clear
- cross-layer coupling is reduced
- later debugging of leaks and lifetime issues becomes easier

### Negative

- some data must be copied or transformed into managed contracts
- convenience shortcuts using shared native pointers are intentionally disallowed

## Alternatives Considered

### Shared pointer ownership across layers

Rejected because it invites hidden lifetime bugs and coupling.

### Expose FFmpeg-native objects through higher-level APIs

Rejected because it would make the public and orchestration layers too dependent on FFmpeg internals.

### Strict single-owner native resource model

Accepted because it protects the architecture from a common source of long-term pain.
