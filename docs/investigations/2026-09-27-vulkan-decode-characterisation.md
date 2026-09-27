# Characterisation: the Vulkan decode backend

**Date:** 2026-09-27
**Issue:** #414. Answers #230 for Vulkan, feeds [ADR-0081](../adr/ADR-0081-fixed-pool-budget.md)'s pool
model, and is the evidence for [the Vulkan exploration](../explorations/vulkan.md).

## Question

`HardwareDecodeBackendKind.Vulkan` had never been measured. Three things were unknown: which corpus
codecs it decodes, whether FFmpeg's Vulkan frame pool is fixed or grows, and how it compares with
the other routes to the same hardware decoder.

## Method

Windows 11 with a discrete NVIDIA RTX 30-series GPU, on a current driver that exposes
`VK_KHR_video_decode_queue` with the H.264, H.265, VP9 and AV1 decode extensions. The pinned FFmpeg
9.0.1 runtime, which is built with `--enable-vulkan`.

1. **Engagement.** A throwaway test in `FrameFlow.Decoding.Tests` opened `VideoDecoder` with
   `HardwareDecodeMode.Required`, `PreferredBackends = [Vulkan]` and `YieldHardwareFrames = true`.
   It read each `GpuVideoFrame` back with `ReadbackToCpuBgra32` and compared a thumbnail (every 8th
   pixel, BGR) with a software decode of the same PTS, by mean absolute difference per channel. This
   is the comparison [the D3D11VA reproduction](2026-09-25-d3d11va-pool-exhaustion.md) used.
2. **Pool model.** The same decoder, opened without `HeldHardwareFrames`, held every frame it
   yielded. Used VRAM was sampled from `nvidia-smi` as the count grew, after the frames were
   released, and after the decoder was disposed.
3. **Throughput.** FFmpeg's CLI with `-benchmark` decoded the 1080p60 H.264 clip looped to 6000
   frames. Each `-hwaccel` ran twice with the frames kept on the GPU (`-hwaccel_output_format`) and
   twice with them read back to system memory.

Linux was not tested. No machine with RADV or ANV was available.

## Results

### Engagement

| Fixture | Stream | Decoded on Vulkan | Frames | Max difference from software |
|---|---|---|---|---|
| `test-video-h264-yuv420p.mp4` | H.264 Constrained Baseline, 320x240 | Yes | 72 of 72 | 2.50 |
| `test-1080p60-h264-aac.mp4` | H.264 Constrained Baseline, 1920x1080 | Yes | 600 of 600 | 1.62 |
| `test-video-h265-yuv420p.mp4` | HEVC Main, 320x240 | Yes | 72 of 72 | 2.35 |
| `test-portrait-hevc-pressure.mp4` | HEVC Main, 720x1280 | Yes | 90 of 90 | 4.98 |
| `test-video-vp9-yuv420p.webm` | VP9 Profile 0, 320x240 | Yes | 72 of 72 | 2.59 |
| `test-video-av1-yuv420p.mkv` | AV1 Main, 4:2:0 | No. `Open` throws `HardwareDecodeUnavailableException` | | |
| `test-video-av1-yuv444p-hard.mkv` | AV1 High, 4:4:4 | No, as above | | |

`DecodeErrors` was 0 on every stream that decoded. The differences are in the range D3D11VA showed
on the same clips (up to 0.73 on the H.264 clip, 4.98 on the HEVC one), which the reproduction put
down to the hardware and software colour conversions, not decode damage.

The driver reports the `reuse_dst_dpb` decode mode. Output images are also the decoder's reference
pictures, so there is no separate DPB pool.

### AV1

