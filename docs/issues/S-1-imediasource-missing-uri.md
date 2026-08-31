# S-1: IMediaSource missing Uri property

**Severity:** Should Fix Soon
**Status:** Open
**Responsible Agent:** Media Contracts Agent
**Detected:** 2026-03-29
**Phase Gate:** Must resolve before Phase 02 (demuxing)

## Problem

`IMediaSource` exposes only `DisplayName` and `IsSeekable`. `MediaSource` adds `Uri` and `FilePath` but these are not on the interface. `IDemuxSessionFactory.OpenAsync(IMediaSource)` cannot access the URI or file path through the interface — a real implementation would need to downcast to `MediaSource`.

## Recommended Fix

Add at least `Uri Uri { get; }` to `IMediaSource`. A demux session needs a URI or path to open — the interface must carry it.
