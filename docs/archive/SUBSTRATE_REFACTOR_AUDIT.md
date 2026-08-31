# Test Suite Audit — Crossbar Substrate Refactor Preparation

> **Archived 2026-06-20 (post-ADR-0049).** This is a migration artifact from the
> Crossbar-pipeline substrate refactor, kept for history. That migration landed:
> the substrate forked into the in-tree `FrameFlow.Graph` (the `using Crossbar`
> dependency is gone), so the file-by-file Crossbar-coupling inventory and the
> phase sequencing below no longer describe the current codebase. Do not treat it
> as live guidance.
>
> **One still-open item carries forward:** Action 4 — the audio-master-clock
> content-coverage gap. The integration tests still exercise only the wallclock
> path because `HarnessAudioSink` / `CapturingAudioSink` do not implement
> `IClockSource`, so a regression on the audio-mastered clock path can slip
> through; a `MasterClockAudioSink` test double would close it. Track that in
> `docs/DEFERRED_WORK.md`, not here.

**Created:** 2026-05-17
**Refactor under consideration:** Crossbar ADR-0014 (primitive-set substrate)
**Purpose:** Classify every FrameFlow test file that touches the Crossbar
pipeline API so the substrate-refactor migration has a checklist instead
of a discovery process.

> **Update 2026-05-17 (evening).** Phases 0, 1, AND 2 of ADR-0014
> are now done:
>
> - **Phase 0** (substrate spike): 23 passing tests in
>   `crossbar/src/Crossbar.Substrate.Next/`. Linear pipelines,
>   fan-out, 1→N operators, joins (`WhenBoth`/`LatestWins`/`PrimaryDriven`),
>   stateful operators, error responses, multi-cadence forks.
> - **Phase 1** (LiveCaptioning shape validation): substrate-side
>   end-to-end synthetic test proves the full topology works (ASR
>   runs ahead of playback, captions correlate to frames). The UI
>   demo rebuild is tracked in this file.
> - **Phase 2** (port one FrameFlow module): `FrameFlow.Video.Next/`
>   ports `FrameFlow.Video`'s three pipeline operators with 5
>   passing tests. The `VideoFrameRef` adapter pattern works
>   without touching `FrameFlow.Media`. Substrate is consumed
>   via PackageReference against the local feed.
>
> **Migration shape so far:** Phase 2 was small (one operator
> module — `FrameFlow.Video.Next`). Phase 3 is the bulk; the
> playback state machine (`PipelineController`) is the long pole.
> Small operator modules port mechanically once the substrate is
> settled.
>
> **Update:** Phase 3 started. `FrameFlow.Audio.Next` and
> `FrameFlow.Whisper.Next` shipped following the Video.Next
> template. Whisper.Next is the architectural highlight:
> `SplitOnPunctuation` and `AnimatedReveal` went from ~55 lines
> of Channel-bridge boilerplate each (line counts of actual files
> in `FrameFlow.Whisper.CaptionPipelineExtensions`) to ~12 lines
> of factory + unchanged domain helper. The substrate's
> `MultiOperatorNode` does what the bridges did.
>
> Substrate also gained `GraphChain<T>` fluent sugar
> (`graph.Pipeline(source).Then(op).To(sink)`) so each remaining
> port has less wiring noise — the patterns are now well-grooved.

## Headline

- **122 test files** in FrameFlow.
- **15 of those** reference the Crossbar pipeline API directly (`FramePipeline`, `FramePacket`, `FrameConsumer`, `AsPipeline`, `.ToSink`, `.Broadcast`, `.Tee`, etc.).
- **~37 total occurrences** of Crossbar API references across those 15 files.
- The remaining **107 test files** test FrameFlow behavior through harnesses; they survive the refactor untouched.

The substrate refactor's test-side impact is bounded to those 15 files,
with the realistic-pain count being closer to **5–7 files** once you exclude
mechanical test-double rewrites that follow from the substrate API change.

## Classification

Each substrate-touching test file falls into one of three categories,
which determine the migration treatment:

| Category | Treatment |
|---|---|
| **Substrate-internals tests** | Tests assert pipeline-mechanics behavior (operator composition, ownership, async-iterator flow). They break when the substrate changes. **Delete or rewrite during migration.** Coverage they provide is replaced by the new substrate's own test suite. |
| **Test doubles** | Implementations of `IAudioSink`/`IVideoSink` etc. that ride on `FrameConsumer<T>`. The shape of these doubles changes with the substrate but the role doesn't. **Rewrite mechanically** following the new substrate's consumer shape. |
| **Behavior-coupled** | Tests that exercise FrameFlow behavior through a harness that *happens* to be implemented via Crossbar. Substrate touch is incidental; the test logic is preserved as long as the harness API stays roughly stable. **Migration-resilient** — usually just imports change. |

## File-by-file inventory

### Substrate-internals tests (5 files) — **delete or rewrite**

