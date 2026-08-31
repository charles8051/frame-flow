# ADR-0055: The codec send/receive loop as a pure Mealy core

**Status:** Accepted (both decoders wired — see "Spike" below)
**Date:** 2026-06-04
**Supersedes:** None
**Related:** ADR-0005 (native resource ownership), ADR-0008 (result types and exception boundaries), ADR-0009 (threading and concurrency), ADR-0013 (cancellation token propagation), ADR-0023 (hierarchical state machine — the sibling pattern one layer up), ADR-0034 (diagnostics surfaces), ADR-0048 (seek discipline)

## Context

### The codec protocol is a small state machine, hand-inlined many times

Driving an FFmpeg decoder is a request/response protocol with a fixed return-code vocabulary. `avcodec_send_packet` returns `0` / `EAGAIN` / `EOF` / `<0`; `avcodec_receive_frame` returns the same set. The correct way to sequence them — send, on `EAGAIN` drain then re-send the *same* packet, otherwise drain the frames the packet produced, and at end-of-stream send a null flush and drain to `EOF` — is a tiny Mealy machine: `δ(state, returnCode) → (state', action)`.

Today that machine is written out longhand, fused with the native calls and the `yield return`, in at least four places that are all the same protocol:

1. `VideoDecoder.DecodePacketCoreAsync` — send with `EAGAIN`-drain-retry, then drain.
2. `VideoDecoder.FlushDecoderAsync` — the same loop, sending a null flush packet.
3. `VideoDecoder.DecodeAsync(formatContextPtr, …)` — the direct-read path, a third read→classify→route copy.
4. `AudioDecoder.DecodeAsync` — **and here the copies have diverged**: audio sends a packet *once* and moves on, with no `EAGAIN`-on-send re-send loop. If `avcodec_send_packet` ever returns `EAGAIN` on the audio path, that packet is dropped rather than re-sent. The branch is rarely reached under the strict drain-after-send discipline both decoders follow, so this is a latent divergence rather than a confirmed live bug — but it is exactly the class of divergence that retyping the same protocol four times produces.

The drain half is duplicated again in `VideoDecoder.Flush` and `AudioDecoder.Flush`, whose "flush buffers, then drain residual frames" blocks carry near-identical comments that *cite each other* to stay in sync ("Mirrors the same defence in AudioDecoder.Flush"). When two methods reference each other to stay aligned by hand, they want to be one function.

None of these loops is unit-tested. They are only exercised end-to-end, through real FFmpeg against corpus media, so the `EAGAIN` / flush / fault branches — the ones most likely to harbour a mistake — have no isolated coverage.

### The deliberate ceiling: this is a control-plane refactor, not a pure decoder

The authoritative decode state — the reorder buffers, the reference frames — lives inside the native `AVCodecContext`. It is opaque, mutable, and not snapshottable; `avcodec_flush_buffers` exists precisely because you cannot reset it functionally. So the decoder can **never** be a pure fold over recorded inputs the way a pure protocol negotiator can: replay and cross-language equivalence are not on the table here, because the state you would thread is behind the FFmpeg wall.

What *is* ours to model purely is the **control plane** — the sequencing of sends and receives, which is just bookkeeping over return codes. That is the weakest-model-that-does-the-job call: a pure fold suffices for the sequencing; the data plane (turning H.264 into pixels) correctly stays in the imperative native engine. This ADR purifies the former and leaves the latter exactly where it is.

## Decision

### 1. Model the protocol as a pure Mealy core

Introduce `FrameFlow.Decoding.Internal.DecodeProtocol`: a total function `Advance(DecodeState, CodecReturn) → DecodeTransition` plus a `Begin()` entry point, over immutable value types:

- `CodecReturn` — the input vocabulary: `Ok | Again | EndOfStream | Fault` (the classified codec return).
- `DecodePhase` — the threaded state: `Idle | Feeding | Draining | DrainingThenRetry | Done`.
- `DecodeAction` — the Mealy output: `SendInput | ReceiveFrame | EmitThenReceive | NeedNextInput | Complete | FaultOnSend | FaultOnReceive`.

The core has **zero** FFmpeg references — no IO, no clock, no cancellation, no threads. It is the entire decision table:

| Phase | `Ok` | `Again` | `EndOfStream` | `Fault` |
|-------|------|---------|---------------|---------|
| **Feeding** (awaiting send result) | → Draining, `ReceiveFrame` | → DrainingThenRetry, `ReceiveFrame` | → Draining, `ReceiveFrame` | → Done, `FaultOnSend` |
| **Draining** (send accepted) | stay, `EmitThenReceive` | → Idle, `NeedNextInput` | → Done, `Complete` | → Done, `FaultOnReceive` |
| **DrainingThenRetry** (send said Again) | stay, `EmitThenReceive` | → Feeding, `SendInput` (re-send same input) | → Done, `Complete` | → Done, `FaultOnReceive` |

The `DrainingThenRetry → Again → SendInput` cell is the re-send branch the audio path lacks today. Routing both decoders through this one table makes the audio/video asymmetry unrepresentable.

### 2. One shared imperative shell cranks it

Introduce `FrameFlow.Decoding.Internal.DecodeDriver.RunAsync<TFrame>(IDecodeCodec<TFrame>, CancellationToken)`: the single loop that performs the effect each `DecodeAction` names, threads the immutable `DecodeState`, and `yield return`s frames. It owns the messy edges (the `yield`, cancellation); every *decision* comes from `DecodeProtocol`. `IDecodeCodec<TFrame>` is the narrow effect surface — `TryBeginNextInputAsync`, `SendCurrentInput`, `ReceiveFrame`, `BuildFrame` — that a real FFmpeg adapter and a test double implement alike. Input acquisition is async because real inputs arrive over a `Channel<T>` filled by the demux pump; the send/receive/build effects are synchronous native calls.

### 3. The codec ABI stays at one seam

`DecodeDriver.Classify(int) → CodecReturn` is the only FFmpeg-aware line, mirroring the existing `DecodingPipeline.ClassifyDemuxReadResult` for the demux-read half. Real `IDecodeCodec` adapters call it; the driver loop never does — it speaks only the pure vocabulary.

### 4. Adopt it in the decoders

**`AudioDecoder` — done.** It now implements `IDecodeCodec<PcmAudioBuffer>` (queue read → send → receive → resample) and its `DecodeAsync` delegates to `DecodeDriver.RunAsync`; the bespoke send/receive loop is gone. The one genuinely behaviour-changing detail: the cloned input packet is now freed only once the decoder *accepts* it (send returned non-`Again`) and is held across a send-`EAGAIN` so the driver can re-send it — the old loop freed unconditionally after a single send, which would have dropped a stalled packet. Held packets are released in `ResetPacketQueue` / `DisposeAsync`, mirroring `VideoDecoder`'s `_pendingRetryPacketPtr` cleanup.

**`VideoDecoder` — done.** Same adapter shape, with the extra care its complexity demands: `ReceiveFrame` builds the managed frame (CPU readback, or the ADR-0038 `GpuVideoFrame` path) and unrefs the native frame **under `_codecSync`**, so a concurrent `Flush` cannot tear the shared `AVFrame` out mid-build; the built frame is stashed for the immediately-following `BuildFrame`. The held-input packet reuses `_pendingRetryPacketPtr`, which now does double duty — held across a send-`EAGAIN` for re-send, and naturally retained when the worker is cancelled un-accepted, so pause/resume still never drops a packet. The dead direct-read `DecodeAsync` overload and `DecodePacketAsync` were removed.

### 5. Related control-plane purifications (separate ADRs/changes)

The same lens applies to two more informal checklists, tracked separately so this ADR stays scoped to the decode loop:

