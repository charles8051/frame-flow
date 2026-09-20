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
[ADR-0076](ADR-0076-playlist-session-protocol.md). Issues #173, #203, #303, #306, #308.

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

This is the surface before this record. Decision 4 changes the third row.

| Event | Carries | Names the item |
|---|---|---|
| `IMediaPlayer.ErrorOccurred` | `PlaybackError(Category, Message, Inner)` | No |
| `IMediaPlayer.LoopRestarted` | `LoopRestarted(LoopCount, ItemDuration)` | No |
| `IMediaPlaylistPlayer.SourceTransitioned` | `PlaylistTransition(Source, MediaInfo, Index, Wrapped)` plus a nullable `Item` | Yes, and does not say why it fired |

The identity exists at the raise site and is thrown away on the way out.
`PlaylistSessionAction.ReportItemFailed` carries `string Source`
(`src/FrameFlow.Playback/PlaylistSessionInput.cs:172`), a display name rather than the item, and
`PlaylistSession.ReportItemFailed` folds it into the message text
(`src/FrameFlow.Playback/PlaylistSession.cs:697-703`). `PlaylistItem` compares by reference exactly
so that the same source added twice is two items; a display name cannot separate those two either.

### Reading the current item inside the handler does not work

The queue has already advanced. `PlaylistSession.Step` runs the whole pure fold inside
`_coordinator.Update(...)` (`src/FrameFlow.Playback/PlaylistSession.cs:475-486`), which takes the
coordinator's lock and commits the new queue; the actions that fold emitted are performed
afterwards (`:451-452`). All four `ReportItemFailed` sites call `Queue.ItemFailed` and
`Queue.DecideNext` in that same fold. So `GetPlaylist().Current` has moved past the failed item
before the report is even queued, let alone before a handler runs. That is a certainty, not a race.

`CurrentSource` is the race. It reads `Queue.Reported?.Source`
(`src/FrameFlow.Playback/PlaylistCoordinator.cs:85`), which moves at `ReportCurrent`, when the next
item starts. It still names the failed item at raise time and moves at an unpredictable later one,
so a handler reads whichever side of that it lands on.

Neither is a workaround. ADR-0075 records the same problem for a loop: a jump recorded during the
rewind is taken as soon as the item has started again.

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
size. `EmitPlaybackTransition` raises only when the collapsed public state changes
(`PlaybackControllerCore.cs:1062`), and a hand-off does not change it.

The payload is right. `IMediaPlayer.MediaInfo` reads `PlaylistMediaPlayerCore.cs:63-67`, which
returns the coordinator's `CurrentMediaInfo`, and that follows the current item. `PlaylistTransition`
already carries the new `MediaInfo` too. The chrome ignores both. The fix is a subscription in
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

`PlaylistSessionAction.ReportItemFailed` takes the `PlaylistItem` rather than `string Source`. The
message text is unchanged.

**The item travels on a new callback, not on `OnRecoverableError`.**
`SessionCallbacks.OnRecoverableError` is `Action<PlaybackError>`
(`src/FrameFlow.Playback/SessionCallbacks.cs:55`) and `PlaylistSession` hands the same delegate to
its item runtimes, so a `SubstrateSession` uses it too: a lateness-recovery fault is reported
through it with no item in the room (`src/FrameFlow.Playback/SubstrateSession.cs:383-390`).
Widening it would make the item nullable for every caller and put "is this one an item failure?"
back on the consumer. `SessionCallbacks` gains `OnItemFailed`, and `OnRecoverableError` is
unchanged.

That is also why decision 2 scopes its advice to *item* failures rather than to `ErrorOccurred` as
a whole. `ErrorOccurred` keeps carrying the controller's own errors and the lateness-recovery
fault, and neither has an item or an `ItemFailed` to pair with.

**Both channels report the failure, and the contract says how they relate.** `ErrorOccurred` is not
withdrawn: it is the only failure signal a caller holding `IMediaPlayer` has, and withdrawing it
would break a single-source caller that gets a mid-stream fault there today. So:

- **`ItemFailed` is authoritative for a caller holding `IMediaPlaylistPlayer`.** Its doc comment
  says to subscribe to it rather than to `ErrorOccurred` for item failures, and `ErrorOccurred`'s
  says the same in reverse.
- **One failure raises both once.** `ItemFailed` carries the same `PlaybackError` instance that
  `ErrorOccurred` raises, by reference. A consumer that subscribes to both can discard the second
  sighting by reference equality without an added identifier.
- **`ItemFailed` is raised first.** One command carries the report — `PostRecoverableError` writes a
  single `RecoverableErrorCommand` (`PlaybackControllerCore.cs:472-480`) — and the dispatch loop
  fans it out to both subjects in that order. Two events, one command, so "both once, in order" is
  a property of the path rather than a rule two producers have to keep.

Reference identity is the correlation key because the two events are one report fanned out on one
path, not two reports that have to be matched.

### Where the pair does not hold, and why

ADR-0075 decision 5 enumerates the points at which a loop is and is not reported. The same is owed
here, because "raises both once" is a promise with four exceptions:

- **The first item.** A failure before anything has played is reported as fatal, not as an item
  failure: the protocol sets `GaveUp` and emits `ReportFatal` with no `ReportItemFailed`
  (`PlaylistSessionProtocol.cs:404-411`, and `OnInitializeDiscarded` at `:614-617`). This matches
  `IMediaPlaylistPlayer`'s existing rule that the first item "is treated as a single source's". So
  **`ItemFailed` does not fire for it**, and a host learns of it from the load's failure or from
  `PlaybackState.Error`. This record documents that rather than changing it.
- **Give-up.** The eighth consecutive failure emits `ReportItemFailed` before `GiveUp`
  (`:415-427`), so that failure does raise `ItemFailed`, and a fatal follows it.
- **Disposal.** `OnFailedStartDiscarded` (`:988-992`) and `OnInitializeDiscarded` (`:614`) emit
  nothing while disposing. Neither event fires.
- **A superseded session.** The dispatch loop drops a `RecoverableErrorCommand` whose generation
  the controller has replaced (`PlaybackControllerCore.cs:1460`). Neither event fires, which is
  what makes the fan-out safe: the drop is upstream of both.

`ErrorOccurred`'s item-failure reporting is the compatibility path for a caller that holds only
`IMediaPlayer`. It ends at a boundary that is **contingent, not scheduled**: ADR-0077 decision 2
keeps `IMediaPlaylistPlayer` separate "for now" and defers the fold "to the next release that
breaks implementers for other reasons". No such release is planned, so an implementer should read
this as a duplication with no end date attached to it, ending whenever that break next happens.

> **Amended 2026-09-20.** "No such release is planned" is no longer true — the pending release
> breaks implementers of `IMediaPlayer` three times over — and the fold was reconsidered on that
> trigger and declined. ADR-0077 decision 2's amendment retires the condition rather than
> satisfying it: the small interface exists to be consumed polymorphically, so folding taxes
> implementers for a surface consumers already reach. The duplication described below therefore has
> no end date attached to it at all, and #308, #306 and #203 are decided on their own merits rather
> than waiting on a fold. Nothing about the ordering or the shared instances below changes. At
that point there is one surface, every caller can take
`ItemFailed`, and withdrawing item failures from `ErrorOccurred` stops being a silent break. The
same boundary governs `ItemLooped` and `LoopRestarted` under decision 3.

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
for the same reason.

Decision 2's exceptions apply here too, with one difference: a loop has no first-item case, because
an item that has not played cannot have looped.

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
unplayable.

`Item` is a nullable `init` property rather than a constructor parameter, null "on a transition
built with the four-argument constructor" (`PlaylistTransition.cs:33-39`). `Previous` is nullable
for a reason of its own, the first item. A consumer attributing a reason therefore handles two
nullable fields, and the doc comment on the reason says which one it is about.

**Why this is in the same record.** The coupling above shows that the reason and `Previous` belong
together; it does not by itself show that either belongs beside `ItemFailed`. What puts them here
is that all three are one defect: a playlist event a consumer cannot attribute to an item. Splitting
decision 4 into its own record is defensible, and *Alternatives considered* says why it was not
taken.

