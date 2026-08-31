# Investigation 002: ExecutionEngineException — Native FFmpeg Crash on Second File

**Date:** 2026-03-29  
**Branch:** `strategy/claude-code-subagents.3`  
**Trigger:** Running the SDL corpus runner across multiple files caused a fatal CLR crash (`System.ExecutionEngineException`) in `FrameFlow.Native.dll` during the second or later file's playback.  
**Status:** ✅ Fixed and committed

---

## Executive Summary

After the SDL hang was resolved (Investigation 001 / commit `cf0d91d`) and single-window reuse was added (commit `f289f2b`), running the corpus runner across multiple files produced a fatal CLR crash. The crash was a `System.ExecutionEngineException` — the CLR's last-resort signal for unrecoverable memory corruption from a native call. The crash site was inside `LibraryImports.g.cs` in `FrameFlow.Native.dll`, specifically in the FFmpeg P/Invoke layer, not in SDL.

Two bugs were identified:

| # | Bug | Severity | Layer |
|---|-----|----------|-------|
| 1 | Demux pump task not awaited before decoder disposal | **Critical** | `PlaybackSession.cs` |
| 2 | Queued clone packets not freed on cancellation | **High** | `VideoDecoder.cs`, `AudioDecoder.cs` |

Bug 1 was the primary cause of the crash. Bug 2 is a memory leak that, under race conditions, could also produce a double-free.

---

## Symptoms

- `System.ExecutionEngineException` thrown from inside `FrameFlow.Native.dll`
- Crash occurred reliably on the second (or later) corpus file
- Debug output placed the fault in the FFmpeg source-generated P/Invoke bindings (`LibraryImports.g.cs`)
- The first file always completed successfully; the crash happened during teardown of file N and startup of file N+1
- No stack trace was recoverable — `ExecutionEngineException` is a fatal runtime abort, not a managed exception

---

## Diagnosis

### Step 1: Confirm crash is in FFmpeg, not SDL

The Visual Studio debug output log was inspected. The faulting module was `FrameFlow.Native.dll`, and the faulting instruction was inside the source-generated P/Invoke wrapper for an FFmpeg function. This immediately ruled out the SDL threading issues addressed in the prior investigation.

### Step 2: Trace the AVPacket* ownership chain

The packet lifecycle across the pipeline was traced:

```
DecodingPipeline.RunDemuxPumpAsync
  │
  │  av_packet_alloc()          ← allocates read buffer (owned by pipeline)
  │  av_read_frame(...)         ← fills read buffer
  │
  ├─ av_packet_alloc()          ← allocates clone (ownership → decoder)
  │  av_packet_ref(clone, buf)  ← clones data into clone
  │  VideoDecoder.SendPacketAsync(clone)  → Channel<(nint, bool)>
  │
  ├─ av_packet_alloc()          ← allocates clone (ownership → decoder)
  │  av_packet_ref(clone, buf)
  │  AudioDecoder.SendPacketAsync(clone)  → Channel<(nint, bool)>
  │
  │  av_packet_unref(buf)       ← unrefs read buffer data (buf itself reused)
  │
  └─ [finally] av_packet_free(ref buf)   ← frees read buffer
```

The pipeline only frees the read buffer. Clone ownership transfers to the decoder. The decoders call `av_packet_free` inside their `DecodeAsync` loops — **but only if the packet is actually dequeued and processed**.

### Step 3: Find the teardown ordering bug

`PlaybackSession.StopWorkersAsync` was:

```csharp
private async Task StopWorkersAsync()
{
    _sessionCts?.Cancel();

    var workers = new[] { _videoTask, _audioTask }  // ← _demuxPumpTask missing!
        .Where(t => t is not null)
        .Select(t => t!)
        .ToArray();

    if (workers.Length > 0)
        await Task.WhenAll(workers).ConfigureAwait(false);

    _videoTask = null;
    _audioTask = null;
    _sessionCts?.Dispose();
    _sessionCts = null;
}
```

`_demuxPumpTask` was **not included** in the worker wait set. This created the following race:

