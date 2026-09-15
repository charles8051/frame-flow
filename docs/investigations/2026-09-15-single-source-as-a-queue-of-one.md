# Spike: a single source as a queue of one

**Date:** 2026-09-15
**Trigger:** The spike
[End of queue, replay and faults on the playlist player](../adr/playlist-end-of-queue-replay-and-faults.md)
asks for under *Deferred: one player type*, and the one
[Looping on both players](../adr/looping-on-both-players.md) defers its single-source half to.

## What was built

`PlaybackController.Create` now builds a `PlaylistSession` over a coordinator of its own, and each
load makes the loaded source that queue's only item. The playlist entry point is unchanged. Both
entry points assemble the controller through one private method (#44).

| Piece | Change |
|---|---|
| `PlaybackController` | `Create` and `CreatePlaylist` build a session factory and call one `Assemble`. `Create`'s factory is a `PlaylistSessionFactory` over an empty coordinator, with `loadsSource` set. |
| `PlaylistCoordinator` | A constructor for an empty queue, and `LoadSource`, which makes the source the only item. It keeps the queue when that queue already plays the source, so a replay from `Ended` keeps what was enqueued meanwhile. |
| `PlaylistSession` | `InitializeAsync` calls `LoadSource` when the factory set `loadsSource`. |
| `IPlaybackSessionFactory` | `RepeatModeChanged`, called with the controller's mode at construction and on every change. The playlist factory passes it to the coordinator, so the queue runs the mode the controller reports. |
| `PlaylistSessionFactory` | Takes `latenessRecovery`, which only the single-source factory used to take. |
| `MediaPlayer` and `MediaPlaylistPlayer` | One construction path. `MediaPlayer.CreateAsync` passes a queue of one and returns the playlist player's core. |

## What the suites say

The whole suite passes on the spike: 1708 tests, 1690 passed and 18 skipped.

The playlist suites were then run a second time with `FRAMEFLOW_SPIKE_SINGLE_SOURCE=1`, which makes
`PlaylistRun` build every one-item playlist through `PlaybackController.Create` instead. All 145
integration tests pass that way too, so the end-of-queue, skip-state, fault, queue, current-item and
loop-reporting tests hold when their playlist of one is a single source.

New tests, all through `PlaybackController.Create`
(`tests/FrameFlow.Integration.Tests/SingleSourceAsAQueueOfOneTests.cs`):

| Test | What it pins |
|---|---|
| `AMidStreamFault_IsReported_AndThePlayerEnds` | The mid-stream fault test the deferral asks for. The fault reaches `ErrorOccurred` and the player ends. |
| `AFaultOnEveryPass_IsReportedEachPass_ThenThePlayerGivesUp` | Under `One`, a source that faults every pass is reported each pass, and the failure guard ends it in `Error`. |
| `UnderAll_ItLoops_AndReportsEachLoop` | Row 6 of the looping record: two loops, with counts 1 and 2. |
| `UnderOne_ItLoopsInPlace_AndBuildsItsVideoChainOnce` | Row 8: the video configurator runs once across two loops. |
| `AnItemEnqueuedAtEnded_PlaysWhenThePlayerReplays` | A replay from `Ended` keeps the queue, so an item enqueued at `Ended` plays. |

Each was checked against a build with its rule removed:

- Dropping the shell's `ReportItemFailed` fails both fault tests.
- Dropping `LoadSource`'s "keep the queue that already plays this source" fails the enqueue test.

## What changes for a single source

Every line here is a behaviour change a single player type has to decide on, not a defect found in
the spike.

1. **`RepeatMode.All` loops.** It used to play one pass and end. Decision 1 of the looping record.
2. **A mid-stream fault ends the player instead of failing it.** The fault is reported on
   `ErrorOccurred` and the player reaches `Ended`, where `PlayAsync` starts it again. It used to
   enter `Error`, which is terminal. Under `One` and `All` the source is rebuilt and reported on
   every pass, and nine failures in a row without progress still end in `Error`.
3. **A loop no longer drives the seek state machine.** The loop is the session's in-place rewind,
   taken as one of its inputs, so `SeekStateChanged` is silent across a loop and
   `IsActivelyPresenting` stays true. ADR-0028 §2 routed the controller's own loop through that
   machine so a user seek could cancel it; the session now orders a seek against a loop itself.
   `PlaybackControllerIntegrationTests.RepeatOne_LoopRewindsInPlace_WithoutTheSeekStateMachine`
   replaces the test that pinned the old route.
4. **The video chain is built once per load, not once per loop.** The in-place rewind keeps the
   graph. An operator that holds state across a loop now sees the timeline jump back, which is #217.
5. **A stale end-of-stream cannot end the player after a seek.** The session drops an end-of-stream
   whose run number is out of date (decision 2 of the end-of-queue record), which is what #195 asks
   of the controller. A deterministic test for it still needs the tooling #143 asks for.
6. **`MediaPlayer.CreateAsync` returns a player that also implements `IMediaPlaylistPlayer`.** The
   declared return type is unchanged, and no public signature changed.

## What the spike does not answer

- **The hardware run.** The deferral asks for hardware decode and the GPU presenter before and
  after, on one machine, with no new presenter stall and no drop in frames presented. Not run here.
- **Row 7 of the looping record**, a single source whose loop ends while paused, needs an
  end-of-stream held until after the pause. `PlaylistSessionProtocolTests` pins it in the core.
- **The public break accounting** and the `PublicAPI` baselines belong to the proposal. This spike
  changes no public signature, but it changes what three of them do.
- **The controller's coordinator has no owner.** `Create` builds one and nothing disposes it; its
  transition subject is never completed. Nothing reads that stream on a single-source controller.
