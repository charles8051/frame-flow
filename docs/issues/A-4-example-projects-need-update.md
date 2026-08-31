# A-4: Example Projects Need Update After Phase 00-07 Implementation

**Severity:** Should Fix Soon
**Status:** Resolved
**Resolved:** 2026-08-24
**Responsible Agent:** Documentation / Samples Agent
**Detected:** 2026-03-29
**Phase Gate:** Phase 08

## Problem

The example projects under `examples/` were written as aspirational "dream code" during Phase 00. After the structural refactor and DI implementation:

1. Namespace changes: `FrameFlowBuilder` is now in `FrameFlow` namespace (was `FrameFlow.Playback`)
2. `FrameFlowApplication` no longer has a `Bootstrapper` property
3. `FrameFlowOptions` no longer has a `Native` property
4. DI registration is now available via `AddFrameFlow()` — the hosted service example should use it
5. `AddFrameFlowOpenAlAudio()` now works on `IFrameFlowBuilder` (DI path) not just `FrameFlowBuilder`

## Recommended Fix

Update all example `Program.cs` files to:
- Use the new namespace imports
- Use `AddFrameFlow()` DI registration where applicable
- Remove references to removed properties
- Add `AddFrameFlowNative()` and `AddHostedBootstrap()` in the hosted service example

## Resolution

The examples were not patched — they were rewritten against the surface that
exists now. `examples/` holds 11 runnable apps plus the shared
`FrameFlow.Examples.Common` library, split across the three construction
surfaces documented in `README.md` (`MediaPlayer.CreateAsync`,
`FrameFlowPlayer.Open(...)`, `PlaybackController.Create`).
`FrameFlow.Examples.HostedServicePlayer` is the reference for the DI path and
uses `AddFrameFlow()` / `AddFrameFlowOpenAlAudio()` / `AddHostedBootstrap()`.

The removed properties named in this report (`FrameFlowApplication.Bootstrapper`,
`FrameFlowOptions.Native`) no longer appear anywhere in the tree.