```
Thread A (StopWorkersAsync):            Thread B (_demuxPumpTask, still running):
  _sessionCts.Cancel()
  await Task.WhenAll(video, audio)      ...still looping av_read_frame...
  → video and audio tasks complete      ...av_packet_alloc() + av_packet_ref()...
  → StopWorkersAsync returns            ...SendPacketAsync(clone) →
DisposeAsync continues:                    WriteAsync to channel...
  await _decodingPipeline.DisposeAsync()
  await _videoDecoder.DisposeAsync()    ...avcodec_send_packet(freedCtx, ...)
    → _codecCtx.Dispose()              ←  CRASH: native call on freed memory
    → av_codec_context freed
```

The demux pump was still alive and calling into decoders whose `CodecContextHandle` had just been freed. The FFmpeg function (`avcodec_send_packet` or similar) received a dangling pointer → memory corruption → `ExecutionEngineException`.

Similarly, `DisposeAsync`'s worker timeout wait also excluded `_demuxPumpTask`:

```csharp
// Bug: only video and audio tasks, not the pump
var workers = new[] { _videoTask, _audioTask }
    .Where(t => t is not null) ...
```

### Step 4: Find the orphaned packet leak

When the session token is cancelled, `DecodeAsync` in both `VideoDecoder` and `AudioDecoder` exits via `OperationCanceledException` propagated from `ReadAllAsync`. The `VideoDecoder.DecodeAsync` loop was:

```csharp
await foreach (var (packetPtr, isFlush) in _packetQueue.Reader.ReadAllAsync(cancellationToken))
{
    // ...
    try
    {
        await foreach (var frame in DecodePacketAsync(packetPtr, cancellationToken))
            yield return frame;
    }
    finally
    {
        var ptr = packetPtr;
        FFAvCodec.av_packet_free(ref ptr);  // ← only reached if packet was dequeued
    }
}
```

If cancellation fires while packets are sitting in the `Channel<>` — never dequeued by the iterator — those `av_packet_alloc`'d clones are never freed. `DisposeAsync` then freed the `CodecContextHandle` and returned without touching the channel. The native `AVPacket*` allocations leaked. Under certain timing this could also become a double-free if the pump and the decoder both touched the same pointer.

---

## Fixes Applied

### Fix 1: `PlaybackSession.StopWorkersAsync` — await `_demuxPumpTask` first

**File:** `src/FrameFlow.Playback/PlaybackSession.cs`

`_demuxPumpTask` is now awaited **before** the video and audio tasks. This ordering is critical: the pump feeds the decoder queues, so it must stop producing before the decoders are allowed to exit and before any dispose runs.

The pump's `finally` block calls `CompletePacketQueue()` on both decoders, which signals `ReadAllAsync` to stop blocking and lets the decoder loops drain and exit cleanly.

```csharp
private async Task StopWorkersAsync()
{
    _sessionCts?.Cancel();

    // Stop the demux pump first — it feeds the decoder queues.
    // Its finally block calls CompletePacketQueue on both decoders,
    // allowing the decoder loops below to drain and exit cleanly.
    if (_demuxPumpTask is not null)
    {
        try { await _demuxPumpTask.ConfigureAwait(false); }
        catch { /* pump exceptions are benign on cancellation */ }
        _demuxPumpTask = null;
    }

    var workers = new[] { _videoTask, _audioTask }
        .Where(t => t is not null)
        .Select(t => t!)
        .ToArray();

    if (workers.Length > 0)
    {
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch { }
    }

    _videoTask = null;
    _audioTask = null;
    _sessionCts?.Dispose();
    _sessionCts = null;
}
```

`DisposeAsync`'s worker wait array was also corrected to include `_demuxPumpTask`:

```csharp
var workers = new[] { _demuxPumpTask, _videoTask, _audioTask }
    .Where(t => t is not null)
    .Select(t => t!)
    .ToArray();
```

### Fix 2: `VideoDecoder.DisposeAsync` — drain and free queued packets

**File:** `src/FrameFlow.Decoding/VideoDecoder.cs`

After marking `_disposed = true` and completing the channel writer, the reader is now fully drained and each stranded packet pointer is freed:

