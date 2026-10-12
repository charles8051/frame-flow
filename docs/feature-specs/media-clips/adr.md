# Media clips: decisions

The decisions behind [the spec](spec.md). Append-only: a decision that changes is superseded by a
new one, not rewritten. A decision a second feature starts citing graduates to a numbered ADR.

## Decision 1: the start position belongs to the queue entry

**Date:** 2026-10-12
**Status:** Accepted

### Context

A consumer that plays moments inside files needs to say where each item starts. Three places could
hold that position: the source, the call that makes an item current, or the queue entry.

### Decision

The position is part of the queue entry. It is given when the item is queued, and the item keeps
it.

### Consequences

- An item that plays next on its own starts at its position. A results list played through in
  order needs this, and the call that makes an item current is not on that path.
- The position of the next item is known before it is current, which is what preparing it ahead
  needs (#25).
- A replay of the item returns to its position.
- Several clips can share one source object, which is what lets the queue see that two clips are
  the same file (`PlaylistQueue.Take` compares sources by reference).

### Alternatives considered

- **On the source** (`IMediaSource.StartPosition`, defaulting to zero, as `DemuxerOptions` and
  `InputFormat` were added). It reaches every entry point that takes a source with one property.
  It was rejected because each position then needs its own source object, so clips of one file
  stop sharing a source and the queue cannot reuse the open file between them. It also makes a
  position a property of where to read rather than of what to play.
- **On the call that makes an item current** (`JumpToAsync(item, position)`). A position given only
  there is lost when the item plays next on its own, loops or is replayed, and it is not known
  early enough to prepare the item ahead. It remains possible to add later, as an override of the
  entry's position for one jump.

## Decision 2: a clip is a value type the queue methods take

**Date:** 2026-10-12
**Status:** Accepted

### Context

The queue methods take a source: `EnqueueAsync`, `SetNextAsync`, `AddAsync`, `ReplaceAsync`, and
the builder's `WithMedia`. An end position is planned (phase 3).

### Decision

`FrameFlow.Media.MediaClip` is a `readonly record struct` of a source and a start. Each queue method
gains one overload that takes a clip, or a sequence of clips. The source overloads stay and mean a
clip that starts at zero. `PlaylistItem` gains `Start`.

The builder gains `WithMedia(MediaClip)` and no sequence overload. `WithMedia([])` builds a player
with nothing loaded, and a second sequence overload would make that call ambiguous.
`ReplaceAsync([])` does become ambiguous, but it always threw.

The end position will be an `init` property on `MediaClip`, so adding it changes no signature.

It is a struct so that `SetNextAsync(null)`, which compiles today against the source overload,
is not made ambiguous by a reference-type overload beside it.

### Alternatives considered

- **Start and end parameters on each method** (`EnqueueAsync(source, start, end)`). `ReplaceAsync`
  and `WithMedia` take sequences and would need a pair type anyway, and adding the end later would
  add a second overload to every method.
- **Caller-built `PlaylistItem`s as input.** An item is an identity the player creates. One built by
  a caller belongs to no player and is refused by `JumpToAsync` and `RemoveAsync`. Using it as
  input as well would give one type two meanings.

## Decision 3: an item is positioned at open, through the seek recipe

**Date:** 2026-10-12
**Status:** Accepted

### Context

Today a consumer seeks after `SourceTransitioned`. By then the item has warmed up at zero: its graph
has run and decoded the first frame, and while playing it may present frames from zero. The seek
then stops the graph, repositions, and builds a new graph.

### Decision

The playlist session passes the clip's start to the item runtime when it opens the item.
`SubstrateSession` repositions before its first warm-up, through `RepositionAsync`, the one
discontinuity recipe the seek path uses (ADR-0056). Before the first play that reposition holds the
item: it moves the demuxer, resets the decoders, seats the clocks on the start and records the
pacer's floor, and starts nothing. The fix for #639 made that hold correct for a session that has
not played. The warm-up then runs once, from the keyframe at or before the start, and the pacer's
floor keeps every frame before the start off the sink.

A replay rewinds the runtime to the clip's start rather than to zero, through the same rewind it
uses now.

### Alternatives considered

- **A positioning step in the playlist protocol**, between opening an item and warming it up.
  It would add a work state to each of the three paths that open an item, for an operation the
  runtime can perform as part of opening. A positioning failure would also need its own handling,
  where at open it is already a failed start.
- **A dedicated start-position method on the runtime** that seeks the demuxer and seats the
  clocks directly. It would be a second, hand-listed copy of the reposition steps, which ADR-0056
  exists to prevent.

## Decision 4: position stays in file time, and replays return to the start

**Date:** 2026-10-12
**Status:** Accepted

### Decision

`Position`, `Duration`, seeks and frame PTS stay in the source's time. A clip does not rebase the
timeline to its start. A loop, a wrap to the only item, a replay from `Ended` and a same-source
replay play from the clip's start. A seek is not bounded by the clip.

### Consequences

- A consumer that indexed a moment by its file timestamp reads that timestamp back from
  `Position`.
- `PlaylistFailureGuard` has to subtract the start. The playlist shell reports a fault with the
  position clock's reading, which is now file time from the start, so the protocol measures
  progress as that reading less the item's start, and sizes the progress it needs from the
  duration less the start.

### Alternatives considered

- **Clip-relative time**, with `Position` zero at the clip's start and `Duration` the clip's
  length. It reads well for a clip with an end, but every consumer that seeks or labels by file
  timestamp would have to convert, and the sink would see PTS that disagree with `Position`.

## Decision 5: a same-source hand-off repositions the open item in every state

**Date:** 2026-10-12
**Status:** Accepted
**Supersedes:** the rule in decision 8 of the end-of-queue record
(`docs/adr/playlist-end-of-queue-replay-and-faults.md`) that only an item that has played is rewound
in place, and only while playing (#194).

### Context

When the next item has the same source object as the current one, the protocol could rewind the
open runtime instead of rebuilding it, but only while playing and only for an item that had played.
Otherwise it disposed the runtime and opened the file again. For clips of one file, that is every
paused hop, and every hop before the first play.

The rule was made because rewinding an item that had never played left its clocks stopped (#194). A
rewind took the resume branch of `RepositionAsync` for a session whose clock had never started, and
nothing then started it. #639 made a reposition hold a session that has not played, as a paused
seek does.

### Decision

A same-source replay rewinds the open item in place whenever the item has not failed, in every run
state.

- While paused, the rewound item waits for Play, as a paused seek does.
- An item that has not played is held by the rewind. While playing, the advance then plays it,
  through the same start path a rebuilt item takes. A failed start is reported and skipped as a
  rebuilt item's is.
- A rewind that fails still falls back to rebuilding the item.

### Consequences

- A jump, skip or end-of-stream to another clip of the current source costs a seek, in any state,
  not an open.
- The ordering explorer's expected failure, a seek or rewind of an unplayed item while not playing,
  is retired. `PlaylistItemModel` models an unplayed runtime as held, and a new invariant says a run
  launches only while playing.

### Alternatives considered

- **Keep rebuilding outside the playing, played case.** It is correct, and costs an open per hop,
  which for clips of one file is most hops in a paused or not-yet-started player.
