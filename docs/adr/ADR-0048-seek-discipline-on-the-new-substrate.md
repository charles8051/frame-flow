# ADR-0048: Seek Discipline on the New Substrate

**Status:** Proposed
**Date:** 2026-05-17
**Supersedes:** None
**Related:**
- `crossbar` ADR-0014 (primitive-set substrate) — the substrate this discipline runs on
- `crossbar` ADR-0017 (graph-session abstraction) — proposed substrate-side lift of the pump-orchestration layer this ADR contracts against
- `crossbar` ADR-0019 (discontinuity via graph rebuild) — substrate-side framing of why seek = rebuild
- ADR-0056 (this assembly) — mechanises this discipline via the `ISeekResettable` interface (the "ISessionResettable" follow-up deferred in Alternative D below)
- `docs/DEFERRED_WORK.md` → "Seek-discipline audit" — the exploration entry this ADR resolves
- Commits: `1a45f83` (avformat_flush), `1e19ca2` + `35323ad` (OpenAL master-clock), `d03e4b0` (DiscardPendingPacket), `6fe6af6` (logger plumbing)

## Context

`FrameFlow.Playback.Next.SubstrateSession` runs a single long-lived
graph per session. Pause/resume is implemented by gates; seek is
implemented by tearing down the demux pump + graph, mutating shared
state (decoders, clock, demuxer position, audio sink), and rebuilding
fresh tasks against the same long-lived participants.

The session has, broadly, three lifetimes:

1. **Session-lifetime** — owned across the entire `IPlaybackSession`,
   torn down by `DisposeAsync`. Example: `_videoDecoder`, `_audioGate`,
   `_clock`.
2. **Pump-run-lifetime** — torn down and rebuilt every time
   `StartSessionTasks` is called. Example: `_sessionCts`, `_pumpTask`,
   the source-node enumerators inside `DecoderSourceAdapters`.
3. **Per-frame** — born and consumed inside a single operator body.
   Example: the per-frame `VideoFrameRef`.

The bug class this ADR addresses is **state on a session-lifetime
participant whose correctness depends on continuity with the prior
pump run.** Four such bugs landed in the last two months:

| Commit | Participant | State that survived seek | Symptom |
|---|---|---|---|
| `1a45f83` | `DemuxSession` libavformat | format-context read-ahead buffer | post-seek video freeze (~1.5 s) |
| `1e19ca2` | `OpenAlAudioSink._baseSourceTime` | source-time anchor at pre-seek PTS | post-seek clock starts behind seek target; PaceUntil freezes |
| `35323ad` | `OpenAlAudioSink` ticker | silent loops 2+ | second loop silent because device clock kept advancing across loop boundary |
| `d03e4b0` | `DecodingPipeline._pendingPacketPtr` | retained pre-seek packet | post-seek pipeline begins with stale-PTS frame, master clock anchors to it, PaceUntil freezes |

Each took focused debugging because `SubstrateSession.SeekAsync`'s
"reset everything that matters" was missing a step. The audit below
enumerates **every** session-lifetime participant in the new
substrate's runtime, classifies the state it owns, identifies the
reset hook, and surfaces any uncovered cases.

The audit ran 2026-05-17 against the post-`d03e4b0` codebase.

## Decision

**Treat `SubstrateSession.SeekAsync`'s step-numbered comments as a
contract.** Every session-lifetime participant in the table below is
either reset by an explicit step OR carries an explicit "survives by
design — here's why" note. Adding a new session-lifetime stateful
component to `FrameFlow.Playback.Next` requires updating both the
table and the `SeekAsync` implementation in the same commit. The
audit table here is the authoritative list.

The 8-step `SeekAsync` discipline is:

1. **Close gates + pause audio.** Frames stop forwarding to sinks;
   audio device goes silent immediately so the user doesn't hear the
   pre-seek tail.
2. **Stop graph.** Cancel session CTS, wait for pump + graph tasks.
   Source-node enumerators see cancellation, cleanup callbacks dispose
   the per-pump-run enumerators.
3. **Deactivate audio sink.** Clears any device-side staging buffer
   and source-time anchor. Required for the OpenAL sink's correct
   post-seek behaviour.
4. **Seek demuxer.** `_demux.SeekAsync(position)` — internally calls
   `av_seek_frame` and `avformat_flush` (since `1a45f83`) so the next
   `av_read_frame` returns post-seek packets.
5. **Reset decoder packet queues + flush codec contexts + drain.**
   `ResetPacketQueue` replaces the bounded channel; `Flush` calls
   `avcodec_flush_buffers` + an explicit drain loop pulling any
   residual frames out of the codec output buffer. Audio decoder's
   `Flush` additionally resets `_syntheticPtsSamples` to 0 and re-arms
   `_swrInitialized = false` so SWR reinitialises on the next frame.
