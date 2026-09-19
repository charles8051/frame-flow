# ADR-XXXX: Every playlist event names the item it is about

## Status

Proposed (2026-09-19). Draft pending number assignment.

This record decides that an event a playlist player raises about an item carries that item, rather
than leaving a subscriber to infer it from a snapshot read inside the handler. It settles #203 and
#306, and the API half of #173. It changes public API on `IMediaPlaylistPlayer` and one internal
action; it changes no behaviour.

It supersedes:

- **[ADR-0075](ADR-0075-looping-on-both-players.md) *Not settled here*, "Pairing a loop with its
  transition (#203)"**, which deferred the question on a condition
  [ADR-0077](ADR-0077-one-player-type.md) has since answered.
- **[ADR-0069](ADR-0069-one-error-model-across-the-playback-stack.md)'s `ErrorOccurred` decision**,
  in one respect only: it decided what a single-source player reports, and ADR-0077 made every
  player a queue without revisiting the payload.

Line numbers cite commit 7be7098.

Related: [ADR-0062](ADR-0062-gapless-playlist-warm-presenter.md),
[ADR-0074](ADR-0074-playlist-queue-model.md),
[ADR-0076](ADR-0076-playlist-session-protocol.md). Issues #173, #203, #303, #306.

## Context

### One change turned two single-source events into playlist events

ADR-0069 put `ErrorOccurred` on `IMediaPlayer` when `PlaybackController.Create` and
`CreatePlaylist` built different players. A caller who wanted a queue asked for one. A caller who
did not never saw an item boundary, so an event with no item on it named the only item there was.

ADR-0077 decision 1 made every player a queue. `PlaybackController.Create` now builds a
`PlaylistSession` whose queue holds one item, and `MediaPlayer.CreateAsync` and
`MediaPlaylistPlayer.CreateAsync` build the same object. Every `ErrorOccurred` subscriber is now a
playlist subscriber. The payload was not revisited.

The same happened to `LoopRestarted`, which ADR-0075 decision 5 added to `IMediaPlayer` on the
strength of both players raising it.

### What a subscriber can and cannot tell today

| Event | Carries | Names the item |
|---|---|---|
| `IMediaPlayer.ErrorOccurred` | `PlaybackError(Category, Message, Inner)` | No |
| `IMediaPlayer.LoopRestarted` | `LoopRestarted(LoopCount, ItemDuration)` | No |
| `IMediaPlaylistPlayer.SourceTransitioned` | `PlaylistTransition(Source, MediaInfo, Index, Wrapped, Item)` | Yes, and does not say why it fired |

The identity exists at the raise site and is thrown away on the way out.
`PlaylistSessionAction.ReportItemFailed` carries `string Source`
(`src/FrameFlow.Playback/PlaylistSessionInput.cs:172`), a display name rather than the item, and
`PlaylistSession.ReportItemFailed` folds it into the message text
(`src/FrameFlow.Playback/PlaylistSession.cs:695-702`). `PlaylistItem` compares by reference exactly
so that the same source added twice is two items; a display name cannot separate those two either.

### Reading the current item inside the handler is a race

A subscriber that calls `GetPlaylist()` or reads `CurrentSource` from an `ErrorOccurred` handler
sees whatever the advance has already made current. The report is part of the advance that skips
the failed item, so the player has moved on. ADR-0075 records the same race for a loop: a jump
recorded during the rewind is taken as soon as the item has started again.

### What this costs a host

A host that keeps its own model of the queue, to mark an entry unplayable, drop it from a rotation,
or report per-entry health, cannot say which entry an event belongs to. The player's own recovery
is unaffected. What is missing is the host's ability to mirror it.

The session also separates `PlaylistItemFailure.FaultedDuringPlayback` from an item that could not
be started, and then discards the distinction into prose. A host that wants to retry one and not
the other cannot.

### #303 is a different defect and is not in scope

`FrameFlowStreamSummary` subscribes only to `StateChanged`
(`src/FrameFlow.Avalonia/FrameFlowStreamSummary.cs:71`). A playlist transition keeps the player
`Playing`, so the summary never re-reads `MediaInfo` and keeps showing the first item's codec and
size. `IMediaPlayer.MediaInfo` follows the current item (`PlaybackControllerCore.cs:507-552`, via
`SessionCallbacks.OnCurrentItemChanged`), and `PlaylistTransition` already carries the new
`MediaInfo`. The payload is right and the chrome ignores it. The fix is a subscription in
`FrameFlow.Avalonia`, and it is not this record's.

## Decision

### 1. An event about an item carries the item

A playlist event type lives in `FrameFlow.Playback`, next to `PlaylistTransition`, because
`FrameFlow.Media` cannot name `PlaylistItem`. That is the layering constraint #203 states. The
event is exposed on `IMediaPlaylistPlayer`, not `IMediaPlayer`.

`PlaybackError` does not gain an item. It lives in `FrameFlow.Media`, and it is also the payload of
a refused transport command, where there is no item to name.

### 2. `IMediaPlaylistPlayer.ItemFailed`

`IObservable<PlaylistItemFailed>`, carrying the `PlaylistItem`, the `PlaybackError` already built
for `ErrorOccurred`, and the `PlaylistItemFailure` the session already decided.

`PlaylistSessionAction.ReportItemFailed` takes the `PlaylistItem` rather than `string Source`, and
`SessionCallbacks.OnRecoverableError` carries it to the controller. The message text is unchanged.

**Both channels report the failure, and the contract says how they relate.** `ErrorOccurred` is not
withdrawn: it is the only failure signal a caller holding `IMediaPlayer` has, and withdrawing it
would break a single-source caller that gets a mid-stream fault there today. So:

- **`ItemFailed` is authoritative for a caller holding `IMediaPlaylistPlayer`.** Its doc comment
  says to subscribe to it rather than to `ErrorOccurred` for item failures, and `ErrorOccurred`'s
  says the same in reverse.
- **One failure raises both once.** `ItemFailed` carries the same `PlaybackError` instance that
  `ErrorOccurred` raises, by reference. A consumer that subscribes to both can discard the second
  sighting by reference equality without an added identifier.
- **`ItemFailed` is raised first.** Both travel the controller's dispatch loop in that order, so a
  consumer holding both sees the item before the bare error.

Reference identity is the correlation key because the two events are one report fanned out on one
path, not two reports that have to be matched. An identifier would be a second mechanism for a
question the object reference already answers.

**The duplication has an end date, and it is already scheduled.** `ErrorOccurred`'s item-failure
reporting is the compatibility path for a caller that holds only `IMediaPlayer`. It exists because
`IMediaPlaylistPlayer` is still a separate interface, which ADR-0077 decision 2 keeps "for now" and
defers folding "to the next release that breaks implementers for other reasons". When that fold
happens there is one surface, every caller can take `ItemFailed`, and withdrawing item failures
from `ErrorOccurred` stops being a silent break. That release is the boundary, and the same one
governs `ItemLooped` and `LoopRestarted` under decision 3. Until then the fan-out is two events on
one payload, not two mechanisms.

Settles #306.

### 3. `IMediaPlaylistPlayer.ItemLooped`

`IObservable<PlaylistItemLooped>`, carrying the `PlaylistItem` and the `LoopRestarted` instance
`IMediaPlayer.LoopRestarted` raises, which already holds the `LoopCount` and `ItemDuration`.

This settles #203 without the ordering promise ADR-0075 decision 5 declined to make. That option
required `LoopRestarted` and `SourceTransitioned` to travel one ordered path, and it still left
every consumer deriving identity from a snapshot read inside a handler. Carrying the item is
unconditional and needs no promise about the other event.

`IMediaPlayer.LoopRestarted` is unchanged, and **the loop pair takes decision 2's contract
verbatim**: `ItemLooped` is authoritative for a caller holding `IMediaPlaylistPlayer`, one loop
raises both once, `ItemLooped` carries the same `LoopRestarted` instance by reference, and
`ItemLooped` is raised first.

`PlaylistItemLooped` nests `LoopRestarted` rather than copying its two fields, so the reference
that makes the pair reconcilable is the payload itself. `PlaylistItemFailed` nests `PlaybackError`
for the same reason. Two events about one occurrence, on one dispatch path, sharing one payload
object.

### 4. `PlaylistTransition` says why it fired, and names the item it left

A `PlaylistTransitionReason`: the first item, a natural end, a skip, a jump, a failed item, or a
loop. And a `Previous` holding the `PlaylistItem` the transition left, null for the first item.

#173 records that a transition fires for all six and reports them identically, so a consumer
counting completed passes counts failures as passes. The protocol already decides which of the six
an advance is. ADR-0075 decision 5 defines which advance is a loop, and
`AdvanceLockedAsync(faulted: true)` is the failure path. The reason is a value the protocol already
holds.

**The reason describes the item that was left, so the record has to name it.** `PlaylistTransition`
carries the item that became current: `Source` is "the source that is now presenting" and `Item` is
"the item that became current" (`src/FrameFlow.Playback/PlaylistTransition.cs:16,39`). A reason of
`FailedItem` on a queue of `[A, B]` where A fails describes A, while every other field on that
record describes B. Without `Previous`, a consumer reading `Item` to attribute the reason marks B
unplayable. The reason and the item it is about travel on one record or the reason is a trap.

This is why decision 4 and `ItemFailed` are one record rather than two. Adding the reason without
`Previous` would have created the misattribution that `ItemFailed` exists to prevent.

#173's documentation fix stands on its own and is not blocked by this.

## Consequences

### Good

- A host can mirror the player's recovery in its own model. That is the one thing the queue took
  away from a host that previously drove the list itself.
- The `PlaylistItemFailure` distinction reaches a caller instead of being flattened into a log line.
- A consumer counting completed passes can exclude failures.

### Bad

- Three more members on `IMediaPlaylistPlayer`, whose doc comment already runs to four paragraphs
  of queue precedence.
- Two ways to learn about a failure. Decision 2 makes them reconcilable by reference identity and
  fixed order, which is a contract a consumer has to know rather than one the types enforce. The
  alternative was a break, and *Alternatives considered* says why it was not taken.
- `PlaylistTransitionReason` is a public enum that has to stay right as the protocol grows paths.
- `PlaylistTransition` grows two fields, on a record that is already five.

### Neutral

- `PlaylistSessionAction.ReportItemFailed` changing from `string` to `PlaylistItem` is internal, and
  `PlaylistSessionProtocolTests` pins it.

## Alternatives considered

### Promise an order between `LoopRestarted` and `SourceTransitioned`

ADR-0075's second option. It needs both events on one ordered path, and it leaves identity to a
snapshot read in the handler, which races a jump recorded during the rewind. It also answers only
the loop half. A failure has no transition to pair with.

### Put the item on `PlaybackError`

`FrameFlow.Media` cannot name `PlaylistItem`. `PlaybackError` is also the payload of a refused
transport command, where an item field would always be null.

### Parse the item out of the message

The message is a log line, unstable by design, and ambiguous between two items of one source.

### Make `ItemFailed` the only item-failure channel

`ErrorOccurred` would stop reporting item failures, which removes the duplication decision 2 has to
contract around. It is a silent break: a single-source caller that gets a mid-stream fault on
`ErrorOccurred` today would get nothing, with no compile error, because ADR-0077 made that caller's
player a queue and its failure an item failure. Reference identity and a fixed order cost a
paragraph of contract; this costs a consumer their only failure signal without telling them.

## Not settled here

- Whether the compatibility paths are withdrawn at the interface fold, or kept. Decisions 2 and 3
  name the release as the boundary; they do not decide what happens at it. Withdrawing both
  duplicates and keeping both are each defensible once there is one surface, and the argument
  against withdrawing now — a silent break — does not apply there.
- Whether `PlaylistTransition` should be raised as a nested payload the way the other two are, so
  all three playlist events share one shape. It predates them and carries its fields directly.

## Validation

| # | What | Where |
|---|---|---|
| 1 | A queue of three whose middle source does not exist raises one `ItemFailed` naming the middle item | Integration |
| 2 | The same source added twice, one copy removed, and a failure names the copy that failed | Protocol |
| 3 | An item that faults mid-playback reports `FaultedDuringPlayback`; one that cannot be opened reports the other | Protocol |
| 4 | A failure raises `ItemFailed` and `ErrorOccurred` once each, `ItemFailed` first, carrying the same `PlaybackError` by reference | Player |
| 5 | A consumer subscribed to both channels that discards by reference equality counts one failure | Player |
| 6 | A single-clip `RepeatMode.All` loop raises `ItemLooped` naming that item | Integration |
| 7 | A loop raises `ItemLooped` and `LoopRestarted` once each, `ItemLooped` first, carrying the same `LoopRestarted` by reference | Player |
| 8 | A jump recorded during a rewind does not change the item on the `ItemLooped` that precedes it | Protocol |
| 9 | On a queue of `[A, B]` where A fails, the transition reports the failure reason, `Item` B and `Previous` A | Protocol |
| 10 | The first item's transition reports the first-item reason and a null `Previous` | Protocol |
| 11 | `RepeatMode.One` on a queue of three reports the loop reason, with `Wrapped` false, and `Previous` equal to `Item` | Protocol |

## Revision history

- **First draft (2026-09-19).** Decision 4 added a reason to `PlaylistTransition` and left carrying
  the item a transition left under *Not settled*. Decision 2 noted that a consumer subscribing to
  both failure channels sees the failure twice, and left the relationship to the doc comments.
- **Revision after automated review of #307 (2026-09-19).** Four findings, all reproduced:
  - **The reason described an item the record did not name.** `PlaylistTransition` carries the item
    that became current. A `FailedItem` reason describes the item that ended, so on a queue of
    `[A, B]` where A fails, a consumer reading `Item` to attribute the reason marks B. Decision 4
    now carries `Previous`, and the *Not settled* entry that deferred it is gone. The finding is
    the same misattribution `ItemFailed` exists to prevent, which is why the two are one record.
  - **The two failure channels had no correlation contract.** Decision 2 now makes `ItemFailed`
    authoritative for a playlist caller, carries the same `PlaybackError` instance by reference,
    and fixes the order. The review's alternative, one authoritative channel, is recorded under
    *Alternatives considered* and rejected: withdrawing item failures from `ErrorOccurred` is a
    silent break for a single-source caller, because ADR-0077 turned that caller's fault into an
    item failure.
  - **ADR-0069's status still said nothing was superseded.** Its opening now scopes the
    supersession to what a queue's `ErrorOccurred` can attribute.
  - **Validation** gained the correlation, `Previous` and first-item rows.
- **Revision after the second turn of automated review of #307 (2026-09-19).** Two findings:
  - **The loop pair had no correlation contract, and the record said it needed none.** Decision 2
    gave `ItemFailed` and `ErrorOccurred` identity and order; decision 3 left `ItemLooped` and
    `LoopRestarted` with neither, and a *Not settled* entry claimed "a loop raises no second event,
    so there is nothing to reconcile". `LoopRestarted` is that second event, inherited on
    `IMediaPlaylistPlayer`, so the claim was false and the asymmetry was an oversight rather than a
    decision. Decision 3 now takes decision 2's contract verbatim, and `PlaylistItemLooped` nests
    the `LoopRestarted` instance rather than copying its fields, which is what makes the reference
    the payload. `PlaylistItemFailed` nests `PlaybackError` the same way.
  - **The duplication had no migration boundary.** The review accepted the contract and repeated
    that the fan-out is still surface a consumer must understand. Decision 2 now names the boundary
    that already exists: ADR-0077 decision 2 defers folding `IMediaPlaylistPlayer` into
    `IMediaPlayer` "to the next release that breaks implementers for other reasons", and at that
    release withdrawing the duplicates stops being a silent break. What happens at it is left
    unsettled rather than pre-decided.
