# Exploration: a Vulkan video path

**Status:** Exploration. Nothing here is decided.

**Date:** 2026-09-27

**Issue:** #414. The measurements are in
[the characterisation](../investigations/2026-09-27-vulkan-decode-characterisation.md). Facts about
Avalonia, Mesa, NVIDIA and ONNX Runtime were read from their sources on 2026-09-27, at the versions
named.

## Where Vulkan stands

| Layer | Today |
|---|---|
| Decode | `HardwareDecodeBackendKind.Vulkan`. Fifth in the Linux default order, absent from the Windows one. |
| Engagement | H.264, HEVC and VP9 decode on NVIDIA's Windows driver. AV1 reaches no hardware backend (#417). A device without `VK_KHR_video_decode_queue` opens and falls back to software (#74). |
| Pool | Grows. Unguarded, like VideoToolbox (ADR-0081). |
| Frame access | `GpuVideoFrame.ReadbackToCpuBgra32` works. Nothing exposes the `VkImage`. |
| Presentation | None. The D3D11 presenter drops a Vulkan frame after one warning. |
| Selection | The player cannot ask for it. `PlayerBuilder` and `PassBuilder` pass only a `HardwareDecodeMode`, so `PreferredBackends` reaches only the dependency-injection decoder factory and direct callers of `VideoDecoder.Open`. |

## Benefits

### Zero-copy presentation off Windows

Off Windows, every hardware-decoded frame is read back today. `av_hwframe_transfer_data` downloads
the NV12 surface, `sws_scale` converts it to BGRA on the CPU, and `AvaloniaVideoSink` copies the
result into a `WriteableBitmap` (about 8 MB a frame at 1080p) for Avalonia to upload again. On the
test driver the download alone added 0.44 ms of wall time a frame to Vulkan decode at 1080p. The
conversion and copies after it were not measured.

A Vulkan path would keep the frame on the GPU: decode, convert NV12 to RGBA on the GPU, and hand the
RGBA image to Avalonia's compositor. Avalonia supports the import side in 11.3.22 and 12.1.3:

- **Handle types.** `ICompositionGpuInterop` imports `VulkanOpaquePosixFileDescriptor` images on
  Linux, and `VulkanOpaqueNtHandle` and `VulkanOpaqueKmtHandle` on Windows. There is no DMA-BUF type.
- **Renderers.** Linux's default GLX renderer imports them through `GL_EXT_memory_object_fd`
  (`ExternalObjectsOpenGlExtensionFeature`). The opt-in Vulkan renderer
  (`X11RenderingMode.Vulkan`) imports them through `VulkanExternalObjectsFeature`. An application
  does not have to change renderer. Avalonia 12's native Wayland backend has no Vulkan mode; whether
  its GL path imports external memory was not checked.
- **Formats.** RGBA8 only on the GL path, and RGBA8 or BGRA8 on the Vulkan path. NV12 cannot be
  imported, so a conversion pass is required, as it is on the D3D11 path.
- **Synchronisation.** Binary semaphores only, on both paths. Neither accepts a timeline semaphore
  for a Vulkan image.

### One decode backend across GPU vendors

Vulkan Video is a Khronos standard that any vendor's driver can implement, on Windows and Linux.
Today FrameFlow needs D3D11VA on Windows, and VAAPI or CUDA on Linux.

