# ADR-XXXX: End of queue, replay and faults on the playlist player

## Status

Proposed (2026-09-12). Draft pending number assignment.

This record fixes three defects in how the playlist player behaves at the end of its queue and on
replay. It records three more, and several found in review, without deciding their fixes. It
changes behaviour, not public API. Single-source playback does not change.

It started as a proposal to run every player on the playlist session. A spike and two independent
reviews narrowed it to this. That history is kept under *Alternatives considered* and *Revision
history*, because it is the reason this record is narrow.

**Nothing here is implemented.** Every defect below was reproduced on this codebase except where
it says "read from code".

Related: [ADR-0028](ADR-0028-internal-layering-and-ownership-cleanup.md),
[ADR-0034](ADR-0034-diagnostics-surfaces.md),
[ADR-0062](ADR-0062-gapless-playlist-warm-presenter.md),
[ADR-0068](ADR-0068-command-driven-test-bench-host.md),
[ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md),
[ADR-0072](ADR-0072-tests-do-not-depend-on-elapsed-time.md). Issues #29, #44, #125, #143.

## Context

### Two sessions behind one controller

`PlaybackController.Create` drives a `SubstrateSession`
(`src/FrameFlow.Playback/PlaybackController.cs:116`). `PlaybackController.CreatePlaylist` drives a
`PlaylistSession` that composes one `SubstrateSession` per item (`PlaybackController.cs:213`).
Both run under the same `PlaybackControllerCore`, whose rules were written for one source.

ADR-0062 put the queue inside the session so the controller would not have to know about it. The
controller therefore cannot see which item is playing, and several of its rules act on the wrong
thing once a playlist is involved.

### How the defects were reproduced

The runs used the generated corpus, software decode, a counting video sink and no audio sink.
Most used the 3-second video-only clip. Defect 5 also used the half-second clip, whose audio stream
was discarded for want of a sink. Faults were injected with a `configureVideo` operator that throws
on its 21st frame. The probes were scratch tests and were not committed.

### Defects on the playlist player

1. **Play from `Ended` with an empty queue faults the player.** Under `RepeatMode.Off`, play to the
   end and call `PlayAsync`. The controller's replay unloads and reloads
   (`PlaybackControllerCore.cs:1059`, `:1075-1077`). The new `PlaylistSession` takes its first item
   from the shared queue, which is empty, and `PlaylistCoordinator.First()` dereferences a null node
   (`PlaylistCoordinator.cs:184`). The call returns "Session initialization failed" with a
   `NullReferenceException` inside, and the controller enters `Error`.

2. **Seek from `Ended` plays nothing.** Play to the end, seek to zero, play. The controller reports
   `Paused` and then `Playing`. The sink stays at 72 frames and the position stays at zero. When
   the queue runs out, `PlaylistSession` disposes the last item before it reports end-of-stream
   (`PlaylistSession.cs:368-387`), so the seek and the play find no runtime
   (`PlaylistSession.cs:209-212`, `:177-178`). A single-source controller given the same sequence
   goes from 72 frames to 83. The same happens after a skip on the last item: presentation stops at
   10 frames, and seek then play leaves it at 10.

3. **A fault on the last item ends playback silently.** Under `Off`, an item that faults mid-stream
   with nothing after it ends in `Ended` with no `ErrorOccurred`. The fault is routed into the
   advance path (`PlaylistSession.cs:271`), logged, and turned into an ordinary end-of-stream
   (`:384-388`). A single-source controller given the same fault enters `Error` and raises
   `ErrorOccurred` once. `IMediaPlayer` documents that "failures that arise mid-playback rather than
   in answer to a command surface on `ErrorOccurred`" (`src/FrameFlow.Player/IMediaPlayer.cs:34-35`).

4. **An item that faults on every pass loops forever, silently.** Under `All`, a single item that
   faults at the same frame on every pass stayed in `Playing` with no `ErrorOccurred`. It faulted
   10 times in 6 seconds and was rebuilt each time. `RepeatMode.One` behaved the same. The
   consecutive-failure guard (`PlaylistSession.cs:53`) is only compared to its limit when an item
   fails to open (`:410`). A mid-stream fault increments it (`:310`) without that check, and the
   next successful start resets it (`:426`).

