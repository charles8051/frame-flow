# S-5: Orphaned tests/FrameFlow.Tests/ directory

**Severity:** Should Fix Soon
**Status:** Open
**Responsible Agent:** Integration Review Agent
**Detected:** 2026-03-29
**Phase Gate:** Clean up soon

## Problem

`tests/FrameFlow.Tests/` contains only `bin/` and `obj/` build artifacts with no `.csproj` or source files. It is not listed in `FrameFlow.slnx`. This misleads contributors into thinking a second test project exists.

## Recommended Fix

Delete `tests/FrameFlow.Tests/` entirely. If a cross-project integration test host is needed later, create it properly with a `.csproj` and add it to the solution.
