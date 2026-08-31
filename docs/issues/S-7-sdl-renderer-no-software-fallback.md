# S-7: SDL Renderer Has No Software Fallback

**Severity:** Should Fix Soon
**Status:** Open
**Responsible Agent:** SDL Presenter Agent
**Detected:** 2026-03-30
**Paths refreshed:** 2026-08-24
**Phase Gate:** Phase 08

## Problem

`SdlVideoSink` (in `src/FrameFlow.Sdl/SdlVideoSink.cs`, line 106) creates the SDL renderer requesting hardware acceleration only:

> **2026-08-24 path refresh.** This report was written against
> `SdlVideoPresenter` and a `SdlInstrumentedPresenter` in the since-deleted
> `examples/FrameFlow.Examples.SdlCorpusRunner`. The presenter was renamed to
> `SdlVideoSink` and the example is gone; the missing fallback is unchanged and
> still present in the shipping code.


```csharp
_renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Accelerated);
if (_renderer == null)
    throw new InvalidOperationException($"SDL_CreateRenderer failed: {_sdl.GetErrorS()}");
```

On any environment that has no hardware-accelerated SDL backend — WSL without GPU driver support, headless CI runners, VMs with no 3-D acceleration, or plain X11 sessions — `SDL_CreateRenderer` returns null and the process throws before displaying a single frame. There is no fallback.

This was confirmed during the 2026-03-30 WSL debugging session: the runner only functioned correctly after setting `DISPLAY=:0`, `WAYLAND_DISPLAY=wayland-0`, `XDG_RUNTIME_DIR=/mnt/wslg/runtime-dir`, and `SDL_VIDEODRIVER=x11` — all required to activate WSLg's hardware-accelerated X11 path. Without those variables the renderer creation fails immediately.

## Affected Files

| File | Type |
|------|------|
| `src/FrameFlow.Sdl/SdlVideoSink.cs` (line 106) | Core library |

## Recommended Fix

Retry with `RendererFlags.Software` when the accelerated renderer fails, and log a warning so callers know they are on the slower path:

```csharp
_renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Accelerated);
if (_renderer == null)
{
    // Log / print warning: falling back to software renderer
    _renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Software);
}
if (_renderer == null)
    throw new SdlException("SDL_CreateRenderer", sdl.GetErrorS());
```

This matches SDL's own recommended robustness pattern and allows the presenter to function correctly in CI, WSL without WSLg, and any headless environment where visual correctness is less critical than the ability to run at all.

## Related

- **S-6** — now obsolete (its subject file was deleted), but its point still applies: a software renderer on some platforms returns a renderer that does not support all texture formats. `SdlVideoSink.EnsureTexture` already handles that correctly.
- **ADR-0018** — SDL Presenter and Audio Adapter: the threading and lifecycle constraints documented there apply equally to the software renderer path.
