# ADR-0017: Struct Field Access via Generated Bindings (Amendment to ADR-0011)

## Status

Proposed

## Context

ADR-0011 chose hand-written `[LibraryImport]` declarations for all FFmpeg interop, rejecting third-party binding libraries (FFmpeg.AutoGen, Sdcb.FFmpeg) on the grounds that they "bring the entire FFmpeg API surface, may lag behind FFmpeg releases, and introduce external dependency management concerns."

That decision was sound for **function calls**. P/Invoke declarations for functions like `avformat_open_input`, `avcodec_send_packet`, and `swr_init` are stable ABI contracts — the function signature doesn't change within a major FFmpeg version, the declaration is a single line, and `[LibraryImport]` source generation produces efficient, trimmer-friendly marshalling code.

However, ADR-0011 did not distinguish between function calls and **struct field access**. In practice, FrameFlow reads fields from FFmpeg C structs (`AVFrame`, `AVFormatContext`, `AVCodecParameters`, `AVPacket`, `AVStream`) using raw pointer arithmetic with hardcoded byte offsets:

```csharp
int nbChannels = *(int*)((byte*)framePtr + 292);   // AVFrame.ch_layout.nb_channels
int sampleRate = *(int*)((byte*)framePtr + 192);    // AVFrame.sample_rate
int codecId    = *(int*)((byte*)codecParPtr + 4);    // AVCodecParameters.codec_id
```

These offsets are maintained in `NativeStructOffsets.cs` (currently ~50 constants) and were derived through a combination of header inspection and empirical probing against FFmpeg 7.1 win-x64 DLLs.

### The problem this caused

Investigation 001 documented **three critical bugs** caused by incorrect struct offsets:

1. **`AVFrame.ch_layout.nb_channels` at +204 was actually +292** (Bug 6). Fields added in FFmpeg 6.x/7.x (flags, duration, best_effort_timestamp) shifted `ch_layout` by 88 bytes. This caused the audio resampler to read 638 channels instead of 2.

2. **`AVFormatContext.duration` at +64 was actually +104** (fixed in commit `b51eeda`). Multiple fields between `streams` and `duration` were not accounted for.

3. **`AVCodecParameters.width` at +56 was actually +72** (fixed in commit `b51eeda`). The `framerate` AVRational field added in FFmpeg 6.x was not accounted for.

Each of these produced silent data corruption (wrong metadata, wrong channel counts) rather than clean failures, making them difficult to diagnose. The investigation required writing empirical probing tests that scanned memory ranges to find the correct values — a process that took significant debugging time per offset.

### Why this will recur

FFmpeg struct layouts are **not ABI-stable** across major versions. When FrameFlow upgrades from FFmpeg 7.x to 8.x, every offset in `NativeStructOffsets.cs` must be re-verified. The structs in question (`AVFrame` alone has 40+ fields) are large and have historically gained new fields in every major release. The risk is not hypothetical — it already happened within the 7.x series due to fields added in the 6.x→7.x transition.

FFmpeg does not publish struct layouts in any machine-readable format. The only authoritative source is the C header files, which require manual computation of offsets accounting for platform-specific alignment and padding rules.

### What generated binding libraries provide

Libraries like FFmpeg.AutoGen use `libclang` to parse the FFmpeg C headers and generate C# struct definitions with correct field offsets:

```csharp
// FFmpeg.AutoGen generates this from frame.h:
public struct AVFrame
{
    // ... 40+ fields with correct layout ...
    public AVChannelLayout ch_layout;
    // ...
}

// Usage: frame.ch_layout.nb_channels (type-safe, no offset math)
```

This eliminates the entire class of offset bugs because the struct layout is derived from the same headers the C compiler uses. When FFmpeg releases a new major version, regenerating the bindings produces the updated offsets automatically.

## Decision

### Amend ADR-0011: use generated struct types, keep hand-written function declarations

The interop approach is split into two categories with different strategies:

| Category | Approach | Rationale |
|----------|----------|-----------|
| **Function calls** | Hand-written `[LibraryImport]` (unchanged from ADR-0011) | Stable ABI, minimal surface, trimmer-friendly, no external dependency needed |
| **Struct field access** | Generated types from FFmpeg.AutoGen or equivalent | Correct-by-construction offsets, eliminates the #1 source of interop bugs |

