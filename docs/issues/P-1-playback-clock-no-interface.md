# P-1: PlaybackClock has no testability seam

**Severity:** Fix Before Phase
**Status:** Open
**Responsible Agent:** Playback Orchestration Agent + Testing Validation Agent
**Detected:** 2026-03-29
**Phase Gate:** Must resolve before Phase 05 (playback session)

## Problem

`PlaybackClock` uses `DateTimeOffset.UtcNow` directly with no injectable time source. ADR-0007 requires deterministic seams for timing behavior. Without an `IPlaybackClock` or injectable `Func<DateTimeOffset>`, clock tests depend on real wall time and are unreliable.

## Recommended Fix

1. Add a `Func<DateTimeOffset>? getUtcNow = null` parameter to the `PlaybackClock` constructor, defaulting to `() => DateTimeOffset.UtcNow`
2. Define `IPlaybackClock` interface so `PlaybackSession` can accept a fake for testing
3. Create a `FakeClock` test double that advances only when explicitly told to
