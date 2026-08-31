# Investigation 001: End-to-End Integration Test Findings

**Date:** 2026-03-29
**Investigator:** Master Coordinator + Architecture Hawk review pending
**Trigger:** User attempted to play corpus files through example programs and encountered cascading failures

## Executive Summary

Building a comprehensive end-to-end integration test suite (`FrameFlow.Integration.Tests`) that exercises every corpus file through the full pipeline exposed five distinct bugs across three layers. Two are fixed, one is partially fixed, and two remain open. The investigation demonstrates that the unit test suite (527 tests, all passing with fakes) provided no coverage for actual FFmpeg interop correctness.

## Test Architecture

The integration tests are organized in tiers, each building on the previous:

| Tier | What it tests | Files | Status |
|------|--------------|-------|--------|
| Bootstrap | FFmpeg loads, reports version | 1 | Pass |
| Demux | Every corpus file opens, metadata valid | 22/22 | Pass |
| Read packets | Every file produces ≥1 packet | 22/22 | Pass |
| Video decode | Every video file decodes ≥1 frame | 16/16 | Pass |
| Audio decode (via session) | Every audio file decodes ≥1 block | 0/11 | **Fail** |
| Full playback session | Every file plays without faulting | 10/22 | **Partial** |

All 10 video-only files pass the full playback session test. All 12 files with audio fail.

---

## Bug 1: FFmpeg Binary Resolution — Empty `runtimes/` Scaffold

**Layer:** `FrameFlow.Native` — `FrameFlowBootstrapper.ResolveBundledPath()`
**Severity:** Blocking — no example could run
**Status:** Fixed

### Problem

The bootstrapper searched for FFmpeg DLLs in `{AppContext.BaseDirectory}/runtimes/{rid}/native/`. The .NET build system creates this directory structure as an empty scaffold in every project's output. `Directory.Exists()` returned `true` for the empty directory, so the bootstrapper accepted it and failed to find any DLLs.

The actual DLLs live in the **repo root's** `runtimes/win-x64/native/` (placed by `scripts/fetch-ffmpeg.cs`).

### Fix

**`Directory.Build.targets`** (new file): Added an MSBuild targets file at the repo root that detects the current RID, finds FFmpeg DLLs in the repo-root `runtimes/` directory, and copies them to every project's output via `CopyToOutputDirectory="PreserveNewest"`. This mirrors the `.targets` file that the NuGet runtime package will provide in production (ADR-0014).

**`FrameFlowBootstrapper.ResolveBundledPath()`**: Simplified back to checking `AppContext.BaseDirectory` only — no tree-walking. The DLLs now appear in the output directory at build time.

### Architectural Notes

The initial workaround walked up the directory tree from `AppContext.BaseDirectory` to find the repo root. This was rejected in favor of the MSBuild targets approach because ADR-0014 explicitly specifies that native binaries should be copied to the output directory by the build system, not discovered at runtime by walking the filesystem.

---

## Bug 2: `avcodec_get_name` String Marshaling Crash

**Layer:** `FrameFlow.Native.Interop` — `FFAvCodec`
**Severity:** Critical — native process abort (0xC0000005)
**Status:** Fixed (by background agent in commit `b51eeda`)

### Problem

`FFAvCodec.avcodec_get_name` was declared with `[return: MarshalAs(UnmanagedType.LPUTF8Str)]`. With `[LibraryImport]` source generation, this generated code that called `CoTaskMemFree` on the returned string pointer. However, `avcodec_get_name` returns a pointer to a **statically-allocated** FFmpeg string — it must not be freed. The `CoTaskMemFree` call corrupted the FFmpeg heap and caused an immediate native abort.

### Fix

Changed the P/Invoke to return `nint`, then wrapped it in a managed method that calls `Marshal.PtrToStringUTF8(ptr)` — reads the string without freeing the pointer.

### Architectural Notes

This is a systemic risk with `[LibraryImport]` and `[return: MarshalAs(UnmanagedType.LPUTF8Str)]`. Any FFmpeg function that returns a `const char*` to static or internally-managed memory must NOT use managed string marshaling. The FFmpeg Expert Agent should audit all string-returning P/Invoke declarations for this pattern.

---

## Bug 3: `AVERROR_EOF` Constant Value Incorrect

**Layer:** `FrameFlow.Native.Interop` — `FFAvUtil_Phase03`
**Severity:** High — all end-of-file handling broken
**Status:** Fixed (by background agent in commit `b51eeda`)

### Problem

`FFAvUtil.AvErrorEof` was defined as `unchecked((int)0xBFB5B0BB)`. The correct value, derived from `FFERRTAG('E','O','F',' ')`, is `0xDFB9B0BB` (decimal `-541478725`).

This caused `av_read_frame` returning `AVERROR_EOF` at end-of-stream to be treated as an unknown error rather than normal termination, throwing `InvalidOperationException` instead of returning `null`.

### Fix

Updated the constant to the correct value with an explanatory derivation comment.

### Architectural Notes

