# ADRs

This folder stores Architectural Decision Records for FrameFlow.

Use an ADR when a decision:

- affects multiple phases
- changes public shape or project structure
- constrains future implementation choices
- would be hard to rediscover from code alone

Good ADR candidates include:

- FFmpeg binary resolution strategy
- chosen audio backend(s)
- synchronization master clock policy
- presenter abstraction boundaries
- hardware acceleration strategy
- public DI and options registration surface

## A note on the Crossbar citations

Many ADRs here cite **Crossbar** — `crossbar ADR-0014`, "the Crossbar substrate",
`crossbar/Directory.Build.props`, and similar. Those references cannot be
followed, and this note exists so that is a known fact rather than a puzzle.

Crossbar was a first-party library by the same author: a Holoscan-class
processing-graph substrate that FrameFlow consumed as a NuGet package. In
2026-05 it was forked into this repository — a one-time verbatim copy that
became `src/FrameFlow.Graph`, with `using Crossbar;` becoming
`using FrameFlow.Graph;` throughout. [ADR-0049](ADR-0049-frameflow-graph-fork-from-crossbar.md)
records that decision and its rationale in full.

Three consequences worth stating plainly:

- **There is no Crossbar dependency.** No `PackageReference`, no `using`. Every
  mention in this repository is history, not a live edge. `FrameFlow.Graph` is
  the substrate, in-tree and diverged.
- **The Crossbar repository is not published**, so its ADR numbers and file paths
  are cited as the record of a decision rather than as links. They are named so
  that someone with access can find them.
- **The forked code ships under FrameFlow's licence, not Crossbar's.** Crossbar
  was MIT; `src/FrameFlow.Graph` is covered by `LICENSE.md` like the rest of this
  repository. That is a change of terms, not a continuation of them. The
  licensing note at the top of [ADR-0049](ADR-0049-frameflow-graph-fork-from-crossbar.md)
  records what is known about the fork's provenance and what remains a question
  for the maintainer; this index does not settle it.

Where an ADR's reasoning genuinely depends on a Crossbar document, the ADR says
so at the point of citation. Where it is only provenance — "this mirrors
Crossbar's shape" — it can be read as a historical aside.

## A note on "the kiosk"

Many ADRs and investigations here say **the kiosk** — "the kiosk's Intel HD
620", "on-kiosk A/B", "the confirmed kiosk choppiness". Like the Crossbar
citations above, this note exists so the term is a known referent rather than a
puzzle.

The kiosk is a downstream first-party application by the same author: a
single-window digital-signage player that consumes FrameFlow. It is not
published, and it is not this repository. It appears in these documents because
it was the deployment most of the performance and stability work was measured
against, and naming the machine is what makes those measurements checkable.

Its properties are the ones that keep showing up in the reasoning:

- **A weak Intel iGPU.** Most of the hardware-decode, zero-copy, and
  colour-conversion decisions turn on what is slow or broken there, not on what
  a discrete GPU does. Individual ADRs name the exact part wherever a
  measurement depends on it.
- **Single monitor, fullscreen, one window.** Several diagnostics are
  process-wide precisely because there is only ever one active sink.
- **Long uptime, unattended.** Resource leaks that a desktop app would never
  notice are load-bearing failures over weeks of runtime.
- **Offline / locked-down.** No first-run network egress, which is why model
  and native-binary acquisition are pre-seeded rather than downloaded
  ([ADR-0051](ADR-0051-model-acquisition-strategy.md)).

Two consequences worth stating plainly:

- **Measurements citing it are records, not reproduction steps.** "Measured on
  the kiosk" means it was measured on that hardware, and you cannot re-run it
  here. Where a result is reproducible on ordinary hardware, the ADR says so.
- **It is one consumer's shape, not a constraint on yours.** FrameFlow does not
  assume a kiosk. Where a decision was made *because* of that deployment and
  another consumer might reasonably want the opposite, the ADR records it as a
  trade-off rather than a rule.