5. **The controller keeps the first item's duration.** On a playlist of a 0.5 s clip followed by a
   3 s clip, after the advance the controller's `Duration` still reads 0.5 s. The player facade
   hides this by reading the coordinator (`src/FrameFlow.Player/PlaylistMediaPlayerCore.cs:61-67`).
   The controller's loop-stall evaluator does not (`PlaybackControllerCore.cs:205`). Switching to
   `RepeatMode.One` on the 3 s item raised `LoopStalled` once during healthy playback. ADR-0062 said
   a boundary callback would have to refresh the controller's snapshot (ADR-0062:411-416). That
   callback was never built.

6. **`SetNext` under `All` grows the rotation.** `SetNext` pushes onto the front of the queue
   (`PlaylistCoordinator.cs:138`), and every dequeued item is copied into the loop buffer
   (`:250`). A coordinator unit probe of `a b c` with one jump to `c` looped as `a c b c` from then
   on. Every further jump adds another permanent entry. The in-tree AvaloniaPlayer example jumps
   this way under `All` (`examples/FrameFlow.Examples.AvaloniaPlayer/MainWindow.axaml.cs:406`).

One related behaviour was checked and is not a defect on today's tree: `EnqueueAsync` while
`Ended`, followed by `PlayAsync`, plays the enqueued item.

### Drift that is not a defect