FFmpeg error constants are defined as macro expressions (`FFERRTAG`) that are not trivially portable to C#. A regression test in `BootstrapDiagnosticTests` now validates key constants against empirically observed FFmpeg behavior.

---

## Bug 4: PlaybackSession Missing Demux Pump

**Layer:** `FrameFlow.Playback` — `PlaybackSession`
**Severity:** Critical — no real playback possible
**Status:** Fixed (video), partially fixed (audio)

### Problem

The `PlaybackSession` called `_videoDecoder.DecodeAsync(CancellationToken)` — the parameterless `IVideoDecoder` interface method. The real `VideoDecoder` threw `NotSupportedException` because its decode loop needed a format context pointer (it self-feeds from `av_read_frame`).

For audio, `_audioDecoder.DecodeAsync(CancellationToken)` read from an internal packet queue (`Channel<T>`), but nothing ever wrote packets to that queue. The session had no demux pump to route packets from the demux layer to the decoders.

### Root Cause

The `PlaybackSession` was built with fake decoders (149 unit tests, all passing) that implement the parameterless `DecodeAsync()` trivially. The real decoders have a fundamentally different packet-feeding architecture:

- `VideoDecoder` has a self-feeding overload `DecodeAsync(nint formatContextPtr, int streamIndex, CancellationToken)` that calls `av_read_frame` itself
- `AudioDecoder` expects packets fed via `SendPacketAsync(nint packetPtr)` from an external producer

Neither architecture matches the session's assumption that decoders self-feed from their parameterless `DecodeAsync()` method.

### Fix

**`DecodingPipeline`** (new class in `FrameFlow.Decoding`): A central demux pump that owns the `av_read_frame` loop. It reads packets from the native format context, clones each packet via `av_packet_ref`, and routes clones to the appropriate decoder queue by stream index. This satisfies ADR-0009's requirement for a single demux loop with bounded queues.

**`VideoDecoder`**: Added `SendPacketAsync`/`FlushAsync`/`CompletePacketQueue` queue API (matching `AudioDecoder`). The parameterless `DecodeAsync()` now drains this queue instead of throwing.

**`PlaybackSession`**: Creates a `DecodingPipeline` during `OpenAsync` when concrete decoders are present. `StartWorkersAsync` launches the demux pump task after decoder consumer loops are started.

**Packet lifetime**: The demux pump allocates fresh packets via `av_packet_alloc` + `av_packet_ref` for each queued item. Decoders free them after `avcodec_send_packet` via `av_packet_free`. This avoids the use-after-free crash (0xC0000005) that occurred when the pump's reusable packet was unref'd before the decoder consumed it.

### Architectural Notes

The `DecodingPipeline` lives in `FrameFlow.Decoding` (not `FrameFlow.Playback`) because it needs native interop access to `av_read_frame`, `av_packet_alloc`, etc. The `PlaybackSession` creates and owns the pipeline instance but delegates all native packet routing to it. This preserves the ADR-0005 boundary: native pointers stay within Decoding/Native layers.

The `PlaybackSession` downcasts `IDemuxSession` to `DemuxSession` and `IVideoDecoder`/`IAudioDecoder` to their concrete types to create the pipeline. This is a design tension: the session works with interfaces (good for testing) but needs concrete types for native wiring. The cast is guarded and falls back gracefully — when fakes are provided (as in unit tests), no pipeline is created and the session uses the interface-level `DecodeAsync()` which fakes implement directly.

---

## Bug 5: AudioDecoder SWR Resampler Init Failure

**Layer:** `FrameFlow.Decoding` — `AudioDecoder` constructor
**Severity:** Critical — all audio playback broken
**Status:** Open (deferred SWR init is implemented but still failing for some files)

### Problem

The `AudioDecoder` constructor originally read `AVCodecParameters.format` at struct offset +28 to determine the source sample format, then configured the SWR resampler with it. For many codecs (AAC, MP3, Opus, FLAC), the demuxer does not populate this field — it remains 0 (`AV_SAMPLE_FMT_U8`). Passing `AV_SAMPLE_FMT_U8` to `swr_init` produces `EINVAL (-22)` because the resampler rejects an input format of unsigned 8-bit PCM when the codec actually produces float planar output.

### Partial Fix

The deferred SWR initialization approach was implemented: the constructor allocates the `SwrContext` but does not configure or init it. On the first decoded frame, `InitializeSwrFromFrame` reads `AVFrame.format` at offset +116 (always correctly populated by the decoder) and then configures + inits SWR.

The channel layout is configured via `in_channel_count`/`out_channel_count` integer options rather than the deprecated `in_channel_layout` mask API.

### Remaining Issue

Files with audio still fail at the `PlaybackSession` level. The failures are at `OpenAsync` time (immediate, ~60ms) suggesting the AudioDecoder constructor itself still throws during codec open or frame allocation. Further investigation needed:

1. Verify that `avcodec_find_decoder` succeeds for all audio codec IDs in the corpus
2. Verify that `avcodec_parameters_to_context` and `avcodec_open2` succeed
3. Check if the `AVCodecParameters.codec_id` offset (+4) is correct for audio streams
4. Test each audio file individually with detailed error logging

