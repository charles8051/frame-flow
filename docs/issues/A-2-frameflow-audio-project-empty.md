# A-2: FrameFlow.Audio Project Is Empty After Contract Migration

**Severity:** Housekeeping
**Status:** Resolved
**Resolved:** 2026-08-24
**Responsible Agent:** Architecture Hawk
**Detected:** 2026-03-29
**Phase Gate:** Phase 08

## Problem

After the M-1 structural refactor, all audio contracts (`IAudioSink`, `IAudioSinkFactory`, `AudioSinkCapabilities`) were moved to `FrameFlow.Media`. The `FrameFlow.Audio` project no longer contains any source files — only build artifacts remain.

## Options

1. **Keep as placeholder** — `FrameFlow.Audio` could host future audio utility implementations (resampling helpers, format converters, audio processing)
2. **Remove** — if all audio-related types will live in either `FrameFlow.Media` (contracts) or `FrameFlow.Audio.OpenAL` (implementation), the intermediate project adds no value

## Recommendation

Keep the project for now. Evaluate during Phase 08 polish whether it serves a purpose.

## Resolution

Option 1. `FrameFlow.Audio` now holds the audio processing that is neither a
shared contract nor an OpenAL detail: `IAudioResampler`,
`FfmpegAudioResampler`, and `AudioOperators`. The project earns its place; no
further action.
