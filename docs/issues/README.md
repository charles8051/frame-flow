# Issue Log

Architectural and test coverage issues identified by agent review. The Master Coordinator must check this log before starting any new phase and ensure open items for that phase gate are resolved.

Status values: **Open**, **Resolved**, **Obsolete** (the code the report was about no longer exists).

The numbered phases are all complete, so the Phase Gate column now records which gate an item was raised against rather than a deadline. **S-7 is the only item still open.**

## Must Fix

| ID | Title | Agent | Status | Phase Gate |
|----|-------|-------|--------|------------|
| M-1 | [Audio.OpenAL boundary violation](M-1-audio-openal-boundary-violation.md) | Architecture Hawk + API Steward | Resolved | Phase 00b |
| M-2 | [PcmAudioBuffer missing ownership](M-2-pcm-audio-block-missing-ownership.md) | Media Contracts Agent | Resolved | Phase 04 |
| M-3 | [Bootstrapper not thread-safe](M-3-bootstrapper-not-thread-safe.md) | Native Bootstrap Agent | Resolved | Phase 01 |

## Should Fix Soon

| ID | Title | Agent | Status | Phase Gate |
|----|-------|-------|--------|------------|
| S-1 | [IMediaSource missing Uri](S-1-imediasource-missing-uri.md) | Media Contracts Agent | Resolved | Phase 02 |
| S-2 | [BootstrapResult.ResolvedPath ambiguous](S-2-bootstrap-result-resolved-path-ambiguous.md) | Native Bootstrap Agent + API Steward | Resolved | Phase 01 |
| S-3 | [Options.Native coupling](S-3-options-native-coupling.md) | API Steward + Architecture Hawk | Resolved | Phase 00b |
| S-4 | [IFrameFlowBootstrapper location](S-4-bootstrapper-interface-location.md) | Architecture Hawk + Media Contracts Agent | Resolved | Phase 01 |
| S-5 | [Orphaned tests/FrameFlow.Tests/](S-5-orphaned-tests-directory.md) | Integration Review Agent | Resolved | Soon |
| S-6 | [SdlInstrumentedPresenter.EnsureTexture missing null check](S-6-sdl-ensure-texture-missing-null-check.md) | SDL Presenter Agent | Obsolete | Phase 08 |
| S-7 | [SDL renderer has no software fallback](S-7-sdl-renderer-no-software-fallback.md) | SDL Presenter Agent | Open | Phase 08 |

## Fix Before Phase

| ID | Title | Agent | Status | Phase Gate |
|----|-------|-------|--------|------------|
| P-1 | [PlaybackClock no testability seam](P-1-playback-clock-no-interface.md) | Playback Orchestration Agent | Resolved | Phase 05 |
| P-2 | [No ILogger wiring](P-2-no-logger-wiring.md) | Native Bootstrap Agent | Resolved | Phase 01 |
| P-3 | [Func vs IAudioSinkFactory](P-3-func-vs-factory-inconsistency.md) | API Steward | Resolved | Phase 00b |

## Architectural Concerns (Post Phase 07)

| ID | Title | Agent | Status | Phase Gate |
|----|-------|-------|--------|------------|
| A-1 | [FFmpeg integration test environment mismatch](A-1-ffmpeg-integration-tests-environment-mismatch.md) | Testing Agent + Native Bootstrap | Resolved | Phase 08 |
| A-2 | [FrameFlow.Audio project is empty](A-2-frameflow-audio-project-empty.md) | Architecture Hawk | Resolved | Phase 08 |
| A-3 | [Phases 08-09 need deeper implementation](A-3-phase-08-09-implementation-depth.md) | Master Coordinator | Resolved | Phase 08 |
| A-4 | [Example projects need update](A-4-example-projects-need-update.md) | Documentation Agent | Resolved | Phase 08 |
