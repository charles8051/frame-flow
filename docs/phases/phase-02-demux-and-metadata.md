# Phase 02 — Demux and Metadata

## Status

**Done.** `FrameFlow.Decoding.DemuxSession` opens inputs, enumerates streams, surfaces `MediaInfo`, and seeks.

## Goal

Create the first usable media-opening path by building `DemuxSession`, stream inspection, and metadata surfaces.

## In scope

- media source abstraction review
- `DemuxSession` implementation
- open media from file/URI where practical
- enumerate streams
- surface container and stream metadata
- basic seek support in the demux session

## Out of scope

- actual frame or PCM decoding
- playback clocks
- UI

## Deliverables

- `IDemuxSessionFactory` implementation
- `MediaInfo` filled from real FFmpeg metadata
- stream selection policy for first audio/video streams
- seek behavior at the demux layer

## Risks

- stream metadata normalization
- time base translation correctness
- URL/file source differences

## Validation

- open multiple media files with different stream layouts
- verify metadata values against external tools where practical
- verify seekable vs non-seekable source behavior

## Exit criteria

- media can be opened and inspected without UI
- `MediaInfo` is useful enough to drive decoder creation in later phases
- demux session owns its native resources cleanly

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: applies (avformat API usage, stream enumeration, seek API selection)
- **Gate 3 — Testing Review and Implementation**: applies
