# ADR-XXXX: The playlist player's queue: a playlist with a cursor, and items that play once

## Status

Proposed (2026-09-14). Draft pending number assignment. Revised the same day after an independent
review, and again after automated review of #198; *Revision history* says what changed.

This record replaces the playlist player's queue with a model that keeps its items. It decides what
each existing verb means in that model, adds the verbs a caller needs to move around it and edit
it, and lists the behaviour that changes. **Nothing here is implemented.**

It settles #171, in which `SetNextAsync` under `RepeatMode.All` adds a permanent copy of its source
to the loop. It also settles the defect
[End of queue, replay and faults on the playlist player](playlist-end-of-queue-replay-and-faults.md)
left open as "switching to `All` mid-queue never wraps", and three defects found while writing this
record. It revises the direction that record's 2026-09-13 amendment sketched for `SetNext`;
*Alternatives considered* says how and why.

Line numbers cite commit 2b82f03.

Related: [ADR-0062](ADR-0062-gapless-playlist-warm-presenter.md),
[ADR-0068](ADR-0068-command-driven-test-bench-host.md),
[ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md),
[Stream-backed media sources](stream-backed-media-sources.md). Issues #171, #172, #173.

## Context

### The coordinator keeps two structures that can disagree

`PlaylistCoordinator` holds an upcoming queue and, under `RepeatMode.All`, a loop buffer
(`src/FrameFlow.Playback/PlaylistCoordinator.cs:39-40`):

- **The upcoming queue** holds what has not played yet. `First` and `DecideNext` take items from
  its head (`:196-209`, `:266-267`). `EnqueueAsync` adds to its tail (`:122-127`), and `SetNextAsync`
  adds to its head (`:133-139`).
- **The loop buffer** holds what has played in the current pass. Every item taken while the mode is
  `All` is copied into it (`:206`, `:269`). When the queue is empty under `All`, the buffer refills
  the queue and is cleared (`:253-257`).
- **Items taken under any other mode are discarded.** Under `Off` a played item is gone. Under `One`
  nothing is taken: `DecideNext` returns a replay of the current item whenever it has one (`:247`).

The session asks the coordinator for the first item, and for what follows the current one after an
item ends, is skipped or fails to start (`src/FrameFlow.Playback/PlaylistSession.cs:215`, `:623`,
`:707`).

### Defects

Probes 1 to 5 and 7 were a scratch test against `PlaylistCoordinator` with fake sources, walking
`DecideNext` by hand. Probe 6 measured retained memory with `GC.GetTotalMemory`. None was committed.
The independent review reproduced all seven.