These test the substrate's behavior *through FrameFlow's wrapping operators*. The coverage they provide overlaps heavily with what the new substrate's own test suite will cover. During migration, decide per-file whether to port or delete; default to delete unless the test pins FrameFlow-specific semantics that aren't otherwise covered.

| File | LOC | What it tests | Migration action |
|---|---|---|---|
| `tests/FrameFlow.Video.Tests/VideoPipelineExtensionsTests.cs` | ~150 | `.AsPipeline().ConvertPixelFormat().Resize().Observe().RunAsync()` — operator wiring, cardinality, PTS forwarding. | Rewrite as node-graph assertions, or delete if Crossbar's own substrate tests cover the same operator-composition properties. |
| `tests/FrameFlow.Video.Tests/MemoryDomainOperatorsTests.cs` | ~120 | `.AsPipeline().ToCpu().AsDomain().Observe().RunAsync()` — domain-conversion operator behavior. | Same as above. |
| `tests/FrameFlow.Playback.Tests/PipelineControllerPumpSpawnTests.cs` | ~120 | ADR-0045 regression: `PipelineController` spawns pumps when only configurator is supplied. Uses `pipeline.ToSink(...)` to construct test pipelines. | Rewrite once `PipelineController` is refactored to the new substrate; the *intent* (configurator-without-sink path works) survives. |
| `tests/FrameFlow.Playback.Tests/PlaybackPipelineOperatorsTests.cs` | ~220 | `PacedUntil` operator — AV-sync via `IClockSource` signal. Uses `.AsPipeline().PacedUntil().Observe().RunAsync()`. | Rewrite as a node test in the new substrate. The pacing behavior is FrameFlow-specific and must survive. |
| `tests/FrameFlow.Decoding.Tests/GpuFrameYieldIntegrationTests.cs` | ~280 | ADR-0038 GPU yield path: hwaccel decoder → `.ToCpu()` operator → assertions. Integration-flavored but uses substrate API directly for assertion. | Refactor the assertion mechanism to use a harness; the GPU-readback behavior assertion is FrameFlow-specific and must survive. |

### Test doubles (7 files) — **mechanical rewrite**

These implement FrameFlow's sink interfaces, which expose a `Crossbar.FrameConsumer<T>` property per ADR-0010. When the substrate's consumer shape changes (probably to `Operator<TIn, Void>` per ADR-0014), each double rewrites mechanically. None of these contain logic that's hard to port; the cost is volume, not difficulty.

| File | Role | LOC |
|---|---|---|
| [tests/FrameFlow.Integration.Tests/Harness/HarnessAudioSink.cs](../../tests/FrameFlow.Integration.Tests/Harness/HarnessAudioSink.cs) | Integration-test audio sink with lifecycle hooks | ~100 |
| [tests/FrameFlow.Integration.Tests/Harness/HarnessVideoSink.cs](../../tests/FrameFlow.Integration.Tests/Harness/HarnessVideoSink.cs) | Integration-test video sink with 16 ms pump | ~150 |
| [tests/FrameFlow.Integration.Tests/Harness/Capture/CapturingAudioSink.cs](../../tests/FrameFlow.Integration.Tests/Harness/Capture/CapturingAudioSink.cs) | Content-capture audio sink | ~80 |
| [tests/FrameFlow.Integration.Tests/Harness/Capture/CapturingVideoSink.cs](../../tests/FrameFlow.Integration.Tests/Harness/Capture/CapturingVideoSink.cs) | Content-capture video sink | ~100 |
| `tests/FrameFlow.Playback.Tests/Doubles/FakeAudioSink.cs` | Playback-unit test audio sink | ~80 |
| `tests/FrameFlow.Playback.Tests/Doubles/FakeDecodedMediaStream.cs` | Fake decoded stream (`Video`/`Audio` as `FramePipeline<T>`) | ~120 |
| `tests/FrameFlow.Playback.Tests/Doubles/StubPlaybackSession.cs` | Stub session with exposed FramePipelines | ~150 |

**Recommended preparation:** Catalog the role of each double here so the migration has a one-line summary per file. Done above.

### Behavior-coupled (3 files) — **migration-resilient**

These tests use harnesses that *happen* to be substrate-implemented. The test logic itself doesn't depend on the substrate's shape, so they survive the migration with at most import changes.

