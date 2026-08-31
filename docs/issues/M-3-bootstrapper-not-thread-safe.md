# M-3: FrameFlowBootstrapper.Initialize() is not thread-safe

**Severity:** Must Fix
**Status:** Open
**Responsible Agent:** Native Bootstrap Agent
**Detected:** 2026-03-29
**Phase Gate:** Must resolve before Phase 01 (FFmpeg binding)

## Problem

`FrameFlowBootstrapper.Initialize()` reads `IsInitialized`, then sets it, with no synchronization. Two concurrent callers can both pass the `if (IsInitialized)` check and both execute the initialization body. The existing test `Initialize_ConcurrentCalls_AllReturnSuccess` asserts thread-safety behavior the implementation cannot guarantee.

## Recommended Fix

Use `Interlocked.CompareExchange` or a `lock` around the initialization check-and-set. The test already asserts the correct behavior — the implementation needs to match.
