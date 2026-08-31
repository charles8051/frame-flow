# P-3: Builder uses Func<IAudioSink> but IAudioSinkFactory exists

**Severity:** Fix Before Phase
**Status:** Open
**Responsible Agent:** API Steward
**Detected:** 2026-03-29
**Phase Gate:** Should resolve before Phase 00b (DI registration)

## Problem

`FrameFlowBuilder.UseAudioSinkFactory` accepts `Func<IAudioSink>` while a dedicated `IAudioSinkFactory` interface already exists in `FrameFlow.Audio`. Consumers have two mental models for the same concern.

## Recommended Fix

Decide before DI registration which shape is canonical:

1. `UseAudioSinkFactory` accepts `IAudioSinkFactory` instead of `Func<IAudioSink>`
2. Or document explicitly that `Func<IAudioSink>` is the builder-path shape and `IAudioSinkFactory` is the DI-path shape, making the distinction intentional

Same applies to `UseVideoPresenterFactory`.
