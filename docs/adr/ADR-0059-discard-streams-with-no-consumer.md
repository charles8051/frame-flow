# ADR-0059: Discard decodable streams that have no consumer

## Status

Accepted.

**Date:** 2026-06-05
**Related:**
- ADR-0003 (audio-master synchronization policy — a null audio sink falls back to the wallclock pacer)
- ADR-0009 (threading and concurrency model — one demux loop reads the single `AVFormatContext`)
- ADR-0044 (sink ownership and disposal)
- ADR-0036 (decoded media stream decoupled from playback — where the demux pump lives)

## Context

A media source is opened once and demuxed by a **single** pump,
`DecodingPipeline.RunDemuxPumpAsync`. That pump reads packets sequentially from
the one `AVFormatContext` (ADR-0009) and routes each packet, by stream index, to
the matching decoder's bounded packet queue:

```csharp
int readRet = FFAvFormat.av_read_frame(fmtCtx, packetPtr);     // one read for ALL streams
...
if (streamIndex == _videoStreamIndex && _videoDecoder is not null) { /* clone + queue */ }
else if (streamIndex == _audioStreamIndex && _audioDecoder is not null) { /* clone + queue */ }
```

Each decoder queue is bounded (`AudioDecoder`: 512 packets, `FullMode = Wait`) and
drained by that decoder's `DecodeAsync`, which only runs when the decoder is wired
as a source node in the playback graph. A decoder is wired into the graph only
when its stream has a **consumer** — a sink, or a consumer-supplied
configurator/tap.

`SubstrateSession`, until now, created the audio decoder **unconditionally**
whenever the source had an audio stream, regardless of whether an audio sink or
audio configurator existed. The video decoder was likewise created whenever a
video stream existed.

### The bug

Play an A/V source with **no audio sink and no audio configurator** — e.g.
`PlaybackController.Create(audioSink: null)`, the path a downstream kiosk uses for
muted signage video (which then falls back to the wallclock pacer per ADR-0003).
The audio decoder is still created and the pump still routes audio packets into
its queue, but nothing drains that queue — the audio branch is never built in the
graph (it requires a sink or configurator). So:

1. The pump reads interleaved audio + video packets and pushes audio into the
   512-deep queue.
2. Video flows normally at first: paced to the wallclock, presented frame by
   frame.
3. After ~512 audio packets buffer (~10 s of AAC at 44.1 kHz) the queue is full.
   The next `SendPacketAsync` blocks in `WriteAsync` (the queue is `FullMode =
   Wait`).
4. That blocks the **single** pump. It stops reading — including the video
   packets the video branch is still draining.
5. The already-decoded video frames drain out, then video freezes. The clip looks
   like it hangs ~10 s in.

Observed downstream: a signage clip with an audio stream froze at ~240 presented
frames (~10 s at 24 fps) on **both** the CPU presenter and the GPU zero-copy
presenter — presenter-independent, which locates the stall upstream of the
presenter, in the pump. A second clip on the same kiosk that *did* have an audio
sink draining its audio played fine throughout. The trigger was
"Opened media source ... with 1 video and 1 audio stream" together with a null
audio sink.

The failure is **symmetric**: a source played with `videoSink: null` (and no
video configurator) but a real audio sink — "play this music video as background
audio" — has the same shape. The video decoder is created and fed, nothing drains
it, the video queue fills, the pump blocks, and audio starves. The root cause is
not "audio" specifically; it is "a decoded stream with no consumer backpressures
the shared pump."

This is distinct from the OpenAL multi-instance clobber (ADR-0058): same audio
subsystem, different failure mode. ADR-0058 was a cross-wired clock; this is
demux backpressure from an undrained queue, and it reproduces with no audio sink
at all.

## Decision

**A decodable stream with no consumer is not decoded.** When a stream type has
neither a sink nor a configurator/tap to consume it, `SubstrateSession`:

1. **Discards the stream at the demuxer** by setting `AVStream.discard =
   AVDISCARD_ALL`, via a new `DemuxSession.DiscardStream(int streamIndex)`. After
   this, `av_read_frame` skips that stream's packets — they are not copied into
   managed memory, not counted, and not routed to a decoder. (One caveat: because
   discard is set *after* `avformat_find_stream_info`, a handful of packets the
   probe had already buffered before the flag was set can still leak through on
   the first reads. That is why step 2 is also required — the leaked packets must
   land somewhere harmless.)
2. **Skips constructing that stream's decoder.** With no decoder, the existing
   `_audioDecoder is null` / `_videoDecoder is null` guards (already present
   throughout `SubstrateSession`, because audio-only and video-only sources have
   always produced a null decoder on the absent side) make the rest of the
   pipeline a clean no-op for that stream — including the few probe-buffered
   packets that leak past the discard: the pump reads them, finds no decoder to
   route them to, and unrefs them. With no queue, there is nothing to fill and
   nothing to backpressure the pump.

The rule is applied symmetrically to both stream types in
`SubstrateSession.InitializeAsync`:

```csharp
var videoHasConsumer = _videoSink is not null || _videoConfigurator is not null;
var audioHasConsumer = _audioSink is not null || _audioConfigurator is not null;
// create the decoder when there is a consumer; otherwise DiscardStream each
// stream of that type and leave the decoder null.
```

Discarding is the load-bearing half for the "zero cost" goal (no audio bytes are
read or parsed); skipping the decoder is the load-bearing half for correctness
(nothing exists to fill a queue). Doing both is strictly cleaner than either
alone.

### Why discard at the demuxer rather than drain to a null sink

Three options were on the table for "consume-and-drop the audio so it can't
backpressure":

1. **Discard at the demuxer (chosen).** `AVDISCARD_ALL` on the stream. Zero
   decode cost, zero demux copy cost for the discarded stream — `av_read_frame`
   never even returns its packets.
2. **Don't create/feed the decoder.** Sufficient for correctness (the pump's
   `_audioDecoder is not null` guard drops the packets), but the pump still
   *reads* and unrefs every audio packet — wasted demux work.
3. **Drain decoded audio into a discard sink node.** Wastes full decode CPU on
   audio that is thrown away; the most expensive option.

Option 1 is the cleanest and the most efficient, and it is the FFmpeg-native
mechanism for exactly this ("I do not want this stream"). The chosen fix combines
1 (discard) with the correctness half of 2 (no decoder).

### The `hasAudioConfiguratorOnly` path is preserved

A consumer can supply an audio configurator/tap with **no** audio sink — e.g.
captioning or inference reads decoded audio without playing it. That counts as a
consumer (`audioHasConsumer == true`), so the stream is **not** discarded and the
decoder **is** created. The configurator-only graph branch (audio decoded, tapped,
not sent to a playback sink) is unchanged.

### "No decodable stream" guard

The pre-existing load-time guard threw when both decoders came back null, which —
before this change — was equivalent to "the source has no video and no audio
stream." Now a decoder can be null because a stream was deliberately discarded, so
the guard is re-expressed against **stream presence**, not decoder creation:

```csharp
if (MediaInfo.VideoStreams.Count == 0 && MediaInfo.AudioStreams.Count == 0)
    throw ... "neither a decodable video nor audio stream";
```

This preserves the prior behaviour that a controller created with no sinks loads
an A/V source successfully and runs straight to EOS (it builds no graph branches),
rather than turning that into a load failure.

## Consequences

### Positive

- **The deadlock is impossible by construction for the no-consumer case.** A
  discarded stream produces no packets, so there is no queue to fill and nothing
  to block the pump. Muted signage (`audioSink: null`) plays to completion, and so
  does audio-only playback of an A/V file (`videoSink: null`).
- **Zero cost for the unwanted stream.** No reads, no copies, no decode, no
  resampler work. A muted signage player no longer pays to demux audio it throws
  away.
- **Symmetric and principled.** One rule — "no consumer ⇒ discard" — covers both
  stream types and any future stream type routed through the pump.
- **`MediaInfo` is unchanged.** Stream metadata is built at open time, before any
  discard, so consumers still see that the file *has* an audio stream; the session
  simply chooses not to play it. `Demux.PacketsRead` reflects only the pumped
  (non-discarded) streams, which is the correct accounting.