```csharp
public ValueTask DisposeAsync()
{
    if (_disposed) return ValueTask.CompletedTask;
    _disposed = true;

    _packetQueue.Writer.TryComplete();
    while (_packetQueue.Reader.TryRead(out var item))
    {
        if (!item.isFlush && item.packetPtr != nint.Zero)
        {
            var ptr = item.packetPtr;
            FFAvCodec.av_packet_free(ref ptr);
        }
    }

    _swsCtx?.Dispose();
    _packet.Dispose();
    _frame.Dispose();
    _codecCtx.Dispose();
    return ValueTask.CompletedTask;
}
```

### Fix 3: `AudioDecoder.DisposeAsync` — drain and free queued packets

**File:** `src/FrameFlow.Decoding/AudioDecoder.cs`

Same pattern as VideoDecoder:

```csharp
public ValueTask DisposeAsync()
{
    if (_disposed) return ValueTask.CompletedTask;
    _disposed = true;

    _packetQueue.Writer.TryComplete();
    while (_packetQueue.Reader.TryRead(out var item))
    {
        if (!item.isFlush && item.packetPtr != nint.Zero)
        {
            var ptr = item.packetPtr;
            FFAvCodec.av_packet_free(ref ptr);
        }
    }

    _swrCtx.Dispose();
    _frame.Dispose();
    _packet.Dispose();
    _codecCtx.Dispose();
    return ValueTask.CompletedTask;
}
```

---

## Invariants Established

These invariants now hold across the teardown sequence:

1. **Pump-before-decoders**: `StopWorkersAsync` always awaits the demux pump task before decoder tasks. The pump cannot call into a decoder after the decoder exits.
2. **No stranded AVPacket\* allocations**: Every `av_packet_alloc`'d clone is freed exactly once — either by `DecodeAsync`'s `finally` block (happy path) or by `DisposeAsync`'s drain loop (cancellation path).
3. **No native calls on freed handles**: `CodecContextHandle` is disposed only after all tasks that could invoke `avcodec_send_packet` have completed.

---

## Ownership Contract (AVPacket\* clones)

```
Producer (DecodingPipeline):
  av_packet_alloc() + av_packet_ref()  → clone created, ownership → decoder queue

Consumer happy path (DecodeAsync):
  ReadAllAsync dequeues clone
  avcodec_send_packet(clone)
  finally: av_packet_free(clone)        → freed exactly once ✅

Consumer cancellation path (DisposeAsync drain):
  TryComplete() marks channel done
  TryRead() drains remaining items
  av_packet_free(clone)                 → freed exactly once ✅

Flush sentinel (isFlush = true, packetPtr = nint.Zero):
  No allocation — not freed             ✅
```

---

## Files Changed

| File | Change |
|------|--------|
| `src/FrameFlow.Playback/PlaybackSession.cs` | `StopWorkersAsync`: await `_demuxPumpTask` before video/audio tasks; `DisposeAsync`: include `_demuxPumpTask` in timeout wait |
| `src/FrameFlow.Decoding/VideoDecoder.cs` | `DisposeAsync`: drain `_packetQueue`, free each stranded `nint` packet pointer |
| `src/FrameFlow.Decoding/AudioDecoder.cs` | `DisposeAsync`: drain `_packetQueue`, free each stranded `nint` packet pointer |

---

## Lessons Learned

- **`ExecutionEngineException` in a P/Invoke boundary always means native memory corruption** — double-free, use-after-free, or buffer overrun. The crash site in the stack trace is where corruption was detected, not necessarily where it was introduced.
- **Every `Task` that touches native resources must be in every wait set** — teardown code that awaits "the workers" but misses one task is a latent race condition that only appears under load or with multiple files.
- **Channel-based ownership transfer requires explicit dispose-path cleanup** — when an `IAsyncEnumerable` exits via cancellation rather than normal completion, `finally` blocks on un-dequeued items never run. Any resource transferred via a `Channel<nint>` must be freed defensively in `DisposeAsync`.
- **Ordered teardown matters**: pump → decoders → handles → demux session. Reversing any step in this chain risks a use-after-free.
