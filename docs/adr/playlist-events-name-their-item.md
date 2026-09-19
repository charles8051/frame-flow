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

`ErrorOccurred` reports the same failure, unchanged, for a caller holding only `IMediaPlayer`. A
consumer subscribing to both sees the failure twice, and the doc comment on each says so.

Settles #306.

### 3. `IMediaPlaylistPlayer.ItemLooped`

`IObservable<PlaylistItemLooped>`, carrying the `PlaylistItem` and the `LoopCount` and
`ItemDuration` that `LoopRestarted` already carries.

This settles #203 without the ordering promise ADR-0075 decision 5 declined to make. That option
required `LoopRestarted` and `SourceTransitioned` to travel one ordered path, and it still left
every consumer deriving identity from a snapshot read inside a handler. Carrying the item is
unconditional and needs no promise about the other event.

`IMediaPlayer.LoopRestarted` is unchanged.

### 4. `PlaylistTransition` says why it fired

A `PlaylistTransitionReason`: the first item, a natural end, a skip, a jump, a failed item, or a
loop.

#173 records that a transition fires for all six and reports them identically, so a consumer
counting completed passes counts failures as passes. The protocol already decides which of the six
an advance is. ADR-0075 decision 5 defines which advance is a loop, and
`AdvanceLockedAsync(faulted: true)` is the failure path. The reason is a value the protocol already
holds.

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
- Two ways to learn about a failure. A consumer that subscribes to both double-counts, and only the
  doc comment says so.
- `PlaylistTransitionReason` is a public enum that has to stay right as the protocol grows paths.

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

## Not settled here

- Whether `ErrorOccurred` should stop reporting item failures once `ItemFailed` exists. Removing it
  is a break for a single-source caller that gets a mid-stream fault there today.
- Whether `LoopRestarted` on `IMediaPlayer` should be withdrawn in favour of `ItemLooped`. It is the
  only loop signal a caller holding the small surface has.
- Whether a transition should carry the item it left, as well as the item it entered.

## Validation

| # | What | Where |
|---|---|---|
| 1 | A queue of three whose middle source does not exist raises one `ItemFailed` naming the middle item | Integration |
| 2 | The same source added twice, one copy removed, and a failure names the copy that failed | Protocol |
| 3 | An item that faults mid-playback reports `FaultedDuringPlayback`; one that cannot be opened reports the other | Protocol |
| 4 | A failure raises `ItemFailed` and `ErrorOccurred` once each | Player |
| 5 | A single-clip `RepeatMode.All` loop raises `ItemLooped` naming that item | Integration |
| 6 | A jump recorded during a rewind does not change the item on the `ItemLooped` that precedes it | Protocol |
| 7 | A transition after a failed item reports the failure reason, not a natural end | Protocol |
| 8 | `RepeatMode.One` on a queue of three reports the loop reason, with `Wrapped` false | Protocol |