#173's documentation fix stands on its own and is not blocked by this.

## Consequences

### Good

- A host can mirror the player's recovery in its own model. That is the one thing the queue took
  away from a host that previously drove the list itself.
- The `PlaylistItemFailure` distinction reaches a caller instead of being flattened into a log line.
- A consumer counting completed passes can exclude failures.

### Bad

- Two more members on `IMediaPlaylistPlayer`, whose `<remarks>` already runs to five paragraphs.
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

#203's second option: promise that `LoopRestarted` precedes any `SourceTransitioned` leaving the
item, and that the snapshot read inside the handler is that item.

The promise is the part that cannot be kept cheaply. `PlaylistSession.Step` commits the new queue
inside `_coordinator.Update(...)` and performs the emitted actions afterwards, so honouring it
means deferring the queue commit past the action list — a change to the protocol shell's ordering,
to give a consumer something the payload can carry for free. It also answers only the loop half: a
failure has no transition to pair with.

### Split decision 4 into its own record

Decision 4 is the only one #203 and #306 do not ask for, and it owns most of the *Bad* consequences.
It is here because the misattribution it fixes is the same defect as the other two, and because
`Previous` and the reason have to land together or the reason misleads. Landing the reason
separately would mean rediscovering that coupling. The cost of keeping it is a wider record.

### Put the item on `PlaybackError`

`PlaybackError` is also the payload of a refused transport command, where an item field would
always be null.

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
  name that break as the boundary; they do not decide what happens at it. Withdrawing both
  duplicates and keeping both are each defensible once there is one surface, and the argument
  against withdrawing now — a silent break — does not apply there. Tracked as #308, so the debt
  outlives this record's prose.
- Whether `PlaylistTransition` should be raised as a nested payload the way the other two are, so
  all three playlist events share one shape. It predates them and carries its fields directly.

## Validation

| # | What | Where |
|---|---|---|
| 1 | A queue of three whose middle source does not exist raises one `ItemFailed` naming the middle item | Integration |
| 1a | A queue whose *first* source does not exist raises no `ItemFailed`; the load fails as a single source's does | Integration |
| 1b | A run of failures that reaches the give-up ceiling raises `ItemFailed` for the last one, then the fatal | Protocol |
| 1c | A failure emitted while disposing, and one from a superseded session, raise neither event | Protocol |
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

Each entry says what the record got wrong, not what it now says.

- **First draft (2026-09-19).** Decision 4 added a reason to `PlaylistTransition` and deferred
  naming the item a transition left. Decision 2 left the two failure channels' relationship to the
  doc comments.
- **Automated review of #307, turns 1 to 2.** The reason described the item a transition *left*
  while every other field named the item it entered, so `FailedItem` on `[A, B]` with A failing
  would have led a consumer to mark B unplayable. The loop pair was left without the correlation
  rule the failure pair got, under a *Not settled* entry that wrongly claimed a loop raises no
  second event. The duplication had no end date. ADR-0069's status still said nothing was
  superseded.
- **Independent review, 2026-09-19.** Four mechanism errors the panel did not reach:
  - **`OnRecoverableError` is shared.** `SubstrateSession` reports a lateness-recovery fault
    through it with no item, so widening it as decision 2 originally said would have made the item
    nullable for a caller that never has one. It gains `OnItemFailed` instead.
  - **The first item's failure is not an item failure.** The protocol reports it as fatal, so
    `ItemFailed` never fires for it, and validation row 1 used a middle item and would not have
    caught this. Give-up, disposal and a superseded session were unenumerated too, against
    ADR-0075 decision 5's precedent.
  - **The race was understated.** `GetPlaylist()` is not racy, it is already wrong: the fold
    commits the queue before the report is emitted. `CurrentSource` is the racy one, and it moves
    later. One sentence had described two different failures.
  - **Citations.** The message fold is at `:697-703`, `IMediaPlayer.MediaInfo` resolves through
    `PlaylistMediaPlayerCore` rather than the controller's field, `Item` is a nullable `init`
    property rather than a positional parameter, the ordering alternative is #203's and not
    ADR-0075's, and the member count was three where it is two.