6. **Discard pump's retained pre-seek packet (5b).** The demux pump's
   `_pendingPacketPtr` stash exists for pause/resume continuity — but
   post-seek that retained packet belongs to the pre-seek timeline.
   `DecodingPipeline.DiscardPendingPacket` drops it.
7. **Reset clock to seeked position.** `_clock.Seek(position)` and
   `_ownedClockSource?.Seek(position)`. Dispose `_sessionCts`, null
   the field — the next pump run constructs a fresh one.
8. **Restore sink + graph state.** Branch on `wasPaused`: paused
   path re-activates audio in paused state and leaves gates closed
   for the next user `PlayAsync` to handle; unpaused path reactivates
   audio, starts fresh graph tasks via `StartSessionTasks`, opens
   gates, resumes audio.

## Audit table — every session-lifetime participant

Status of 2026-05-17. Columns:
- **Participant** — the object (and field, where the state is
  internal-by-design)
- **State** — what survives across pump runs
- **Reset hook** — the explicit invalidation, or "session-lifetime"
  if intentionally preserved
- **SeekAsync step** — the numbered step in the discipline
- **Coverage** — whether a regression test asserts the invalidation

| Participant | State | Reset hook | Step | Coverage |
|---|---|---|---|---|
| `DecodingPipeline._pendingPacketPtr` | retained packet from prior pump cancellation | `DiscardPendingPacket()` | 6 (5b) | ✅ `SeekDisciplineNextTests.ForwardSeek_PostSeekVideoFramesAdvanceWithoutStalling` (added in this ADR) |
| `DecodingPipeline._pendingPacketStreamIndex` | stream-index hint for the above | reset alongside ptr | 6 (5b) | covered above |
| `DemuxSession` libavformat read-ahead | format-context internal buffer | `avformat_flush` inside `_demux.SeekAsync` | 4 | indirectly via `SeekTests` / `SeekNextTests` |
| `VideoDecoder._packetQueue` | bounded `Channel<(nint, bool)>` (cap 512) | `ResetPacketQueue()` (replaces channel; drains old) | 5 | covered by SeekTests |
| `VideoDecoder._pendingRetryPacketPtr` | packet saved across EAGAIN-mid-cancellation | `FreePendingRetryPacket()` inside `ResetPacketQueue` | 5 | covered |
| `VideoDecoder._codecCtx` (decode reorder) | codec internal frame queue | `avcodec_flush_buffers()` + explicit drain loop | 5 | covered + the d03e4b0 drain defence |
| `VideoDecoder._hwDeviceCtxRef` | hw device handle (DXVA2/CUDA/D3D11VA) | **NOT reset — session-lifetime by design** | n/a | n/a — device handle reuse is the intended semantics; the codec's per-decode hwaccel state IS flushed by `avcodec_flush_buffers` |
| `VideoDecoder._swsCtx` | sws scaler context | **NOT reset — geometry-keyed cache** | n/a | correct: post-seek geometry of the same stream doesn't change; rebuild path lives in `EnsureSwsContext` and fires on format change |
| `VideoDecoder._swFrame` | scratch CPU `AVFrame*` for hwframe_transfer | per-call `av_frame_unref` in `BuildManagedFrame`'s finally | per-frame | correct (transient) |
| `VideoDecoder._yieldHardwareFrames` | `bool`-as-`int` toggle | **NOT reset — consumer-controlled** | n/a | correct: user-visible setting; not seek state |
| `VideoDecoder._framesDecoded` / `_decodeErrors` / `_packetsDroppedForBackpressure` | diagnostic counters | **NOT reset — session-lifetime** | n/a | correct: cumulative session stats |
| `AudioDecoder._packetQueue` | bounded `Channel<(nint, bool)>` (cap 512) | `ResetPacketQueue()` | 5 | covered |
| `AudioDecoder._codecCtx` (decode state) | codec internal | `avcodec_flush_buffers()` + drain loop | 5 | covered |
| `AudioDecoder._syntheticPtsSamples` | cumulative output sample count for synthesised PTS | `_syntheticPtsSamples = 0` inside `Flush()` | 5 | ⚠️ no targeted regression test — corpus has explicit PTS, so the synthetic path doesn't fire in integration. Unit test viable. |
| `AudioDecoder._swrCtx` (internal delay buffer) | swresample's internal sample buffer | implicit: `_swrInitialized = false` triggers `swr_init` on the next decoded frame, which libswresample documents as clearing the buffer | 5 | ⚠️ NEEDS-VERIFICATION — assumption that `swr_init` clears the buffer is per FFmpeg docs but untested for our seek case; targeted unit test would cement this |
| `AudioDecoder._swrInitialized` | latch for lazy SWR configuration | `_swrInitialized = false` inside `Flush()` | 5 | covered |
| `AudioDecoder._usedSyntheticPts` | latched diagnostic | **NOT reset — session-lifetime** | n/a | correct: diagnostic latch is per-session |
| `AudioDecoder._buffersDecoded` / `_decodeErrors` | diagnostic counters | **NOT reset — session-lifetime** | n/a | correct: cumulative session stats |
| `PausableGate<T>` `_gate` (AsyncManualResetEvent) | gate open/closed state | `_videoGate.Close()` step 1, `_videoGate.Open()` step 8 | 1, 8 | covered |
| `PausableGate<T>` in-flight item | local var inside operator body | resolved by graph cancel (body throws OCE; substrate disposes item via 1→1 ownership protocol) | 2 | covered (substrate refcount discipline) |
| `PaceUntil` in-flight `WaitUntilAsync` | pure async wait; no operator-side state | resolved by graph cancel | 2 | covered |
| `WallClockSource._stopwatch` + `_baseOffset` | elapsed-since-start plus seek offset | `Seek(position)` | 7 | covered |
| `WallClockSource._tickTask` (PeriodicTimer ticker) | running ticker task | **NOT reset — session-lifetime** | n/a | correct: ticker runs for `WallClockSource` lifetime, which is session-lifetime |
| `WallClockSource._subject` (ClockSubject) | latest published value + pending waiters | implicit: `Seek` publishes the new value, which wakes any waiters whose target was crossed | 7 | correct (and tested by `WallClockSource` unit tests) |
| `_clock` (IPlaybackClock) | position + paused state | `_clock.Seek(position)` | 7 | covered |
| `OpenAlAudioSink._stagingBuffer` | partial PCM block awaiting device write | cleared by `DeactivateAsync()` | 3 | ⚠️ unit-only — no integration assertion that a seek-mid-staging drops the partial buffer (`docs/DEFERRED_WORK.md`: "Integration suite's fake audio sinks bypass IClockSource") |
| `OpenAlAudioSink._baseSourceTime` + ticker | source-time anchor at pre-seek PTS | cleared by `DeactivateAsync()` + reset on `ActivateAsync()` | 3, 8 | covered by `OpenAlAudioSinkTests.IClockSource_*` (unit) since `1e19ca2` + `35323ad` |
| `DecoderSourceAdapters` enumerator (video) | iterator state | **fresh source node per `StartSessionTasks`**; old enumerator disposed by the SourceNode's `cleanup` callback when graph cancels | 8 (rebuild) | covered: each pump run constructs a new source node closure |
| `DecoderSourceAdapters` enumerator (audio) | iterator state | same as video | 8 (rebuild) | covered |
| `SubstrateSession._ownedClockSource` (`WallClockSource`) | session-owned clock | `_ownedClockSource.Seek(position)` step 7 | 7 | covered |
| `SubstrateSession._sessionCts` | per-pump-run CTS | disposed + nulled in step 7; recreated in `StartSessionTasks` | 7, 8 | covered |
| `SubstrateSession._eofFired` | int latch for EOS one-shot | `Interlocked.Exchange(ref _eofFired, 0)` at the top of `StartSessionTasks` | 8 | covered (per-pump-run reset) |
| `SubstrateSession._renderersActivated` | session-lifetime first-play flag | **NOT reset — session-lifetime by design** | n/a | correct: renderers stay activated across seeks; the next `PlayAsync` takes the resume branch, not the first-play branch |
| `SubstrateSession._videoGate` / `_audioGate` | session-lifetime gate instances | closed step 1, opened step 8 (unpaused branch) or left closed (paused branch) | 1, 8 | covered |