- **`SeekTransition` as a value — done (ADR-0056).** Seek invalidation was a seven-step checklist across five objects (`avformat_flush` + `DiscardPendingPacket` + both decoders' `Flush` + `ResetPacketQueue` + synthetic-PTS reset + SWR re-arm) whose missing items had already produced a stale-PTS hang (documented in `DemuxSession.SeekAsync`). It is now one `ISeekResettable.ResetForSeek()` per participant, applied in a single registered pass — see ADR-0056.
- **PTS synthesis as a fold — done.** `AudioDecoder`'s `_syntheticPtsSamples` accumulator is now the pure `AudioPtsSynthesis.Advance` fold `(state, frame) → (pts, state', usedSynthetic)` over an immutable `PtsSynthesisState`, with drift tests over a 10 000-frame PTS-less stream — closing the synthetic-PTS coverage gap ADR-0048 flagged.

## Consequences

### Positive

- **One tested transition table** replaces four hand-inlined copies. A future change to the protocol moves exactly one table cell, in review, with no decoder or hardware in the loop.
- **The audio/video divergence becomes unrepresentable** — both decoders crank the same core, so the re-send branch cannot exist in one and not the other.
- **Transcript-testable.** The whole protocol — `EAGAIN` re-send, flush-drains-latent-frames, fault propagation, cancellation — runs green in CI against an in-memory fake codec, with no FFmpeg binaries and no corpus media. (See the spike's 24 tests.)
- **The codec seam shrinks to one line** (`Classify`), consistent with the demux-read half.

### Negative

- Adds a small value vocabulary (three enums + two record structs) and one indirection between the decoder and its native calls.
- `EmitThenReceive` folds "deliver a frame" and "receive again" into one action so the step stays one-output-per-input; it is the one slightly non-obvious cell and is documented as such.
- The headline win is **bounded to the control plane**. It does not — and cannot — make the decoder replayable or portable, because the codec state is native (see Context). Anyone expecting the full pure-core payoff should recalibrate to "one shared, tested protocol," not "a pure decoder."
- The decoder adoption (decision 4) is real hot-path work and carries the usual native-ownership risk; the core landing first de-risks it but does not remove it.

### Neutral

- ADR-0005's value boundary is unchanged — native pointers still never escape; this refactors what happens *inside* the boundary.
- ADR-0009's threading model is unchanged — the driver is still the single decode worker; the channel/backpressure machinery around it is untouched.
- ADR-0013 cancellation semantics are preserved — the driver observes the token at every step and ends the enumeration cleanly.

## Alternatives Considered

### Keep the hand-inlined loops

Rejected. They work, but the duplication is real (four copies + two cross-referencing `Flush` drains), the divergence is real, and none of it is unit-testable in isolation. The cost of the status quo is paid in every future edit to the decode loop and in the class of bug the seek checklist already demonstrated.

### A base class or template-method on a shared decoder

Rejected. Inheritance couples the two decoders' lifecycles and still leaves the sequencing entangled with the native calls and `yield`. The pure-core split keeps the decision testable on its own and composes by delegation, matching the project's composition-over-inheritance stance.

### Source-generate the loop

Rejected as premature. A generator hides the transition table behind tooling; for a twelve-cell machine a plain `switch` expression *is* the table, readable and diff-able in review.

### Purify the whole decoder (thread codec state as a value)

Rejected as impossible, not merely undesirable. The reorder/reference-frame state lives in the native `AVCodecContext` and is not snapshottable. Attempting it would mean reimplementing the codec. The control-plane split is the most purity the problem actually admits.

## Spike

A spike landed on branch `spike/decode-protocol-core` to validate the shape:

- `src/FrameFlow.Decoding/Internal/DecodeProtocol.cs` — the pure core (no FFmpeg).
- `src/FrameFlow.Decoding/Internal/DecodeDriver.cs` — the shared shell + `Classify` seam + `IDecodeCodec<TFrame>`.
- `tests/FrameFlow.Decoding.Tests/DecodeProtocolTests.cs` — 24 tests, all green: the full transition table, the `Classify` seam, and end-to-end driver runs against a scripted fake codec (multi-packet ordering, frames held until flush, send-`EAGAIN` drain-then-resend without dropping frames, fault propagation, cancellation).

Both `AudioDecoder` and `VideoDecoder` are now wired onto `DecodeDriver` (decision 4) and validated against real FFmpeg, so the four hand-inlined send/receive loops are gone and the protocol lives in one tested place.

Validation (worktree, FFmpeg + corpus provisioned):

- `FrameFlow.Decoding.Tests` — 123 passed, 0 failed (the 24 protocol tests + audio contract + demux integration).
- `FrameFlow.Integration.Tests` — 44 passed, 3 skipped (by design), 0 failed: `ContentCaptureNextTests` (decoded audio PCM matches a reference decode within RMS tolerance; audio **and video** PTS strictly monotonic; A/V sync within tolerance; no duplicated audio), play-to-completion frame counts, loop-restart, seek-discipline, and pause/resume — the lifecycle paths that exercise the held-packet and flush-latch logic in both decoders.
- Full solution builds clean (0 errors; the only warnings are pre-existing CA2000s in `FrameFlow.MotionClip`).
