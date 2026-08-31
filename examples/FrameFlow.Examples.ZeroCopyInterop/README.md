# FrameFlow.Examples.ZeroCopyInterop (spike)

A spike of the **true zero-copy GPU video path** on Windows: decode on the GPU
(D3D11VA), keep the NV12 surface in VRAM, color-convert to BGRA with the
fixed-function `ID3D11VideoProcessor`, and import the result **straight into
Avalonia's compositor** via `ICompositionGpuInterop` — no GPU→CPU readback, no
`WriteableBitmap`.

Branch: `spike/zero-copy-presenter`. Backs the 2026-06-04 amendments to
[ADR-0016](../../docs/adr/ADR-0016-avalonia-presenter-frame-delivery-strategy.md)
(compositor interop supersedes the deferred ANGLE/Skia path) and
[ADR-0038](../../docs/adr/ADR-0038-memory-domain-pipeline-operators.md)
(the Phase B `ID3D11VideoFrame` accessor + first GPU-aware sink).

## Where the code lives

The presenter has **graduated out of this example** into a reusable Windows library,
[`src/FrameFlow.Avalonia.Windows`](../../src/FrameFlow.Avalonia.Windows/):

- `CompositionInteropVideoSink : IVideoSink` — frame intake + latest-wins buffering.
- `CompositionInteropVideoView : Control` — owns the compositor surface and the D3D11
  present ring; pulls frames from the sink on a render tick. Get the sink to hand to the
  player via `EnsureSink()`. (Mirrors `AvaloniaVideoSink` / `FrameFlowVideoView`.)
- `D3D11Nv12SharedConverter` (internal) — the NV12→BGRA shared-texture ring.

This project is now just the host that wires the library to `MediaPlayer` and drives it
headlessly. It's Windows-only (Direct3D 11); the cross-platform analogue is a future
`FrameFlow.Avalonia.Vulkan` presenter behind the same `IVideoSink` seam.

## The path

```
H.264 → D3D11VA decode → NV12 texture (VRAM)          GpuVideoFrame (YieldHardwareFrames=true)
      → GpuVideoFrame.TryGetD3D11Texture(tex, slice)  ADR-0038 Phase B accessor (core change)
      → ID3D11VideoProcessor Blt → BGRA (VRAM)         one of a 3-buffer SharedKeyedMutex ring
      → ICompositionGpuInterop.ImportImage(handle)     D3D11TextureGlobalSharedHandle
      → CompositionDrawingSurface.UpdateWithKeyedMutexAsync(img, 1, 0)
      → compositor composites the surface visual       no CPU crossing
```

Keyed-mutex handshake: producer `AcquireSync(0)` → Blt → `ReleaseSync(1)`;
compositor takes `acquire 1` / `release 0`. The shared **NT handle** lets the
compositor open the texture on its own device, so we never touch Avalonia's
render device — which is what removes ADR-0016's original objection.

## What this spike validated (run 2026-06-04, NVIDIA + Intel box)

Confirmed live, end to end, from the run log:

- `Hardware decode bound: codec 'h264' on D3D11Va` — the caps + `yieldHardwareFrames`
  threading engages D3D11VA through the real `MediaPlayer` pipeline.
- `Compositor GPU interop ready. D3D11 global-shared-handle import supported: True`.
- `GpuVideoFrame.TryGetD3D11Texture` returns a valid `ID3D11Texture2D` + array slice.
- `ID3D11VideoProcessor` NV12→BGRA Blt into a shared keyed-mutex texture succeeds.
- **First frame imported + presented with no CPU round-trip.**
- **Sustained continuous playback: 240 frames presented, 0 dropped, 0 errors**
  across a loop boundary, then a clean graceful exit (`--exit-after`).

## Continuous playback: 3-buffer shared-texture ring

The first cut used a **single** shared texture ping-ponged on one keyed mutex,
which stalled after the first frames (`DXGI_ERROR_INVALID_CALL` on `ReleaseSync`)
whenever the compositor's consume cadence diverged from decode — the producer's
next `AcquireSync(0)` blocked on a key the compositor hadn't released yet.

Fixed with a **ring of `BufferCount` (=3) shared BGRA textures** (one
`ID3D11VideoProcessor` + N output textures, each with its own keyed mutex and
shared handle) plus per-buffer present-`Task` tracking: each frame the presenter
targets a buffer whose previous `UpdateWithKeyedMutexAsync` has completed, so
`AcquireSync(0)` never contends with an in-flight present. If all N are still in
flight the frame is dropped (latest-wins). This is the shape of Avalonia's own
`samples/GpuInterop` `SwapchainBase`. Validated at 240/0 above.

**Remaining follow-ups (not blockers):** GPU-side BT.709 color-space config on the
VideoProcessor (currently default conversion), aspect-ratio preservation
(currently stretch-to-fill), running the Blt off the UI thread, and graduating
the presenter out of the example into a `FrameFlow.Avalonia.Windows` library.

## Run it

```powershell
dotnet run --project examples/FrameFlow.Examples.ZeroCopyInterop -- `
  <path-to-h264-or-hevc>.mp4 --log-file zero-copy-interop.log --exit-after 10
```

Requires a Windows box whose GPU supports D3D11VA decode of the file's codec and
whose Avalonia backend advertises `D3D11TextureGlobalSharedHandle` (logged at
startup). `--exit-after <s>` closes the window gracefully so the log flushes —
use it for headless/autonomous runs. No file argument → the window explains what
to pass.

Generate a quick test clip:

```bash
ffmpeg -f lavfi -i testsrc=size=1280x720:rate=30:duration=6 \
  -c:v libx264 -profile:v high -pix_fmt yuv420p -movflags +faststart test.mp4
```

## Scope notes

Windows / D3D11 only (Vortice). NVDEC/CUDA frames would use a different bridge
(`cuGraphicsD3D11RegisterResource` → shared D3D texture, or a Vulkan-backend
import); the `ICompositionGpuInterop` destination is the same. The presenter
lives in the example for the spike; if it graduates it moves to a
`FrameFlow.Avalonia.Windows` presenter (the `IVideoFramePresenter` slot ADR-0016
reserves).
