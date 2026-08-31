# S-6: SdlInstrumentedPresenter.EnsureTexture Missing Null Check

**Severity:** Should Fix Soon
**Status:** Obsolete
**Closed:** 2026-08-24
**Responsible Agent:** SDL Presenter Agent
**Detected:** 2026-03-30
**Phase Gate:** Phase 08

## Problem

In `examples/FrameFlow.Examples.SdlCorpusRunner/Program.cs`, `SdlInstrumentedPresenter.EnsureTexture` unconditionally assigns `_textureWidth` and `_textureHeight` after calling `SDL_CreateTexture`, without checking whether the returned pointer is null:

```csharp
_texture = _sdl.CreateTexture(_renderer, (uint)PixelFormatEnum.Argb8888,
    (int)TextureAccess.Streaming, width, height);

_textureWidth = width;   // written even if _texture == null
_textureHeight = height; // written even if _texture == null
```

If texture creation fails (for example, when an SDL driver does not support the `Argb8888` streaming format), the cached dimensions match the incoming frame dimensions. Every subsequent call to `EnsureTexture` skips the early-return guard and returns without recreating the texture. `UpdateTexture` and `RenderCopy` are then called with a null `_texture` pointer, which SDL silently no-ops — the window stays black with no error or log message.

This was the observed symptom during the 2026-03-30 WSL debugging session: the runner reported the correct frame count (90 frames, PTS monotonic) and non-zero present latencies, yet the SDL window showed only black.

## Contrast with SdlVideoPresenter

`SdlVideoPresenter.EnsureTexture` in `src/FrameFlow.Sdl/SdlVideoPresenter.cs` already handles this correctly — it checks for null, resets the cached dimensions to zero, logs the SDL error via `ILogger`, and returns `false` to suppress the render call:

```csharp
if (_texture == null)
{
    _textureWidth = 0;
    _textureHeight = 0;
    _logger.LogError("SDL_CreateTexture failed for {Width}x{Height}: {SdlError}",
        width, height, _sdl.GetErrorS());
    return false;
}
```

`SdlInstrumentedPresenter` should follow the same pattern.

## Recommended Fix

1. After `CreateTexture`, check `_texture != null` before writing `_textureWidth`/`_textureHeight`.
2. In `RenderPendingFrame`, after `EnsureTexture(...)`, guard the render block:

```csharp
EnsureTexture(cpuFrame.Width, cpuFrame.Height);
if (_texture == null)
    return; // texture creation failed; skip render, window stays black intentionally

var span = cpuFrame.PixelData.Memory.Span;
fixed (byte* pixels = span)
    _sdl.UpdateTexture(_texture, null, pixels, cpuFrame.Stride);
...
```

3. Optionally print a one-time warning to `Console.Error` so the failure is visible without requiring a logger.

## Why this is obsolete

`examples/FrameFlow.Examples.SdlCorpusRunner` no longer exists, so
`SdlInstrumentedPresenter` — the only type this report was about — is gone.

The corrective pattern survives in the shipping code. `src/FrameFlow.Sdl` now
exposes `SdlVideoSink` (the rename of `SdlVideoPresenter`), whose
`EnsureTexture` checks the `CreateTexture` result, resets the cached
dimensions, logs via `LogTextureCreationFailed`, and returns `false`; the
caller at `SdlVideoSink.cs:208` only uploads and renders when that returns
true. No action remains.
