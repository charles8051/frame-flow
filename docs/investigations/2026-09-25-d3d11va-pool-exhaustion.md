# Reproduction: an exhausted D3D11VA decode pool

**Date:** 2026-09-25
**Issue:** #370. Feeds [ADR-0081](../adr/ADR-0081-fixed-pool-budget.md) and its phase-1 slices
(#229, #383, #384).

## Question

#370 read FFmpeg 9.0's source and concluded that when every slice of a D3D11VA pool is held
downstream, the H.264 decoder drops the picture with one log line and carries on, so FrameFlow sees
no error. ADR-0057 and `DecodePoolMetrics` describe the same event as the decoder stalling. Neither
had been observed.

## Method

A throwaway test in `FrameFlow.Decoding.Tests`, run on Windows with an NVIDIA RTX 30-series GPU and
the pinned FFmpeg 9.0 runtime:

1. Decode the clip in software and keep a thumbnail of every frame (every 8th pixel, BGR), keyed
   by PTS.
2. Open `VideoDecoder` with `HardwareDecodeMode.Required` and `YieldHardwareFrames = true`, queue
   every packet, and enumerate `DecodeAsync`, keeping every `GpuVideoFrame` it yields.
3. When the enumeration ends, read the held frames back and release them. If it ended with an
   exception, enumerate `DecodeAsync` again, reading back and releasing each frame.
4. Compare each hardware thumbnail with the software one for the same PTS by mean absolute
   difference per channel.

Clips: `test-1080p60-h264-aac.mp4` (600 frames, one keyframe) and
`test-portrait-hevc-pressure.mp4` (720x1280, 90 frames, two keyframes).

## Results

| | H.264 1080p60 | HEVC 720x1280 |
|---|---|---|
| Frames held when the pool ran out | 20 | 5 |
| FFmpeg log | "Static surface pool size exceeded." then "Failed to allocate a d3d11/nv12 frame from a fixed pool of hardware frames." | Same |
| What `DecodeAsync` did | Threw `InvalidOperationException: avcodec_send_packet failed.` | Same |
| `DecodeErrors` | 0 | 0 |
| Frames yielded after release and a new enumeration | 579 | 82 |
| Frames never produced | 1 | 3 |
| Difference from software before the gap (max) | 0.73 | 4.98 |
| Difference from software after the gap | 2.2 rising to 9.4, mean 6.3 | Max 4.6, mean 2.1 |

## Findings

1. **Exhaustion is a hard error, not a silent drop or a stall.** `avcodec_send_packet` returns an
   error, `DecodeDriver` throws, and the decode enumeration ends. In the player the graph faults:
   a single source reports `FatalError` and the player enters Error, and a playlist reports the
   item as faulted. #370's source reading predicted a drop; why the call fails instead is not
   established. `DecodeErrors` does not count it, because the driver throws rather than counting.
2. **Decoding after the fault uses a missing reference.** Resuming the enumeration after the
   release works, but the packet that failed is gone. The H.264 clip has no keyframe after the
   gap, and its pictures drift away from the software decode frame by frame. HEVC's hardware and
   software conversions already differ by as much as the drift would, so this run cannot show
   damage there.
3. **HEVC runs out at 5 held frames.** That is exactly what the player's own D3D11 path holds:
   `ClockSelectVideoSink`'s ring of 3, the presenter's slot of 1 and one frame in flight. H.264
   on this clip ran out at 20. The spare count depends on the codec and the stream, which is what
   #229's per-decoder ceiling and #384's `extra_hw_frames` are for.

## What this changes

- ADR-0081's guard (#383) replaces a fatal decode error, not a silent drop, with back-pressure.
- #384's allowance matters most for HEVC, where the player's path uses the whole spare count.
- The regression test for #383 is this reproduction with its assertion inverted: holding frames
  makes the decoder wait, and every frame arrives once they are released.
