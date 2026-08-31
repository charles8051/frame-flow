# S-4: IFrameFlowBootstrapper should live in FrameFlow.Media

**Severity:** Should Fix Soon
**Status:** Open
**Responsible Agent:** Architecture Hawk + Media Contracts Agent
**Detected:** 2026-03-29
**Phase Gate:** Should resolve before Phase 01

## Problem

`IFrameFlowBootstrapper` lives in `FrameFlow.Native`, forcing any layer that consumes the bootstrapper contract to reference `Native`. This prevents the Playback layer from being tested in isolation — you can't fake the bootstrapper without pulling in the native project.

## Recommended Fix

Move `IFrameFlowBootstrapper` and `FrameFlowBootstrapResult` to `FrameFlow.Media`. The concrete `FrameFlowBootstrapper` stays in `FrameFlow.Native`. Higher layers depend on the interface via `Media`, and tests can fake it without a `Native` reference.
