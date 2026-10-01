# Can a Vulkan-decoded frame reach Avalonia on Linux without leaving the GPU?

**Date:** 2026-09-30
**Issue:** follows #414. Spike: `spikes/VulkanPresenterProbe`.

## Question

Off Windows, every hardware-decoded frame is read back, converted by `sws_scale` and copied into a
`WriteableBitmap` ([the Vulkan exploration](../explorations/vulkan.md)). Can a frame FFmpeg decodes
on Vulkan be converted on the GPU and handed to Avalonia's compositor instead, what does the
hand-off need, and what does it save?

## Verdict

**Yes, on the decoder's own device, and it holds 4K60 at the CPU cost of 1080p.** The probe
converts each NV12 frame to RGBA8 with a compute shader on FFmpeg's `VkDevice`, into a ring of
images exported as opaque file descriptors, and Avalonia imports them with a pair of binary
semaphores per image. Colours match the CPU path within 3 levels a channel, and the picture is the
right way up.

Process CPU per presented frame, GLX renderer, Avalonia 11.3.22, two 10-second runs each:

| Clip | Path | Presented | Process CPU | Per frame |
| --- | --- | --- | --- | --- |
| 1080p60 H.264 | Vulkan decode, GPU conversion | 59.9/s | 22–23% of a core | 3.7–3.9 ms |
| | Hardware decode, read back (today) | 59.9/s | 60% | 10.0 ms |
| | Software decode | 59.9/s | 64–65% | 10.7–10.9 ms |
| 2160p60 H.264 | Vulkan decode, GPU conversion | 59.9/s | 22–23% | 3.7–3.8 ms |
| | Hardware decode, read back (today) | 45–47/s | 135–139% | 29.4–29.7 ms |
| | Software decode | 50.0/s | 178–181% | 35.5–36.1 ms |

"Today" is the player with `FrameFlowVideoView`: its sink takes CPU frames, so the player decodes on
the first backend in the Linux default order that binds, CUDA on this GPU, and downloads every
frame. At 4K it falls behind; the Vulkan path does not.

One run each on the other backends:

| Avalonia | Renderer | GPU conversion | Read back (today) |
| --- | --- | --- | --- |
| 11.3.22 | Vulkan (`X11RenderingMode.Vulkan`) | 8.3 ms, 59.9/s | 11.9 ms, 60.0/s |
| 12.1.3 | GLX | 4.7 ms, 59.9/s | 10.9 ms, 59.9/s |

## Findings

### 1. Converting on FFmpeg's device works, so nothing has to be imported across devices

The exploration left open whether a converter-owned device could import FFmpeg's decode image, or
had to borrow FFmpeg's device against ADR-0064. The D3D12 presenter (#429) already converts on the
decoder's device, kept alive by a borrowed `HardwareDevice`. The same works here, and removes the
question: the converter allocates its own exportable images and never exports FFmpeg's.

What the decode pool gives a converter, read from the first frame:

- `img_flags` `MUTABLE_FORMAT | ALIAS | EXTENDED_USAGE | VIDEO_PROFILE_INDEPENDENT`, and usage that
  includes `STORAGE` and `SAMPLED`. A compute shader reads the NV12 image through one view per plane
  (`R8_UNORM` on plane 0, `R8G8_UNORM` on plane 1, each restricted to `STORAGE` usage), with no copy.
- `queue_family` `VK_QUEUE_FAMILY_IGNORED`: the images are concurrent across FFmpeg's queue families,
  so a compute queue FFmpeg created can read them without an ownership transfer.
- Layout `VIDEO_DECODE_DPB_KHR`. The driver reuses output images as reference pictures, so the
  converter changes the layout of an image the decoder will read again.

That last point is why the protocol in `hwcontext_vulkan.h` matters. Under `lock_frame`, the
converter waits on `sem[0]` at `sem_value[0]`, signals `sem_value[0] + 1`, and writes back the layout
and access its barrier left (`GENERAL`, `SHADER_STORAGE_READ`). FFmpeg's next barrier on the image
starts from what was written back. The decoder reported no decode errors in a run of about 835 frames, at 1080p or at 4K, and the sink
dropped none inside the measurement window.

The queue is locked with the deprecated `lock_queue`, which avutil 61 still has
(`FF_API_VULKAN_SYNC_QUEUES` is on until avutil 62).

### 2. What Avalonia's import needs

On both renderers and both versions, `ICompositionGpuInterop` offers `VulkanOpaquePosixFileDescriptor`
for images and semaphores, synchronised by semaphores, and reports the same device UUID as FFmpeg's
physical device. The GL path fixes the rest (`ExternalObjectsOpenGlExtensionFeature`):

