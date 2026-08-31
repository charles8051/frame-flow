# S-3: FrameFlowOptions.Native couples Playback to Native layer

**Severity:** Should Fix Soon
**Status:** Open
**Responsible Agent:** API Steward + Architecture Hawk
**Detected:** 2026-03-29
**Phase Gate:** Should resolve before Phase 00b (DI registration)

## Problem

`FrameFlowOptions` in `FrameFlow.Playback` has a `Native` property of type `FrameFlowNativeOptions` from `FrameFlow.Native`. This drives the `Playback` -> `Native` project reference and conflates environment bootstrap options with playback session options.

## Recommended Fix

Evaluate whether `FrameFlowOptions` should remain hierarchical (nested sub-options) or flat (independent options configured separately via DI). If hierarchical, document the deliberate choice. If flat, let `FrameFlowNativeOptions` be configured independently in the DI container.

Related to M-1 — if the builder moves to a shared layer, the options structure should be revisited at the same time.
