# M-1: Boundary Violation — FrameFlow.Audio.OpenAL references FrameFlow.Playback

**Severity:** Must Fix
**Status:** Open
**Responsible Agent:** Architecture Hawk + API Steward
**Detected:** 2026-03-29
**Phase Gate:** Must resolve before Phase 00b (DI registration)

## Problem

`FrameFlow.Audio.OpenAL` has a project reference to `FrameFlow.Playback`. This is an upward dependency from an adapter layer to the orchestration layer. The reference exists because `FrameFlowOpenAlBuilderExtensions` extends `FrameFlowBuilder`, which lives in `FrameFlow.Playback`.

The same pattern exists in `FrameFlow.Avalonia` via `FrameFlowAvaloniaBuilderExtensions`.

## ADR Context

This violates the intended layer hierarchy where adapters depend only on their contract layer (`FrameFlow.Audio`) and the shared domain (`FrameFlow.Media`), not on the orchestration layer.

## Recommended Fix

Move `FrameFlowBuilder` and `FrameFlowApplication` out of `FrameFlow.Playback` into a layer that adapters can reference without creating upward dependencies. Options:

1. **New thin `FrameFlow.Builder` project** — hosts only the builder and application types
2. **Move into `FrameFlow.Media`** — the shared domain layer already has no dependencies

Either option removes the upward reference from both `Audio.OpenAL` and `Avalonia`.
