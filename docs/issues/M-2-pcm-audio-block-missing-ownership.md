# M-2: ADR-0012 Drift — PcmAudioBuffer has no ownership semantics

**Severity:** Must Fix
**Status:** Open
**Responsible Agent:** Media Contracts Agent
**Detected:** 2026-03-29
**Phase Gate:** Must resolve before Phase 04 (audio decode)

## Problem

`PcmAudioBuffer` is a `sealed record` holding `ReadOnlyMemory<short> Samples` with no `IDisposable` implementation. ADR-0012 specifies `IMemoryOwner<short> RentAudioBuffer(int sampleCount, int channelCount)` for the frame buffer pool, implying pooled audio buffers that must be returned via disposal.

`CpuVideoFrame` correctly follows this pattern with `IMemoryOwner<byte> PixelData` and `IDisposable`. `PcmAudioBuffer` diverges.

## ADR Context

ADR-0012 describes the memory management strategy for both video and audio buffers. The audio side cannot use the pool abstraction as currently implemented.

## Recommended Fix

Either:

1. **(Preferred)** Change `PcmAudioBuffer` to hold `IMemoryOwner<short>` and implement `IDisposable`, matching the `CpuVideoFrame` pattern
2. Document a deliberate decision that audio blocks use simple allocation (no pooling) and update ADR-0012 to reflect this
