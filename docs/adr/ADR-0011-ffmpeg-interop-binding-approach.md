# ADR-0011: FFmpeg Interop Binding Approach

## Status

Accepted

## Context

ADR-0002 covers how FrameFlow resolves and loads FFmpeg shared libraries at runtime, but it does not address how FFmpeg functions are actually called from managed code once the libraries are loaded.

FFmpeg exposes a large C API surface across several libraries: libavformat, libavcodec, libavutil, libswscale, and libswresample. FrameFlow needs only a subset of these functions for v1: format context operations, codec open and decode, packet and frame allocation and freeing, pixel format conversion, and audio resampling.

ADR-0005 requires that native pointers stay within native-owning layers. The interop approach must support that boundary by making it practical to wrap raw interop in safe, managed abstractions.

The project targets .NET latest with `LangVersion latest`, which means `[LibraryImport]` with source generation is available and preferred over the older `[DllImport]` P/Invoke marshalling approach.

Several community FFmpeg binding libraries exist (FFmpeg.AutoGen, Sdcb.FFmpeg), but they bring the entire FFmpeg API surface, may lag behind FFmpeg releases, and introduce external dependency management concerns for a library that wants tight control over its native boundary.

## Decision

### Hand-written LibraryImport declarations

FrameFlow will use hand-written `[LibraryImport]` declarations with source generation for the FFmpeg functions it actually calls. The project will not generate bindings for the entire FFmpeg API surface and will not depend on third-party binding generators.

```csharp
[LibraryImport("avformat", EntryPoint = "avformat_open_input")]
internal static partial int AvFormatOpenInput(
    out nint formatContext,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
    nint inputFormat,
    nint options);
```

### Organization by FFmpeg library

Interop declarations will be organized into static partial classes named after the FFmpeg library they wrap:

| Static class | FFmpeg library | Responsibility |
|--------------|---------------|----------------|
| `FFAvFormat` | libavformat | Container I/O, demuxing, stream discovery |
| `FFAvCodec` | libavcodec | Codec open, decode, encode |
| `FFAvUtil` | libavutil | Frame/packet alloc, pixel formats, error codes |
| `FFSwScale` | libswscale | Pixel format conversion |
| `FFSwResample` | libswresample | Audio resampling and channel layout conversion |

All interop classes live in `FrameFlow.Native` and are `internal`.

### Safe wrappers with SafeHandle

Each category of native resource will have a corresponding `SafeHandle` subclass that ensures deterministic cleanup:

- `FormatContextHandle` wrapping `AVFormatContext*`
- `CodecContextHandle` wrapping `AVCodecContext*`
- `FrameHandle` wrapping `AVFrame*`
- `PacketHandle` wrapping `AVPacket*`
- `SwsContextHandle` wrapping `SwsContext*`
- `SwrContextHandle` wrapping `SwrContext*`

SafeHandle types enforce that native pointers cannot leak and integrate with the `using`/`IDisposable` pattern naturally. They also participate in the .NET P/Invoke marshalling pipeline correctly, handling ref-counting and preventing premature collection.

### Minimal surface, expanded incrementally

Only functions that FrameFlow actually calls will be declared. The interop surface grows as implementation phases add features:

- Phase 01 (native bootstrap): library loading, version queries
- Phase 02 (demux): format open, stream info, packet read
- Phase 03 (decode): codec find, open, send/receive frame
- Phase 04 (present): sws_scale for pixel conversion
- Phase 05 (audio): swr for resampling

This avoids maintaining unused declarations and keeps the native surface auditable.

### Pinned FFmpeg major version

FrameFlow v1 will target a single FFmpeg major version (7.x at time of writing). The interop layer will document which FFmpeg version it targets and will not attempt runtime adaptation across major versions, which change ABI.

Minor version differences within the same major version are tolerable as long as the functions FrameFlow uses remain ABI-stable.

## Consequences

### Positive

- Source-generated `[LibraryImport]` produces efficient, trimmer-friendly marshalling code
- Hand-written declarations mean every interop call is intentional and auditable
- SafeHandle wrappers enforce deterministic cleanup and prevent native pointer leaks
- Minimal surface reduces maintenance burden and makes the native boundary easy to review
- No external binding library dependency to manage, version-match, or work around

### Negative

- Hand-writing declarations requires reading FFmpeg headers and understanding C types, which is more effort than auto-generated bindings
- Adding new FFmpeg functionality requires adding new declarations manually
- Pinning to a single FFmpeg major version means a new FrameFlow release is needed when FFmpeg ships a new major version
- No coverage of FFmpeg functions that FrameFlow does not use, which could limit extensibility for consumers who want deeper FFmpeg access

## Alternatives considered

### FFmpeg.AutoGen or Sdcb.FFmpeg

Rejected because they bring the entire FFmpeg API surface (thousands of functions), may lag behind FFmpeg releases, and introduce an external dependency that FrameFlow cannot control. They also typically use the older `[DllImport]` approach rather than source-generated `[LibraryImport]`.

### Full auto-generation from FFmpeg headers

Rejected because it produces a massive binding surface that is difficult to audit, review, and maintain. It also requires a build-time code generation step that adds toolchain complexity. FrameFlow's actual usage surface is small enough that hand-written declarations are manageable and preferable.

### Runtime dynamic invocation via NativeLibrary.GetExport

Rejected as the primary approach because it loses compile-time type safety and source generation benefits. However, `NativeLibrary` is already used for library resolution (ADR-0002). The interop declarations build on top of the loaded libraries.

### DllImport instead of LibraryImport

Rejected because `[DllImport]` uses runtime stub generation rather than source generation, is not trimmer-friendly, and has less efficient marshalling for common patterns like UTF-8 strings. Since FrameFlow targets modern .NET, there is no reason to use the legacy approach.
