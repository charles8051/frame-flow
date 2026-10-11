# Exploration: a pure decode core for stateless hardware decoders

**Status:** Exploration. Nothing here is decided.

**Date:** 2026-10-11

**Issues:** #591 (the linux-arm64 epic), #593, #596, #602. Builds on the V4L2 M2M ADR draft
([hardware-decode-on-arm-socs-through-v4l2-m2m.md](../adr/hardware-decode-on-arm-socs-through-v4l2-m2m.md)).
That draft leaves stateless decoders out of its decision (Decision 7). It names "direct V4L2 requests
from managed code" as an alternative it does not take. This exploration examines that alternative and
compares it with the patched FFmpeg of #596.

## The question

Can FrameFlow decode video through one pipeline whose logic is pure, with FFmpeg behind it on
desktops and the stateless decoders of ARM SoCs behind it on Linux boards?

## Summary

- **Two layers can be pure, and they are different layers.** The sequencing above the codec is pure
  today: packet admission, the send and receive protocol, first-packet fallback and decoder choice, in
  `Decoding.Core` and `Decoding.Internal`. FFmpeg sits under it. The codec logic below it is opaque
  inside `AVCodecContext`: parameter sets, picture order, the decoded picture buffer (DPB), reference
  lists and output order.
- **A stateless decoder hands the codec logic back to the client.** The hardware keeps pixel
  surfaces. The client keeps everything else. For this hardware the codec logic can be a pure Mealy
  core over immutable values. ADR-0055 rejected purifying the whole decoder as impossible. That holds
  for FFmpeg's context. It does not hold here.
- **FFmpeg cannot back the codec core.** Its hwaccel interface and its coded-bitstream library are
  internal to libavcodec. FFmpeg can back the layer above the core, as one engine beside the stateless
  one. It stays the demuxer and the software fallback.
- **A patched FFmpeg reaches the same hardware with no new codec logic.** LibreELEC applies a
  v4l2-request patch set to FFmpeg 9.0.2 for Allwinner and Rockchip. It covers MPEG-2, H.264, HEVC,
  VP8, VP9 and AV1. It registers a device-context hwaccel with a new device type that outputs DRM
  PRIME frames. FrameFlow's candidate path already takes configs of that shape. This settles the open
  question in Decision 7 of the V4L2 ADR draft from source, before a build exists. Creating the
  device proves nothing, so the draft's node probe is still needed to gate it.
- **The pure core rewrites what FFmpeg's decoders already do.** FFmpeg's v4l2-request code is about
  4,900 lines because FFmpeg's H.264 and HEVC decoders already parse and manage references. The closest
  managed-language prior art, cros-codecs, spends about 6,200 lines of Rust on H.264 alone.
