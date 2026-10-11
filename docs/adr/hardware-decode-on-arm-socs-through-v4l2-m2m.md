# ADR-XXXX: Hardware decode on ARM SoCs reaches the V4L2 M2M decoders through a probed node

## Status

**Proposed (2026-10-11).** Drafted under a slug; the number is assigned at acceptance.

Amends Decision 2 of [ADR-0083](ADR-0083-decoder-choice-is-a-decide-over-decoder-and-backend.md) and
the rejection of explicit decoder names in [ADR-0033](ADR-0033-hardware-decode-selection.md), for the
V4L2 mem2mem decoders only. Motivating issues: #593 and #596, under the linux-arm64 epic #591. The
sink and presenter work in that epic (#594, #595) is not decided here.

## Context

### What the epic assumed, and what a device shows

Issue #593 proposes admitting the `*_v4l2m2m` decoders on ARM SoCs, and #596 proposes a linux-arm64
FFmpeg build with the v4l2-request hwaccel for the stateless decoders. Both rested on vendor
documentation. This ADR adds what was measured on one board and in FFmpeg's source.

**The board.** An Orange Pi 3 LTS (Allwinner H6, Armbian trixie, kernel 6.18.44) running the bundled
linux-arm64 FFmpeg 9.0 build. It has two video nodes:

| Node | Driver | Coded formats the node takes |
| ---- | ------ | ---------------------------- |
| `/dev/video1` | `cedrus` | `MG2S` (MPEG-2), `S264` (H.264), `S265` (HEVC), `VP8F` (VP8) |
| `/dev/video0` | `hantro-vpu` (G2) | `VP9F` (VP9) |

Every one of these is a slice or frame format. The node expects parsed slice data and per-frame
controls, and it does not take a bitstream. These are stateless decoders.

**The wrappers fail on it.** With the bundled build:

```
h264_v4l2m2m: Could not find a valid device
h264_v4l2m2m: can't configure decoder
Error while opening decoder: Invalid argument
```

`vp9_v4l2m2m` fails the same way. The failure is at `avcodec_open2`, which FrameFlow's bind loop
already records as a `HardwareDecodeAttempt` before it falls to software.

**The bundled build.** `ffmpeg -decoders` lists nine V4L2 M2M wrappers: `h263`, `h264`, `hevc`,
`mpeg1`, `mpeg2`, `mpeg4`, `vc1`, `vp8` and `vp9`, each named `<codec>_v4l2m2m`. `ffmpeg -hwaccels`
lists `cuda`, `drm`, `opencl`, `vulkan` and `amf`. The configuration has `--enable-libdrm`,
`--enable-vulkan` and `--disable-vaapi`. The library holds no `v4l2_request` symbol.

**Upstream.** The `libavcodec` trees of FFmpeg `master` and of the `n9.0` tag hold `v4l2_m2m*`,
`v4l2_buffers*`, `v4l2_context*` and `v4l2_fmt*`, and no `v4l2_request*` file (read through the GitHub
tree API on 2026-10-11). The hwaccels for MPEG-2, H.264 and HEVC were posted to ffmpeg-devel as a
patch series in August 2024, and the trees show no merge. #596 listed this as unverified. It is now
checked: the stateless path is not in FFmpeg 9.0.

### Two kinds of V4L2 decoder, and only one is reachable

| | Stateful | Stateless |
| - | -------- | --------- |
| Node takes | A bitstream (`H264`, `HEVC`, `VP90`) | Parsed slices (`S264`, `S265`, `VP9F`) |
| Reached in FFmpeg by | The `*_v4l2m2m` decoders, upstream | A v4l2-request hwaccel, out of tree |
| In the bundled build | Yes | No |
| Seen on | Not measured. Vendor documentation names the Raspberry Pi 4 H.264 decoder. | The H6 (Cedrus, Hantro) |

On upstream FFmpeg 9.0, an ARM SoC reaches hardware decode only through a stateful node. Of the boards
in the epic, vendor documentation puts that at the Raspberry Pi 4 H.264 decoder. It also puts the
Raspberry Pi 5, the Pi 4 HEVC decoder and the Rockchip decoders on the stateless side or on `rkmpp`,
which is out of tree as well. None of that was measured, except that the one board tried is stateless.

### Why the wrappers do not fit today

1. **No device type.** A wrapper opens its own `/dev/video*` node inside `avcodec_open2`. It has no
   `AVHWDeviceType`, no `AVCodecHWConfig` and no `hw_device_ctx`. `HwAccelCandidate` and
   `TryBindSingle` assume all three, and `HardwareDecodeBackendKind` has no member for it.
2. **They are not seen at all.** `HardwareDecoders` looks past the default decoder only when the
   default has no device-context config. The default `h264` decoder carries configs for every hwaccel
   the build compiled in, whatever the host has, so the lookup stops at `h264` and never reaches
   `h264_v4l2m2m`.
3. **They are excluded by flag.** The wrappers carry `AV_CODEC_CAP_HARDWARE` (`ffmpeg -h
   decoder=h264_v4l2m2m` lists `delay avoidprobe hardware`), which ADR-0083 Decision 2 uses to leave
   out `_cuvid`, `_qsv` and `_amf`.
4. **Engagement is read from a pixel format.** `TrackHardwareEngagement` compares each frame's format
   with the hardware pixel format of the binding. A wrapper returns system-memory frames, so the
   comparison reads "bound but software" for a decoder that is decoding in hardware.

### Why ADR-0033's rejection does not carry over

ADR-0033 rejected explicit decoder names such as `h264_cuvid` because they duplicate a path FFmpeg
also offers as the CUDA hwaccel, and they tie the choice to one vendor. A V4L2 M2M decoder is the
Linux kernel's generic interface, used by several SoC vendors. FFmpeg offers no hwaccel for a stateful
node, so on those SoCs the wrapper is the only hardware path the build has. The rejection reasons
describe `_cuvid`. They do not describe this.

## Decision

### 1. A backend kind for the V4L2 M2M decoders

Add `HardwareDecodeBackendKind.V4l2M2m`. It is a public enum member, so it goes in
`PublicAPI.Unshipped.txt` and in `BREAKING-CHANGES.md` as an addition that a caller's exhaustive
`switch` over the enum has to handle. `HardwareDecodeCapabilities.Available` lists it on Linux, with
`AvDeviceTypeName` `"v4l2m2m"`. It is not an FFmpeg device type, so `ClassifyBackend` and
`ToAvHwDeviceType` do not map it.

`PlatformDefault(OsFamily.Linux)` places it after Vulkan and before Drm. A host has a stateful
decoder node beside VAAPI or CUDA in no case measured, so the position decides nothing known.

### 2. The probe reads the nodes, and a pure function classifies them

The probe does not call `av_hwdevice_ctx_create`. It lists `/dev/video*` and runs `VIDIOC_QUERYCAP` and
`VIDIOC_ENUM_FMT` on each node that reports a mem2mem capability, which is `V4L2_CAP_VIDEO_M2M` or
`V4L2_CAP_VIDEO_M2M_MPLANE` in `device_caps` when `V4L2_CAP_DEVICE_CAPS` is set in `capabilities`, and
in `capabilities` otherwise. The H6 shows why: `capabilities` is `0x84208000` and `device_caps` is
`0x04208000` on Cedrus, which is single-planar, and `0x84204000` and `0x04204000` on Hantro, which is
multi-planar. It does not start streaming. The
result is a list of `V4l2Node` values (path, driver, card, coded formats the node takes, formats it
returns). It runs only on Linux, through the same `LibraryImport` pattern as the other native calls.

`FrameFlow.Decoding.Core.V4l2Nodes.Classify(nodes)` is pure. It returns, for each node, the codec IDs
it takes as a stateful decoder, and separately the codec IDs it takes only as slice data. A stateful
format is one in a fixed table of bitstream fourccs (`H264`, `HEVC`, `VP80`, `VP90`, `MPG2`, `MPG4`,
`H263` and the others a wrapper names). A slice format is one in a second table (`S264`, `S265`,
`VP8F`, `VP9F`, `MG2S`). A format in neither table is ignored.

The classifier mirrors the search the wrapper runs itself: a node whose coded input is the codec's
bitstream format and whose capture side returns a format the wrapper can map. When the two disagree, a
bind that fails at `avcodec_open2` and a first packet that faults are both already handled, so a wrong
prediction costs a fallback and not a failure.

`V4l2M2m` is `Initialized` when at least one node is stateful. Otherwise it is listed with
`Initialized` false and a `DiagnosticMessage` that names what was found, for the H6:

```
no stateful V4L2 decoder node; /dev/video1 (cedrus) takes only slice formats
S264, S265, MG2S, VP8F, and /dev/video0 (hantro-vpu) takes VP9F, which need a
stateless hwaccel this FFmpeg build does not have
```

A node that cannot be opened for permission is listed with the reason, not skipped silently.

### 3. A candidate that opens its own device

`HwAccelCandidate` gains a `Binding`: `DeviceContext` for every candidate today, and `OwnDevice` for a
wrapper. An `OwnDevice` candidate has no `AvHwDeviceType` and no hardware pixel format. `Decide` is
unchanged in signature and rule. It ranks by `Kind`, and `Kind` is `V4l2M2m` for the wrapper.

`HardwareDecoders` gains a third source, separate from the two in ADR-0083 Decision 2. It adds the
registered decoders named `*_v4l2m2m` for the codec, whatever the default decoder has, when the
classifier reports a stateful node for that codec. The `AV_CODEC_CAP_HARDWARE` test keeps excluding
every other wrapper, and the name test is the exception for this one. Nothing else changes for H.264,
HEVC, VP8 and VP9 on a host with no stateful node.

`TryBindSingle` binds an `OwnDevice` candidate by allocating the context from the wrapper decoder,
copying the parameters, and calling `avcodec_open2`, with no device context and no `extra_hw_frames`.
A failure there is a `HardwareDecodeAttempt` for `V4l2M2m`, as for any backend.

### 4. A wrapper that opened is engaged

The decoder has no `get_format` refusal to wait for. A wrapper that opened decodes on the node, and its
frames are in system memory. For an `OwnDevice` binding, `HardwareBackend` is `V4l2M2m` from the first
received frame, and `TrackHardwareEngagement` does not compare pixel formats.

### 5. Frames are CPU frames, copied out

`HardwareFrameChoice.ForBackend(V4l2M2m)` is CPU only, so a player that binds `V4l2M2m` fixes the CPU
domain and `EmittedDomains` is CPU. `DecodePoolGuard` has no pool for it: `ExtraSurfacesFor` is zero
and `CheckFrameBudget` has nothing to judge.

A frame the wrapper returns can alias one of the node's capture buffers, which its free callback
queues back to the node. The wrapper has a fixed count of them (`-num_capture_buffers`, default 20 in
the bundled build). If frames do alias, a holder keeps one of those 20. Until that is measured, the
decoder copies each frame into memory it owns before it returns the frame, so a holder
never pins a capture buffer. The copy is one memory copy per frame. It can be removed once a stateful
device shows the buffers are not pinned.

The wrapper can also return DRM PRIME frames. This ADR does not use them, because nothing downstream
takes a DRM frame (#594, #595).

### 6. The fallback is the one that exists

A wrapper that fails to open falls to the next candidate and then to software, as every backend does.
The first-packet fallback of ADR-0083 Decision 4 applies, and `Required` throws
`HardwareDecodeUnavailableException` with the attempt list.

A wrapper decodes asynchronously, so a refusal can surface at receive, or as silence that returns no
error at all. ADR-0083 left receive-side refusals open because the bundled hardware refused at send.
For this backend they are expected, and the candidate is not offered under `Auto` until a policy
covers them. The policy has three parts, and a packet count is not one of them. A count cannot tell a
stalled node from codec startup or frame reordering, so it is not used as a signal.

1. **An error before the first frame.** The decoder keeps the packets it has sent until the first
   frame is received. A receive error before then reopens on the prepared software context and sends
   the kept packets again. The keep is bounded by the node's input queue (`-num_output_buffers`,
   default 16), because a node cannot hold more than that unanswered. If a send blocks or fails at that
   bound with no frame, that is a refusal too.
2. **Silence before the first frame.** A shell-owned deadline, read from an injected `TimeProvider`
   and counted from the first send, treats a node that has produced nothing as refusing, with the same
   reopen. The core holds no clock: it receives "deadline passed" as an input, as the other cadence
   decisions do. The value is not chosen here. It comes from measuring startup latency on a stateful
   device, and the candidate stays out of `Auto` until it exists.
3. **After the first frame.** A receive error is a fault, as for any other backend. A silent stall is
   what any decoder's stall is: the player's existing stall handling sees that frames stopped, and
   the decoder does not swap.

The merge gate for slice 3 includes a stream the node refuses and a node that is made to go silent,
each of which must fall back to software, and not only a clip that decodes.

### 7. Stateless decoders are not part of this decision

The stateless path needs a linux-arm64 FFmpeg 9 with a v4l2-request patch set. #596 stays deferred and
is no longer conditional on how far the wrappers get: for the H6, Cedrus and Hantro, it is the only
route. Whether the patched hwaccel appears in `avcodec_get_hw_config` as a device-context config, which
the existing candidate path would take with no change, or as an internal config, which
`ReadHwConfigs` skips, is unknown until a build exists.

### 8. Slices

1. **The pure classifier** (`V4l2Nodes`, the fourcc tables, `V4l2Node`). No FFmpeg, no device. Tests
   take the H6's two nodes as fixtures and assert that both are slice-only and that neither is
   stateful, plus synthetic stateful nodes for the positive cases.
2. **The probe, the enum member and the capabilities entry.** On the H6 bench the entry is listed with
   `Initialized` false and the diagnostic above. On a host with no `/dev/video*` it is absent or
   uninitialised without an error.
3. **The candidate, the bind, engagement, the frame copy and the `ForBackend` row.** This slice has a
   success path that the H6 cannot exercise. It does not merge as supported until a stateful device
   decodes a corpus clip and falls back to software on a stream the node refuses, under the
   receive-side policy of Decision 6. The bench for that is a Raspberry Pi 4, or any board with a stateful
   decoder node.
4. **Documentation.** `BREAKING-CHANGES.md` for the enum member, and the epic's issues updated with
   the stateful and stateless split.

## Consequences

### Positive

- The H6 reports why it has no hardware decode, in the log and in the capabilities, where before it
  said nothing.
- The classification is a table and a function, testable with the measured nodes and no device.
- A stateful board gets hardware decode with no new public option, and `ExcludedCodecs` already turns
  it off per codec.
- #596 is scoped by evidence: the stateless boards need a patched build.

### Negative

- The success path is unverified on hardware until a stateful device is on the bench. Slices 1 and 2
  are verified on the H6. Slice 3 is not.
- Of the boards the epic names, only the Raspberry Pi 4 H.264 decoder is reachable on upstream FFmpeg,
  per vendor documentation. The epic's goal of hardware decode on the Pi 5 and the Rockchip boards
  depends on a patched FFmpeg that FrameFlow would build and maintain.
- A new public enum member breaks an exhaustive `switch`.
- The classifier duplicates part of the wrapper's own node search and can disagree with it.
- The per-frame copy costs memory bandwidth until it is shown unnecessary.

### Neutral

- Windows and macOS see no change.
- `Decide` keeps its shape. `FirstPacketFallback` gains a receive-side input for this backend, under
  the policy in Decision 6.
- Audio has no hardware path and is unaffected.

## Alternatives considered

### Software decode only on ARM

The Pi 5 and RK3588 have fast cores, and 1080p H.264 and HEVC decode in software there. This is a real
option for those boards and it is what happens today. It leaves the Pi 4 and the small SoCs, where
software HEVC and 1080p60 H.264 do not keep up, with no hardware path, and it leaves the capabilities
silent about why. It is what slices 1 and 2 give alone if slice 3 never lands.

### A decoder name option

A caller sets `DecoderName = "h264_v4l2m2m"` on the options. It needs no probe and no `Decide` change.
It is the explicit name ADR-0033 rejected, it fails the same way as the H6 on any stateless board, and
the failure reads as a bug in the caller's configuration. A probe knows before the bind.

### Offer the wrapper and rely on the bind loop

Skip the probe, the classifier and the capabilities entry. Offer `<codec>_v4l2m2m` as a candidate on
Linux ARM and let `avcodec_open2` fail where there is no stateful node. It is the smaller change, and
slice 3 does not depend on slices 1 and 2 to be correct. Three things argue against making it the
whole design. The record the bind loop keeps for a failure is `avcodec_open2 returned -22`, which does
not say that the node is stateless. A host that is not an SoC but has a webcam would have the wrapper
open every video node at each decoder open, unless the candidate is gated on the architecture, and an
ARM board with CSI cameras has the same nodes. And `HardwareDecodeCapabilities` lists every backend
with its reason, so a backend missing from it is a gap. Slices 1 and 2 are also the part that can be
verified on the H6 now. If the probe is judged too much before a stateful device exists, slice 3 can
go first behind an architecture gate and the probe can follow.

### Probe by opening each wrapper

Open `<codec>_v4l2m2m` for each codec at bootstrap and keep the ones that open. It reuses FFmpeg's own
search and writes no ioctl code. The wrapper's search opens every `/dev/video*` node in order,
including a webcam, once per codec, at startup on every Linux host. Reading capabilities of the mem2mem
nodes only is narrower.

### Admit the wrappers by a new flag test

Replace the `AV_CODEC_CAP_HARDWARE` exclusion with an allow-list of vendor wrappers. The wrappers are
named `<codec>_v4l2m2m` in all nine cases in the bundled build, and a name test says what it means.
A flag change would reopen the decision for `_cuvid` and `_qsv`.

### Build the stateless FFmpeg first

It answers the H6 and most of the epic's boards. It is a patched FFmpeg that FrameFlow maintains across
releases, and nothing yet shows that the wrappers do not already cover the cases that matter. #596
stays after the stateful path for that reason, and not for lack of need.

### Direct V4L2 requests from managed code

Drive the stateless decoders without FFmpeg, parsing slices in FrameFlow. It is a decoder, not a
selection change.

## Open questions

1. **What a stateful device returns.** The format of the wrapper's frames (NV12 or YUV420P), whether
   they alias capture buffers, and whether the copy in Decision 5 is needed. The Raspberry Pi 4 is the
   bench.
2. **The receive-side policy in Decision 6.** The deadline for the first frame, and how a node is made
   to go silent in a test.
3. **Seek and loop.** Whether `avcodec_flush_buffers` on a wrapper restarts the node cleanly in
   FrameFlow's seek and loop paths.
4. **The stateless hwaccel's config shape** in a patched build (Decision 7).
5. **DRM PRIME frames.** Whether the wrapper's DRM output is worth taking once a sink can use it
   (#594, #595).

## References

- [ADR-0033](ADR-0033-hardware-decode-selection.md): hardware decode capability probing and selection
- [ADR-0083](ADR-0083-decoder-choice-is-a-decide-over-decoder-and-backend.md): decoder choice is a
  `Decide` over decoder and backend
- #591, #593, #594, #595, #596
- The measurements are on #593 (comment of 2026-10-11).
- [Add V4L2 Request API hwaccels for MPEG2, H.264 and HEVC](https://ffmpeg.org/pipermail/ffmpeg-devel/2024-August/332034.html):
  the ffmpeg-devel patch series, August 2024