Unrelated: `src/FrameFlow.MotionClip/scripts/install-kiosk-task.ps1` and its
paired uninstaller use "kiosk" in the ordinary sense — an auto-login machine
running one application at logon. Those scripts are a supported deployment mode
of MotionClip and have nothing to do with the specific deployment above.

## A note on the investigation and commit citations

Two more kinds of reference in this repository cannot be followed. Like the
Crossbar note above, this exists so that is a known fact rather than a puzzle.

**Three investigations are cited but not published.** Comments and ADRs refer
to them by date, usually with a section number:

| cited as | what it is |
|---|---|
| `investigation 2026-06-12`, often `§6` or `§9` | the composition-interop presenter teardown deadlock, and the live-playback `VideoProcessorBlt` hang found on the same deploy |
| `perf survey`, usually `§A1`, also `§A3` / `§A4` / `B5` | a 2026-06-11 survey of pacing-clock cadence, the held-lease coupling, and thread pressure |
| `2026-06-06 investigation` | the DirectComposition / MPO overlay presenter measurements behind [ADR-0061](ADR-0061-dcomp-overlay-video-surface.md) |

They are wholly about debugging a downstream deployment. Each cites paths and
line numbers in a repository that is not published, a host profile that is not
published, and crash-dump locations on a specific machine. Generalising them
line by line would leave documents that no longer describe anything, so they
are kept outside this repository rather than rewritten into it.

**Every citation to them is provenance, not a dependency.** The comment or ADR
section that carries one already states the mechanism it is describing; the
date is there to record where the finding came from. Nothing in this repository
requires reading those documents to be understood, and where a decision
genuinely turned on one, the ADR says so at the point of citation — see
[ADR-0057](ADR-0057-pull-based-master-clock.md) §Amendment,
[ADR-0061](ADR-0061-dcomp-overlay-video-surface.md),
[ADR-0063](ADR-0063-nv12-pixel-shader-color-conversion.md) and
[ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md), each of which
names the investigation and states plainly that it is not published here.

**Commit hashes do not resolve either.** ADRs and `docs/DEFERRED_WORK.md` cite
short hashes — `04ab378`, `aeec5dc`, `a193260` and others — as the record of
when something landed. This repository was published as a fresh tree without
its development history, so none of them resolve against the published remote
regardless of whether they were valid before. Read a cited hash as a date-stamp
on a claim, not as a link. Hashes qualified as Crossbar's (`Crossbar dcee5f1`)
were never in this repository's history in the first place.

## Naming

`ADR-<4-digit number>-<kebab-case-slug>.md`, matching the existing series:

- `ADR-0001-api-first-foundation-sequencing.md`
- `ADR-0002-ffmpeg-bootstrap-strategy.md`
- `ADR-0003-audio-master-sync-policy.md`

Numbers are assigned at merge, not at authoring — see "Drafts pending number assignment" below.

## Cross-repo references

**Always qualify an ADR reference from another repository with the repo name:
`Crossbar ADR-0014`, never bare `ADR-0014`.**