### Negative / trade-offs

- **`DiscardStream` mutates `AVStream` state and is irreversible for the session.**
  The discard flag persists across `SeekAsync` (it is a stream property, not buffer
  state). There is no "re-enable a stream mid-session" path, because sinks and
  configurators are fixed at session construction — a stream's consumer set cannot
  change after load. If dynamic track switching is ever added, that feature owns
  re-evaluating discard.
- **A new public method on `DemuxSession`.** It is concrete-only (not on
  `IDemuxSession`); `SubstrateSession` already holds the concrete `DemuxSession`,
  and keeping it off the interface keeps the demux contract minimal. It can be
  lifted to the interface if a second caller appears.

### Neutral

- A/V sync policy (ADR-0003) is untouched: with a null audio sink the wallclock is
  still the pacer; with an audio sink the audio clock still masters. Only *whether
  an unconsumed stream is decoded* changed.
- `AudioDecoderOptions` gains a `PacketQueueCapacity` knob (default 512, the prior
  hard-coded value). It exists mainly so the backpressure boundary can be observed
  deterministically in a test without first buffering ~10 s of audio; production
  callers rarely set it.

## Alternatives considered

### A. Discard at the demuxer + skip the decoder (accepted)

Chosen — see Decision. Cleanest and most efficient; removes the deadlock by
construction.

### B. Skip the decoder only (no demux discard)

Correct (the pump drops packets it cannot route), but the pump still reads and
unrefs every packet of the unconsumed stream — wasted demux work on every frame.
Rejected as strictly worse than also discarding.

### C. Widen / unbound the decoder queue

Make the audio queue large enough that it "never" fills. Rejected: it only delays
the freeze, scales memory with clip length, and does nothing for the symmetric
video case. It treats the symptom, not the undrained-queue cause.

### D. Per-stream demux pumps with independent cancellation

Give each stream its own pump so one full queue cannot block another. A much
larger change to the single-`AVFormatContext` threading model (ADR-0009) and
unnecessary once unconsumed streams are simply not pumped. Out of scope.

## Validation

- **Decoding layer (fast, deterministic) —
  `NoConsumerStreamDiscardTests`.** Three tests over the real `test-av-h264-aac.mp4`
  fixture with a deliberately tiny audio queue (`PacketQueueCapacity = 8`) so the
  mechanism triggers in milliseconds instead of ~10 s:
  - the undrained audio decoder makes `RunDemuxPumpAsync` block and never reach
    EOF (the deadlock, reproduced);
  - `DiscardStream(audio)` drops nearly all audio packets from the read loop
    (compared against a baseline un-discarded count — a few probe-buffered
    packets leak, far below the full stream), while video is untouched;
  - with the audio stream discarded, the same tiny-queue undrained pump runs
    cleanly to EOF (the handful of leaked packets stay well under the queue
    bound).
- **Playback layer (end-to-end, public path) —
  `PlaybackControllerNextIntegrationTests.LoadPlay_AvFileWithNullAudioSink_PlaysToEndAndDiscardsAudio`.**
  `PlaybackController.Create(audioSink: null)` over an A/V file reaches `Ended`
  with the full video frame count, and `GetDiagnostics().Pipeline.Stream.Demux.PacketsRead`
  stays in the video-only band — proving the audio stream was discarded, not
  pumped. Fails before the fix (the surplus audio packets push the count past the
  video total), passes after.

## References

- `src/FrameFlow.Decoding/DecodingPipeline.cs` — the single demux pump.
- `src/FrameFlow.Decoding/DemuxSession.cs` — `DiscardStream`.
- `src/FrameFlow.Decoding/AudioDecoder.cs` / `AudioDecoderOptions.cs` — the
  bounded queue and its now-configurable capacity.
- `src/FrameFlow.Playback/SubstrateSession.cs` — the no-consumer ⇒ discard policy.
- FFmpeg `AVDISCARD_ALL` / `AVStream.discard` — demuxer-level packet discard.
