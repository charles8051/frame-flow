# Phase 04 — Audio Decode

## Status

**Done.** `FrameFlow.Decoding.AudioDecoder` plus `FrameFlow.Audio.FfmpegAudioResampler` produce stable PCM blocks.

## Goal

Implement the first real audio decode path that emits stable PCM blocks suitable for output sinks.

## In scope

- audio codec discovery and opening
- resampler strategy
- PCM format standardization
- `PcmAudioBuffer` contract refinement
- timestamp extraction and normalization

## Out of scope

- real-time playback orchestration
- sync policy
- UI

## Deliverables

- `AudioDecoder` implementation
- conversion to stable PCM output, likely stereo S16 for the first backend
- sample/timestamp surface suitable for sink integration

## Risks

- planar vs interleaved sample handling
- channel layout normalization
- timestamp drift or inconsistent presentation times

## Validation

- decode sample files with varying channel counts and formats
- inspect PCM output length and timing
- optionally export WAV data in a harness for manual verification

## Exit criteria

- audio decoding produces stable PCM blocks
- downstream sinks can consume blocks without FFmpeg-specific knowledge
- resource lifetime and conversion logic are isolated from playback orchestration

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: applies (audio decode API, resampler configuration, channel layout evolution)
- **Gate 3 — Testing Review and Implementation**: applies
