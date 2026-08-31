# Phase 03 — Video Decode

## Status

**Done.** `FrameFlow.Decoding.VideoDecoder` decodes and converts video frames; the hardware-decode path landed later under ADR-0033.

## Goal

Implement the first real video decode path that emits managed frame objects with timestamps.

## In scope

- video codec discovery and opening
- reusable decode frame allocation
- pixel conversion strategy
- decoded video frame contract refinement (shipped as `IVideoFrame` / `CpuVideoFrame` / `PooledCpuVideoFrame` in `FrameFlow.Media`, not the `DecodedVideoFrame` name used here)
- timestamp extraction and normalization

## Out of scope

- synchronization
- full playback loop
- UI control logic

## Deliverables

- `VideoDecoder` implementation
- frame conversion to a stable presentation format such as BGRA
- first-frame decode path
- sequential decode path over video packets

## Risks

- timestamp correctness
- pixel format conversion performance
- native buffer lifetime mistakes

## Validation

- decode first frame from representative sample files
- decode sequential frames and verify dimensions and timestamps
- optionally export frames to image files in a harness

## Exit criteria

- video frames can be decoded deterministically
- frame contracts are usable by a future presenter
- decoder/resource ownership is isolated and reliable

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: applies (decode API, pixel format conversion, frame lifecycle)
- **Gate 3 — Testing Review and Implementation**: applies
