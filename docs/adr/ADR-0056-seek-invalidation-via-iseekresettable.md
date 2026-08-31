# ADR-0056: Seek invalidation via ISeekResettable

**Status:** Accepted
**Date:** 2026-06-04
**Supersedes:** None
**Related:** ADR-0048 (seek-discipline audit — this mechanises it), ADR-0055 (decode protocol as a pure Mealy core — this fulfils its `SeekTransition` follow-up), ADR-0009 (threading and concurrency)

## Context

A seek is a timeline discontinuity: every session-lifetime component holding state tied to the *pre-seek* position must invalidate it before post-seek packets flow. ADR-0048 audited **every** such participant and its reset hook, and documented the four production bugs that came from a *partial* invalidation — each was "`SubstrateSession.SeekAsync`'s reset-everything was missing a step."

ADR-0048 left the *application* as a hand-listed checklist — `SeekAsync` steps 5 and 5b called five methods across three objects in sequence:

```csharp
_videoDecoder?.ResetPacketQueue();
_audioDecoder?.ResetPacketQueue();
try { _videoDecoder?.Flush(); } catch { }
try { _audioDecoder?.Flush(); } catch { }
_pipeline?.DiscardPendingPacket();
```

ADR-0048's Alternative D explicitly deferred the type-system enforcement (an `ISessionResettable` interface) to a follow-up, noting "the audit's exploration produces the list that `ISessionResettable` would enforce." ADR-0055's retrospective independently flagged the same shape as the `SeekTransition` follow-up: a transition modelled as a hand-listed checklist is one a future edit can leave incomplete — exactly the bug class ADR-0048 catalogued.

The audit is done; this ADR mechanises its application.

## Decision

### 1. `ISeekResettable` — one reset per participant

Introduce `FrameFlow.Decoding.ISeekResettable` with a single `void ResetForSeek()`. Each pre-seek-stateful component folds its **entire** seek-invalidation behind that one method, so the orchestrator resets a participant with one call instead of knowing its internal reset steps.

- `IVideoDecoder` / `IAudioDecoder` gain a **default interface implementation** of `ResetForSeek` that composes the existing building blocks — `ResetPacketQueue()` then `Flush()` (which also resets the audio decoder's synthetic-PTS accumulator and re-arms lazy SWR). Every decoder, real or fake, inherits it; no concrete decoder changed. `Flush`'s `ObjectDisposedException` (the dispose race) is swallowed, matching the prior behaviour.
- `DecodingPipeline.ResetForSeek()` = `DiscardPendingPacket()` — drop the pump's retained pre-seek packet.
- `DemuxSession` is **not** an `ISeekResettable`: its reset (`av_seek_frame` + `avformat_flush`) is the position change itself, performed by `_demux.SeekAsync(position)`, not a post-seek invalidation.

### 2. Register once, reset uniformly

`SubstrateSession` builds an `IReadOnlyList<ISeekResettable>` in `InitializeAsync`, next to where the components are constructed, and `SeekAsync` replaces steps 5+5b with one pass:

```csharp
foreach (var resettable in _seekResettables)
    resettable.ResetForSeek();
```

Adding a new pre-seek-stateful component is now: implement `ISeekResettable`, add it to the registration in `InitializeAsync`. The reset can't be split or half-applied on the seek path, and the per-component reset lives next to the component's state — not in a distant `SeekAsync`.

## Consequences

### Positive

- **The seek path can't apply a partial checklist.** The five scattered calls become one loop; each component owns its complete reset. The bug class ADR-0048 catalogued (a missing step) is closed within the loop.
- **Locality.** When a decoder grows a new field that must reset on seek, the fix lives in that decoder's `ResetForSeek` (or its `Flush`), right next to the field — not in `SubstrateSession`.
- **Registration is co-located with construction**, so adding a participant and making it seek-safe are the same edit.
- Net simplification: ~25 lines of step-numbered checklist in `SeekAsync` become a 3-line loop.

### Negative

- Adding a participant still requires *registering* it — this is not reflection-based auto-discovery. That was a deliberate non-goal: reflection over session fields would be speculative and slower, and the registration point is one obvious line. The ADR-0048 audit table remains the human-readable source of truth for "what survives a seek."
- A small new abstraction (`ISeekResettable`) on the public decoder contracts.

### Neutral

- Behaviour is unchanged — the same components reset in the same effective order; this is a structural refactor, validated by the existing seek-discipline regression tests.
- The reset *building blocks* (`ResetPacketQueue`, `Flush`, `DiscardPendingPacket`) remain public — `ResetForSeek` composes them rather than replacing them, since finalize-on-EOF still uses `Flush`/`FlushAsync` independently.

## Alternatives Considered

### Keep the hand-listed checklist (status quo from ADR-0048)

Rejected — it is exactly the shape that produced four bugs. ADR-0048 documented the discipline; ADR-0048 itself proposed mechanising it.

### Three explicit `ResetForSeek` calls in `SeekAsync`, no list

Rejected. Collapsing five calls to three is better but still hand-lists participants on the seek path, so adding a fourth component still means editing `SeekAsync` — the forget-a-step risk persists. The registered list moves that decision to construction time.

### Reflection / attribute-based auto-discovery of resettables

Rejected as speculative for three participants, and it would obscure the (deliberately explicit) order.

### A pure `SeekPlan` value that returns the invalidation set

Considered, to match ADR-0055's "model it as a value" framing literally. Rejected: unlike the decode protocol, seek invalidation has no input-dependent decision — the set is fixed and the work is pure IO (flush native buffers, swap channels). A "pure plan" would be a constant list wrapped in ceremony; the interface + registered list captures the same "one transition, can't lose a step" benefit without pretending there's a decision to compute.

## Validation

- `FrameFlow.Decoding.Tests` — 123 passed, 0 failed (the default-interface-method change leaves the decoder/contract tests green).
- `FrameFlow.Integration.Tests` — 44 passed, 3 skipped (by design), 0 failed, including the `SeekDiscipline` regression tests (multi-seek keeps playback flowing; forward seek advances without stalling) that form ADR-0048's regression floor.
- Full solution builds clean (0 errors).
