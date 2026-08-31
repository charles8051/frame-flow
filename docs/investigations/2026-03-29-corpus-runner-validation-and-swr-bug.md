# Corpus Runner Validation Gaps and swr_convert Indirection Bug

**Date:** 2026-03-29
**Scope:** SdlCorpusRunner, AudioDecoder, FFSwResample P/Invoke, test-expectations.json

## Summary

The SdlCorpusRunner was meant to be an end-to-end integration test, but it was not enforcing its own test expectations. Several fields in `test-expectations.json` — duration, resolution, sample rate, channels — were never validated. Worse, playback ran at full decode speed with no real-time pacing, so a 3-second video would complete in ~50ms and be marked OK.

Tightening the validation uncovered a critical P/Invoke bug in `swr_convert` that was silently corrupting all decoded audio output.

## Bug 1: swr_convert pointer indirection error (critical)

**Files:** `FFSwResample.cs`, `AudioDecoder.cs`

### Root cause

The P/Invoke declaration for `swr_convert` declared its input parameter as `ref nint`:

```csharp
internal static partial int swr_convert(
    nint ctx, ref nint output, int out_count, ref nint input, int in_count);
```

The C signature is:

```c
int swr_convert(SwrContext *s, uint8_t **out, int out_count,
                const uint8_t **in,  int in_count);
```

The `input` parameter expects `uint8_t**` — a pointer to the array of per-channel plane pointers. `AVFrame.extended_data` is already this value. The caller was passing:

```csharp
nint inData = (nint)frame.extended_data;  // byte** stored as nint
FFSwResample.swr_convert(swrPtr, ref outBuf, max, ref inData, nbSamples);
```

`ref inData` creates `&inData` (a stack address), which is `byte***` — one level of indirection too many. The native function interprets `in[0]` as the first plane pointer, but it actually reads the `extended_data` value itself (a pointer-to-pointer) instead of `extended_data[0]` (a pointer-to-data).

### Impact by sample format

**Packed/interleaved formats (AAC, MP3, Opus):** Did not crash because `in[0]` pointed to valid memory (the `data[]` array within the AVFrame struct). However, `swr_convert` read pointer bytes as audio samples — **all decoded audio was garbage**. This was invisible because `MeteringAudioSink` only counts samples and never validates content.

**Planar formats (FLAC S32P):** Crashed with a segfault. `in[1]` (the second channel) read from the stack 8 bytes after the local variable, hitting unmapped or random memory.

### Fix

Changed the P/Invoke input parameter from `ref nint` to `nint` and pass `frame.extended_data` directly:

```csharp
// FFSwResample.cs
internal static partial int swr_convert(
    nint ctx, ref nint output, int out_count, nint input, int in_count);

// AudioDecoder.cs — caller
nint inPlanes = (nint)frame.extended_data;
FFSwResample.swr_convert(swrPtr, ref outBuf, maxOutputSamples, inPlanes, nbInputSamples);
```

The output parameter (`ref nint output`) is correct as-is: `outBuf` is a single `byte*`, and `ref outBuf` creates the expected `byte**`.

### Note on the output parameter

The `ref nint` pattern for output works by coincidence — the output is always a single interleaved S16 buffer, so `out[0] = outBuf` is the only plane accessed. If the output were ever changed to a planar format, the same bug would appear on the output side.

## Bug 2: No playback duration validation

**Files:** `Program.cs` (SdlCorpusRunner)

The test expectations defined `durationSeconds` and `durationToleranceMs` for every corpus file, but the validation code never checked them. A 3-second video decoding in 50ms and a 3-second video decoding in 3 seconds both received "OK".

### Root cause

The runner used `NoDelaySyncStrategy` (returns `TimeSpan.Zero` for all delays), so both video and audio ran at full decode speed. The event loop exited as soon as the session reached `Ended` state, which happened immediately after all frames were decoded.

Even switching to `AudioMasterSyncStrategy` alone would not have fixed this, because `MeteringAudioSink.GetPlaybackTime()` returned cumulative decoded duration (jumping instantly to 3s), not wall-clock time. The sync strategy would compute `frame.PTS - 3.0s < 0` → no delay for every frame.

### Fix

Content duration is validated from the decoded data rather than wall-clock time:

1. **Video PTS duration**: computed from `lastPTS - firstPTS + 1/fps`, validated against `durationSeconds ± tolerance`.
2. **Audio sample duration**: computed from `totalSamplesPerChannel / sampleRate`, validated against `durationSeconds ± tolerance`.

The runner keeps `NoDelaySyncStrategy` for fast decode — the correctness signal is that the decoded content spans the expected time range, not that the process takes that long.

## Bug 3: Missing validation checks

**Files:** `Program.cs` (SdlCorpusRunner)

The following expectation fields were defined but never checked:

| Field | Was checked | Now checked |
|-------|------------|-------------|
| `durationSeconds` | No | Yes (audio duration, video PTS duration, wall-clock playback) |
| `durationToleranceMs` | No | Yes (used for all duration comparisons) |
| `width` / `height` | No | Yes (compared against presenter min/max dimensions) |
| `audioSampleRate` | No | Yes (compared against observed output rate) |
| `audioChannels` | No | Yes (compared against observed output channels) |

## Bug 4: Incorrect test expectations

**File:** `test-expectations.json`

**Audio sample rate:** All non-Opus files listed `44100` (the source file rate), but `AudioDecoder` always resamples to `48000 Hz` via `SwrContext`. The metering sink observes the output rate, not the source rate. Fixed to `48000`.

**Mono channel count:** `test-audio-mono-aac.m4a` listed `audioChannels: 1`, but `AudioDecoder` always outputs stereo (2 channels). Fixed to `2`.

**Frame count for test-with-subtitles.mkv:** Listed as `60` but `ffprobe -count_packets` confirms only `58` video packets in the file. The corpus generation script created a shorter video stream than intended. Fixed to `58`.

## Verification

After all fixes, the corpus runner passes 22/22 files. Content duration is validated
from the decoded data (video PTS span, audio sample count) — not wall-clock time.
The runner decodes at full speed (~2 seconds for all 22 files).