- **The engine seam does not exist yet in the player path.** `SubstrateSession` and `PassBuilder`
  build the FFmpeg decoder directly and cast to `VideoDecoder`. `DecodingPipeline` takes a concrete
  `VideoDecoder` and sends it `AVPacket` pointers. The registered `VideoDecoderFactory` is never
  resolved (#602). The seam belongs inside `VideoDecoder`, under its packet queue.
- **Most of the pure core is testable with no board.** It runs over conformance bitstreams on any
  machine. The kernel's `visl` driver accepts every stateless control on an x86 VM, and GStreamer's CI
  already runs its stateless client against it.
- **Recommendation:** take the patched FFmpeg for hardware coverage. Treat the pure core as a separate
  choice about owning codec logic, and spike its H.264 core with no hardware before committing to it.

## What a stateless decoder asks of its client

A stateful V4L2 node takes a bitstream and returns pictures. A stateless node takes one picture's
compressed data plus the decoding state for that picture, and writes one surface. Everything that
spans pictures is the client's.

### The uAPI per codec

From the kernel's
[stateless codec controls](https://docs.kernel.org/userspace-api/media/v4l/ext-ctrls-codec-stateless.html)
and [stateless decoder interface](https://docs.kernel.org/userspace-api/media/v4l/dev-stateless-decoder.html).
"Stable in" is the first release with the controls in the public uAPI headers.

| Codec | Output format | Controls (`V4L2_CID_STATELESS_…`) | Stable in |
| ----- | ------------- | --------------------------------- | --------- |
| H.264 | `S264` | `H264_SPS`, `_PPS`, `_SCALING_MATRIX`, `_SLICE_PARAMS`, `_PRED_WEIGHTS`, `_DECODE_PARAMS`, `_DECODE_MODE`, `_START_CODE` | 5.11 |
| VP8 | `VP8F` | `VP8_FRAME` | 5.13 |
| MPEG-2 | `MG2S` | `MPEG2_SEQUENCE`, `_PICTURE`, `_QUANTISATION` | 5.14 |
| VP9 | `VP9F` | `VP9_FRAME`, `VP9_COMPRESSED_HDR` | 5.17 |
| HEVC | `S265` | `HEVC_SPS`, `_PPS`, `_SLICE_PARAMS`, `_SCALING_MATRIX`, `_DECODE_PARAMS`, `_DECODE_MODE`, `_START_CODE`, `_ENTRY_POINT_OFFSETS`; `HEVC_EXT_SPS_ST_RPS` and `_LT_RPS` in 7.0 | 6.0 |
| AV1 | `AV1F` | `AV1_SEQUENCE`, `_TILE_GROUP_ENTRY`, `_FRAME`, `_FILM_GRAIN` | 6.5 |

H.264 and HEVC have two decode modes. A slice-based node takes one request per slice. A frame-based
node takes one request per picture and must not be sent slice parameters. VP8, VP9, AV1 and MPEG-2
take one frame per request.

### What the client computes

A reference picture is named by the timestamp of the capture buffer that holds it, read with
`v4l2_timeval_to_ns()`. Timestamps must be unique. The client copies a timestamp onto each output
buffer, and the node copies it onto the capture buffer it writes.

| Codec | Reference state the client supplies |
| ----- | ----------------------------------- |
| H.264 | A 16-entry DPB: per entry, the reference timestamp, `PicNum` or `LongTermPicNum`, `frame_num`, field flags, top and bottom POC, and valid, active and long-term flags. The bit sizes of `pic_order_cnt` and `dec_ref_pic_marking` in the slice header. For slice-based nodes, the reference lists after modification. |
| HEVC | The DPB as timestamp, long-term flag and POC. The current POC. The `PocStCurrBefore`, `PocStCurrAfter` and `PocLtCurr` index lists. The bit sizes of the short-term and long-term RPS. Per slice, reference indices, the data offset and entry points. |
| VP8, VP9 | The last, golden and alt-ref timestamps. For VP9 the client keeps the 8-slot reference map. |
| AV1 | `reference_frame_ts[8]`, kept by the reference frame update process (spec section 7.20), with order hints and `ref_frame_idx`. |

For H.264 the client parses the SPS, PPS and slice headers, computes picture order counts, runs
reference marking (the sliding window and the memory management operations) and bumps pictures out
in display order. For frame-based H.264 nodes the kernel builds the reference lists itself from the
DPB in `v4l2-h264.c`, so POC and `PicNum` must still be right.

The mainline drivers fix the decode mode, so a client that serves every driver implements the
slice-based superset:

| Driver | H.264 and HEVC mode | Start code | What the client adds |
| ------ | ------------------- | ---------- | -------------------- |
| `cedrus` (Allwinner) | slice-based | none | Per-slice parameters, reference lists after modification, prediction weights, one request per slice with the capture buffer held between them |
| `hantro` (Verisilicon) | frame-based | Annex B | The whole access unit with start codes |
| `rkvdec` (Rockchip) | frame-based | Annex B | As `hantro` |

Read from the control definitions on `master` (7.3-rc6) on 2026-10-11: `cedrus.c`, `hantro_drv.c`,
`rkvdec.c`.

### The request flow

From the [Request API](https://docs.kernel.org/userspace-api/media/mediactl/request-api.html):

1. `MEDIA_IOC_REQUEST_ALLOC` on the node's media device returns a request file descriptor.
2. `VIDIOC_S_EXT_CTRLS` with the request descriptor stores the picture's controls on the request.
3. `VIDIOC_QBUF` queues the output buffer with the request. Capture buffers are queued without one.
4. `MEDIA_REQUEST_IOC_QUEUE` submits the request.
5. Poll the request descriptor for `POLLPRI`, then dequeue both buffers.
6. `MEDIA_REQUEST_IOC_REINIT` resets the request for reuse.

A slice-based node sets `V4L2_BUF_FLAG_M2M_HOLD_CAPTURE_BUF` on every output buffer of a picture but
the last, so the slices land in one capture buffer. The media device is found from the video node
through `MEDIA_IOC_G_TOPOLOGY`, matching the node's major and minor numbers.

### Which boards have what

Mainline at 7.3-rc6, from the research behind this document:

| SoC | Stateless decode in mainline | Driver |
| --- | ---------------------------- | ------ |
| Allwinner H6 | H.264, HEVC, MPEG-2, VP8; VP9 | `cedrus`; `hantro` G2 |
| Rockchip RK3399 | H.264, HEVC, VP9; MPEG-2, VP8 | `rkvdec`; `hantro` |
| Rockchip RK3588 | H.264, HEVC (since 7.0); AV1 (since 6.5) | `rkvdec` VDPU381; `hantro` VPU981 |
| i.MX8M | H.264, MPEG-2, VP8; HEVC, VP9 (8MQ) | `hantro` G1; G2 |
| MediaTek MT81xx | H.264, VP8, VP9, HEVC, AV1 (per-SoC split unverified) | `mtk-vcodec` |
| Raspberry Pi 4 and 5 | None. The HEVC driver is at v6 on the list (March 2026), not merged. | `rpi-hevc-dec`, downstream only |

The Raspberry Pi 5 decodes only HEVC in hardware; its other codecs run in software
([Raspberry Pi documentation](https://www.raspberrypi.com/documentation/computers/processors.html)).
The Pi 4 H.264 decoder the V4L2 ADR draft names as stateful is `bcm2835-codec`, which exists only in
the Raspberry Pi kernel. On a mainline kernel neither Pi has a hardware video node.

### Capture formats are often tiled

| Driver | Capture formats |
| ------ | --------------- |
| `cedrus` | `NV12_32L32` (32x32 tiles), `NV12`, `NV21`, `YUV420`, `YVU420` |
| `hantro` on the H6 (G2) | `NV12_4L4`, `NV12`, `P010_4L4`, `P010` |
| `hantro` on i.MX8M | `NV12_4L4`, `NV12`, `NV15_4L4`, `P010` |
| `rkvdec` | `NV12`, `NV15` (packed 10-bit), `NV16`, `NV20` |

Every driver above offers linear NV12 for 8-bit content, which `sws_scale` already converts. The
tiled formats save memory bandwidth. They need a detiling function, which is pure, or a DRM format
modifier on import (#594, #595). The Raspberry Pi HEVC decoder writes only a 128-byte column format,
`NV12MT_COL128`.

## Where FrameFlow stands

### The pure pieces that exist

| Piece | Where | What it decides |
| ----- | ----- | --------------- |
| `GopShedGate` | `Decoding/Internal/GopShedGate.cs` | Whether a packet is admitted or shed to the next keyframe |
| `DecodeProtocol` | `Decoding/Internal/DecodeProtocol.cs` | The send and receive sequence over `Ok`, `Again`, `EndOfStream`, `Fault` |
| `FirstPacketFallback` | `Decoding/Core/FirstPacketFallback.cs` | Whether a refused first send reopens on software |
| `DecoderChoice` | `Decoding/Core/DecoderChoice.cs` | Which decoder and backend pairs to try, in order |
| `KeyframeSearch` | `Decoding/Core/KeyframeSearch.cs` | Where a seek lands |

`DecodeDriver.RunAsync` cranks `DecodeProtocol` against `IDecodeCodec<TFrame>`. It is an internal
interface with four members: `TryBeginNextInputAsync`, `SendCurrentInput`, `ReceiveFrame` and
`BuildFrame`. ADR-0055 wrote it so "a real FFmpeg adapter and a test double implement alike". Only
`DecodeDriver.Classify(int)` knows FFmpeg's return codes. The interface is the right engine contract.

### The coupling that blocks a second engine

1. **The player builds the FFmpeg decoder by name.** `SubstrateSession.cs:521-538` and
   `PassBuilder.cs:340-350` call `DecoderFactories.CreateVideo(...)` and cast the result to
   `VideoDecoder`. They read `BoundBackend`, `EmittedDomains` and `CheckFrameBudget`, and set
   `YieldHardwareFrames` and `DropNewestWhenQueueFull`, on the concrete type.
2. **The registered factory is dead.** `FrameFlowDecodingOptions.VideoDecoderFactory` is registered as
   a `Func<IDemuxSession, IVideoDecoder?>` singleton, and nothing in the repository resolves that type
   (#602).
3. **The pump sends native packets to a concrete type.** `DecodingPipeline` takes `VideoDecoder?`.
   `QueueClonedPacketAsync` calls `VideoDecoder.SendPacketAsync(nint)` with a cloned `AVPacket`
   (`DecodingPipeline.cs:464`).
4. **The graph hooks test the type.** `DecoderSourceAdapters.cs:78-81` wires the budget and domain
   checks only when the decoder `is VideoDecoder`.
5. **No codec configuration crosses the boundary.** `VideoStreamInfo` has a codec name, size and frame
   rate. A client that parses the bitstream needs the extradata (`avcC`, `hvcC`) and, for MP4, the
   NAL length size.
6. **No V4L2 or `ioctl` interop exists** anywhere in `src/`.

Items 1, 3 and 4 say where the seam goes. Every caller already holds a `VideoDecoder`. The packet
queue, the shed gate, the pool watchdog and the driver loop inside it are not FFmpeg-specific. The
`AVCodecContext` work is FFmpeg-specific: the bind loop, `avcodec_flush_buffers`, `skip_frame`, the
hardware frame transfer and `sws_scale`. Splitting `VideoDecoder` there leaves every caller as it is.
The demuxer is FFmpeg in every design here, so `AVPacket` pointers can keep crossing the seam. The
stateless engine copies a packet's payload once, into the node's output buffer.

The patched FFmpeg route needs none of this. It is a new device type on the existing path.

## The shape

```
DemuxSession (libavformat)                                   unchanged
   │ AVPacket*
VideoDecoder: packet queue, GopShedGate, DecodeDriver loop    unchanged, minus the codec context
   │ IDecodeCodec<IVideoFrame>  (+ backend, domains, budget, flush)
   ├── FfmpegVideoEngine        AVCodecContext, hwaccels, sws_scale    today's code, moved
   └── StatelessVideoEngine
          ├── codec core        pure: parse, POC, DPB, reference lists, output order
          ├── control mapper    pure: core values to the node's control structs
          └── accelerator       shell: device, requests, buffers, poll, frames
```

### The codec core

One core per codec, each a Mealy machine over immutable values. H.264 as the worked case:

```csharp
namespace FrameFlow.Decoding.Stateless.Core;

internal readonly record struct PictureId(long Value);

internal abstract record CoreInput
{
    internal sealed record AccessUnit(ReadOnlyMemory<byte> Data, NalFraming Framing, long Pts, bool HasPts) : CoreInput;
    internal sealed record EndOfStream : CoreInput;
    internal sealed record Reset : CoreInput;          // a seek: drop the DPB, wait for an IDR or recovery point
}

internal abstract record CoreCommand
{
    internal sealed record Configure(CodedFormat Format, int MinSurfaces) : CoreCommand;
    internal sealed record Decode(PictureId Target, H264Picture Picture) : CoreCommand;
    internal sealed record Output(PictureId Picture, long Pts) : CoreCommand;
    internal sealed record Unreference(PictureId Picture) : CoreCommand;
    internal sealed record Refuse(string Reason) : CoreCommand;
}

internal static class H264Core
{
    public static H264State Begin(CodecConfig config, DriverTraits traits);
    public static (H264State Next, ImmutableArray<CoreCommand> Commands) Step(H264State state, CoreInput input);
}
```

- **State** is the active parameter sets, the POC state of the previous reference picture, the DPB,
  the next `PictureId` and a phase. A DPB entry holds a `PictureId`, frame number, POC, reference kind
  and whether it awaits output. The phase is awaiting a random access point, decoding, or refused.
- **`Configure`** comes first, and again on a resolution or format change after the outputs that drain
  the old DPB. `MinSurfaces` is the DPB size the SPS needs plus the picture being decoded.
- **`Decode`** carries the parsed SPS, PPS and slice headers, the slice data ranges, the DPB as the
  node must see it and the header bit sizes. Nothing in it is a V4L2 type.
- **`Output`** is in display order, from the bumping process.
- **`Unreference`** says the core no longer needs a picture. Its surface is free once the frame built
  from it is also released downstream.
- **`Refuse`** is a stream the core does not handle: interlaced H.264, 4:2:2, more than 8 bits, or a
  profile the node's profile menu does not list. Before the first `Decode` it is a first-packet
  refusal, and `FirstPacketFallback` already reopens on software for that. After it, it is a fault.
- **`DriverTraits`** is the decode mode and start code the node reports. It decides whether the core
  builds per-slice parameters and reference lists.

`PictureId` doubles as the buffer timestamp that names a reference, so the shell keeps no table between
the two.

### The control mapper

`V4l2H264Controls.From(Decode, DriverTraits)` turns a `Decode` into the bytes of the kernel's control
structs. It is pure, and it is the only code that knows the uAPI layout. Each struct gets a size test
against the kernel header. Keeping V4L2 types out of the core leaves room for other stateless APIs
without committing to them (see [Other stateless APIs](#other-stateless-apis)).

### The accelerator shell

`V4l2RequestAccelerator` opens the video node and its media device, sets the formats, allocates
buffers and starts streaming. On a frame-based node, a `Decode` becomes one request: it fills an
output buffer with the access unit, sets the controls on the request, queues the buffer and queues the
request. On a slice-based node, a `Decode` becomes one request per slice. Each carries that slice's
data and slice parameters, all share the picture's timestamp, and every output buffer but the last
sets `V4L2_BUF_FLAG_M2M_HOLD_CAPTURE_BUF`, so the slices land in one capture buffer. The shell polls
for completion and turns a finished capture buffer into an `IVideoFrame`. It owns the file
descriptors, the mapped memory and the wait.

The surface ledger is pure. A surface is free when three things hold: the core has unreferenced it,
no queued request names it as a reference or a target, and no frame built from it is alive. The
second condition matters because the core runs ahead of the node. It can unreference a picture while
an earlier request that predicts from it is still queued, and reusing that surface would corrupt the
earlier decode. A request's pins are released when it completes, and a reset or format-change drain
waits for them. If frames are copied out to CPU memory, no frame pins a surface. The capture buffer
count is then `MinSurfaces` plus the requests in flight. If frames alias capture buffers, the count
adds the held-frame budget the player already computes (ADR-0081).
`DecodePoolGuard.ExtraSurfacesFor` does that arithmetic for `extra_hw_frames` today, and the new
backend would be a row in `SpareSurfaces` with no spare.

### How the engine speaks `IDecodeCodec`

- `SendCurrentInput` steps the core on the packet and starts the `Decode` commands it returns. It
  returns `Again` when no surface is free, which makes the driver drain frames first. `DecodeProtocol`'s
  `DrainingThenRetry` row already handles that case.
- `ReceiveFrame` returns `Ok` when the next `Output` picture has finished decoding. It returns `Again`
  when it has not and the node has room for more work, so the driver feeds another packet. It blocks
  on the poll when the node is full. `avcodec_receive_frame` on a hardware context blocks the same way.
- A flush steps the core with `EndOfStream`, which bumps every remaining picture.

### How it enters `Decide`

The candidate is a decoder and backend pair, as ADR-0083 made it. The new decoder is the managed core
for the codec, and the new backend kind is the V4L2 request path. The V4L2 ADR draft's probe
(Decision 2) already reads every mem2mem node. Its classifier returns, per node, the codec IDs the
node takes only as slice data (`S264`, `S265`, `VP9F` and the others), so availability is per codec,
not per node. The draft's slices 1 and 2 serve both routes.

A coded format says the node takes the codec. It does not say the node takes this stream. Decode mode
and start code do not narrow it, because the core handles both modes. Profile, bit depth and chroma
format do. The probe can read the node's profile and level menus for the codec (for H.264,
`V4L2_CID_MPEG_VIDEO_H264_PROFILE`), and the core refuses what they exclude before its first `Decode`.
A refusal there falls back to software at the first packet, which costs a reopen and not a failure.

## What FFmpeg can back

| Role | Possible | Why |
| ---- | -------- | --- |
| Demuxer | Yes | Unchanged in every option |
| Engine beside the stateless one | Yes | Today's `VideoDecoder` internals, behind the seam |
| Software fallback | Yes | `FirstPacketFallback` already reopens on the software candidate |
| Converter for CPU frames | Yes | `sws_scale` takes linear NV12 from a mapped capture buffer |
| Accelerator under the codec core | No | `FFHWAccel` is declared in `hwaccel_internal.h`, and the hwaccel list is fixed at build time in `hwaccels.h`. There is no plugin interface. |
| Parser for the codec core | No | The coded-bitstream (`cbs_*.h`) headers are not installed. `av_parser_parse2` returns frame boundaries and picture type, not the syntax a DPB needs. |
| Oracle for the parser | Yes | The `trace_headers` bitstream filter logs every header syntax element of a stream |
| Oracle for the core | Yes | Output order and frame count against a software decode of the same file |

### The patched FFmpeg

The v4l2-request hwaccels are not upstream. The August 2024 series on ffmpeg-devel became
[pull request #20847](https://code.ffmpeg.org/FFmpeg/FFmpeg/pulls/20847), which adds MPEG-2, H.264,
HEVC, VP8, VP9 and AV1. It is a draft with merge conflicts, read on 2026-10-11. Its last push was on
2026-03-12. On 2026-08-14 a comment for the author said a rebase was planned.

Distributions carry it anyway.
[LibreELEC's FFmpeg package](https://github.com/LibreELEC/LibreELEC.tv/blob/master/packages/multimedia/ffmpeg/package.mk)
builds FFmpeg 9.0.2 and applies `patches/v4l2-request/0001-v4l2-request.patch` for its Allwinner and
Rockchip targets, with extra patches for the RK356x, RK3576 and RK3588. Its Raspberry Pi target applies
a different patch set. Read from the patch on 2026-10-11:

- It adds `AV_HWDEVICE_TYPE_V4L2REQUEST` at the end of `enum AVHWDeviceType`, after
  `AV_HWDEVICE_TYPE_OHCODEC`. Existing values do not move.
- Each codec registers `HW_CONFIG_HWACCEL(1, 0, 0, DRM_PRIME, V4L2REQUEST, …)`. That is a
  device-context config with `AV_PIX_FMT_DRM_PRIME` as the hardware pixel format.
- The frames context implements `transfer_data_from` and `map_from`, so `av_hwframe_transfer_data` can
  copy a frame to system memory.
- It is configured with `--enable-libudev --enable-v4l2-request`.
- `device_create` returns success without looking for a decoder; the enumeration is a `TODO` in the
  patch. The decoder is found when the frames context is initialised, during the first packet, by
  enumerating media devices through libudev and probing their video nodes for the codec's slice
  format.

`ReadHwConfigs` (`VideoDecoder.HwAccel.cs:513`) keeps configs with the device-context method, so the
existing candidate path would take this one. It needs a `HardwareDecodeBackendKind` member, a mapping
in `ClassifyBackend` and `HardwareDecodeProbe.ToAvHwDeviceType`, and the CPU readback that every
hardware backend already has.

The bootstrap probe cannot rely on `av_hwdevice_ctx_create` for this device type. It would succeed on
any Linux host, with or without a stateless decoder. The V4L2 ADR draft's probe and classifier (its
slices 1 and 2) already find the slice-only nodes, and they would decide whether the backend is
initialised. A decoder that is still not found surfaces at the first packet, as a refused format or a
refused packet. `FirstPacketFallback` and the engagement check already handle both.

The upstream request has moved past this. Its review moved device probing into libavutil, and on
2026-03-13 its author reported support for the Raspberry Pi HEVC decoder posted for mainline. Whether
the device opens and frames transfer on a board is not measured for either version. The libudev
dependency is a question for a portable build.

## Other stateless APIs

VAAPI, D3D11 and D3D12 video (DXVA), NVDEC's decode call and Vulkan Video are stateless in the same
sense. The client supplies picture parameters, slice parameters and the reference set. Vulkan's
specification says the application "maintains the association between DPB slot indices and
corresponding video picture resources". GStreamer, Chromium and cros-codecs each drive several of
these from one codec core. FrameFlow has no reason to drive them itself, because FFmpeg already does.
They matter here only as a constraint: the core's values should not be V4L2 structs.

## Prior art

| Project | Codec core | Backends | Interface |
| ------- | ---------- | -------- | --------- |
| GStreamer `gst-libs/gst/codecs` | Base classes own parsing, POC, marking, reference lists, the DPB and bumping | v4l2codecs, VA, NVDEC, Vulkan, D3D11 and D3D12 through DXVA | `new_sequence`, `new_picture`, `start_picture`, `decode_slice`, `end_picture`, `output_picture`. Installed, but marked unstable API. |
| Chromium `media/gpu` | `H264Decoder`, `H265Decoder`, `VP9Decoder`, `AV1Decoder` | VAAPI, V4L2 stateless, D3D11 | `H264Accelerator`: `SubmitFrameMetadata`, `SubmitSlice`, `SubmitDecode`, `OutputPicture` |
| cros-codecs (Rust, BSD-3-Clause) | `StatelessDecoder` over per-codec parser, DPB and picture modules | VAAPI, V4L2 (H.264, VP8, VP9, AV1), and a dummy backend for running the codec logic with no hardware | `StatelessH264DecoderBackend`: `new_sequence`, `new_picture`, `start_picture`, `decode_slice`, `submit_picture` |
| FFmpeg | The native decoders (`h264dec`, `hevcdec`, `vp9`, `av1dec`) | Every hwaccel | `FFHWAccel`: `start_frame`, `decode_slice`, `end_frame`. Internal. |
| SharpMP4 (C#, MIT) | Syntax parsers for H.264, H.265, H.266, VP9, AV1, including slice headers, reference list modification and reference marking | None | No POC or DPB logic |

The split is the same in every one: a codec core that parses and owns references, and a backend that
turns one picture into API calls.

### Size

Line counts of existing code, comments included:

| Codec | GStreamer (C): parser, base class, picture/DPB, v4l2codecs element | cros-codecs (Rust): parser, DPB, picture, decoder | FFmpeg PR #20847 (C): hwaccel |
| ----- | ------------------------------------------------------------------ | ------------------------------------------------ | ----------------------------- |
| H.264 | 3,986 + 3,385 + 1,225 + 1,673 | 3,023 + 1,243 + 415 + 1,501 | 532 |
| HEVC | 5,609 + 2,313 + 579 + 1,922 | 4,784 + 276 + (in decoder) + 1,365 | 752 |
| VP9 | 1,802 + 675 + 0 + 1,364 | 1,478 parser | 530 |
| AV1 | 5,082 + 842 + 140 + 1,681 | 4,216 parser | 636 |
| Shared | `gstv4l2decoder.c` 1,569 | | `v4l2_request.c` 784, `hwcontext_v4l2request.c` 1,126 |

FFmpeg's hwaccel files are small because they are only the mapper and the shell. The parser and DPB
they need are FFmpeg's own decoders. A managed core carries the cros-codecs column as well as the
FFmpeg one.

### Licensing

FrameFlow is PolyForm Small Business. GStreamer and FFmpeg are LGPL, and a C# port of either's codec
logic would be a derivative of LGPL code. The specifications are free to read: ITU-T H.264 and H.265,
the VP9 bitstream specification and the AV1 specification. Chromium's `media/gpu` and cros-codecs are
BSD-3-Clause and can be ported with their notices kept. A core written from the specifications, with
the BSD sources as a cross-check, keeps FrameFlow's license as it is. A patched FFmpeg keeps the LGPL
obligations FrameFlow already meets for the bundled build.

## Risks of the pure core

- **Broken streams.** FFmpeg's parsers have had years of fuzzing and field reports. A new parser has
  to be total: every byte sequence gives a value or a typed error, never an exception, never an
  unbounded loop. It needs a fuzz harness before it ships.
- **Kernel drivers parse what they are given.** The core should check every value against the range
  the specification allows before it submits, and not rely on the driver to.
- **Driver differences.** Decode mode, start code, capture formats and supported profiles differ per
  driver, and a driver can have defects of its own. `DriverTraits` and the profile menus cover the
  reported part. Defects need a bench per SoC.
- **Interlaced H.264** is a large part of the DPB logic: field pairs and field reference lists.
  Refusing it in the first slice leaves interlaced transport streams on software.
- **Resolution changes mid-stream** need the drain, `STREAMOFF` and reallocation sequence on the
  capture queue. The shell owns it and the core orders it.
- **Two decoders disagree.** A stream can decode on FFmpeg and fail on the core, or decode
  differently. The fallback catches refusals and faults, not wrong pictures.

## Testing without a board

| Layer | Test | Needs |
| ----- | ---- | ----- |
| Parser | Totality fuzzing. Syntax values against FFmpeg's `trace_headers` output for the same stream. | Nothing |
| Core | Conformance bitstreams through `Step`. Output order and frame count against FFmpeg's decode of the same file. Command transcripts as golden files. | The bitstreams |
| Mapper | Struct sizes against the kernel header. Control payloads field by field against a `v4l2-tracer` trace of GStreamer's v4l2codecs on `visl`, after normalising reference timestamps. | A Linux VM, once per golden trace |
| Shell | The real request flow against `visl`: buffer and request sequence, reference resolution, no pixels | A Linux VM with the module |
| End to end | One MD5 per conformance vector through Fluster, and corpus checksums against a software decode | A board |

[`visl`](https://docs.kernel.org/admin-guide/media/visl.html) is the kernel's virtual stateless
decoder, merged in 6.2, with AV1 since 6.8. It accepts every stateless control in both decode modes,
and the hold flag. It does not decode. It writes a test pattern carrying the sequence number, the
timestamp and each reference's resolved buffer into the capture buffer. It logs the control payloads to
ftrace. GStreamer's CI runs Fluster against it in a virtme-ng VM (`tests/virtme/virtme-run.sh`), with
its own suites in `ci/fluster/visl_references`. Those checksums are of `visl`'s test pattern, not of
decoded pictures. They test that the client sends the same request sequence and resolves the same
references, and they say nothing about pixels. The script also loads `visl` with `stable_output` and
`codec_variability`, which the mainline driver documentation does not list. Pixel conformance needs a
board. A container on the shared CI runner cannot load a kernel module, so the `visl` tests need a VM.

[`v4l2-tracer`](https://www.mankier.com/1/v4l2-tracer) records the stateless ioctls and control
payloads as JSON, field by field, and can replay a trace onto another node. Reserved bytes are not
recorded, and GStreamer names a reference by `frame_num × 1000` ns. So a comparison is per field after
normalising timestamps, not byte for byte.

## Options

| | Patched FFmpeg (#596) | Pure core and V4L2 shell | Software only |
| - | --------------------- | ------------------------ | ------------- |
| Codecs | MPEG-2, H.264, HEVC, VP8, VP9, AV1 | One per slice of work, H.264 first | All |
| Boards | Allwinner, Rockchip, i.MX8M, MediaTek on mainline. The Pi needs the Raspberry Pi patch and kernel. | The same, as each codec lands | All |
| What FrameFlow owns | A linux-arm64 FFmpeg 9 build applying a third-party patch, followed per release | A codec core per codec, in C#, plus the engine seam in `VideoDecoder` | Nothing |
| Codec logic | FFmpeg's | FrameFlow's, pure | FFmpeg's |
| Testable without a board | Only the selection logic | Parser, core and mapper; the shell on a VM | Not applicable |
| Fits the existing candidate path | Yes: a device-context config, gated by the V4L2 node probe | After the engine seam | Yes |
| Frames | DRM PRIME, or CPU through `av_hwframe_transfer_data` | CPU NV12 first, DMA-BUF export later | CPU |
| Upstream risk | The patch is out of tree and its upstream request has stalled | None | None |
| License | LGPL, as today | Unchanged if written from the specifications | As today |

## Recommendation

1. **Take the patched FFmpeg for hardware coverage.** Build linux-arm64 FFmpeg 9 with LibreELEC's
   v4l2-request patch, following the macOS build. Land the V4L2 ADR draft's probe and classifier to
   gate the backend. Add the backend kind and the mapping, and decode to CPU frames first. On the H6
   this covers H.264, HEVC and VP9 with no new codec logic in FrameFlow.
   It amends #596, which assumed the patch set's FFmpeg 9 status was unknown. It also settles Decision 7
   of the V4L2 ADR draft. That needs an ADR of its own.
2. **Decide the pure core on what it is: owning codec logic.** It buys a decoder that is pure and
   testable without hardware, independence from an out-of-tree patch, and the codec state as values.
   It costs a codec core per codec, as large as the cros-codecs column above, plus the engine seam in
   hot code. None of that is needed for hardware coverage once step 1 lands.
3. **If the pure core is wanted, spike it with no hardware.** `FrameFlow.Decoding.Stateless`, an assembly
   with no FFmpeg reference, holding the H.264 parser, POC, DPB, reference lists and output order for
   progressive 8-bit 4:2:0. Pass mark: syntax values equal `trace_headers` output, and output order and
   frame count equal FFmpeg's on every conformance stream and corpus clip the core does not refuse.
   The fuzz harness must also find nothing. Then the shell against `visl` in a VM, then `cedrus` on
   the H6. `cedrus` is slice-based, so it exercises the whole core. The engine seam is split from
   `VideoDecoder` only once the core passes.

The two routes do not exclude each other. Both enter `Decide` as candidates, so the core can land
codec by codec beside the patched hwaccel and be ranked above it where it passes.

## Open questions

1. Whether the patched hwaccel finds `cedrus` and `hantro` on the H6 under FrameFlow's build, and what
   `av_hwframe_transfer_data` returns for their capture formats.
2. Whether a portable linux-arm64 build can link libudev, or needs it from the host.
3. How soon LibreELEC's patch follows an FFmpeg 9 point release, and who rebases it if LibreELEC
   stops.
4. The capture buffer count a node accepts, and whether it covers the DPB, the held-frame budget and
   the requests in flight on the small SoCs.
5. Whether to export capture buffers as DMA-BUF for #594 in the first slice, or copy to CPU first.
6. How the core's `Refuse` and a node's profile menu combine with `KnownRefusal` rows.

## References

- [ADR-0055](../adr/ADR-0055-decode-protocol-as-a-pure-mealy-core.md): the codec send and receive loop as a pure Mealy core
- [ADR-0081](../adr/ADR-0081-fixed-pool-budget.md): the fixed pool budget
- [ADR-0083](../adr/ADR-0083-decoder-choice-is-a-decide-over-decoder-and-backend.md): decoder choice is a `Decide` over decoder and backend
- [The V4L2 M2M ADR draft](../adr/hardware-decode-on-arm-socs-through-v4l2-m2m.md)
- #591, #593, #594, #595, #596, #602
- [Stateless codec controls](https://docs.kernel.org/userspace-api/media/v4l/ext-ctrls-codec-stateless.html),
  [the stateless decoder interface](https://docs.kernel.org/userspace-api/media/v4l/dev-stateless-decoder.html),
  [the Request API](https://docs.kernel.org/userspace-api/media/mediactl/request-api.html)
- [FFmpeg pull request #20847](https://code.ffmpeg.org/FFmpeg/FFmpeg/pulls/20847) and
  [the August 2024 series](https://ffmpeg.org/pipermail/ffmpeg-devel/2024-August/332034.html)
- [LibreELEC's FFmpeg package](https://github.com/LibreELEC/LibreELEC.tv/tree/master/packages/multimedia/ffmpeg)
- [GStreamer codecs library](https://gitlab.freedesktop.org/gstreamer/gstreamer/-/tree/main/subprojects/gst-plugins-bad/gst-libs/gst/codecs),
  [Chromium `media/gpu`](https://chromium.googlesource.com/chromium/src/+/main/media/gpu/),
  [cros-codecs](https://github.com/chromeos/cros-codecs),
  [SharpMP4](https://github.com/jimm98y/SharpMP4)
- [visl](https://docs.kernel.org/admin-guide/media/visl.html), [v4l2-tracer](https://www.mankier.com/1/v4l2-tracer),
  [Fluster](https://github.com/fluendo/fluster)
- [Raspberry Pi HEVC decoder, v6](https://lkml.iu.edu/hypermail/linux/kernel/2603.0/06622.html)