### Architectural Notes

The deferred SWR init is architecturally correct: frame-level format is the ground truth, not codec params. However, the FFmpeg Expert Agent should review the full AudioDecoder constructor flow for offset correctness on FFmpeg 7.x, particularly the `NativeStructOffsets.CodecParSampleRate` and `NativeStructOffsets.CodecParNbChannels` offsets which have been a recurring source of bugs due to FFmpeg struct evolution across major versions.

---

## Recommendations

1. **FFmpeg Expert review**: All `NativeStructOffsets` values should be validated against the FFmpeg 7.1 headers (not empirically guessed). A deterministic test should compare offset-read values against `ffprobe` output for known corpus files.

2. **String marshaling audit**: All `[LibraryImport]` declarations that return strings should be reviewed for the `CoTaskMemFree` issue (Bug 2).

3. **Integration tests in CI**: The `FrameFlow.Integration.Tests` project should run in CI with the corpus files and FFmpeg DLLs present. The `[SkipIfNoFfmpeg]` attributes ensure graceful degradation when FFmpeg is unavailable.

4. **Audio decoder isolation test**: Add a standalone audio decode test (outside PlaybackSession) that directly exercises the AudioDecoder with packets from the demux layer, to isolate whether the bug is in the decoder or the session wiring.

5. **Packet lifetime documentation**: The `av_packet_ref`/`av_packet_free` ownership transfer between DecodingPipeline and decoders should be documented as a formal ownership contract, similar to the frame ownership contract in ADR-0012.

---

## Bug 6: `AVFrame.ch_layout.nb_channels` Offset Wrong

**Layer:** `FrameFlow.Native.Interop` — `NativeStructOffsets.FrameNbChannels`
**Severity:** Critical — caused SWR to read 638/361 channels instead of 2
**Status:** Fixed

### Problem

`FrameNbChannels` was defined as +204 (based on `ch_layout` at +200 and `nb_channels` at +4 within the struct). Empirical probing of a decoded AAC frame showed `nb_channels=2` at **offset +292**, not +204. The +200 starting offset for `ch_layout` was wrong — additional fields between `sample_rate` (+192) and `ch_layout` in FFmpeg 7.x (flags, duration, best_effort_timestamp, etc.) pushed `ch_layout` to +288.

### Fix

Updated `NativeStructOffsets.FrameNbChannels` from 204 to **292**. Verified empirically by scanning offset +120 through +400 of a decoded stereo AAC frame — value 2 was found only at +292.

### Architectural Notes

This is the third empirically-discovered offset bug. The FFmpeg struct layout for `AVFrame` in version 7.x has diverged significantly from the documented offsets in earlier versions. A probe-based regression test (`FrameOffsetProbe`) has been added to `FrameFlow.Decoding.Tests` to detect future offset drift.

---

## Bug 7: SWR Channel Layout API Deprecated in FFmpeg 7.x

**Layer:** `FrameFlow.Decoding` — `AudioDecoder.InitializeSwrFromFrame`
**Severity:** Critical — `swr_init` returned EINVAL (-22) for all audio
**Status:** Fixed

### Problem

The SWR resampler was configured using the legacy `in_channel_layout` / `out_channel_layout` mask API (e.g., `av_opt_set_int(swrPtr, "in_channel_layout", 0x3, 0)` for stereo). In FFmpeg 7.x, this API is fully deprecated. `swr_init` rejects configurations that use the mask API, returning EINVAL (-22) even when all other parameters are correct.

An intermediate fix attempted `in_channel_count` / `out_channel_count` integer options, but FFmpeg 7.x also rejected this with `Input channel layout "" is invalid`.

### Fix

Replaced with the FFmpeg 7.x string-based channel layout API:
```csharp
FFAvUtil.av_opt_set(swrPtr, "in_chlayout", "stereo", 0);
FFAvUtil.av_opt_set(swrPtr, "out_chlayout", "stereo", 0);
```

Added `av_opt_set` (string version) P/Invoke to `FFAvUtil_Phase03`. Channel counts are mapped to standard layout names ("mono", "stereo", "5.1", "7.1") or the generic "Nc" format.

### Architectural Notes

This confirms that FFmpeg 7.x has fully removed support for the legacy channel layout mask API in the SWR resampler. All SWR configuration in FrameFlow must use `av_opt_set` with string layout names. The FFmpeg Expert Agent should audit all `in_channel_layout` / `out_channel_layout` references.

---

## Final Results After All Fixes

| Tier | Tests | Status |
|------|-------|--------|
| Bootstrap | 1 | Pass |
| Demux (all corpus files) | 22 | Pass |
| Read packets (all files) | 22 | Pass |
| Video decode (video files) | 16 | Pass |
| Audio decode via session (audio files) | 11 | Pass |
| Full playback session (all files) | 22 | Pass |
| Audio decoder diagnostic | 15 | Pass |
| **Total integration tests** | **109** | **All pass** |
| **Total unit tests** | **504** | **All pass** |
| **Grand total** | **613** | **All pass** |