FrameFlow's series and Crossbar's series collide on number. FrameFlow's
ADR-0014 is native binary packaging; Crossbar's ADR-0014 is the substrate
migration whose "Phase 3 / Phase 4" milestones a lot of this codebase was
written against. FrameFlow's ADR-0010 is logging and diagnostics; Crossbar's
ADR-0010 is consumer-function unification, which is what the
`FrameConsumer<TFrame>` ownership comments mean. Unqualified citations sent
readers to the wrong document in both cases (issue #97).

When the claim also has a FrameFlow ADR that covers it, cite both — for example
`(ADR-0030; Crossbar ADR-0014 Phase 4)` on the sink dataflow contracts.

## Suggested template

Each ADR should capture:

1. status
2. context
3. decision
4. consequences
5. alternatives considered

## Drafts pending number assignment

ADRs are numbered at merge, not at authoring, so parallel branches never collide
on the same number. Drafts in flight live here under a slug filename until they
land.

- [Stream-backed media sources, via a custom AVIO context](stream-backed-media-sources.md) —
  there is no way to hand FrameFlow bytes. An in-memory clip, an embedded resource or a
  decrypted blob has to be written to a temp file first, which needs a writable filesystem
  and leaves plaintext on disk. Proposes `MediaSource.FromStream` over a custom
  `AVIOContext`, taking a factory that opens a fresh stream on every open, and reading
  seekability from each opened stream's `CanSeek` rather than from the caller. The factory
  replaced a `Stream` parameter in a 2026-09-13 amendment: a loop, replay, a playlist and a
  second player all open a source again, and a stream is spent after one play.
  The reason it is an ADR rather than a PR is the lifetime rules: FFmpeg holds raw
  function pointers into managed callbacks, reallocates the buffer it was given, and calls
  back from the demux thread. It declines the implicit `string` conversion #108
  also asks for, because a path and a URL are indistinguishable at the call site. Two
  acceptance conditions stand, neither measured: whether a forward-only stream probes
  completely, and how teardown behaves when the read callback throws. Revised once after
  an independent review, which caught that the draft's way of marking a stream
  non-seekable did not work — FFmpeg reads seekability off whether the seek pointer is
  non-NULL, so the draft would have run its own gating experiment against a configuration
  where the mechanism under test could not engage. Its Revision history keeps the
  superseded reasoning, which is what three of the open questions are about.
- [A video-only pipeline that falls behind skips decode work](lateness-driven-decode-skip.md) —
  it stays realtime by shrinking decode cost under a lateness-driven discard level,
  rather than losing time. Implements the drop responsibility ADR-0003 already assigns
  to the playback layer. Its acceptance prerequisite is confirmed — `skip_frame`
  suppresses frame output on the hardware path, ~15 s of lag down to ~0.26 s — and
  its second condition is settled too: the escalation middle is a proportional skip
  of the GPU-to-CPU readback rather than a `skip_frame` level, recovering fully at
  1 in 4 while keeping four times the frames keyframes-only would, on an exactly
  even cadence — including on reordered video, since `avcodec_receive_frame` hands
  back frames in presentation order. The two mechanisms are one path ordered by
  damage, and thinning ceasing to help is what identifies a decode-bound pipeline,
  so nothing has to classify one up front — a rule that is itself gated on being
  measured against a running policy before the default changes. Its rejected alternative F
  records the packet-pacing design this replaced, and the measurement that ruled it
  out.
- [One builder, two terminals](one-builder-two-terminals.md) — **superseded** by
  [ADR-0079](ADR-0079-the-pass-and-the-player.md). The fluent surface
  returned the weaker object: `BuildAsync` yields a single-shot `MediaPass`, while
  the eleven-parameter `MediaPlayer.CreateAsync` holds the whole state machine. Adds
  `BuildPlayerAsync` as a second terminal on the same chain, and narrows the chain to
  `IMediaPlayerBuilder` the moment an option only a player can honour is set, so
  setting repeat mode and then asking for a session is a compile error rather than a
  dropped setting. Records the cost of the narrowing — a second overload for every
  builder extension method — and the interface break it accepts to get there.
- [End of queue, replay and faults on the playlist player](playlist-end-of-queue-replay-and-faults.md) —
  the playlist player disagrees with the single-source player about what happens at the end. Six
  defects were reproduced: seek from `Ended` plays nothing, play from `Ended` with an empty queue
  faults into `Error`, a fault on the last item ends in a clean `Ended`, an item that faults on
  every pass loops forever without reporting it, the controller keeps the first item's duration and
  raises a false `LoopStalled`, and `SetNext` under `All` grows the rotation. Decides fixes for
  three without changing public API or single-source playback: keep the last item at the end of the
  queue and pause it first on a skip, refuse play from `Ended` on an empty queue with a failed
  `Result`, and feed the controller the current item from its dispatch loop. The first two are
  implemented with #170, which also refuses a seek from `Ended` when nothing was kept, and the third
  with #183. The `SetNext` defect,
  and other defects found in review, are recorded without decisions because the proposed fixes
  failed review. A later decision, implemented with #180, settles the two fault defects: every
  failed item is reported on `ErrorOccurred` and the playlist carries on, and the player enters
  `Error` only after nine items fail in a row, where a skip, a natural end, or five seconds or half
  an item's length of play breaks the run. Another, implemented with #182, makes an advance follow
  the controller's state: a skip while paused leaves the next item paused, a skip at `Ended` does
  nothing, and under `Off` a skip on the last item while paused ends the playlist. Its first draft proposed
  running every player on the playlist session; a spike passed the suites that way, and a review that
  reproduced it showed the queue under test never advanced and single-source faults went silent.
  Two reviews shaped it, and its revision history says what each changed. An amendment records,
  without deciding it, a direction for the `SetNext` defect: a playlist with a cursor for the loop
  and a separate up-next queue for items that play once.
- [Frame-pool ownership for buffered decoded video](frame-pool-ownership.md) — a held
  D3D11VA frame pins a slice of a fixed decode pool, so the pacing ring cannot grow past the
  spare slices, while VideoToolbox and software decode have no such ceiling. Gives fixed-pool
  backends a FrameFlow-owned pool that each decode slice is copied into, which is the copy the
  presenter's converter already performs per frame, and leaves ADR-0025's sink-owned pool
  alone. Paired with the [video lookahead](../feature-specs/video-lookahead/spec.md) spec,
  which is the only thing that would spend the depth.
- [Declared pull: the master clock as a graph-visible dependency](declared-pull-clock.md) — the
  substrate models edges and nodes, and the master clock is neither, so no rule and no diagnostic
  can see which nodes depend on one. It set out to register clock readers on the `Graph`. Drafting
  it against the wiring found that the clock's author sits outside the graph on the no-audio path,
  so a reader-only registry validates nothing, and that the one in-graph clock reader, `PaceUntil`,
  has no call site left. It decides the rule instead: the clock stays a pull, no pump body awaits
  it, and `PaceUntil` goes. The registry is deferred, with the condition that would revive it.
- [An immutable blueprint for the graph](immutable-graph-blueprint.md) — `Graph` is a description
  and a runner in one type, so the topology is assembled by side effect, validated only inside
  `RunAsync`, and re-run by resetting mutable port state. `_resets`, `BeforeEachRun` and
  `SubstrateSession`'s `GraphPolicy` are three answers to one question. Proposes a `GraphBlueprint`
  value validated at construction, a separate `GraphInstance` owning the channels and the pumps,
  and node specs as factories, which is the layer [ADR-0078](ADR-0078-graph-chain-forks-joins-and-termination.md)
  named as missing when it rejected reusable blueprints. The costs are the typed `Connect`'s
  compile-time proof and a break across 80 construction sites. `FrameFlow.Graph` has shipped
  nothing, so that break is free until it does.
- [Every playlist event names the item it is about](playlist-events-name-their-item.md) —
  `ErrorOccurred` and `LoopRestarted` carry no `PlaylistItem`, so a host that keeps its own model of
  the queue cannot say which entry an event belongs to, and reading `GetPlaylist()` inside the
  handler races the advance that raised it. Both events were decided when a single source and a
  queue were different players; [ADR-0077](ADR-0077-one-player-type.md) made every player a queue
  without revisiting either payload. Proposes `IMediaPlaylistPlayer.ItemFailed` and `ItemLooped`,
  carrying the item, plus a reason on `PlaylistTransition` so a consumer counting completed passes
  stops counting failures as passes. Supersedes [ADR-0075](ADR-0075-looping-on-both-players.md)'s
  deferral of #203, whose stated condition is now answered, and one respect of
  [ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md). Settles #203 and #306 and the
  API half of #173; #303 is a chrome refresh bug and is excluded, with the reason recorded. Revised
  after an independent review, which caught that the reason describes the item a transition *left*
  while every other field on that record describes the item it entered — so `FailedItem` on a queue
  of `[A, B]` where A fails would have led a consumer to mark B. The transition now carries
  `Previous`, and decision 2 gained the correlation rule the two failure channels needed:
  `ItemFailed` first, carrying the same `PlaybackError` by reference. The cost is three more members
  on a surface whose doc comment already runs to four paragraphs, and a duplication reconcilable
  only by a contract the types do not enforce.

## Recently numbered

Numbered and accepted on 2026-09-16, once each record's implementation had landed. The
summaries below were written while they were drafts; the records themselves are current.

- [Sync-window join for media-time correlation](ADR-0073-sync-window-join.md) — the substrate
  fans out and cannot rejoin, so four consumers hand-roll the same correlation outside
  the graph. Adds a two-input node that pairs a slow secondary onto a fast primary by
  media time, with two match policies sized to those four. Ships with the LiveCaptioning
  detection overlay migrated onto it, which deletes the `_inferenceBusy` gating in
  favour of a `LatestWins(1)` edge; the caption overlay waits on ADR-0047's lookahead.
- [The playlist player's queue: a playlist with a cursor, and items that play once](ADR-0074-playlist-queue-model.md) —
  the playlist coordinator keeps an upcoming queue and a loop buffer that disagree: `SetNext` under
  `All` grows the rotation, a switch to `All` mid-queue loops only what played after it, a skip under
  `One` restarts the item, and enqueueing on every hand-off under `All` grows without bound. Proposes
  kept playlist items with a cursor, plus next and queued items that play once, in one defined
  order. `EnqueueAsync` and `SetNextAsync` keep their orders but stop joining the loop and return
  the item they add; `AddAsync`, `JumpToAsync`, `RemoveAsync`, `ClearAsync`, an atomic `ReplaceAsync`
  and a `GetPlaylist` snapshot are new; items have their own identity; a skip under `One` moves on;
  and Play from `Ended` starts the playlist again. Rejects keeping every enqueued item, on a measured
  296 bytes per item under the documented rotation pattern, and ADR-0062's replaceable `SetNext`
  slot. Revised after an independent review found that the first draft's cursor retried a failed
  item, a jump racing an advance was lost, and removal left the cursor undefined. Implemented with
  #171.
- [Looping on both players](ADR-0075-looping-on-both-players.md) — the single-source and playlist players
  disagree about looping. `RepeatMode.All` ends a single source and loops a playlist of one, the
  two players rewind differently (#172), a single source reports a loop with `LoopRestarted` and a
  playlist only with `SourceTransitioned` (#173), `IMediaPlayer` exposes no loop event, and the
  loop-stall watchdog does not watch a playlist of one under `All`. Proposes to supersede ADR-0021:
  `All` repeats the queue, so a single source loops under it; the queue decides repeats; a loop
  that ends while paused goes back to its start and stays paused; a playing loop rewinds in place,
  because the full seek the single source reverted to keeps the same decode device; `LoopRestarted`
  fires on both players once the item is back at its start, carries a per-item count, and joins
  `IMediaPlayer`; and the watchdog watches every expected loop. The playlist half changes the
  playlist session's protocol core. A single source gets the rest by running as a queue of one,
  which a separate record decides; the controller changes it would otherwise need are kept as a
  fallback. Revised after an independent review measured the decode device and loop gaps on both
  paths, and again onto the playlist session protocol. Nothing is implemented.
- [The playlist session as a pure protocol, with its queue as a value](ADR-0076-playlist-session-protocol.md) —
  the playlist session's decisions are spread over 1,051 lines of async code behind a transition
  gate, and the session builds its own item runtimes, so its orderings are tested only over real
  playback with holds inside the gate. Proposes the ADR-0055 and `PlaybackProtocol` pattern for it:
  the queue becomes an immutable value with pure operations under the coordinator's lock, and the
  session becomes a pure step function whose shell awaits each step on an unbounded channel, as the
  controller's does. Tests become tables over an abstraction of the state, transcripts that fail
  with their fix reverted, and an ordering explorer over an item model with invariants. Migration
  first adds seams so transcripts pin today's behaviour. Revised after an independent review found
  that the first draft's non-awaiting event loop hung disposal and dropped live end-of-stream.
  All four steps of the migration are implemented: the seams, transcripts that fail with their fix
  removed, the queue as an immutable value, the session as a pure core with an awaiting shell, and an
  ordering explorer over the core.
- [One player type: every player is a queue](ADR-0077-one-player-type.md) — the single-source player and the
  playlist player run different sessions, and every fix since #170 has had to say which one it was
  for. Proposes that `PlaybackController.Create` build the playlist session over a coordinator of
  its own, so a single source is a queue of one: each load makes the loaded source that queue's only
  item, a replay from `Ended` keeps what was enqueued, and both player factories build one
  implementation. `RepeatMode.All` then loops a single source, a mid-stream fault is reported and
  ends the player instead of entering the terminal `Error`, and the loop is the session's in-place
  rewind, which deletes the controller's loop path, its repeat input and two protocol cells, and
  takes a loop out of the seek state machine that ADR-0028 §2 routed it through. Supersedes the
  end-of-queue record's decision 1 and the looping record's decision 8. Rests on a spike whose
  suites pass with every one-item playlist built as a single source, and on a hardware run where the
  two builds present the same frames at the same rate with no stall. Defers folding
  `IMediaPlaylistPlayer` into `IMediaPlayer`, which would break external implementers for no
  behaviour. Nothing is implemented.
- [The chain declares its forks and joins, and the builder always terminates it](ADR-0078-graph-chain-forks-joins-and-termination.md) —
  `GraphChain<T>` covers a linear segment and cannot carry a cloner, so every fork-and-rejoin
  consumer drops to port-level `Connect`, and which branch inherits the incoming ref is a
  wiring-order fact that both call sites restate wrongly. Adds `Branch` and a chain-returning
  `Join`, and has a chain-built fork mark its trunk as the inheritor, with ADR-0054's
  first-cloner-less scan unchanged for `Connect`, because a plain fan-out over a one-shot frame
  depends on it. Then collapses the configurator to one contract: it returns an open chain
  and the builder terminates it, which removes the configurator-terminated path and with it
  ADR-0057's carve-out, so every configured graph is a single-sink graph whose configured segment
  sits upstream of the pacer. The cost is a behaviour break that is not a compile error, for the
  three examples that terminate inside their configurator.
  **Implemented** in #242, #243, #244 and #245; its amendment records three departures,
  two motivations the code does not support, and a corrected cycle search.

- [The pass and the player: a clock decides the entry](ADR-0079-the-pass-and-the-player.md) —
  the builder's two terminals read as a difference of transport surface, and the difference is
  pacing: `MediaPass` holds no clock and never touches `ClockSelectVideoSink` or `PaceUntil`,
  so video through `BuildAsync` runs as fast as the sink accepts. Both in-repo users are audio-only,
  where the device supplies the timing, which is why the gap has not shown. Proposes a second entry
  point so the choice is made in the first call rather than the last: `FrameFlowPass.Create(path)`
  builds a `MediaPass` run with `RunToCompletionAsync`, and the pass takes one source at its entry
  and has no queue, because the sink is what is worth reusing across files and the caller already
  owns it. That lets the player's builder drop the narrowing whose only job was keeping player-only
  options off a chain that could still end in a session, folding `IMediaPlayerBuilder` into
  `IPlayerBuilder`, and it closes the run-time hole breaking change 24 opened. Supersedes
  [One builder, two terminals](one-builder-two-terminals.md). Defers whether a pass yields hardware
  frames, and whether `MediaPlayer` survives. **Implemented**; its *As implemented* section
  records the sink rule, the clock seam and where the fold landed.

(Most recently, tests stopped depending on elapsed time as
[ADR-0072](ADR-0072-tests-do-not-depend-on-elapsed-time.md) — wall time is `TimeProvider`
faked with `FakeTimeProvider`, a test about a background worker waits on a signal from the
worker rather than a duration, health gates assert counts and conservation instead of
wall-clock-derived values, and only `FrameFlow.Integration.Tests` and a type whose defining
property is timing may read the clock; it extends
[ADR-0007](ADR-0007-testing-and-validation-strategy.md), which asked for deterministic seams
and said nothing about what a test may do once they exist, and it is enforced by a
banned-API ratchet in `tests/` shaped like the public-API baseline in `src/`, because the
clean state of sixteen test projects out of twenty-one had been reached by nobody deciding
anything. Before it, the audio clock was told what it is allowed to believe as
[ADR-0071](ADR-0071-what-the-audio-clock-is-allowed-to-believe.md) — a paused device's
sample counter is no longer read at all, a reading that advances further than elapsed
playing time is treated as audio the device dropped rather than played, and a removed
endpoint is named through `ALC_CONNECTED` instead of being indistinguishable from
playback; it amends [ADR-0003](ADR-0003-audio-master-sync-policy.md), whose "most stable
continuous time source" holds while the device is playing and says nothing about when it
is not, and it restores in delta form the `min(audioTime, sessionElapsed)` clamp that was
deleted for clamping the clock to zero after every seek. Before it, the FFmpeg resolver
stopped waiting to be installed as
[ADR-0070](ADR-0070-resolver-installs-itself-on-first-native-use.md) —
`SetDllImportResolver` moved to a module initializer in `FrameFlow.Native`, and a P/Invoke
that finds nothing loaded runs a default bootstrap once rather than throwing
`Unable to load DLL 'avformat'`; it amends [ADR-0002](ADR-0002-ffmpeg-bootstrap-strategy.md),
whose rejection of implicit loading it answers by making the remaining failure carry the
bootstrap's own diagnostic, and it closes issues #124 and #55, which were the same
convention failing one layer apart. Before it, the playback stack's two error models were
collapsed into one as
[ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md) — `IMediaPlayer`'s
transport commands return `Result` like the controller under them, instead of flattening
`ErrorCategory` into an `InvalidOperationException` message, and gain the `ErrorOccurred`
channel the player surface never had; the deciding evidence was on the consuming side, where
every chrome call site wrapped the player in a bare `catch { }` that discarded refusals and
chrome bugs alike. Before it, the command-driven test-bench host landed as
[ADR-0068](ADR-0068-command-driven-test-bench-host.md) — a console host under `tools/` that
drives a real player from typed commands, so reproductions stop living in example launch
profiles; its Decision 6 was reopened mid-flight and the bespoke assertion grammar dropped in
favour of C# file-based repro apps, which closed six of its own open questions. Before it, the
pacing-clock timer-resolution decision landed as
[ADR-0067](ADR-0067-high-resolution-pacing-timers.md), moving the Windows high-resolution timer
from a thing every host had to ask for into the library's own default and superseding the
consumer guidance in ADR-0018 and the deferral in ADR-0057; before that, the zero-copy converter
decode-device identity / ownership decision landed as
[ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md), fixing the warm-sink player-swap
presenter freeze (Decision 1) and then — Decision 2, implemented 2026-06-21 — making the swap
gapless by giving the converter its own D3D11 device so it rebinds in place instead of rebuilding,
plus splitting enqueued-vs-committed present observability.)
