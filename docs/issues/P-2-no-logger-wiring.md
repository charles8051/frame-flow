# P-2: ILogger not wired into FrameFlowBootstrapper

**Severity:** Fix Before Phase
**Status:** Open
**Responsible Agent:** Native Bootstrap Agent
**Detected:** 2026-03-29
**Phase Gate:** Must resolve before Phase 01 (FFmpeg binding)

## Problem

No `ILogger<T>` usage exists in any source project. ADR-0010 explicitly warns that deferring logging to Phase 08 causes painful retrofits. The bootstrapper is the first code that will do real initialization work in Phase 01.

## Recommended Fix

Add `ILogger<FrameFlowBootstrapper>` to the bootstrapper constructor now. No log calls needed yet — just the wiring, so Phase 01 implementers have the logger available from the start. The DI registration work in Phase 00b should register the logger correctly.