| # | Sequence | Result |
|---|---|---|
| 1 | `[a b c]` under `All`; `SetNext(c)` while `a` plays | `a c b c a c b c …`. Every jump adds a permanent copy to the loop (#171). |
| 2 | `[a b c]` under `Off`; play `a` and `b`; switch to `All` while `b` plays | `b c c c …`. Only items taken after the switch loop, so `c` loops alone and `a` and `b` are gone. |
| 3 | `[a b]` under `One`; `SetNext(c)`; advance three times | `a a a a`. Under `One` every advance replays, so a skip restarts the current item and the jump is ignored. `c` plays only once the mode leaves `One`. |
| 4 | `[a b c]` under `All`; play `a` and `b`; switch to `Off` while `b` plays, take `c`, switch back to `All` | `c a b a b …`. `c` left the rotation because it was taken under `Off`. |
| 5 | `[a b]` under `Off`; `SetNext(x)`, then `SetNext(y)` | `a y x b`. Each call goes ahead of the last, as the method's summary says: "ahead of anything already queued". |
| 6 | Retained memory per queued item | 296 bytes for a `MediaSource.FromFile` source on a 33-character path, counting the path string, plus its queue node. 48 bytes for the node alone when the source object is shared. |
| 7 | `[a]` under `All`; enqueue a new source on each of 1,000 hand-offs | The loop buffer holds 1,001 items. The queue never empties, so the buffer is never cleared. |

Probes 1, 2, 4 and 7 are the two structures disagreeing. Probe 3 contradicts `SkipToNextAsync`'s
summary, "End the current item now and hand off to the next". It also defeats the in-tree
AvaloniaPlayer example's jump under `One`: the example jumps with `SetNextAsync` then
`SkipToNextAsync` (`examples/FrameFlow.Examples.AvaloniaPlayer/MainWindow.axaml.cs:394-412`), and
its transport bar can switch the player to `One`.

Probe 7 is the rotation pattern `IMediaPlaylistPlayer` documents for `Off`
(`src/FrameFlow.Player/IMediaPlaylistPlayer.cs:28-30`), run under the factory's default mode, `All`.
It grows without bound. At one hand-off every ten seconds and probe 6's 296 bytes, that is about 77
MB a month.

### What a caller cannot do

- **Move to an item in the playlist.** The example imitates a jump with `SetNextAsync` then
  `SkipToNextAsync`. Under `All` that is probe 1, and under `One` it is probe 3.
- **Read the playlist.** Nothing returns the queue or the items that have played.
- **Remove an item, or clear the playlist.** Nothing removes an item, so a caller cannot replace the
  playlist without disposing the player.
- **Start the playlist again from `Ended` without re-adding it.** Items played under `Off` are gone.
  Play from `Ended` with nothing queued is refused (#170), and `docs/BREAKING-CHANGES.md` tells a
  caller to enqueue every item again.

### The shipped contract and ADR-0062 disagree about `SetNextAsync`

ADR-0062 designed a replaceable slot for the item that plays immediately after the current one,
with null clearing it (`docs/adr/ADR-0062-gapless-playlist-warm-presenter.md:316-320`). The shipped
method pushes onto the head of the queue, and null is a no-op
(`src/FrameFlow.Player/IMediaPlaylistPlayer.cs:67-71`). The push has been the public behaviour since
the first release.

### Identity

The same source can be in a playlist twice. `MediaSource` is a record
(`src/FrameFlow.Media/MediaSource.cs:8-13`), so two `MediaSource.FromFile` calls on one path compare
equal while being different objects, and the coordinator compares sources by reference
(`PlaylistCoordinator.cs:276`). An item a caller wants to move to or remove needs an identity of its
own. Neither the source's equality nor its reference names one position in a playlist that holds
the source twice.

## Decision

### 1. Items, collections and the cursor

- **An item** is a `PlaylistItem`: a source with an identity of its own. The player creates it, and
  it compares by reference. The same source added twice is two items.
- **The playlist** is an ordered list of items. The factory's sources are its first items, in order,
  and `AddAsync` appends to it. Playlist items stay after they play, and under `All` the player
  loops over them.
- **Next items** are added by `SetNextAsync`. Each call goes ahead of the next items already there,
  as it does today (probe 5).
- **Queued items** are added by `EnqueueAsync`, in call order.
- **Next and queued items are one-shot.** Each leaves its collection when it is taken, and none
  joins the playlist.
- **The cursor** is a gap in the playlist: before the first item, between two items, or after the
  last. Taking a playlist item puts the cursor just after it. Taking a one-shot item does not move
  the cursor. Removing a playlist item closes the space it held, and the cursor stays between the
  items on either side, so the item that followed a removed item is the one after the cursor.
- **Taking an item** removes it from next or queued, or moves the cursor past it, and makes it
  **the current item**. The current item is opening until the session starts it, and
  `SourceTransitioned` reports it when it starts. An item that fails to start stays current until
  the next take replaces it.
- **An item is in the player** while it is in the playlist, next or queued, or is the current item
  and has not been removed.

### 2. The order

When the player takes an item after the current one ends, is skipped, or fails to start, it takes
the first of these that exists:

1. The pending jump's target (decision 5).
2. The first next item.
3. The playlist item after the cursor.
4. The first queued item.
5. The end of the pass. The first playlist item, reported with `Wrapped = true`, if the playlist has
   any items and either the mode is `All`, or the advance is a skip or failed start under `One`.
   Otherwise end-of-stream.

A queued item plays after the playlist items left in the pass, which is where "append to the tail of
the play queue" put it before. A playlist item added while queued items are playing comes before the
rest of them, because it is left in the pass.

Nothing about the repeat mode is stored in the collections. Switching mode at any point applies to
the whole playlist, which fixes probes 2 and 4.

Removing or clearing never stops the current item, including one still opening. Removal changes
only what is taken later.

### 3. `RepeatMode.One` repeats the current item until the caller moves on

- **A natural end or a fault** replays the current item, as today, whether it is a playlist item or
  a one-shot item.
- **A skip or a jump** moves on in decision 2's order, wrapping at the end of the pass.
- **A removed current item** does not repeat. Its end moves on as a skip does.

This fixes probe 3.

### 4. An item that fails to start is passed over

An item that cannot be opened or started is reported and counted as today (decision 7 of the
previous record), and the player takes the next item as it would after a skip, under every mode.
Under `One` that means a failed item is not retried in place, and the item the caller skipped away
from is not rebuilt.

Because the cursor moves when an item is taken, not when it starts, the next take does not return
the item that failed.

### 5. A jump makes an item current

`JumpToAsync(item)` records the item as the pending jump, replacing any earlier one. The target is
taken at the next take, which happens:

- **While `Playing` or `Paused`,** at once. The state rules are the skip's (#182). While `Playing`
  the target plays. While `Paused` it becomes current and stays paused.
- **Before the first play, or while the player is loading,** at the first play.
- **At `Ended`,** at the next `PlayAsync`. That is the Play that starts the playlist again
  (decision 7), or, after a seek out of `Ended`, the Play that resumes the kept item. A skip at
  `Ended` is still dropped.

Taking the target moves the cursor just after it if it is a playlist item, or removes it from its
collection if it is one-shot. Next and queued items are otherwise untouched and follow the target in
the usual order.

A jump is never lost to an advance already under way:

- **`JumpToAsync` records the target, then asks the session to advance.** It records the target
  under the coordinator's lock. Its request is not subject to the session's generation check. It
  waits for the transition gate and advances only if a jump is still pending when it gets the gate.
- **An advance already under way also looks.** Once its item has started, it checks for a pending
  jump before it releases the gate.
- **Whichever runs first takes the target.** The other finds nothing pending and does nothing.

If the jump was recorded before that check, the check takes it. If it was recorded after, the
jump's own request runs as soon as the advance releases the gate. Either way the target becomes
current straight after the item the advance started, and that item does not play through.

- **A jump to the current item,** including one still opening, does nothing and succeeds. A caller
  that wants to restart it seeks to zero.
- **A jump to an item that is not in the player,** or to any item while the player is in `Error` or
  disposed, is refused with `ErrorCategory.InvalidOperation`.
- **`ClearAsync` and `ReplaceAsync` discard a pending jump,** and removing the target removes it.

### 6. Remove, clear and replace

- **`RemoveAsync(item)`** removes a playlist, next or queued item, or marks the current item removed,
  including one still opening. A removed current item starts, if it has not, and plays on. When it ends, the player takes the next item in decision 2's
  order. An item that is not in the player is refused with `InvalidOperation`.
- **`ClearAsync()`** removes every playlist, next and queued item, discards a pending jump, puts the
  cursor before the first position and marks the current item removed. The current item plays on.
  When it ends, nothing is left to take, so the playlist ends under every mode.
- **`ReplaceAsync(sources)`** is one edit under the coordinator's lock. It clears the player as
  `ClearAsync` does, adds the sources as the playlist, and makes the first new item the pending
  jump. It returns the new items. The jump then follows decision 5, so a playing player moves to the
  new playlist at once. An empty list is an `ArgumentException`. The player in `Error` or disposed
  refuses it.

`ReplaceAsync` exists because the separate calls race the current item. With `ClearAsync`, then
`AddAsync`, then a skip under `All`, the current item can end between the clear and the add, and the
player reaches `Ended` with the new items added and the skip dropped.

### 7. Play from `Ended` starts the playlist again

- **What it plays.** `PlayAsync` from `Ended` takes the pending jump's target, or else the first item
  decision 2's order yields at steps 2 to 4. If neither exists, it starts again from the first
  playlist item, reported with `Wrapped = false`. If the player holds nothing to take, the Play is
  refused and the player stays in `Ended`, as #170 refuses it today.
- **The item is taken before anything is unloaded.** The controller's replay unloads its session
  and loads a new one. The coordinator takes the item before the unload, and a taken item plays even
  if a `ClearAsync` arrives before the new session starts. Without that, an edit between the check
  and the load leaves the new session nothing to open, and the failed load puts the player in
  `Error`.
- **An item that fails to open is passed over.** Once the player has started any item, a new
  session whose first item cannot be opened reports it and takes the next one, as an advance does.
  The load fails only if the failure count gives up. Only the player's very first item still fails
  the load when it cannot be opened, as decision 7 of the previous record says.

The previous record chose refusal over replaying the last item, partly because a refusal can be
relaxed later without breaking a caller. This is that relaxation. Starting the playlist again is not
an arbitrary choice of item, which was the objection to replaying the last one.

### 8. The failure count belongs to the coordinator

`PlaylistFailureGuard` moves from the session to the coordinator, so a count of failures in a row
survives the new session that Play from `Ended` loads. Otherwise every Play would start the count
again, and a caller that plays on every `Ended` over a playlist of fewer than nine broken items would
never see the player give up. The rules for counting and resetting do not change.

### 9. The public surface

In `FrameFlow.Playback`:

```csharp
public sealed class PlaylistItem
{
    public IMediaSource Source { get; }
}

public sealed class PlaylistSnapshot
{
    public IReadOnlyList<PlaylistItem> Playlist { get; }
    public IReadOnlyList<PlaylistItem> Next { get; }
    public IReadOnlyList<PlaylistItem> Queued { get; }
    public PlaylistItem? Current { get; }
    public bool CurrentStarted { get; }
    public int ResumeIndex { get; }          // index in Playlist of the item after the cursor
    public PlaylistItem? PendingJump { get; }
    public long Revision { get; }
}

public sealed record PlaylistTransition(IMediaSource Source, MediaInfo MediaInfo, int Index, bool Wrapped)
{
    public PlaylistItem? Item { get; init; }  // new: set on every transition the player raises
}
```

On `IMediaPlaylistPlayer`:

```csharp
IMediaSource? CurrentSource { get; }                                                   // unchanged
IObservable<PlaylistTransition> SourceTransitioned { get; }                            // unchanged
Task SkipToNextAsync(CancellationToken cancellationToken = default);                   // unchanged

Task<PlaylistItem> EnqueueAsync(IMediaSource source, CancellationToken cancellationToken = default);
Task<PlaylistItem?> SetNextAsync(IMediaSource? source, CancellationToken cancellationToken = default);

PlaylistSnapshot GetPlaylist();
Task<PlaylistItem> AddAsync(IMediaSource source, CancellationToken cancellationToken = default);
Task<Result> JumpToAsync(PlaylistItem item, CancellationToken cancellationToken = default);
Task<Result> RemoveAsync(PlaylistItem item, CancellationToken cancellationToken = default);
Task ClearAsync(CancellationToken cancellationToken = default);
Task<Result<IReadOnlyList<PlaylistItem>>> ReplaceAsync(
    IEnumerable<IMediaSource> sources,
    CancellationToken cancellationToken = default);
```

- **`EnqueueAsync` and `SetNextAsync` return the item they add.** Without it a caller holding two
  equal sources could name its own item only by position, and a position races the advance
  (alternative E). `SetNextAsync` with null adds nothing and returns null.
- **`GetPlaylist` is a snapshot,** copied under the coordinator's lock and polled like
  `GetDiagnostics`. `Playlist` keeps the order items were added in. `Current` is the item most
  recently taken. It may be a one-shot item, and so not in `Playlist`, a removed item, or an item
  still opening, in which case `CurrentStarted` is false. `IMediaPlaylistPlayer.CurrentSource` keeps
  reporting the source of the last item that started. `ResumeIndex` equals `Playlist.Count` at the
  end of a pass.
  `Revision` rises on every edit and every hand-off, so a poller can skip a snapshot that has not
  changed.
- **The snapshot and the item are classes with no public constructor,** not positional records.
  Adding a positional parameter to a record replaces its constructor and `Deconstruct`, which
  `PipelineDiagnosticsSnapshot.VideoPresentationLag` records.
- **`PlaylistTransition.Item` is init-only** for the same reason. Every transition the player raises
  sets it, so a subscriber can tell two items of one source apart. It is nullable only because the
  four-argument constructor remains for callers that build transitions themselves, such as test
  doubles, and those carry no item. `Index` keeps its meaning, a count of hand-offs (#173); an item's
  position is in the snapshot.
- **`SetNextAsync` keeps its push.** ADR-0062's slot is not adopted (alternative C).

`MediaPlaylistPlayer.CreateAsync` keeps its signature. Its sources become the playlist.

### 10. What the session changes

The session still asks the coordinator for its first item and for what to take next. Around those
calls:

- **The take says why.** The coordinator is told whether the advance followed a natural end, a
  fault, a skip or a failed start, because decisions 3 and 4 depend on it. The skip latch used
  before the first play keeps that reason too.
- **A jump has its own entry point.** It cannot go through the skip handler, which drops a skip at
  `Ended` (`PlaylistSession.cs:447-451`). The handler also detaches when its session is disposed,
  so a request during a replay's reload reaches the coordinator and waits for the new session.
- **A jump's advance is keyed on the pending jump,** not on the session's generation, and the
  advance checks for a pending jump under the gate after it starts an item (decision 5).
- **The coordinator records the current item when it is taken,** and whether it has started.
- **`CanReplay` becomes a take.** The controller asks the session to take the replay's item before
  it unloads (decision 7), and is refused when there is none.
- **`InitializeAsync` passes over items that fail to open** once the player has started any item.
- **`ReportCurrent` carries the item** at its three call sites (`PlaylistSession.cs:236`, `:717`,
  `:831`).
- **The failure guard is the coordinator's** (decision 8).

### 11. What stays the same

- The advance's state rules (#182), fault reporting and the failure count's rules (#180), the
  item kept at `Ended` and seek from there (#170), and the controller never looping a playlist (#197).
- Same-source loops still raise `SourceTransitioned` (#173), and the in-place rewind still decides
  by source reference, because it is about reusing the open runtime rather than position.
- The playlist concept stays out of `PlaybackControllerCore`, as ADR-0062 decided.

## Consequences

### Positive

- Probes 1 to 4 and 7 are fixed. `SetNextAsync` and `EnqueueAsync` never grow the rotation, a mode
  switch applies to the whole playlist, and a skip under `One` moves on.
- A caller can read the playlist, move to an item, remove items and replace the playlist without
  rebuilding the player.
- The documented rotation pattern plays in the same order as today under `Off` and `All`, and holds
  nothing after each item plays.
- Play from `Ended` starts the playlist again, and `ReplaceAsync` swaps what plays in one call. For a
  playlist of one item these are what a single-source player's replay and load do, which the
  previous record's *Deferred: one player type* section needs.

### Negative

These go in `docs/BREAKING-CHANGES.md` when the record is implemented. No behaviour change below is
a compile error. The new members and return types are, to implementers and to code that reads the
results.

- **`EnqueueAsync` and `SetNextAsync` no longer join the loop under `All`.** Today a source added
  either way joins the rotation for good. A caller that grows a rotation by enqueueing must call
  `AddAsync`. The rotation pattern plays in the same order while it keeps enqueueing, because queued
  items play before the wrap. When it stops, the player wraps to the playlist, where today it loops
  over every item it enqueued.
- **`SetNextAsync` then `SkipToNextAsync` under `All` plays the source once.** Today the source joins
  the rotation. ADR-0068's table of ways to change source needs that noted, with `JumpToAsync` and
  `ReplaceAsync` as the ways to change what the rotation plays.
- **A skip under `One` moves on.** It used to restart the current item.
- **Play from `Ended` with nothing queued now plays.** It was refused. No caller can depend on the
  refusal from a release, because #170 landed after `v0.9.0-alpha.1`. `BREAKING-CHANGES.md` entry 7
  and the "At the end of the queue" remarks on `IMediaPlaylistPlayer` have to be rewritten.
- **`EnqueueAsync` and `SetNextAsync` return values.** `await player.EnqueueAsync(source);` still
  compiles. An assembly compiled against the old signatures and not rebuilt throws
  `MissingMethodException`, as `BREAKING-CHANGES.md` records for `IMediaPlayer.Diagnostics`.
- **Six new members on `IMediaPlaylistPlayer`.** A type outside this repository that implements the
  interface, such as a test double or a decorator, stops compiling.
- **Playlist items are kept for the life of the player.** That includes the factory's sources under
  `Off`, which are discarded as they play today. A caller that adds without removing holds probe 6's
  cost per item. `EnqueueAsync` is the verb for items that should not be kept, and the docs for both
  have to say so.
- **A removed current item does not repeat under `One`.**
- **Sources are opened again.** Playlist items are reopened on every pass under `All`, which is not
  new, and now also when Play from `Ended` starts the playlist again. A source must open more than
  once, which is why the stream-backed sources draft takes a factory.
- **New public surface.** Two types, one property and eight members with new or changed signatures,
  each needing `PublicAPI` entries.

### Neutral

- The session gains the changes listed in decision 10. Its advance and state rules do not change.

## Alternatives considered

### A. Fix `SetNext` alone

#171 offers two fixes that keep today's structures: add a jump target to the loop buffer only once,
or treat a jump to a source already in the rotation as moving the play position. Rejected. Either
leaves probes 2, 3, 4 and 7, and neither gives a caller a playlist to read, jump within or clear.

### B. Every enqueued item joins a kept playlist

The previous record's amendment mapped `EnqueueAsync` to the end of a kept playlist and `SetNextAsync`
to a queue of items that play once. Rejected for `EnqueueAsync`:

- **Keeping every enqueued item grows memory under the documented rotation pattern.** Probe 6 puts
  one enqueued `FromFile` source at 296 bytes, and the pattern adds one per hand-off indefinitely.
- **Changing the pattern's verb is a silent break.** A caller that keeps using `EnqueueAsync` would
  see no difference until the process had grown.

Decision 1 keeps that record's split between kept items and one-shot items. It gives kept items a
new verb, `AddAsync`, and leaves `EnqueueAsync` doing what the pattern needs.

### C. `SetNextAsync` as a replaceable slot

ADR-0062's design: one slot, replaced by each call and cleared by null. Rejected. The push has been
the shipped behaviour since the first release, and its summary describes it. A slot would silently
drop an earlier call's item, and a null that used to do nothing would start clearing. A push already
answers "make this the very next item".

### D. Enqueued items play straight after the current item

Rejected. The rotation pattern over a playlist of several sources would change order under `Off`,
from `a b c x1 x2` to `a x1 b x2 c`. Next items already play straight after the current item.

### E. Verbs that take positions

`JumpToAsync(int index)` and `RemoveAsync(int index)`. Rejected. A position names a different item
once the playlist is edited, or an advance takes an item, between reading it and using it. An item
names one place for as long as it is in the player.

### F. A jump to the current item restarts it

Rejected. While the item plays, that is a same-source advance through the in-place rewind on a
running graph, which falls back to the cancelling path (`SubstrateSession.cs:1119-1122`). Before
the first play it rebuilds an item that has already warmed up. A seek to zero restarts the item
without either.

### G. Clear, add and skip instead of `ReplaceAsync`

Rejected, because the three calls race the current item's end; decision 6 gives the sequence.

### H. Put the queue in the controller

Still rejected, for ADR-0062's reason (ADR-0062:479-486).

## Not settled here

- **Change notifications.** A playlist UI polls `GetPlaylist` and compares `Revision`, or tracks its
  own edits. An observable of edits can be added later without a break.
- **Insert at a position, move and shuffle.** Additive, and not needed by any caller in this
  repository.
- **Why a transition happened.** Natural end, skip, jump or failure is not on `PlaylistTransition`
  (#173).
- **`RepeatMode.All` on a playlist of one item.** It loops, while the single-source player's docs
  say `All` behaves like `Off` (`src/FrameFlow.Media/RepeatMode.cs:17-24`). A single player type has
  to settle which one holds.
- **A `next` command on the test bench** (ADR-0068). Its table would need `JumpToAsync` and
  `ReplaceAsync` as well.

## Validation

Write each test first and confirm it fails on the tree before this record lands, for the reason
given. The coordinator's rules need no media, so they belong in `PlaylistCoordinatorTests` as
deterministic unit tests.

| # | Decision | Test | Today |
|---|---|---|---|
| 1 | 2 | `[a b c]` under `All`, `SetNext(c)` while `a` plays: one full cycle holds three items. | [four: `a c b c`] |
| 2 | 2 | `[a b c]` under `Off`, switch to `All` while `b` plays: the pass after `c` is `a b c`. | [`c` alone] |
| 3 | 3 | `[a b]` under `One`, skip while `a` plays: `b` is next. | [`a` replayed] |
| 4 | 2 | `[a b c]` under `All`, switch to `Off` at `b` and back to `All` at `c`: the next pass is `a b c`. | [`a b`] |
| 5 | 2 | `[a]` under `All`, enqueue on each of 1,000 hand-offs: the coordinator holds one playlist item and at most one queued item. | [1,001 items in the loop buffer] |
| 6 | 2 | `[a b c]` under `Off` and under `All`, enqueue on each hand-off: the order is `a b c x0 x1 x2`. | [passes] |
| 7 | 2 | `[a b]` under `Off`, `SetNext(x)` then `SetNext(y)`: the order is `a y x b`. | [passes] |
| 8 | 4 | `[a b c]` under `Off`, `All` and `One`, `b` fails to start after `a`: the next take is `c`. | [`c` under `Off` and `All`; under `One`, `b` is never taken, because the advance after `a` replays `a`] |
| 9 | 5 | `[a b c]` under `All`, jump to `c` while `a` plays: after `c` come `a`, reported as a wrap, then `b` and `c`. | [no verb] |
| 10 | 5 | A second jump replaces the first; `ClearAsync` and removing the target discard it. | [no verb] |
| 11 | 6 | `[a b c]`, remove `b` while `b` is current: `c` is next. | [no verb] |
| 12 | 6 | `[a b c]`, `SetNext(x)`; `a` ends and `x` is current; remove `a`: `b` is next. Remove an item before the cursor: the next item is unchanged. | [no verb] |
| 13 | 6 | `ReplaceAsync([d e])` while `a` plays: returns two items; the pending jump is `d`; a snapshot holds only `d e`. | [no verb] |
| 14 | 2, 3 | Clear under `One`, then skip: the playlist ends. | [no verb] |
| 15 | 7 | `[a b]` under `Off` at the end of the pass, then switched to `All`: the replay's take is `a`, not wrapped. | [nothing to take] |
| 16 | 7 | The replay's take, then `ClearAsync`: the taken item is still the new session's first item. | [no verb] |
| 17 | 8 | Four failures, a new session, five more: the player gives up. | [the count starts again] |
| 18 | 1, 6 | A take makes its item `Current` with `CurrentStarted` false; `RemoveAsync` and `JumpToAsync` on it succeed; it becomes started when the session reports it. | [no verb] |

Tests 6 and 7 guard today's orders, which the decisions keep. Test 8's `Off` and `All` cases guard
today's behaviour against a cursor that moves only when an item starts, which the review showed
retries a failed item until the player gives up.

Integration tests over real playback, in `FrameFlow.Integration.Tests`:

| # | Decision | Test |
|---|---|---|
| 19 | 5 | Jump while `Paused`: the item is current, the state is `Paused` and the position is zero; Play presents it. |
| 20 | 5 | Jump at `Ended`, then Play: the target plays. Jump at `Ended`, seek to zero, then Play: the target plays. |
| 21 | 5 | Hold an advance inside the session's gate, jump, then release: the target becomes current straight after the held item starts. Again, jumping just after the held item's transition is observed: the target becomes current. |
| 22 | 7 | Play from `Ended` with nothing queued: the first playlist item plays. |
| 23 | 7 | Play from `Ended` over a playlist whose first item's file has been removed: one `ErrorOccurred`, the second item plays, and the player is not in `Error`. |
| 24 | 3 | Under `One`, skip: the next item plays and repeats. |
| 25 | 5 | Jump while the player is in `Error`: refused with `InvalidOperation`. |
| 26 | 6 | Hold an advance after its take and before its item starts, remove that item, then release: the item starts and plays on, and when it ends the next item in order plays. |

Tests 21 and 26 can hold the advance with `HoldableClock`, as `PlaylistSkipStateTests` does, on a clock
call the advance makes while it holds the gate.

The AvaloniaPlayer example moves its jump to `JumpToAsync`, and the rotation pattern's docs say which
verb keeps items.

## Revision history

- **First draft (2026-09-14).** Proposed playlist entries with a cursor that moved when an entry
  became current, next and queued items, and `AddAsync`, `JumpToAsync`, `RemoveAsync`, `ClearAsync`
  and `GetPlaylist`.
- **Revision after independent review (2026-09-14).** The review reproduced probes 1 to 7 and found
  three rules that failed as written, all read from code:
  - **The cursor.** A cursor that moved only when an item started made the take after a failed
    start return the same item, which would have retried it until the player gave up. The cursor
    now moves when an item is taken, and a failed start is passed over under every mode.
  - **Racing jumps.** A jump that arrived during an advance already under way was dropped by the
    session's generation check, and the other item played through. A jump is now a pending target
    that the advance checks again once its item has started.
  - **Removal.** Nothing said where the cursor went when its item was removed. The cursor is now a
    gap, and removal keeps it between its neighbours.
  - **Play from `Ended`.** The replay could put the player in `Error` over an unopenable first item,
    or over an edit between the check and the load, and it reset the failure count. Its item is now
    taken before the unload, items that fail to open are passed over once the player has started
    any item, and the failure count moved to the coordinator.
  - **Smaller gaps.** The review also found an undefined end of pass with no playlist items, an
    undefined jump across a seek out of `Ended` and during a reload, and a clear-add-skip recipe that
    could leave the player stopped. It found `EnqueueAsync` and `SetNextAsync` unable to name their
    own item, a list of breaking changes that missed implementers and stale docs, and an understated
    list of session changes. Those led to decision 5's timing rules, `ReplaceAsync`, the two return
    values, the snapshot's `ResumeIndex`, `PendingJump` and `Revision`, and decision 10.
  - **Renames.** The item type was renamed from `PlaylistEntry` to `PlaylistItem`, because "entry"
    named both the type and the kept collection, and the AvaloniaPlayer example already has a
    `PlaylistEntry`.
  - **A jump to the current item** now does nothing instead of restarting it (alternative F).
- **Revision after automated review of #198 (2026-09-14).** Three findings, all fixed:
  - **An item still opening could not be named.** It had been taken, so it was in no collection, and
    it was not yet current, so `JumpToAsync` and `RemoveAsync` would have refused the item a caller
    had just been handed. A taken item is now the current item from the take, and the snapshot's
    `CurrentStarted` says whether it has started. Removing it marks it removed, and it starts and
    plays on, as a removed playing item does.
  - **A jump could arrive after the advance's last check.** The previous revision had the advance
    look for a pending jump after its item started, but nothing picked up a jump recorded just after
    that look. `JumpToAsync` now requests its own advance, which is keyed on the pending jump rather
    than on the session's generation, so whichever of the two runs first takes the target.
  - **`PlaylistTransition.Item` had no stated guarantee.** Every transition the player raises now
    sets it. It stays nullable because the four-argument constructor remains for callers that build
    transitions themselves.
