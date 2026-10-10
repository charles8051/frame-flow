# Exploration: falling back from one hardware backend to the next

**Status:** Exploration. Nothing here is decided.

**Date:** 2026-10-09

**Issue:** #533. Builds on [ADR-0083](../adr/ADR-0083-decoder-choice-is-a-decide-over-decoder-and-backend.md),
which kept a replacement after `Open` to software only and left this open. The measurements were
made on one Windows machine with an NVIDIA RTX 30-series GPU and the bundled FFmpeg 9.0 build, with
`Auto` and `Required` driven through `VideoDecoder.Open`.

## The gap

`Auto` binds the first candidate that opens. If that backend is then refused for the stream, the
decoder falls back to software. It never tries the next hardware candidate.

There are two ways a backend is refused for a stream, and the decoder meets both after `Open`:

- **A refused first packet.** `avcodec_send_packet` fails (#572). `Auto` reopens on software.
- **A refused format.** The send succeeds, FFmpeg's `get_format` picks a software pixel format, and
  the context decodes in software while still holding the hardware device. `Auto` does nothing, and
  `HardwareBackend` reads `null` (`HwAccelEngagement`).

Neither tries the next backend. On this machine the sweep finds the gap in two files out of 37 that
some backend decodes on hardware:

| File | Engages on | Bound but decodes in software on | `Auto` ends |
| ---- | ---------- | -------------------------------- | ----------- |
| HEVC 4:4:4, 8-bit | CUDA, Vulkan | D3D11VA, D3D12VA, DXVA2 | software |
| HEVC 4:4:4, 16-bit | CUDA, Vulkan | D3D11VA, D3D12VA, DXVA2 | software |

`Auto` tries D3D11VA first on Windows, so it lands on a backend that cannot decode the profile, where
CUDA is fourth in the order and can. Seven more files that look like the same gap are MJPEG on CUDA,
which `Auto` now skips on purpose (#574). The issue names two hosts that were not measured here: a
Vulkan device without `VK_KHR_video_decode_queue`, and a VAAPI device without the codec's entrypoint.

## What is knowable, and when

The question is when the decoder can tell. A spike drove the first packets of 11 stream and backend
pairs through `SendCurrentInput` and read the codec context after each send:

| Stream | Backend | First send | `pix_fmt` after it | `hw_frames_ctx` |
| ------ | ------- | ---------- | ------------------ | --------------- |
| H.264 4:2:0 | D3D11VA | Ok | hardware | set |
| H.264 4:2:0 | CUDA | Ok | hardware | set |
| HEVC 4:2:0 (mp4 and ts) | D3D11VA | Ok | hardware | set |
| HEVC 4:2:0 | D3D12VA | **Fault** | hardware | set |
| HEVC 4:4:4 | D3D11VA | Ok | **software** | none |
| HEVC 4:4:4 | CUDA | Ok | hardware | set |
| HEVC 4:4:4 | Vulkan | Ok | hardware | set |
| MJPEG 4:2:2 | CUDA | Ok | **software** | none |
| VP9 4:4:4 | CUDA | Ok | **software** | none |
| VP9 4:2:0 | D3D11VA | Ok | hardware | set |

In all 11 the answer was on the context after the first packet. A refusal of the format shows as a
software `pix_fmt` and no `hw_frames_ctx`. A refusal of the packet shows as a `Fault`. No frame had
been received in any of them.

This holds because `get_format` runs inside the first send when the decode is single threaded, and
FrameFlow sets no `thread_count`. It would not hold for a stream whose first packet carries no
parameter sets, such as one cut mid-GOP: the decoder discards packets until it reaches a keyframe, so
`pix_fmt` stays unset. The eleven cases here, including an MPEG-TS one, all began at a keyframe.

So the decoder can decide on the first packet, still holding that packet, with nothing emitted. The
packet can go to the next candidate unchanged. That is the cheap case, and it is the one the sweep
shows.

## What a swap to another backend invalidates

The player builds its graph from what `Open` returns. In `SubstrateSession` and `PassBuilder`, right
after the decoder opens, it reads `BoundBackend` once and:

1. narrows the decision to keep frames on the GPU to the bound backend's memory domain
   (`HardwareFrameChoice.ForBackend`);
2. checks the path against the decoder's `EmittedDomains` (`FrameDomainMismatchFor`);
3. judges the path's frame budget against the bound backend's pool, fixed or growable
   (`CheckFrameBudget`, `DecodePoolGuard.Judge`);

and the decoder itself opened with `extra_hw_frames` sized for that backend and that path
(`ExtraSurfacesFor`).

A swap to a backend with another domain or another pool kind leaves 1 to 3 describing a decoder that
no longer exists, which is why ADR-0083 limited a replacement to software. But every one of them is
about frames that stay on the GPU. When the decoder downloads its frames to system memory, the graph
is the same whichever backend decoded them, and `EmittedDomains` already includes CPU because a
decoder can fall to software at any time. Nothing downstream can tell.

So the question is narrower than "can the decoder switch backends". It is what the decoder does about
frames when the new backend's domain is not the one the graph was built for.

## Options

| | Option | Gets the next backend | Cost |
| - | ------ | --------------------- | ---- |
| A | Keep the software fallback | No | None. Today. |
| B | Refuse a device before binding it (the issue's first fix: `AVVulkanDeviceContext.qf[]`, `vaQueryConfigEntrypoints`) | For the two hosts the issue names, at bind | Separate code per backend, outside FFmpeg. Cannot see a per-stream refusal such as HEVC 4:4:4 on D3D11VA. |
| C | At the first packet, try the next candidate. If its domain is not the graph's, download the frames. | For any refusal the first packet shows | A second reopen at the moment of fallback, under the codec lock. Frames from a swapped backend arrive on the CPU. |
| D | Prime before the graph is built: send the first packet inside `Open`'s caller, settle the backend, then read `BoundBackend` | Yes, and GPU frames stay GPU when the path takes the new domain | The session reads a packet from the demuxer during initialisation and the decoder must not be sent it twice. |
| E | Remember refusals, so the next item opens on the next backend | From the second item | A store keyed on backend, codec and stream shape. Does nothing for the first item. |

**B** does not cover the case the sweep finds, and it is the one the ADR already rejected as the first
step ("ask the driver"). **E** alone leaves the first item in software, and ADR-0083 cut it for want of a
motivating symptom. **D** is the complete answer and the most invasive, because it moves a decoder
decision into session initialisation and changes what the pump sends first. **C** needs no change to
the session or the graph.

## Recommendation: C

Decide on the first packet, hold it, and walk `Decide`'s candidates.

1. **Trigger.** After the first send, if `FirstPacketFallback` still has an unspent fallback: a `Fault`,
   or an `Ok` with a software `pix_fmt` and no `hw_frames_ctx`, means this candidate is refused. Take the
   next hardware candidate from the ordered list `Decide` returned, and software when none is left.
   `FirstPacketFallback.After` gains the observation as an input, and `ReopenOnSoftware` becomes
   `TryNextCandidate`. The rule covers a refusal settled on the first send. If `pix_fmt` is still unset
   after it, nothing is swapped and the decoder behaves as it does today: a later answer would have
   to carry the packets sent so far, because sequence headers and reorder state may sit in them, and
   that is open question 2.
2. **Reopen.** Bind the candidate with `TryBindSingle`, as `Open` did, close the old context and send the
   held packet again. On the first send the packet is the only state to carry, and nothing has been
   emitted. The discard level carries across, as it does for the software fallback. The candidate binds
   with no held frames, so `extra_hw_frames` is zero: it downloads, and a download releases each surface
   as it copies. The original count was sized for the backend `Open` chose and does not carry over.
3. **Frames.** The graph was built for the backend `Open` bound. If the candidate that holds is another
   backend, the decoder sets a private flag and downloads its frames, whatever `YieldHardwareFrames` says.
   `BoundBackend` stays the backend the decoder opened on, because that is what the graph was checked
   against. A separate current backend, which `HardwareBackend` already is, drives the decoder's own
   pool tracking, engagement tracking and readback. The pool guard has nothing to do for a decoder that
   downloads.
4. **Logs.** One warning per swap, naming the backend left, the one reached and why, as the software
   fallback does now.
5. **`Required`.** A refused first candidate would try the next hardware candidate too, which is what
   `Required` promises, and fail only when every candidate refuses. That makes the open question in
   ADR-0083 a decision: `Required` would no longer return software frames when a bind succeeds and
   `get_format` refuses. It is a behaviour change for any caller that relies on that, and is best made
   separately from the swap.

What this does not do: keep frames on the GPU after a swap to a backend the path was not checked for.
A player that asked for GPU frames from CUDA and got HEVC 4:4:4, say, decodes it on Vulkan and receives
CPU frames. That beats software decode and loses to D, and D can follow if a host needs it.

## What would be tested

- `FirstPacketFallback` stays pure. Its table gains the engagement observation, and the test asserts the
  answer for every send result with and without an unspent fallback and with and without a software
  `pix_fmt`.
- A decoder test on the HEVC 4:4:4 clip with D3D11VA and CUDA both initialised: `Auto` decodes on CUDA,
  `BoundBackend` still reads the opened backend, `HardwareBackend` reads CUDA, and 12 frames arrive as CPU
  frames. Skipped where no second backend engages.
- `Required` on the same clip, once decided.
- The sweep before and after: only rows where the first backend is refused for the stream should change.
- A mutation for each rule: never taking the next candidate, taking it without downloading the frames,
  and releasing the fallback before the observation.

## Open questions

1. **Does the swap belong in `Open`'s caller instead (option D)?** It keeps GPU frames, and it costs an
   initialisation read. The spike shows the first packet is enough, which is what makes D possible.
2. **Is the first packet always enough?** A stream that starts mid-GOP leaves `pix_fmt` unset after the
   first packet. Holding the packets sent so far until it is set is possible and unmeasured here.
3. **How many candidates to try.** Windows lists five. Each failed one costs a device and a context. A cap,
   or a remembered refusal (option E) to skip known failures, is a later step.
4. **Other hosts.** VAAPI, VideoToolbox and QSV were not measured, and the issue's own two cases (Vulkan
   without a decode queue, VAAPI without an entrypoint) are untested here.