| File | What it actually tests | Coupling to substrate |
|---|---|---|
| [tests/FrameFlow.Integration.Tests/Harness/Capture/PlaybackHarness.cs](../../tests/FrameFlow.Integration.Tests/Harness/Capture/PlaybackHarness.cs) | Plays corpus file to natural EOF, returns captures + terminal state. *This is the gold-standard harness pattern.* | Pull-mode capture uses `.Observe().RunAsync()` directly. **Hide behind helper method (see Action 1 below).** |
| `tests/FrameFlow.Playback.Tests/PlaybackSessionTeardownTests.cs` | ADR-0044 ownership boundary: session deactivates sinks but never disposes them. | Imports `Crossbar` but tests are behavior-level; substrate refactor changes nothing about what's asserted. |
| [tests/FrameFlow.Audio.Tests/AddFrameFlowOpenAlTests.cs](../../tests/FrameFlow.Audio.Tests/AddFrameFlowOpenAlTests.cs) | DI registration shape for `IAudioSink`. | Imports `Crossbar` incidentally; the actual tests don't construct pipelines. |

## Preparation actions

### Action 1: Hide pull-mode capture behind a helper method (1 file change)

[`PlaybackHarness.cs`](../../tests/FrameFlow.Integration.Tests/Harness/Capture/PlaybackHarness.cs) currently has `CollectVideoAsync` / `CollectAudioAsync` private methods that inline `.Observe().RunAsync()`. They're already encapsulated within the harness class. Recommended evolution:

- Extract the `.Observe(consumer).RunAsync(ct)` pattern into a single named helper (e.g., `IntegrationTestHelper.CaptureFromPipelineAsync<T>(FramePipeline<T> source, Action<T> capture, CT ct)`). Future substrate refactor changes one helper, not two methods.

### Action 2: Add `SubstrateCoupling=Direct` trait to substrate-internals tests (5 file changes)

The existing test suite uses `[Trait("Category", "Integration")]` and `[Trait("Tier", "1")]` extensively. Add a parallel trait for substrate-coupling to enable the migration to selectively run/exclude these tests:

```csharp
[Trait("SubstrateCoupling", "Direct")]
public sealed class VideoPipelineExtensionsTests { ... }
```

Files to tag: the 5 listed in the "Substrate-internals tests" section above. Done in this preparation pass (see `git diff`).

Usage during migration:
```bash
dotnet test --filter "SubstrateCoupling!=Direct"   # behavior-only safety net
dotnet test --filter "SubstrateCoupling=Direct"    # explicit substrate review
```

### Action 3: Pin ADR-0047 acceptance criteria as a skipped failing test

``tests/FrameFlow.Integration.Tests/AudioLookaheadCaptioningTests.cs`` created in this preparation pass. Documents the desired post-ADR-0047 behavior: ASR must produce captions at the spoken audio's PTS, not at arrival time. Currently `Skip`-ed because the underlying substrate doesn't support audio-ahead-of-playback.

When ADR-0047 (or the equivalent in the new substrate) lands, removing the `Skip` exercises the new capability against a concrete corpus-driven assertion.

### Action 4 (deferred, document only): Catalog gaps in master-clock coverage

`docs/DEFERRED_WORK.md` already flags this: all integration tests run against the wallclock path because `HarnessAudioSink` / `CapturingAudioSink` don't implement `IClockSource`. The bug fixed in commit `1e19ca2` was missed for that reason. A `MasterClockAudioSink` test double would close the gap.

Not actioned in this preparation pass — orthogonal to the substrate refactor; tracked in `docs/DEFERRED_WORK.md`.

## Migration sequencing implications

These classifications imply a clean migration order:

1. **Phase 0 (Crossbar spike):** No FrameFlow test impact.
2. **Phase 1 (LiveCaptioning rebuild):** Validate against `AudioLookaheadCaptioningTests` (Action 3). If the test passes after Phase 1, the substrate redesign genuinely solves the captioning latency problem.
3. **Phase 2 (one FrameFlow module):** Tag the substrate-internals tests for the chosen module as `SubstrateCoupling=Pending` and either delete or rewrite as part of the module migration. Behavior-coupled tests for that module should pass before and after.
4. **Phase 3 (rest of FrameFlow):** Rewrite the 7 test doubles in one focused commit per ADR-0014 §"Strategy". Mechanical follow-on work.
5. **Phase 4 (delete old Crossbar):** Remove the `SubstrateCoupling=Direct` trait — no longer needed.

## What this audit does NOT cover

- **Tests that depend on `Crossbar.IClockSource`** (separate concern; `IClockSource` survives the refactor unchanged per ADR-0014).
- **Tests that depend on `Crossbar.FrameMetadata`** (same — metadata survives).
- **Tests for FrameFlow operators that don't currently exist** (e.g., `WithLookaheadBuffer`). The acceptance test in Action 3 is the closest thing.
- **Performance regression tests.** The always-refcount model in ADR-0014 introduces a small per-operator atomic cost. No FrameFlow performance test currently exists that would catch a regression in the < 1% range. Adding one is a separate piece of pre-migration work, deferred.

## Maintenance

This document is a migration artifact, not a permanent fixture. Once
the substrate refactor lands and the `SubstrateCoupling=Direct` trait
is removed, this document can be archived (move to `docs/archive/` or
similar). Until then, update the file-by-file inventory if new
substrate-touching tests are added.
