# ADR-0063: NV12 to BGRA via pixel shader (replacing VideoProcessorBlt)

## Status

**Accepted (2026-06-12).** Implemented on `feat/nv12-pixel-shader-converter`. Supersedes the
`ID3D11VideoProcessor` color-convert inside the zero-copy presenter
([ADR-0016](ADR-0016-avalonia-presenter-frame-delivery-strategy.md) amendment) — the
`D3D11Nv12SharedConverter` now converts NV12 to BGRA with an HLSL pixel shader on the general 3D
pipeline instead of `ID3D11VideoContext.VideoProcessorBlt`. The public interface
(`BufferCount`, `GetSharedHandle`, `ConvertInto`, `IsDeviceLost`, the shared keyed-mutex BGRA ring)
is unchanged, so `CompositionInteropVideoView` is untouched. Color + throughput validated on dev
hardware (RTX 3080 Ti); **on-kiosk A/B on the Intel HD 620 — the box where the hang reproduces — is
the final validation gate.**

## Context

[ADR-0016](ADR-0016-avalonia-presenter-frame-delivery-strategy.md) established the zero-copy
presenter: a hardware-decoded D3D11VA NV12 surface is color-converted to BGRA on the GPU and
imported straight into Avalonia's compositor. The color convert was
`ID3D11VideoContext.VideoProcessorBlt` — the **fixed-function VideoProcessor** block, the same
single hardware unit DWM uses for overlays.

A 2026-06-12 teardown-deadlock investigation (§9), not published with this
repository because it is written against a downstream deployment, root-caused an
unkillable UI-thread freeze in a two-presenter configuration — one per monitor,
sharing a weak **Intel HD 620** iGPU. Two concurrent `VideoProcessorBlt`s
contend on that one fixed-function unit and **hang in the driver**. A thread wedged inside a GPU
driver call is unkillable by the kernel, so the box needs a reboot.

Reading VLC's `modules/video_output/win32/direct3d11.cpp` confirmed the industry norm: VLC, mpv,
OBS, and Chrome all do NV12 to RGB with a **pixel shader on the general 3D pipeline**, and use
`VideoProcessorBlt` only as a legacy fallback for D3D11 < 11.1 (`// NV12/P010 to RGB for
D3D11 < 11.1`). The 3D shader pipeline is fully concurrent — N streams = N draw calls — which is why
every other player runs many streams trivially. FrameFlow was on the legacy fixed-function path; the
fixed-function unit is the contention point.

## Decision

Convert NV12 to BGRA with an HLSL pixel shader on the 3D pipeline, behind the **identical**
converter interface.

- **Dual-SRV NV12 sample.** Two SRVs over the NV12 input: the Y plane as `R8_UNORM`, the interleaved
  UV plane as `R8G8_UNORM`. A fullscreen-triangle vertex shader (positions synthesized from
  `SV_VertexID`, no vertex/index buffers) and a pixel shader sample Y + UV and apply the **BT.709
  studio (limited) range to full-range RGB** matrix, replicating exactly the colorspace the old path
  set (`DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709` to `RgbFullG22NoneP709`). Input and output share
  G22 transfer + P709 primaries, so there is no gamma/primary conversion — only the YCbCr to RGB
  matrix with the limited to full range expansion folded in. The draw goes into each ring buffer's
  BGRA texture via an `ID3D11RenderTargetView`.
- **Decode-slice copy (shader-readability).** FrameFlow lets FFmpeg auto-allocate the D3D11VA decode
  pool, which creates the texture array `D3D11_BIND_DECODER`-only (confirmed at runtime: `bind=Decoder`),
  so its slices cannot be bound as a shader resource. Each frame the chosen array slice is
  `CopySubresourceRegion`'d — a cheap same-device **copy-engine** blit, **not** the contended
  VideoProcessor — into a private `D3D11_BIND_SHADER_RESOURCE` NV12 texture that the SRVs view. The
  decode slice is macroblock-aligned (e.g. 1920x1088 for a 1080p stream), so the copy uses an
  explicit frame-sized `Box` to take only the top-left coded-frame region.
- **Borrowed device, isolated 3D state.** The draw runs on FFmpeg's **borrowed** decode device (the
  only device that sees the NV12 array), exactly as `VideoProcessorBlt` did. The immediate context's
  3D pipeline state is isolated from the decoder by recording the shader pass into a **deferred-context
  command list** per ring buffer (the sample texture and SRVs are fixed, so the lists are recorded
  once at construction) and replaying it with `ExecuteCommandList(restoreContextState: true)`. This is
  allocation-free per frame and never disturbs the decoder's context state.
- **Unchanged.** The per-buffer keyed-mutex bracket (`AcquireSync(0, 1000)` to `ReleaseSync(1)`) and
  the reactive device-loss handling (`IsDeviceLost` on the same HRESULTs) are preserved verbatim.

Shaders are compiled at runtime via `D3DCompile` (new `Vortice.D3DCompiler` dependency, which wraps
the OS `d3dcompiler_47`).

## Consequences

- **The §9 concurrent-`VideoProcessorBlt` hang is removed at the source.** Concurrent streams issue
  independent 3D draws and copy-engine blits; nothing contends on the single fixed-function
  VideoProcessor. This is precisely why every other player runs many streams without wedging.
- **One extra same-device NV12 copy per frame** (a copy-engine blit, far cheaper than the readback the
  CPU path pays, and off the fixed-function unit). The durable optimization that drops it — make the
  decoder emit `BIND_SHADER_RESOURCE` textures and sample the decode slice directly (true zero-copy) —
  is deferred (investigation §6 step 5) because it is a decoder-side change.
- **New dependency:** `Vortice.D3DCompiler` (runtime HLSL compile from embedded source).
- **Cleanly revertible / A-B-able.** The change is isolated to `D3D11Nv12SharedConverter.cs` plus the
  package reference; reverting restores the `VideoProcessorBlt` version with no call-site changes.
- **Relationship to the §9 watchdog + kiosk recovery (Phases 1-2, already shipped).** Those remain
  valuable defense-in-depth for any residual *device-level* wedge, but the shader removes the specific
  concurrency contention they were detecting/recovering from. The §9 "off-thread Blt" idea (Phase 3) is
  now largely moot for the concurrency case — there is no Blt to move.
- **Validation.** Dev hardware (RTX 3080 Ti) shows correct color, smooth playback, and 0 dropped
  frames over sustained playback. The freeze itself only reproduces on the Intel HD 620, so on-kiosk
  A/B (color parity against the VideoProcessorBlt build, and the absence of the concurrent-stream
  freeze) is the final gate.

## Alternatives considered

- **Off-thread `VideoProcessorBlt` (§9 Phase 3).** Moves the hanging call off the UI thread, but two
  streams still serialize on the one fixed-function unit — it relocates the freeze rather than removing
  the contention. The shader eliminates the contention entirely, and is what the reference players do.
- **Private device + shareable NV12 (investigation §6 step 5).** The durable end-state (decode device
  fully decoupled from presentation), but a larger rework. Deferred; the shader convert is orthogonal
  and ships the concurrency win now on the borrowed device.
- **Direct SRV on the decode slice (no intermediate copy).** Requires the decoder to allocate the
  D3D11VA pool with `D3D11_BIND_SHADER_RESOURCE` (a `get_format` / `hw_frames_ctx` change in
  `FrameFlow.Decoding`). Out of scope for a converter-local change; the copy keeps the edit inside
  `D3D11Nv12SharedConverter` and off the churny decoder. Tracked as the zero-copy follow-up above.