## Tests added by this ADR

A new integration-test class
`tests/FrameFlow.Integration.Tests/SeekDisciplineNextTests.cs` adds
two regression tests:

1. **`MultiSeek_PlaybackKeepsFlowing`** — performs five seeks in
   sequence (mix of forward, backward, and seek-to-zero) and asserts
   at least one new video frame is captured within 5 s of wallclock
   after each seek. Catches cumulative state leaks across pump runs
   — bugs that no single-seek test surfaces because the leak only
   compounds across boundaries.
2. **`ForwardSeek_PostSeekVideoFramesAdvanceWithoutStalling`** —
   single forward seek to mid-stream, asserts at least 3 new frames
   land within 2 s of wallclock. The `d03e4b0` bug class (retained
   pre-seek packet contaminating post-seek timeline) freezes video
   for the pre-seek-to-target gap in real seconds; this is the
   targeted regression for it.

These supplement the existing `SeekNextTests` (which assert seek
state-machine transitions but not the post-seek liveness window).

## Open gaps that warrant follow-up

Filed in `docs/DEFERRED_WORK.md` rather than blocking this ADR:

1. **`AudioDecoder._syntheticPtsSamples` reset** — the corpus has
   explicit AAC PTS, so the synthetic-PTS path doesn't fire in
   integration. A unit test against `AudioDecoder` directly (feeding
   a packet with `AV_NOPTS_VALUE`) would assert the post-seek
   reset.
