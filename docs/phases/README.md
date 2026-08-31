# Phase Documents

Each phase document is the execution-level counterpart to the architecture and roadmap.

Use these docs to answer:

- what exactly are we building in this phase?
- what is explicitly out of scope?
- what are the risks and unknowns?
- how will we validate success?
- what does "done" mean?

Recommended workflow:

1. review `ARCHITECTURE.md`
2. check `ROADMAP.md`
3. implement against the relevant phase doc
4. update the phase doc when scope or learning changes
5. record high-impact decisions in `docs/adr/`

Phase numbering should follow `ROADMAP.md`.

## Standard sub-phase gates

Every phase must pass through standard review gates before completion. See `SUB_PHASE_GATES.md` for:

1. **Architectural Scrutiny** — Architecture Hawk reviews structural integrity (all phases)
2. **FFmpeg Domain Scrutiny** — FFmpeg Expert Agent reviews API correctness (applicable phases only)
3. **Testing Review and Implementation** — Testing / Validation Agent implements test suite (all phases)

## Current sequence

- `phase-00-api-and-foundation-design.md`
- `phase-00b-di-registration-and-host-integration.md`
- `phase-00c-test-corpus-generation.md`
- `phase-01-bootstrap-and-probe.md`
- `phase-01a-runtime-download-script.md`
- `phase-02-demux-and-metadata.md`
- `phase-03-video-decode.md`
- `phase-04-audio-decode.md`
- `phase-05-playback-session.md`
- `phase-06-sync-and-seeking.md`
- `phase-07-avalonia-adapter.md`
- `phase-08-polish-and-diagnostics.md`
- `phase-09-acceleration-and-presenters.md`
