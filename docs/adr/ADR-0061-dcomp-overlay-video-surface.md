# ADR-0061: DirectComposition Overlay Video Surface

## Status

**Superseded / reverted (2026-06-07).** The DComp overlay surface was removed from `main`;
`CompositionInteropVideoView` (ADR-0016) remains the production presenter. The work is preserved
on the `archive/dcomp-overlay` branch and in the `v0.4.1-alpha.1` tag. See the **Post-mortem** at
the end for why.

## Context

[ADR-0016](ADR-0016-avalonia-presenter-frame-delivery-strategy.md) established the Avalonia
zero-copy presenter: a hardware-decoded D3D11VA NV12 surface is color-converted to BGRA on
the GPU (`ID3D11VideoProcessor`) and imported straight into Avalonia's compositor via
`ICompositionGpuInterop` + `CompositionDrawingSurface` (`CompositionInteropVideoView`). That
removed the GPU to CPU readback, but the imported texture is still **composited by Avalonia's
render thread** (Skia over ANGLE) into the window, and the window is then composited again by
DWM. The video is drawn with `Canvas.DrawBitmap` into the single window render target; it is
never on its own scanout surface.

A field comparison drove this ADR: on typical signage hardware (an Intel HD 620
iGPU, Win10 LTSC 2019) an incumbent app built as an embedded Chromium webview
used dramatically less CPU than an Avalonia kiosk for the same fullscreen video. A 2026-06-06 investigation, not
published with this repository because it is written against a downstream
deployment, measured why on the actual hardware:

- The `CompositionInteropVideoView` path owns **no swapchain** (Avalonia's default Windows
  composition mode is `WinUIComposition`, which renders into a DWM-owned composition surface);
  PresentMon attributes 100% of presents to `dwm.exe`. Video presentation cost the kiosk
  process ~5.9% CPU (normalized to 4 cores) and ~12% of the GPU 3D engine, plus a per-frame
  VideoProcessor Blt and the decode.
- Chromium is cheap because it puts the video on its **own DirectComposition swapchain**, so
  the app-level compositor is out of the per-frame path entirely.

The dominant cost, measured, is the **in-app compositor**, not the lack of a hardware overlay
plane. Removing the video from Avalonia's compositor is most of the available win.

## Decision

Add a third `IVideoSurface` implementation, **`DCompOverlayVideoView`** (in
`FrameFlow.Avalonia.Windows`), that presents hardware-decoded frames on a **DirectComposition
overlay hosted in a native child HWND** (`NativeControlHost`), bypassing Avalonia's compositor.

- **Pipeline (Flavor A).** NV12 to BGRA via the GPU `ID3D11VideoProcessor` straight into a
  flip-model **composition swap chain** (`IDXGIFactory2.CreateSwapChainForComposition`), which
  a DComp visual composes on the host HWND. Reuses the ADR-0016 VideoProcessor + color-space
  plumbing; the difference is the destination (a swap chain back buffer, not a shared
  compositor texture).
- **Device split.** The VideoProcessor and swap chain run on the **decode device** (borrowed
  from the NV12 texture) because that is the only device that can see the D3D11VA decode array,
  so there is no cross-device copy. `DCompositionCreateDevice` requires
  `D3D11_CREATE_DEVICE_BGRA_SUPPORT`, which FFmpeg's d3d11va device does not set, so a small
  **dedicated BGRA device backs the DComp device**; DComp composes the decode-device swap chain
  cross-device on the same adapter.
- **Selection + fallback.** The surface is chosen per view (the kiosk already has independent
  `UseHardwarePresenter` flags for attract and signage). It must be capability-gated at runtime
  (`IDXGIOutput3::CheckOverlaySupport`) with fallback to `CompositionInteropVideoView`, so an
  unsupported box degrades rather than shows a broken overlay.
- **Airspace.** A native child HWND composites above Avalonia content, so Avalonia cannot paint
  over the video. UI over the video is handled by **a transparent, fullscreen, topmost Avalonia
  overlay window** layered over the video window (validated as `CtaOverlayWindow`); for attract,
  the whole transparent root is the touch target ("touch anywhere to begin"). Views with no
  overlapping UI (most signage) need nothing extra. The two fullscreen windows align trivially
  on the single-monitor kiosk.

This **extends** ADR-0016; it does not supersede it. `CompositionInteropVideoView` remains the
default and the fallback (it has no airspace constraint and is the cross-platform-friendly
shape), and the software `FrameFlowVideoView` remains for non-D3D11VA frames.

### Measured outcome (reference kiosk, Intel HD 620)

| Metric | interop (ADR-0016) | dcomp overlay | dcomp + CTA window |
|---|---|---|---|
| Process CPU (norm /4 cores) | 5.9% | ~1.3% | ~0.9% |
| GPU 3D engine | 12% | 0 | 0 |

The overlay surface removes the app-side composite (~78% process-CPU reduction, 3D engine to
zero). The transparent CTA window adds no measurable steady-state cost because Avalonia only
composites it on change.

### What we explicitly did not do: chase the MPO plane

