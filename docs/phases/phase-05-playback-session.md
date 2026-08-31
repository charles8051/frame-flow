# Phase 05 — Playback Session

## Status

**Done.** Superseded in shape by the substrate rework — the headless session is now `FrameFlow.Playback.SubstrateSession` driven by `PlaybackController` (ADR-0023, ADR-0032, ADR-0036), not the originally sketched `PlaybackSession`.

## Goal

Compose demuxing, decoding, buffering, and output contracts into the first headless playback session.

## In scope

- `IPlaybackSession` implementation
- playback state machine
- initial demux loop
- packet routing to audio/video paths
- queue/backpressure policy
- play/pause/stop lifecycle

## Out of scope

- final sync tuning
- polished UI integration
- hardware acceleration

## Deliverables

- `PlaybackSession` implementation
- explicit state transitions
- bounded packet and frame queues
- basic orchestration between decoders and sinks/presenters

## Risks

- hidden coupling reappearing in the orchestration layer
- cancellation/disposal race conditions
- queue policies that work for samples but fail on longer media

## Validation

- exercise open/play/pause/stop in a headless harness
- verify queues stay bounded
- ensure resources shut down cleanly

## Exit criteria

- a headless playback session can run end-to-end
- state transitions are explicit and understandable
- the orchestration layer remains decoupled from UI and backend-specific details

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: applies (flush/EOF semantics at demux/decode boundary)
- **Gate 3 — Testing Review and Implementation**: applies