### Concrete changes

1. **Add `FFmpeg.AutoGen` (or `Sdcb.FFmpeg`) as a dependency of `FrameFlow.Native` only.** The generated types are `internal` and do not leak into higher layers (ADR-0005 preserved).

2. **Replace `NativeStructOffsets.cs` pointer arithmetic** with typed struct access. For example:

   ```csharp
   // Before (fragile):
   int sampleRate = *(int*)((byte*)framePtr + 192);

   // After (correct-by-construction):
   ref AVFrame frame = ref Unsafe.AsRef<AVFrame>((void*)framePtr);
   int sampleRate = frame.sample_rate;
   ```

3. **Keep hand-written `[LibraryImport]` for function calls.** The existing declarations in `FFAvFormat`, `FFAvCodec`, `FFAvUtil`, `FFSwScale`, `FFSwResample` remain unchanged. They are correct, minimal, and benefit from source generation.

4. **Remove `NativeStructOffsets.cs`** once all struct access is migrated. The file becomes unnecessary when typed structs are available.

5. **Pin the FFmpeg.AutoGen version to match the target FFmpeg major version.** When FrameFlow upgrades FFmpeg targets (e.g., 7.x → 8.x), update the FFmpeg.AutoGen package version correspondingly. This is no different from the existing requirement to update interop declarations — but it's automated rather than manual.

### What this does NOT change

- The ADR-0005 native boundary is preserved. Generated struct types stay in `FrameFlow.Native` and `FrameFlow.Decoding`. Higher layers still consume managed contracts only.
- The ADR-0011 function-call approach is preserved. Only struct access changes.
- The `SafeHandle` types (`FormatContextHandle`, `CodecContextHandle`, etc.) remain. Generated structs complement them; they don't replace resource ownership patterns.
- `FrameFlow.Native` remains the sole project with native interop. No new projects gain FFmpeg dependencies.

## Consequences

### Positive

- Eliminates the entire class of struct offset bugs that caused 3 critical failures in Investigation 001
- Struct field access becomes type-safe and self-documenting (`frame.ch_layout.nb_channels` vs `*(int*)((byte*)frame + 292)`)
- FFmpeg major version upgrades become a package version bump rather than manual offset re-verification
- Removes ~50 hand-maintained offset constants and their associated XML documentation
- Reduces the surface area that the FFmpeg Expert Agent must audit on each change

### Negative

- Adds one external NuGet dependency to `FrameFlow.Native`
- The generated binding package may lag behind FFmpeg point releases by days/weeks (mitigated: FrameFlow pins to a major version, not a point release)
- The generated types include the full FFmpeg struct surface even though FrameFlow uses a small subset (mitigated: types are `internal`, unused types are trimmed, no runtime cost)
- Developers must understand that function calls use `[LibraryImport]` while struct access uses generated types — two patterns in one interop layer

## Alternatives considered

### Keep hand-written offsets, add regression tests

Rejected. The probing tests (`FrameOffsetProbe`) catch offset bugs after the fact, but each bug still requires manual investigation to find the correct offset. The probing approach treats the symptom (detecting wrong values) rather than the cause (computing offsets manually). It also requires FFmpeg DLLs to be present in CI, which the generated-struct approach does not.

### Generate our own struct bindings from FFmpeg headers

Rejected for v1. Building a `libclang`-based generator is significant toolchain work. FFmpeg.AutoGen already does this well and is actively maintained. If the external dependency becomes unmaintained, forking or building a generator becomes the fallback — but there's no reason to start there.

### Use `Marshal.OffsetOf` at runtime

Rejected. `Marshal.OffsetOf` works for .NET struct types, not for native C structs. It cannot determine the layout of an FFmpeg struct at runtime without the struct type already being correctly defined — which is the problem we're trying to solve.

### Replace all interop with FFmpeg.AutoGen (functions too)

Rejected. FFmpeg.AutoGen uses `[DllImport]` (not `[LibraryImport]`) for function declarations, which loses source generation benefits, is not trimmer-friendly, and uses less efficient marshalling for patterns like UTF-8 strings. The hybrid approach (generated structs + hand-written `[LibraryImport]` functions) takes the best of both worlds.