- **RGBA8 only.** `glTexStorageMem2DEXT` is called with `GL_RGBA8`.
- **`TRANSFER_SRC_OPTIMAL` on hand-off.** Avalonia waits with `GL_LAYOUT_TRANSFER_SRC_EXT`, and
  signals the release semaphore with no layout, so the next write starts from `UNDEFINED`.
- **`TopLeftOrigin = true`.** The default is GL's bottom-left origin, and the first run showed the
  picture upside down.
- **A binary semaphore pair per ring image.** The converter signals one when the image is written
  and waits on the other before writing it again, and only once the update task has completed, so
  a signal has been submitted before the wait.

### 3. Avalonia's GL path copies every update into a new texture

`UpdateWithSemaphoresAsync` does not present the imported image. `SnapshotWithSemaphores` waits on
the semaphore, allocates a texture of the image's size and copies the image into it
(`GlSkiaExternalObjectsFeature.TakeSnapshot`), then signals. So every frame costs one full-size GPU
copy and one texture allocation after the conversion. The keyed-mutex update in the same class
takes the same snapshot, so the D3D11 presenter pays it on Windows too. It did not limit 4K60 here,
and a FrameFlow presenter cannot remove it.

### 4. An untagged stream is BT.601

The corpus clips carry no colour tags. The first run chose BT.709 for anything 720 lines or taller,
and the bars came out 255,23,0 for red and 0,216,0 for green, where the CPU path shows 252,0,0 and
2,251,0. With the matrix and range taken from `AVFrame.colorspace` and `color_range`, and
unspecified meaning BT.601 as `sws_scale` treats it, the two paths agree within 3 levels.

### 5. PRIME render offload stalled the render loop, on every path

The first runs rendered Avalonia on the NVIDIA GPU through PRIME render offload onto the VM's
virtual display. The render thread blocked in `glXSwapBuffers` inside NVIDIA's GLX library, which
logged "Damage event slots full". Animation frames ran at 0.2 a second, on the CPU path as well as
the Vulkan one, against 115–138 a second on an X server the NVIDIA GPU drives. Every measurement
above is from the latter, with no display attached. Offload on real hybrid-graphics hardware was not
tried.

## What a presenter needs beyond the spike

- **A Vulkan accessor on `GpuVideoFrame`.** The probe reads the `AVFrame` by reflection. Consumers
  need the `AVVkFrame`, its frames context for `lock_frame` and `unlock_frame`, and the device
  context. The protocol in finding 1 is easy to get wrong, so FrameFlow could run it and hand out
  the image, its current layout and the wait and signal values instead.
- **P010 and chroma filtering.** The probe converts NV12 with nearest-neighbour chroma.
- **Resize, device loss and teardown.** The probe builds its ring once, at the first frame's size.
- **Backend selection.** The presenter's sink prefers Vulkan (#532). Where Vulkan decode is off,
  as on Intel's Mesa driver by default, #533 decides whether that costs hardware decode.
- **VAAPI frames on Intel.** A Vulkan-only presenter shows Intel nothing but read-back frames, unless
  VAAPI surfaces are mapped into Vulkan through DMA-BUF.

## Not tested

AMD and Intel drivers, Wayland, 10-bit and HDR streams, device loss, runs longer than 20 seconds,
and playback with an audio clock.

## Method

An Ubuntu 24.04 VM with a Turing-generation NVIDIA workstation GPU passed through, NVIDIA's
580-series driver, and the pinned FFmpeg 9.0.1 runtime. The driver exposes
`VK_KHR_video_decode_queue` with H.264, H.265 and VP9, and `GL_EXT_memory_object_fd` and
`GL_EXT_semaphore_fd`. A second X server runs on the NVIDIA GPU with
`AllowEmptyInitialConfiguration` and a 1920x1080 virtual screen; the VM's own display server is
kept off the GPU with `AutoAddGPU false`, since two servers cannot both hold its modesetting
permission.

The 1080p60 clip is the corpus's `test-1080p60-h264-aac.mp4`, 600 frames. The 2160p60 clip is
`testsrc2` encoded with FFmpeg's `h264_vulkan` encoder at 40 Mb/s, 600 frames, High profile. Neither
has an audio sink, so the clock is the wall clock, and both loop.

```bash
DISPLAY=:2 dotnet run --project spikes/VulkanPresenterProbe -c Release -- vulkan|cpu|sw <clip> 10 4
```

Each run warms up for 4 seconds, then counts for 10: frames the sink accepted, process CPU time from
`Process.TotalProcessorTime`, and Avalonia animation frames as evidence the render loop is live. For
the Vulkan path every accepted frame also completed its compositor update, with none faulted.
Colours were read from root-window screenshots at the centre of each bar.