The benefit is smaller than it looks. On Windows, D3D11VA already covers every vendor and is what
the only presenter consumes. On Linux, Vulkan decode is off by default on Intel and version-gated on
AMD (see [driver maturity](#driver-maturity)), where VAAPI is on by default. It becomes a
benefit when a single presenter can consume one frame type on every Linux GPU.

In FFmpeg's CLI, D3D11VA spent about ten times the CPU of Vulkan and CUDA to decode the same 6000
frames. That is a reason to profile D3D11VA inside FrameFlow's player, not a reason to switch.

## Costs

### A Vulkan image accessor on `GpuVideoFrame`

ADR-0025 deferred `AsVulkan()` until a GPU backend existed, and ADR-0038 names a Vulkan accessor as
future work. The accessor would expose FFmpeg's `AVVkFrame` and the device it lives on
(`AVVulkanDeviceContext`):

- `img[]`, `mem[]`, `layout[]`, `access[]` and `queue_family[]` for each image.
- `sem[]` and `sem_value[]`, one timeline semaphore for each image.
- The `VkInstance`, `VkPhysicalDevice`, `VkDevice` and queue families.

Using that frame carries rules that a D3D11 texture and slice index do not. Every submission waits
on `sem_value` and signals the next value. The caller takes `lock_frame` around the submission, and
updates `layout[]` and `access[]` after every barrier (`hwcontext_vulkan.h`). The struct layout also
changes between FFmpeg majors (`access[]` became `VkAccessFlagBits2` in 9.0), so the P/Invoke
definition is tied to the pinned runtime.

### Presenter interop

A Linux presenter is the D3D11 presenter's work again, in another API.
`FrameFlow.Avalonia.Windows` is 2,905 lines across 8 files, and most of it is the NV12 converter
(`D3D11Nv12SharedConverter`, 718 lines) and the view that drives it
(`CompositionInteropVideoView`, 1,411 lines). The pieces:

1. **A device for the converter.** ADR-0064 made the D3D11 converter own its device instead of
   borrowing the decode device. The Vulkan equivalent imports the decode image's memory into a
   converter-owned `VkDevice`. FFmpeg makes its pool images exportable (`vulkan_pool_alloc` chains
   `VkExportMemoryAllocateInfo`: opaque FD on Linux, opaque Win32 on Windows), but reading the handle
   with `vkGetMemoryFdKHR` is not a documented FFmpeg API. Borrowing FFmpeg's device instead is the
   model ADR-0064 retired.
2. **Conversion.** NV12 to RGBA8 in a compute shader, or a sampling pass through
   `VkSamplerYcbcrConversion`, into a ring of exportable images.
3. **Export.** Each ring image is exported once as an opaque FD and imported into the compositor
   once.
4. **Synchronisation.** FFmpeg signals timeline semaphores and Avalonia waits on binary ones. The
   converter waits on FFmpeg's value on its own queue, signals a binary semaphore Avalonia waits on,
   and waits on Avalonia's release semaphore before reusing a ring image.
5. **Device loss and resize.** The D3D11 presenter's `EvaluateConverterAction` cases have Vulkan
   equivalents.
6. **A package and its dependencies.** `FrameFlow.Avalonia.Windows.csproj` names a future
   `FrameFlow.Avalonia.Vulkan`. It needs Vulkan bindings, which FrameFlow does not reference today,
   and SPIR-V shaders built into it.

### Queue synchronisation

The decode queue family is not the graphics or compute family. When its device uses more than one
queue family, FFmpeg creates images with concurrent sharing and sets `queue_family[]` to
`VK_QUEUE_FAMILY_IGNORED`, so no ownership transfer is needed inside FFmpeg's device. A converter on
its own device imports the memory and synchronises only through semaphores. That is the part most
likely to fail silently, with torn or stale frames rather than an error.

### Backend selection

A Vulkan presenter needs Vulkan frames, but the backend is chosen without knowing the sink. ADR-0025
has the sink own a pool in the memory domain it prefers. The player would need either `PreferredBackends`
passed through the builders, or the sink's preference driving the order.

## Driver maturity

**Tested.** NVIDIA's Windows driver decodes H.264, HEVC and VP9 correctly, with differences from
software in the range D3D11VA shows. It decodes AV1 4:2:0 through FFmpeg's native decoder, and has
no AV1 High profile (4:4:4). Reading a frame back costs more than CUDA's readback on the same GPU.

**Not tested, from vendor sources:**

| Driver | Decode on by default | Codecs |
|---|---|---|
| RADV (AMD), Mesa | From 24.3 on RDNA3, 25.0 on RDNA2 and VCN2, 25.2 on RDNA4, and from 25.3 on any GPU whose decode queue supports `WRITE_MEMORY`. Otherwise it needs `RADV_PERFTEST=video_decode`, renamed `RADV_EXPERIMENTAL` by 26.1. | H.264 and H.265 since 23.1, AV1 since 24.1, VP9 since 25.2 where the firmware supports it |
| ANV (Intel), Mesa | No. Still behind `ANV_DEBUG=video-decode` in 26.2.3 and on main. | H.264 since 23.1, H.265 since 23.2, AV1 since 25.0 on Gen12 and later, VP9 since 25.2 |
| NVIDIA, Linux | Yes | H.264, H.265, AV1 and VP9, listed on NVIDIA's Vulkan driver page |

Mesa's default build option `video-codecs=all_free` leaves out H.264 and H.265, so whether a
distribution's RADV decodes them depends on how it was built. AMD's VAAPI driver is also Mesa, so the
same option applies to it.

On Intel, Vulkan decode is off by default on every Mesa release. On AMD, it depends on the Mesa
version and the GPU generation. VAAPI is on by default on both.
FrameFlow already handles a device that opens without a decode queue (#74), so a Vulkan-first order
would degrade to software rather than fail, but it would lose hardware decode that VAAPI would have
given.

## GPU-resident inference

Out of scope for this issue (#288), but the issue's premise was checked. ONNX Runtime 1.30.0 has no
Vulkan execution provider. Two things come close:

- **The WebGPU execution provider** runs outside the browser over Dawn, which uses Vulkan on Linux and
  D3D12 or Vulkan on Windows. It also ships as the plugin package
  `Microsoft.ML.OnnxRuntime.EP.WebGpu`. It can be given a caller's Dawn device, but nothing
  documents importing Vulkan memory into it; its tensors are buffers it allocates itself.
- **`OrtInteropApi`** imports Vulkan opaque FD or Win32 memory and Vulkan timeline semaphores. The
  only provider in the tree that implements it is TensorRT RTX, which goes through CUDA external
  memory. The C# binding exposes the function-table slot and no managed wrapper.

So a Vulkan frame can reach inference on NVIDIA, through CUDA. On NVIDIA, NVDEC into CUDA already
reaches the same provider with no import step. Nothing in ONNX Runtime gives AMD or Intel a
Vulkan-to-inference path today, so this does not change the issue's conclusion. It would change if
the WebGPU provider gains external memory import.

## Recommendation

- **Windows default order: no change.** D3D11VA feeds the only presenter, and a Vulkan frame there is
  dropped. The CPU cost D3D11VA showed in FFmpeg's CLI is worth profiling inside the player first.
- **Linux default order: no change until a presenter consumes Vulkan frames.** Without one, every
  Linux frame is read back. VAAPI is on by default on AMD and Intel, where Vulkan decode is off
  or version-gated. On NVIDIA, Vulkan's readback was slower than CUDA's, and CUDA comes first.
- **Where it belongs: behind a Linux presenter, chosen by the sink.** When a presenter that imports
  Vulkan frames is attached, it asks for Vulkan first. Sinks that read back keep today's order.
  Promoting Vulkan in the default order is the wrong lever, because a player without that presenter
  gains nothing from it.

In order, before a presenter is worth building:

1. #417, so AV1 can reach hardware at all.
2. A way for a sink or a caller to choose the backend through the player.
3. Measurements on RADV, and on ANV with decode enabled: engagement, pool behaviour and readback
   cost.
4. The cost of today's Linux readback path in the player at 1080p and 4K (readback, `sws_scale`, and
   the bitmap copy), which is the size of the benefit.

## Open questions

- Whether a converter-owned device can import FFmpeg's NV12 image, or has to borrow FFmpeg's device
  against ADR-0064. Import across devices needs matching `deviceUUID` and `driverUUID`, and support
  for exporting the decode image's format and usage, which is driver-specific.
- Whether Avalonia 12's native Wayland backend imports external memory on its GL path.
- Whether FrameFlow's player sees the D3D11VA CPU cost that FFmpeg's CLI showed.