2. **`AudioDecoder._swrCtx` internal delay buffer** —
   `_swrInitialized = false` is documented to reset the buffer via
   `swr_init` on the next frame. Worth a targeted unit test that
   asserts no pre-seek samples bleed into post-seek output.
3. **`OpenAlAudioSink._stagingBuffer` integration coverage** — the
   existing integration harness uses `HarnessAudioSink` /
   `CapturingAudioSink` which do NOT use a real device-side staging
   buffer. The `MasterClockAudioSink` test double proposed in
   `docs/DEFERRED_WORK.md` would close this gap.

These are non-blocking — the current discipline is correct as
documented; the gaps are about strengthening the assertion floor
for future contributors, not fixing latent bugs.

## Consequences

### Adopted

The audit table is the source of truth for "what survives a seek and
how it's invalidated." Future contributors adding stateful components
to `FrameFlow.Playback.Next` are expected to:

1. Update the table in this ADR with the new participant's row.
2. Wire the reset (or document the "session-lifetime by design" case).
3. Ship a regression test that asserts the reset.

The two integration tests added by this ADR (`SeekDisciplineNextTests`)
form the regression floor for the discipline.

### Costs

| Cost | Magnitude | Notes |
|---|---|---|
| Maintenance burden of keeping the table current | Small | The list is finite and grows slowly; each new component adds one row |
| The discipline doesn't enforce itself at compile time | Small-Medium | The `ISessionResettable` interface proposed in `docs/DEFERRED_WORK.md` would mechanise this; tracked separately |

### Benefits

| Benefit | Magnitude |
|---|---|
| The "what resets when" decision tree is no longer scattered across 8 numbered comments in `SeekAsync` | Large |
| The 4-bug history is captured in one place with the participant + reset hook for each | Large |
| Future contributors can read the table before adding a stateful component, instead of finding the gap during a 2-week debugging stretch | Medium-Large |
| The two regression tests catch the bug shape that has surfaced 4 times in 2 months | Medium |

## Alternatives Considered

### A. Skip the ADR; rely on the `SeekAsync` step comments

Rejected. The 8 step comments are good, but they describe the
**procedure**, not the **set of participants**. A contributor reading
those comments has no way to know whether their new component should
be reset — they have to read every file involved in playback to
infer membership. The audit table makes membership explicit.

### B. Inline the audit into the `SeekAsync` method's doc comment

Rejected on size. The table is ~30 rows; the method's doc would
balloon. The ADR is the right home for the structural decision; the
method's doc comment can reference it.

### C. Defer the audit until ADR-0017 (graph-session abstraction) lifts the pump-orchestration

Considered and rejected for sequencing. ADR-0017 in `crossbar` lifts
the pump-orchestration upstream of `FrameFlow.Playback.Next`, but
the FrameFlow-specific participants (decoders, OpenAL sink,
playback clock) remain in this assembly post-lift. The audit is
correct regardless of where the orchestration lives.

### D. Implement `ISessionResettable` first, then the audit walks the registered set

Reversed: the audit's exploration produces the list that
`ISessionResettable` would enforce. Trying to design the interface
first risked missing components or carrying speculative ones; the
audit-first approach grounds the interface design in real
participants. `docs/DEFERRED_WORK.md` → "ISessionResettable" tracks the
interface work as a follow-on.

## References

- `docs/DEFERRED_WORK.md` → "Seek-discipline audit" — the exploration entry
  this ADR resolves
- `docs/DEFERRED_WORK.md` → "ISessionResettable" — companion entry; type-system
  enforcement of the discipline
- `crossbar` ADR-0017 — graph-session abstraction; the substrate-side
  lift of pump-orchestration
- `crossbar` ADR-0019 — discontinuity via graph rebuild; substrate-
  side framing of the rebuild model
- `src/FrameFlow.Playback.Next/SubstrateSession.cs` — the
  `SeekAsync` method this ADR contracts against
- `tests/FrameFlow.Integration.Tests/SeekDisciplineNextTests.cs` —
  the regression tests added by this ADR
- Commits: `1a45f83`, `1e19ca2`, `35323ad`, `d03e4b0` — the four
  bug-class instances this discipline closes