`SubstrateSessionFactory` passes `LatenessRecoveryOptions` to its session
(`SubstrateSessionFactory.cs:86`). `PlaylistSession` builds its items without it
(`PlaylistSession.cs:276-288`). `PlaybackController.Create` exposes the option
(`PlaybackController.cs:127`), but no playlist entry point does, so no playlist consumer can ask for
it today. The two controller factories duplicate their wiring (#44), and the video configurator runs
at different points depending on how the player was built (#125).

## Decision

This record decides fixes for defects 1, 2 and 5, and a documentation change for `Error`. Defects 3,
4 and 6 are recorded here and left under *Not settled here*, because both proposed fixes failed
review.

### 1. Single-source playback stays on `SubstrateSession`

The two session types remain. This record changes `PlaylistSession` and two internal points in
`PlaybackControllerCore`: the replay check in decision 4 and the snapshot update in decision 5.
`IMediaPlayer`, `IMediaPlaylistPlayer`, `IPlaybackController` and both player factories keep their
shapes.

### 2. The end of the queue keeps the last item

When the queue runs out after a clean end-of-stream, the session reports end-of-stream and keeps
the last item's runtime. It is disposed at the next advance, on unload, or on disposal. At `Ended`
the item can be sought, its counters can be read, and audio already queued on the device plays out.

This covers an item that played to its end. It does not cover a queue whose final item fails to
open, where the previous item has already been disposed; that case is under *Not settled here*.

Evidence:

- The spike under alternative A ran this change through the single-source integration suites, and
  it fixed the failures that disposal caused there: diagnostics read after `Ended`, the audio tail,
  and replay ordering. Those runs used a queue of one.
- On a scratch build of the playlist player with decisions 2 and 3 applied, seek then play from
  `Ended` went from 72 frames to 83. Two further seek-and-play passes each played the item through
  again, reaching 144 and then 216 frames. Pipeline diagnostics at `Ended` reported 72 decoded
  frames instead of 0.
- The existing playlist tests still passed, but none of them reaches the end of a queue, so they
  are not evidence for this decision.

### 3. A skip at the tail pauses the item and then keeps it

A skip on the last item under `Off` pauses that item before reporting end-of-stream, then keeps it
as decision 2 does. The pause is `SubstrateSession.PauseAsync` on the item itself, which pauses its
workers, clock and audio sink. It cannot go through `PlaylistSession.PauseAsync`, which waits on the
transition gate the advance already holds.

Keeping the item without pausing it lets it run on: frames went from 10 at `Ended` to 35 one second
later. With the pause, on the scratch build, presentation stopped at 10, seek moved to `Paused`, play
reached 35 frames, and the item then ended normally.

### 4. Play from `Ended` on an empty queue is refused

- If items were enqueued while `Ended`, `PlayAsync` plays the next one. This is today's behaviour.
- If the queue is empty, `PlayAsync` returns a failed `Result` with `ErrorCategory.InvalidOperation`
  and the player stays in `Ended`. It does not unload, and it does not enter `Error`.

The refusal has to happen before the controller's replay unloads anything. Today the replay checks
only that a session and a loaded source exist (`PlaybackControllerCore.cs:1066-1072`) and then
unloads. The controller asks the session whether a replay has something to play before it unloads;
a playlist session with an empty queue answers no. This is an internal member on
`IPlaybackSession`, so no public API changes.

Refusing is the smaller change of the two options, and it can be relaxed later without breaking a
caller. Alternative D records the other.

### 5. The controller's duration follows the current item

At each hand-off the session tells the controller the new item's `MediaInfo`. The controller's
`Duration`, `MediaInfo` and loop-stall evaluator then use the current item. This is the refresh
ADR-0062 called for.

The update is applied on the controller's dispatch loop and tagged with the session generation it
came from. The controller zeroes its snapshot when it disposes a session
(`PlaybackControllerCore.cs:1439-1440`), and a playlist advance runs on the thread pool. Without the
tag, a late update from an unloaded session could restore a stale duration, or overwrite the
snapshot of a session loaded after it. No state machine changes.

### 6. The docs say `Error` is terminal

`Error` keeps its only exit, a `Reset` trigger that nothing fires (`PlaybackProtocol.cs:398-402`).
`PlaybackState.Error` already says "an unrecoverable error occurred"
(`src/FrameFlow.Media/PlaybackState.cs:44`). The XML docs for `PlaybackState.Error`,
`IPlaybackController` and `IMediaPlayer` add what that means for a caller: a player in `Error` accepts
no further commands and must be disposed.

The docs do not promise that a new player built over the same sinks recovers. That path is untested
(`tests/FrameFlow.Integration.Tests/ErrorPathTests.cs:18-23`, #29).

## Consequences

### Positive

- Defects 1, 2 and 5 are fixed. A playlist at `Ended` can be sought, reports its counters, and does
  not fault when Play finds nothing to play.
- None of it changes public API, and single-source playback is untouched.

### Negative

- **The last item holds its resources after `Ended`.** That is its demuxer, decoders and graph, an
  active audio sink, a hardware decode device when one is in use, and GPU frames under
  `yieldHardwareFrames`. A single source already holds these at `Ended`. A playlist did not.
- **Play from `Ended` differs between the two players.** A single source replays. A playlist with an
  empty queue refuses. Before this record the playlist faulted, so no working caller loses anything.
- **Decision 5 relaxes ADR-0028's immutable loaded snapshot for playlists.** ADR-0062 already
  accepted this and it was never done.

### Neutral

- Two ways to loop remain. Under `RepeatMode.One`, a single source loops through the controller
  and a playlist loops inside the session. This record does not change either.

## Alternatives considered

### A. Run every player on the playlist session now

This was the first draft of this record. Behind an environment switch, `PlaybackController.Create`
built single-source controllers over `PlaylistSession` as a queue of one, with the last item kept at
the end of the queue and `RepeatMode.One` left with the controller. The integration, playback and
player suites all passed that way, three runs out of three. Without keeping the last item, fourteen
tests failed.

An independent review found the evidence did not support the proposal:

- **The queue never advanced or repeated.** Each load got its own coordinator with repeat fixed at
  `Off`. The advance, wrap, in-place replay and skip code did not run in any passing test.
- **Mid-stream faults went silent on single sources.** A single source that faults ends in `Error`
  today. On the spike build it ended in `Ended` with no error under `Off`, and stayed in `Playing`
  with frames frozen under `One`. The suites have no mid-stream fault test against a real session,
  so they could not catch it.
- **The controller's replay and duration are wrong once a queue advances.** These are defects 1
  and 5, and a unified path would have spread them to every player.
- **`RepeatMode.All` on one item cannot mean one thing.** Its public doc says a single-source
  player treats `All` like `Off` (`src/FrameFlow.Media/RepeatMode.cs:17-24`). The playlist player
  defaults to `All` and loops a single clip, and existing tests pin that.
- **Keeping the last item without pausing it** let a skipped item keep presenting (decision 3).

Rejected. The conditions for revisiting it are under *Deferred: one player type*.

### B. Put the queue in the controller

Still rejected, for ADR-0062's reason (ADR-0062:479-486): it threads queue policy through three
state machines. Decision 5 gives the controller the one fact it needs, the current item, without
giving it the queue.

### C. Document today's behaviour instead of changing it

Rejected. Defect 2 reports `Playing` while nothing plays, and defect 1 turns an ordinary call into a
terminal state.

### D. Replay the item that ended

Considered for decision 4: on an empty queue, `PlayAsync` replays the last item from the start, so a
playlist of one item behaves like a single source. Rejected for now. It picks the last item
arbitrarily, since it does not restart the playlist, and it raises `SourceTransitioned` for an item
nobody queued. It is also harder to take back: a refused call that later succeeds is unlikely to
break a caller, while a replay that later becomes a refusal breaks anyone who relied on it.

## Not settled here

### Defect fixes that failed review

- **Faults at the end of the queue (defects 3 and 4).** The proposal was: a fault with nothing after
  it becomes fatal, mid-stream faults count toward the consecutive-failure guard, and the guard
  resets only when an item reaches its natural end. Review found:
  - The guard would still never trip, because its limit is only checked on an open failure
    (`PlaylistSession.cs:410`).
  - An item that is skipped never reaches its natural end. A rotation advanced by
    `SkipToNextAsync` with one permanently bad item would go fatal after nine wraps, which breaks the
    skip-a-bad-item behaviour ADR-0062 intends. A source with no natural end, such as a network
    stream, would go fatal on accumulated transient faults.
  - Making the fault fatal turns a state a caller can recover from today (enqueue, then play) into
    terminal `Error`. Recovering onto the same sinks is untested (#29).
  - "Nothing after it" has no meaning under `RepeatMode.One`, where the next decision is always a
    replay.

  A fix needs a reset rule that survives skip-driven rotation, a limit check on the fault path, and
  a decision between a fatal error and a non-fatal channel the controller does not have yet
  (`PlaybackControllerCore.cs:914` raises `ErrorOccurred` only on the way into `Error`).

- **`SetNext` under `All` (defect 6).** The proposal was that an item placed by `SetNext` plays once
  and never joins the loop buffer. Review found it changes a documented way to change source:
  ADR-0068 lists `SetNextAsync` plus `SkipToNextAsync` as one (ADR-0068:196-200), and under the
  default `All` the new source would play once and the old rotation would return. The AvaloniaPlayer
  example picks entries that are already in the rotation (`MainWindow.axaml.cs:390-407`), so the
  picked file would repeat one item later. An implementation would also have to mark queue nodes,
  not sources, because the queue can hold the same source twice. ADR-0062 designed a replaceable
  "next" slot instead (ADR-0062:317). The shipped doc describes a push
  (`src/FrameFlow.Player/IMediaPlaylistPlayer.cs:41-45`).

### Defects found in review

- **Advances ignore the controller's state.** `AdvanceLockedAsync` always plays the next item
  (`PlaylistSession.cs:401`). A skip while `Paused` presented the next item, 10 frames to 46, while
  the state stayed `Paused`. Enqueue then skip while `Ended` presented, 72 frames to 109, while the
  state stayed `Ended`. A skip on the last item while `Paused` dropped the end-of-stream, because
  `Paused` has no transition for it (`PlaybackProtocol.cs:308-313`), and a later play presented
  nothing.
- **The final item fails to open.** On `[clean, corrupt]` under `Off`, the clean item is disposed
  before the corrupt one is tried, the queue then ends in `Ended` with no error, and seek then play
  leaves the sink at 72 frames. Decision 2 does not cover this.
- **Switching to `All` mid-queue never wraps.** A coordinator created under `Off` and switched to
  `All` after its first item played ran `a b` and ended. Items dequeued under `Off` never enter the
  loop buffer (`PlaylistCoordinator.cs:186-187`, `:249-250`).

### Open questions

- **Loop reporting on a playlist.** A playlist raises no `LoopRestarted`. `SourceTransitioned` with
  `Wrapped = true` fires on a natural wrap. From reading the code it also fires on each rebuild
  after a fault; the probe for defect 4 counted 11 transitions but did not record `Wrapped`. Neither
  tells a consumer "the item played to its end".
- **In-place rewind or full seek for a same-clip loop.** Under `One`, a single source rewinds with a
  full `SeekAsync(0)` (`PlaybackControllerCore.cs:483`). A comment at `:473` records that the
  in-place rewind was reverted on 2026-06-12 over a suspected presenter stall. The playlist's
  same-clip loop uses the in-place rewind (`PlaylistSession.cs:451`). Answering this needs runs
  with hardware decode and the GPU presenter.
- **The audio tail at item boundaries.** Disposing an item deactivates the audio sink
  (`SubstrateSession.cs:1268`), which stops the OpenAL source and unqueues every buffer on it
  (`OpenAlAudioSink.cs:873-887`). The spike measured that loss when `PlaylistSession` disposed the
  item of a queue of one: 8,918 samples short against a budget of 4,800. The same disposal runs at
  every item boundary. That the tail is cut there too is inferred, not measured.
- **Diagnostics across item boundaries.** The session returns the current item's counters
  (`PlaylistSession.cs:121`), so they restart at each boundary. The controller's diagnostics
  generation advances only when it creates or disposes its session
  (`PlaybackControllerCore.cs:822`, `:1447`). Two snapshots either side of a boundary carry the same
  generation while their counters are not comparable. Read from code.
- **Late end-of-stream after a seek or replay.** `PlaylistSession` handles an item's end-of-stream
  on the thread pool (`PlaylistSession.cs:297`), and its generation tags guard only against item
  replacement. An end-of-stream raised just before a seek could land after it. Read from code; this
  is the kind of interleaving #143 is about.

## Deferred: one player type

One player type, where a single source is a queue of one and `IMediaPlaylistPlayer` folds into
`IMediaPlayer`, is still a reasonable direction. It would remove the drift above and give one way
to change source instead of ADR-0068's three. It does not make switching between different sources
cheaper: opening the source, creating decoders and rebinding the presenter's converter happen at
every such boundary either way.

It should not be proposed again until all of these hold:

- The decisions in this record have landed, and the defects under *Not settled here* have decisions.
- A spike of the real design exists: one coordinator shared with the player facade, `RepeatMode`
  honoured by the queue, `Create` and `CreatePlaylist` on one construction path, and queues that
  advance and wrap.
- That spike passes a mid-stream fault test against a real session that fails on a build which
  loses the fault.
- A deterministic interleaving test for late end-of-stream exists and passes. That needs the tooling
  #143 asks for.
- The same machine has run hardware decode and the GPU presenter before and after the change, with
  no new presenter stall and no drop in frames presented in the player's diagnostics.
- `RepeatMode.All` on one item, loop ownership and loop reporting each have a recorded answer.
- The public break is accounted for: the `PublicAPI` baselines and an entry in
  `docs/BREAKING-CHANGES.md`.

## Validation

Write each test first and confirm it fails on the tree before this record lands, for the reason
given. These tests wait on real playback, so they belong in `FrameFlow.Integration.Tests`, the one
suite ADR-0072 permanently allows to read the clock. The probe results are in brackets.

| # | Decision | Test | Today |
|---|---|---|---|
| 1 | 2 | Playlist, `Off`: play to `Ended`, seek to 0, play. Frames advance. | [72 → 72 frames, position 0] |
| 2 | 2 | Playlist, `Off`: play to `Ended`. Pipeline diagnostics report decoded frames. | [0 decoded frames] |
| 3 | 2 | Playlist of two items, `Off`: play to `Ended`, seek to 0, play. The second item's frames advance. | [not probed] |
| 4 | 3 | Playlist, `Off`: skip on the last item, then seek to 0 and play. Frames advance. | [frames stay at 10] |
| 5 | 4 | Playlist, `Off`: play to `Ended`, play with an empty queue. Failed `Result` with `InvalidOperation`; state stays `Ended`; no `ErrorOccurred`. | [`NullReferenceException`, `Error`] |
| 6 | 5 | Playlist of a 0.5 s then a 3 s clip: after the advance `Duration` is 3 s; under `One`, no `LoopStalled` across two passes. | [0.5 s; one `LoopStalled`] |

Two guard tests pass today and must keep passing:

| # | Decision | Test | Today |
|---|---|---|---|
| 7 | 4 | Playlist, `Off`: enqueue while `Ended`, play. The enqueued item plays. | [passes] |
| 8 | 3 | Playlist, `Off`: skip on the last item. `Ended`, and presentation stops. | [passes] |

Test 8 cannot fail on today's tree for the reason decision 3 exists. It fails on a build that keeps
the last item without pausing it, where frames went from 10 to 35.

## Revision history

- **First draft (2026-09-12)** proposed running every controller on the playlist session, with a
  single source as a queue of one. An independent review, which reproduced the spike, found that the
  queue under test never advanced or repeated, that single-source mid-stream faults went silent on
  the spike build, and that the controller's replay and duration were wrong once a queue advanced.
  The second draft withdrew that proposal (alternative A) and narrowed the record to the playlist
  player's own defects, with eight decisions.
- **Third draft (2026-09-12)**, after a second independent review that reproduced defects 1 to 4 and
  6 and built decisions 2 and 3. Decision 4 changed from replaying the ended item to refusing the
  call (alternative D). The fixes for faults at the end of the queue and for `SetNext` moved to *Not
  settled here*: the guard as proposed never tripped and would have broken skip-driven rotation, and
  one-shot `SetNext` would have changed a documented way to change source. Decision 3 now names the
  pause it uses. Decision 5 now says where its update runs. The review's new defects were added
  under *Not settled here*, and several citations and claims were corrected.
