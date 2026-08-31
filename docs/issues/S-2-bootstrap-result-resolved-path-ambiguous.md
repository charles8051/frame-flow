# S-2: FrameFlowBootstrapResult.ResolvedPath semantics ambiguous

**Severity:** Should Fix Soon
**Status:** Open
**Responsible Agent:** Native Bootstrap Agent + API Steward
**Detected:** 2026-03-29
**Phase Gate:** Must resolve before Phase 01 (FFmpeg binding)

## Problem

`ResolvedPath` is set to `_options.CustomFfmpegPath` regardless of which binary source was selected. When `UseBundledBinaries` is true and `CustomFfmpegPath` is null, the result reports `ResolvedPath: null` — but it's unclear whether null means "no custom path configured" or "probing did not find a path."

## Recommended Fix

Add XML documentation to `ResolvedPath` clarifying what the field means for each `FfmpegBinarySource` variant. When real probing lands in Phase 01, the field should hold the actually resolved path.