The hardware overlay plane (`Hardware Composed: Independent Flip`) **did not engage** even with
the example fullscreen as the sole foreground app (kiosk killed): it stayed `Composed: Flip`.
The blocker is structural: the video is a DComp child HWND nested inside a DWM-composed Avalonia
parent (`WinUIComposition`), so DWM composites parent + child rather than independent-flipping
the child. Chromium gets the plane because it composes its **whole top-level** through DComp
(`WS_EX_NOREDIRECTIONBITMAP`). Capturing that increment would require a full-DComp top-level
that partly replaces Avalonia's renderer, and on this hardware the increment is small (DWM's own
3D composite was ~2.5% for a simple fullscreen video). It is not worth the cost now. The
process-side win above is independent of it.

## Consequences

**Positive**
- Large, measured CPU/GPU reduction for hardware video presentation on the target fleet,
  reached through the existing `IVideoSurface` seam with no change to the playback core.
- Avalonia is retained for all UI (via the overlay-window pattern); the decision is contained
  to `FrameFlow.Avalonia.Windows` and is per-view selectable with a safe fallback.

**Constraints / costs**
- **Windows-only** (Vortice Direct3D 11 + DirectComposition); the cross-platform analogue stays
  on the compositor-import path.
- **Host apps must ship an application manifest** declaring a supported-OS list (the Windows 10
  GUID), or Avalonia's `NativeControlHost` cannot create its host child window. This applies to
  every consumer of this surface, including the kiosk.
- **Airspace**: UI over the video requires the transparent-overlay-window pattern (or keeping UI
  outside the video rect). Rich Avalonia controls cannot render directly on top of the video.

**Follow-ups before production hardening**
- Device-lost / TDR recovery and resize/letterbox handling (the spike is stretch-to-fill, fixed
  at first frame), plus a CPU-frame fallback and off-thread present.
- Flavor B: an NV12 **decode swap chain**
  (`IDXGIFactoryMedia::CreateDecodeSwapChainForCompositionSurfaceHandle`) so the driver does the
  NV12 to RGB conversion at scanout, dropping the VideoProcessor Blt (~6% VideoProcessing engine)
  as well.
- Runtime capability gating + fallback wiring, then integrate into the kiosk signage and attract
  views.

## References

- [ADR-0016: Avalonia Presenter Frame Delivery Strategy](ADR-0016-avalonia-presenter-frame-delivery-strategy.md) (extended by this ADR)
- [ADR-0015: GPU-resident frame pipeline extensibility](ADR-0015-gpu-resident-frame-pipeline-extensibility.md)
- The 2026-06-06 DComp / MPO overlay investigation (full measurements and option analysis) — not published with this repository

## Post-mortem (2026-06-07): why this was reverted

The overlay surface was wired into the kiosk and shipped (`v0.4.0` / `v0.4.1-alpha.1`) **before the
"follow-ups before production hardening" listed above were done.** On real fleet hardware it failed
on three axes and was removed from `main`. The work is preserved on `archive/dcomp-overlay` and the
`v0.4.1-alpha.1` tag; `CompositionInteropVideoView` (ADR-0016) is the production presenter.

**1. The performance win did not hold at the system level.** A controlled, isolated re-benchmark
(attract clip only, all non-video subsystems mocked off, Splashtop disconnected) measured *total*
kiosk-process CPU, not just the presentation component:

| Path | Total process CPU (% of one core) | GPU 3D | GPU VideoProcessing |
|---|---|---|---|
| HW decode + CPU present (readback) | ~164% | ~33% | 0 |
| HW decode + zero-copy interop (ADR-0016) | ~73% | ~23% | ~11% |
| HW decode + DComp overlay (this ADR) | ~82% | ~19% | ~11% |

The real win is **zero-copy interop** (CPU roughly halved vs. CPU-present). The overlay (interop ->
overlay) is a **wash on CPU** and only ~4pp lower on the GPU 3D engine. The earlier "5.9% -> 1.3%"
figure was the *presentation component in isolation*, not total cost: once interop has eliminated
decode/convert/readback, all that remains for the overlay to remove is the final composite of an
already-GPU-resident frame — cheap, and pure-GPU. Even a perfect MPO plane (which never engaged, as
this ADR already noted) would have been marginal.

**2. The decisive failure was resilience, not performance.** The overlay owns an app-level D3D
device + composition swap chain + DirectComposition visual on a native child HWND. On *abrupt*
termination (`taskkill` / `Stop-Process` — i.e. every redeploy and every crash) there is no
graceful dispose, and the GPU/display-driver cleanup of that app-owned, cross-process composition
state **deadlocks in kernel mode**, leaving an un-killable 1-thread zombie that only a reboot
clears. Controlled evidence: across the benchmark matrix, **every** DComp-overlay process wedged
un-killable on kill while **every** CPU/interop process (which differ only in the presenter) died
cleanly. The hardening this ADR deferred (device-lost/TDR, off-thread present, graceful teardown)
was load-bearing.

**3. It caused the field "Phidget freeze," as a cascade.** An un-killable overlay zombie holding
the USB stack also keeps the Phidget hub claimed; the next kiosk instance then cannot open the hub
(`in use by pid <zombie>`), enters an open/fault/rebuild loop, starves the render thread, and the
signage video "freezes." The overlay's un-killable-on-termination bug was the **root** of that
incident; the Phidget fault loop was the downstream symptom.

**Lesson.** Stay inside the managed compositor (ADR-0016 interop): it captured essentially the
entire real win and tears down cleanly. If an OS overlay / MPO plane is ever pursued again, the
precondition is **isolating the GPU presenter in its own process** (Chromium's GPU-process model)
so a driver-cleanup deadlock cannot take down the host — *and* completing the hardening list above
first.
