# A-3: Phases 08 and 09 Need Deeper Implementation

**Severity:** Expected
**Status:** Resolved
**Resolved:** 2026-08-24
**Responsible Agent:** Master Coordinator
**Detected:** 2026-03-29
**Phase Gate:** Phase 08, 09

## Context

Phases 08 (Polish and Diagnostics) and 09 (Acceleration and Presenters) are defined as refinement and exploration phases. Their scope is intentionally open-ended:

- Phase 08: richer logging, stronger error models, options ergonomics, documentation cleanup, test hardening, package readiness
- Phase 09: hardware acceleration spikes, additional presenter targets

## Current State

All preceding phases (00 through 07) have been implemented with:
- Full contract layer in FrameFlow.Media
- DI registration surface with IFrameFlowBuilder
- Real FFmpeg bootstrap with NativeLibrary loading
- Demux session with real FFmpeg avformat interop
- Video decoder with codec open, send/receive, SwScale conversion
- Audio decoder with SwResample
- Playback session with Channel<T> queues, state machine, worker loops
- PlaybackClock with ITimeSource testability seam
- AvaloniaVideoPresenter with frame ownership transfer

## Next Steps

Phase 08 should focus on:
1. Consolidating warning suppressions (CA2000 disposable warnings)
2. Adding structured logging throughout the pipeline
3. Improving error messages for common failure modes
4. Documentation updates to match implemented state
5. Example project updates to use real APIs

Phase 09 is exploratory and should be deferred until the software path is validated with real media files.

## Resolution

Both phases shipped. Phase 08 delivered structured logging (source-generated
`LoggerMessage` partials throughout), diagnostics surfaces (ADR-0034), 19 test
projects, and repo-wide packaging (`src/Directory.Build.props`, ADR-0041).
Phase 09 delivered hardware-decode selection (ADR-0033), the zero-copy Windows
presenter (ADR-0061, ADR-0063, ADR-0064), the SDL presenter (ADR-0018,
ADR-0019), and the DirectML/CUDA inference backends.

Work past Phase 09 is tracked by ADR and in the deferred-work backlog rather than by
phase. See `docs/ROADMAP.md`, "Where the work is tracked now".