AV1 does not reach hardware in FrameFlow, on Vulkan or any other backend (#417). `VideoDecoder`
opens the decoder `avcodec_find_decoder` returns, which for AV1 is `libdav1d`, and `libdav1d`
advertises no hardware configs. FFmpeg's native `av1` decoder decodes the 4:2:0 fixture on both
Vulkan and D3D11VA from the CLI, which picks that decoder whenever `-hwaccel` is set. It fails the
4:4:4 fixture with "Function not implemented": this driver has no AV1 High profile decode.

### Pool model

Used VRAM above the level before the decoder opened, in MiB:

| Clip | Frames held, and growth | After release | After dispose |
|---|---|---|---|
| 1080p60 H.264 | 100: +369, 200: +669, 300: +969, 400: +1269, 500: +1569, 600: +1869 | +1869 | -30 |
| HEVC 720x1280 | 15: +251, 30: +273, 45: +294, 60: +316, 75: +338, 90: +338 | +338 | -25 |
| VP9 320x240 | 15: +49, 30: +52, 45: +55, 60: +58, 72: +60 | +60 | -30 |

Every clip was held to its last frame with no fault, no log line and no budget wait. At 1080p the
growth is 300 MiB per 100 frames, which is one 1920x1088 NV12 surface (3.0 MiB) per held frame. The
HEVC clip grows by 1.45 MiB a frame against a 1.32 MiB surface. Each decoder also has a cost that
does not grow with held frames: about 70 MiB for H.264, 230 MiB for HEVC and 45 MiB for VP9, not
broken down further. Released frames returned no memory until the decoder was disposed.

FFmpeg 9.0.1's source agrees:

- `vulkan_pool_alloc` (`libavutil/hwcontext_vulkan.c`) creates, binds and prepares a new image on
  every request and never checks a count. `vulkan_frames_init` hands it to `av_buffer_pool_init2`.
- `ff_vk_frame_params` (`libavcodec/vulkan_decode.c`) sets no `initial_pool_size`, and `decode.c`
  adds `extra_hw_frames` only to a non-zero initial size. On Vulkan, `extra_hw_frames` has no
  effect.
- A released frame goes back to the `AVBufferPool`, which keeps it until the frames context is
  freed. That is the high-water mark seen after release.

### Throughput

The same NVIDIA decoder reached through three APIs: 6000 frames of 1080p60 H.264, the mean of two
runs, from FFmpeg's CLI.

| `-hwaccel` | Frames kept on the GPU | CPU (user + system) | Frames read back to system memory | CPU (user + system) |
|---|---|---|---|---|
| `d3d11va` | 11.8 s, 508 fps | 13.9 s | 11.8 s, 508 fps | 14.5 s |
| `vulkan` | 7.9 s, 758 fps | 1.3 s | 10.6 s, 568 fps | 4.0 s |
| `cuda` | 7.1 s, 848 fps | 1.5 s | 7.2 s, 839 fps | 5.7 s |

The two runs of each row differed by less than 1% in wall time. Vulkan pays 2.6 s for the readback
where CUDA pays 0.1 s. The D3D11VA route uses about ten times the CPU of the other two. These runs do
not show why, and they measure FFmpeg's CLI, not FrameFlow's player.

## Findings

1. **Vulkan decodes H.264, HEVC and VP9 from the corpus on this driver.** Frames arrive as
   `GpuVideoFrame` with `Backend == Vulkan`, and `ReadbackToCpuBgra32` reads them back correctly.
2. **Vulkan's pool grows.** It belongs with VideoToolbox, not D3D11VA: nothing runs out, and each
   held frame costs one surface of VRAM that stays allocated until the decoder closes.
3. **AV1 never reaches hardware** on any backend, because of how the decoder is chosen (#417).
4. **The player cannot select Vulkan.** `PlayerBuilder` and `PassBuilder` hand the decoder
   `new HardwareDecodeOptions { Mode = ... }`, so `PreferredBackends` reaches only the decoder
   factory registered through dependency injection, and callers of `VideoDecoder.Open`. On Windows
   the player never binds Vulkan, which is not in the default order. On Linux it binds Vulkan only
   when VAAPI, CUDA, VDPAU and QSV all fail.
5. **Nothing presents a Vulkan frame.** `GpuVideoFrame.TryGetD3D11Texture` returns false for one,
   and `CompositionInteropVideoView.Present` drops a GPU frame it cannot unwrap.

## What this changes

- `DecodePoolGuard.SpareSurfaces` returns null for Vulkan, as it does for VideoToolbox. A Vulkan
  decoder is unguarded, opens with no `extra_hw_frames`, and is not refused for a path that holds
  without bound. Before this, it opened with `extra_hw_frames` it ignored, and the guard parked it at
  that count.
- A hardware test, `AVulkanDecoder_HoldsEveryFrame_WithoutABudget`, holds every frame of an H.264
  clip on Vulkan with the player's allowance of 5 and checks the decoder never parks. With Vulkan's
  spare count put back to 0 it fails: "The decoder parked at a budget of 5 with 5 frames held."
- ADR-0081 and frame-pool ownership record Vulkan as growable.
