# ADR-XXXX: Decoder choice is a Decide over decoder and backend

## Status

**Proposed (2026-10-08).** Not implemented. Assign the number at merge.

Amends the selection algorithm in [ADR-0033](ADR-0033-hardware-decode-selection.md) (step 7 and the
2026-10-08 amendment). Follows the shape of `DecodeBackendOrder` (#532) and the pure-core pattern of
[ADR-0055](ADR-0055-decode-protocol-as-a-pure-mealy-core.md).

Motivating issues: #417 (AV1 never decodes on hardware), #572 and #573 (a backend binds, then
refuses the first packet), #574 (CUDA MJPEG clips full-range JPEGs), #355 (capabilities have no
per-codec dimension). Not covered: #388 (frames carry no colour range) and #575 (`FromStill` cannot
open GIF, ICO or AVIF).

## Context

### Choice is made once, and the answers arrive later

`VideoDecoder.Open` chooses a decoder and a backend in one pass:

1. `avcodec_find_decoder(codecId)` returns one decoder.
2. `EnumerateCandidates` reads its `AVCodecHWConfig` table and keeps the `HwAccelCandidate`s whose
   backend has an initialised device.
3. `SortByPolicy` orders them by `PreferredBackends`, then by `PlatformDefaultOrder()`.
4. `TryBindSingle` tries each and records a `HardwareDecodeAttempt` for each failure. If none binds,
   `Auto` opens the software decoder and `Required` throws `HardwareDecodeUnavailableException`.

Whether a backend decodes a particular stream is not known at that point. It becomes known at four
moments, in this order, and a different piece of code watches each:

| Moment | Question | Watched by | On a negative answer |
| ------ | -------- | ---------- | -------------------- |
| Bind | Did the device and the codec context open? | `TryBindSingle` | Next candidate, then software |
| First packet | Did the first `avcodec_send_packet` succeed? | `SendCurrentInput` | `Auto`: reopen on the prepared software context (#573). `Required`: fault. |
| Engage | Did `get_format` choose the hardware pixel format? | `TrackHardwareEngagement`, per frame | The frames are software frames. Nothing else happens. |
| Output | Is the picture right? | Nothing | Nothing |

`get_format` runs inside the first send, because no `thread_count` is set, but its answer is read
from the first received frame. So an engagement answer always arrives after a packet was accepted,
and it can never replace the decoder.

### What a codec by backend sweep shows

A sweep ran 88 fixtures (84 opened) through software, CUDA, D3D11VA, D3D12VA, DXVA2 and Vulkan on the
bundled FFmpeg 9.0 LGPL build, on one machine with an NVIDIA RTX 30-series GPU. VAAPI, QSV and
VideoToolbox were not measured.

- **Support follows the stream.** HEVC 4:4:4 engages on CUDA and Vulkan and is software on D3D11VA,
  D3D12VA and DXVA2. H.264 4:4:4 is software on every backend. `HardwareDecodeCapabilities` lists
  which devices opened, and profile, chroma format and bit depth decide the rest.
- **Bound is not engaged.** `Required` with an explicit backend returned software frames for H.264
  4:4:4, VP9 4:4:4, MPEG-2 4:2:2, MJPEG 4:2:2, H.263 on CUDA and FFV1 on Vulkan.
- **The first packet is refused for some streams.** Progressive and CMYK JPEG on CUDA (#572), HEVC on
  D3D12VA at 640x360 and ProRes on Vulkan fail `avcodec_send_packet`. Plain FFmpeg fails the same
  way for the HEVC and ProRes clips. An HEVC clip from another encoder at the same size decodes on
  D3D12VA, and so does a 1920x1080 clip, so the cause is not frame size alone.
- **The decoder is chosen before the backend.** `avcodec_find_decoder` returns `libdav1d` for AV1,
  which has no hardware configs, so AV1 has no candidate on any backend. FFmpeg's native `av1`
  decoder engages NVDEC on the same build. It has no software path: without a hardware config it
  fails `avcodec_send_packet`. H.264, HEVC, VP8 and VP9 are unaffected because their native decoders
  are registered first.
- **Some pairs give wrong output.** CUDA MJPEG expands full-range data as limited range (#574), for
  video and for stills. `Auto` picks CUDA for MJPEG because it is the only backend with a config.
- **`Auto` order is platform order.** On Windows D3D11VA precedes CUDA, so CUDA wins only where
  D3D11VA has no config: MJPEG, MPEG-4 part 2, VP8 and MPEG-1. On Linux VAAPI comes first and was
  not measured.

### Where the smell is

- **A candidate is the wrong size.** `HwAccelCandidate` names a backend. The sweep shows the unit is
  a decoder paired with a backend (#417, #355).
- **A fact about a stream and a backend has nowhere to live.** "Not CUDA for MJPEG" and "the native
  AV1 decoder when hardware is wanted" would each be a special case inside `Open`. ADR-0033 rejected
  an automatic "too cheap to accelerate" rule because cost depends on the consumer. A backend that
  gives wrong output is a correctness fact, and that rejection does not cover it.
- **The choice is split across layers.** `DecodeBackendOrder` and `HardwareFrameChoice` are pure
  `Decide` functions in `Playback.Core`. Candidate enumeration, sorting and `PlatformDefaultOrder()`
  sit in the `VideoDecoder` shell, and the last reads `OperatingSystem`. Nothing about ordering is
  tested today.
- **The first-packet fallback is a condition written inline.** `SendCurrentInput` holds it, and a
  change to it is a change to hot code.

Not explained by this: #574's wrong output starts in the NVDEC path and also reflects that frames
carry no range (#388). #575 has the same shape on the source side and is a separate decision.

## Decision

### 1. The choice of candidates is a `Decide`

Add `FrameFlow.Decoding.Core.DecoderChoice`, beside `DisplayGeometry` and `KeyframeSearch`. No FFmpeg
reference, no IO, no clock. It follows `DecodeBackendOrder`: a static `Decide` and a `Decision`
record that carries a `Reason` for the log.

```csharp
internal readonly record struct DecoderChoiceDecision(
    IReadOnlyList<HwAccelCandidate> Hardware,
    string SoftwareDecoder,
    string Reason);

internal static class DecoderChoice
{
    public static DecoderChoiceDecision Decide(
        StreamShape stream,
        string softwareDecoder,
        IReadOnlyList<HwAccelCandidate> configs,
        HardwareDecodeMode mode,
        IReadOnlyList<HardwareDecodeBackendKind> preferred,
        IReadOnlyList<HardwareDecodeBackendKind> platformDefault,
        HardwareDecodeBackendKind? borrowed,
        IReadOnlyList<KnownRefusal> refusals);
}
```

- `HwAccelCandidate` gains a `Decoder` name. It keeps `Kind`, `AvHwDeviceType` and `HwPixelFormat`,
  which binding needs.
- `configs` is what `EnumerateCandidates` produces today, from every eligible decoder, already
  intersected with the initialised devices.
- `StreamShape` is built from `VideoStreamInfo` and the codec parameters. It adds the profile,
  chroma format, bit depth and whether the source is a still.
- `preferred` is `DecodeBackendOrder.Decide`'s result and `platformDefault` is a value the shell
  supplies. The core ranks preferred, then platform default, then the rest, as `SortByPolicy` does,
  with a stable order. It never reads the operating system.
- `borrowed` keeps today's rule: a borrowed device fixes the backend, and no order applies.

### 2. The decoder is part of the candidate

The shell lists the registered decoders for the codec and offers a decoder as a hardware candidate
when it is a decoder, is not experimental, is not itself a hardware wrapper (`*_cuvid`, `*_qsv`,
`*_amf`, rejected in ADR-0033), and has configs of the device-context kind. Names are unique, so
`mpegvideo` does not duplicate `mpeg2video`.

The software candidate stays what `avcodec_find_decoder` returns today. `Disabled` is unchanged, and
for AV1 the software decoder stays `libdav1d`. For AV1 the hardware candidates are `av1` on each
backend with a config.

This needs `av_codec_iterate`, `av_codec_is_decoder` and a read of `AVCodec.capabilities` in
`FFAvCodec`.

### 3. The fallback context is built from the software candidate

Today `Open` prepares the first-packet fallback from whichever decoder bound. With AV1 that would be
the native `av1` decoder, which has no software path, so a host with a device and no AV1 engine
would move from working `libdav1d` playback to a fault. The prepared context is built from the
software candidate only.

### 4. First-packet fallback is a predicate, and replaces only to software

Add `FirstPacketFallback.Applies(mode, hasPreparedContext, anyPacketAccepted, CodecReturn)` in
`Decoding.Core`. It states the rule from the ADR-0033 amendment: under `Auto`, a faulted send before
any packet was accepted reopens on the software candidate, and nothing else does. `SendCurrentInput`
calls it in place of the inline condition. It reuses `CodecReturn` and adds no state machine.

A replacement after `Open` is always to software, never to another hardware backend. The player and
the pass read `BoundBackend` once, right after `Open`, and fix the frame domain, `YieldHardwareFrames`
and the pool budget from it. A swap to software leaves them valid, because the emitted domains
already include CPU. A swap to a different hardware backend would not, so cross-backend fallback
happens only inside `Open`'s bind loop. The swap to software clears `BoundBackend` after `Open`;
#579 tracks the two-read race that makes reachable.

A refusal at receive stays a fault, as the amendment says.

### 5. A known refusal is a row

Add `KnownRefusal`: a backend, a codec ID, a predicate over `StreamShape` and a `Reason` that cites
an issue. `Decide` drops a refused candidate and says so in the `Reason`.

A refusal applies under `Auto` when the backend was not asked for. A backend in `PreferredBackends`,
a borrowed device and `Required` keep the candidate: a caller who names a backend gets it, and
`Required` keeps its binding, as #574 describes. The first row is CUDA with MJPEG, for video and
stills, citing #574. It leaves with that issue. A row records a backend that gives wrong output or
none for a stream shape, and never a judgement about cost.

### 6. No public API changes

`HardwareDecodeOptions`, `HardwareDecodeMode`, `PreferredBackends`, `HardwareDecodeAttempt`,
`HardwareBackend` and `BoundBackend` keep their meaning and shape. Every new type is internal.

### 7. Slices

Each ships on its own and keeps the suite green.

1. **Extract `Decide` and add the decoder to the candidate.** Tests pin `Decide` with the orders
   passed as data for Windows, Linux and macOS, because nothing pins the order today. Build the
   fallback context from the software candidate (Decision 3), with a test for it. AV1 on hardware
   (#417) is the first visible change.
2. **Add `KnownRefusal` and the CUDA MJPEG row (#574).** The #573 tests use `Auto` with
   `PreferredBackends = [Cuda]`, which Decision 5 keeps, so they pass unchanged.
3. **Extract `FirstPacketFallback`.** Optional. It moves a condition and changes no behaviour.

## Consequences

### Positive

- Ordering, the decoder dimension and refusals are values. A test asserts them with no FFmpeg, no
  GPU and no media, and the `Reason` goes to the log.
- #417 and #574 become a decoder list and a row, not special cases in `Open`.
- The sweep's matrix becomes test data: each row is a `StreamShape` and the candidates expected.

### Negative

- The core cannot know what a backend does with a stream. It orders candidates, and the answers
  still come from running the decoder. The matrix was measured on one machine, so a row describes
  that hardware generation.
- It adds a small vocabulary (`StreamShape`, `KnownRefusal`, the decision record, one predicate) and
  moves enumeration out of `Open`, which is hot code with native ownership.
- A table of refusals goes stale unless rows are removed. Each names an issue and leaves with it.
- The design does not give the four moments one owner. Engagement and output remain unwatched by
  policy, and `Required` can still return software frames.

### Neutral

- Only the sequencing becomes pure, as in ADR-0055. Decoding stays in the native decoder.
- Audio has no hardware path and is unaffected.
- Slice 1 changes behaviour for AV1. A hardware-decoded AV1 stream takes the hardware paths: a
  fixed or growable pool with a budget, unbounded holders refused (`CheckFrameBudget`) and GPU
  frames by default when the path takes them. A player that loaded AV1 in software before can fail
  to load, or change its frame domain. This goes in `BREAKING-CHANGES.md` as a behaviour change.
- Slice 2 changes behaviour: `Auto` stops picking CUDA for MJPEG and uses software, or the next
  backend with a config.

## Alternatives considered

### Patch #417 and #574 in place

A decoder-name lookup in `Open` and a skip for MJPEG on CUDA under `Auto` are each small, and they
fix both reported symptoms. This is a fair choice, and the cost shows only later. The decoder
dimension and the refusal rows would be special cases inside the hot path, with no test that pins
ordering today. This ADR is that same work with a home for the rules.

### A bind protocol over the four moments

An earlier draft modelled bind, first packet, engage and output as a Mealy machine with six inputs
and a candidate position. Review found it did not earn its size. Engagement always follows an
accepted packet, so it cannot replace the decoder. A replacement after `Open` is only to software,
so the machine reduces to one predicate. A machine whose cells are mostly no-ops adds vocabulary and
a second reader of `avcodec_send_packet`'s return beside `DecodeProtocol`.

### Remember refused attempts for the life of the process

It would stop a playlist failing the same stream on every pass. #573 removed that symptom, and a
record keyed on stream shape needs a public-record change or a new internal type, and unsettled
decisions about size and transient causes. Not now.

### Decode stills in software by default

#574 fix 3 and #575 suggest it. It fixes still images, not MJPEG video, and it is a decision about
source kind that belongs with #575.

### Ask the driver what it decodes

`cuvidGetDecoderCaps`, the D3D11 and D3D12 decode profile queries and VAAPI surface attributes could
fill a per-codec table. Separate code per backend, outside FFmpeg, on the platforms hardest to test,
and it answers whether the hardware can decode and not whether FFmpeg's hwaccel accepts this stream.
A probe can later feed `StreamShape` rules.

### Decode a trial packet at open

`Open` runs before the demuxer has delivered a packet, and it would allocate a second device context.

### Leave it to `get_format`

That is the engage moment alone. It cannot reorder backends or name a decoder.

### Explicit hardware decoder names (`h264_cuvid`)

Rejected in ADR-0033 and unchanged. It ties choice to one vendor's wrapper.

## Open questions

1. **`Required` and software frames.** `Required` promises hardware. Today it returns software
   frames when the bind succeeds and `get_format` refuses. Failing the item matches the promise and
   changes behaviour for callers who rely on it not failing. This ADR does not change it.
2. **What a refusal is keyed on.** The D3D12VA HEVC refusal is not frame size alone. Whether a row
   can be written for it, and with which predicate, is not settled by one sweep.
3. **Linux and macOS.** VAAPI and VideoToolbox rows are unmeasured, and on Linux VAAPI outranks
   CUDA, so the MJPEG row may not describe the default there.
4. **Receive-side refusals.** A native `av1` decoder that refuses at receive rather than at send
   would fault under `Auto`, as the amendment says. The bundled build refuses at send.

## References

- [ADR-0033](ADR-0033-hardware-decode-selection.md): hardware decode capability probing and selection
- [ADR-0055](ADR-0055-decode-protocol-as-a-pure-mealy-core.md): the codec send/receive loop as a pure Mealy core
- #417, #532, #355, #388, #572, #573, #574, #575
