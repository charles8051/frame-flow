# Media clips: playlist items that start and end at a position

**Status:** Phases 1 to 4 built: start positions, reuse across clips of one file, end positions,
hardware devices kept across items, and items prepared ahead. Living document, rewritten as the
feature changes.

**Date:** 2026-10-13.

**Decision record:** [adr.md](adr.md).

**Issue:** #656.

**Related:** #639 (a seek before an item's first play, fixed in #642), #25 (gapless multi-item
playlists: a stable decode device and preroll), #544 (frame step), #56 (decoded-frame cache for
scrubbing).

## What

A player's queue holds sources, and every item starts at the beginning of its source. Playing a
moment inside a file means waiting for the item to start and then seeking it. That costs a warm-up
at zero, a second graph build for the seek, and a round trip through `SourceTransitioned`. The
item's position is also lost: an item that plays next on its own, loops, or is replayed starts at
zero again.

Many consumers work with moments rather than files: search hits across a library, chapters,
bookmarks, highlights. Each is a source and a position, and several can share one file. This
feature makes that pair a first-class queue entry, a **media clip**, so the player opens each item
where it is meant to start.

## Where the time goes today

Measured on 1080p30 H.264 MP4s, 60 s long, with hardware decode (D3D11VA) on a discrete NVIDIA
GPU. A headless sink records when each frame is presented. Times run from the call to the
presentation of the target frame.

| Operation | Keyframe every 2 s | Keyframe every 10 s |
|---|---|---|
| Seek within the playing item | 11–80 ms | 200–390 ms |
| Switch to another file, a decode device per item (default) | 214–287 ms (median 248) | |
| Switch to another file, one device lent with `WithHardwareDevice` | 22–42 ms (median 25) | |
| Switch to another file, software decode | 21–34 ms (median 30) | |
| Switch, then seek, with a lent device | 41–94 ms | 279–503 ms |

Two things follow. Creating a hardware device per item is most of a default switch, and lending
one device removes it. After that, the walk from the keyframe to the target is most of what is
left, and it grows with the keyframe interval.

With phase 1, on the same files and setup, with a lent device, to a random target between 5 and
55 s:

| Operation | Keyframe every 2 s | Keyframe every 10 s | Frames from the file's start shown |
|---|---|---|---|
| Switch, then seek (before) | 24–125 ms (median 107) | 248–528 ms (median 436) | 1 in 10 switches |
| Switch to a clip | 20–110 ms (median 78) | 249–513 ms (median 421) | none |
| Move to another clip of the open file | 4–120 ms (median 62) | 95–541 ms (median 299) | none |

Opening at the start saves the warm-up at zero and the second graph build, and no frame from the
file's start is shown. The decode walk to the target is unchanged, and it is now most of the cost.

With phase 4a, a player that is not lent a device keeps one per backend. Switching to another file
and seeking to a random target, medians of five, with no device lent:

| | Before | After |
|---|---|---|
| Keyframe every 2 s | 240 ms | 77 ms |
| Keyframe every 10 s | 526 ms | 436 ms |

The default player now switches as fast as one lent a device.

With phase 4b, an item can be prepared before the jump to it. Jumping to a clip of another file at
a random target, medians of eight, with no device lent:

| | Keyframe every 2 s | Keyframe every 10 s |
|---|---|---|
| Not prepared | 41 ms | 282 ms |
| Prepared, jump as `PrepareAsync` completes | 18 ms | 249 ms |
| Prepared, jump 1 s after `PrepareAsync` completes | 7 ms | 17 ms |

`PrepareAsync` completes once the item is open and decoding. The decode from the keyframe to the
start goes on after that, so a jump that follows at once waits for the rest of it.

With phase 2, a hop to another clip of the open file while paused repositions the open item. Medians
of ten hops between random clips of one file, from `JumpToAsync` (phase 1 reopened the file):

| | Phase 1 | Phase 2 |
|---|---|---|
| Jump to transition, a decode device per item | 212 ms | 1 ms |
| Jump to transition, a lent device | 25 ms | 2 ms |
| Jump and Play to the target frame, a device per item, keyframe every 2 s | 263 ms | 63 ms |
| Jump and Play to the target frame, a device per item, keyframe every 10 s | 388 ms | 127 ms |

## Requirements

1. **An item opens at its start.** The first frame presented is the first at or after the clip's
   start. Nothing before the start is presented, including while the item waits for Play.
   `Position` reads the start when the item becomes current. *Phase 1.*
2. **Opening at a start costs what a seek costs.** No warm-up decode at zero, no second graph
   build, and no command round trip between the item starting and its seek. *Phase 1.*
3. **A replay returns to the start.** A loop under `RepeatMode.One`, a wrap to the only item under
   `RepeatMode.All`, and a replay from `Ended` all play from the clip's start. *Phase 1.*
4. **Clips that share a source reuse the open file.** Moving from one clip to another of the same
   source object repositions the open item instead of reopening the file, in any state: playing,
   paused, or before the current item has played. *Phase 1 set the target to the next clip's
   start. Phase 2 extended the reuse from a playing, played item to every state (decision 5).*
5. **Failure accounting measures from the start.** `PlaylistFailureGuard` counts progress from
   the clip's start and sizes the progress it needs from what remains after the start, so a clip
   late in a file that faults at once is not counted as having played. *Phase 1.*
6. **A clip can end at a position.** The item ends when playback reaches the clip's end, as at
   end of stream: it advances, loops or ends the queue by the repeat mode. Nothing presented at or
   after the end is shown, and the source is not read past it. *Phase 3, decision 6.*
7. **A switch does not create a hardware device.** A player keeps the devices its items decode
   on, one per backend, so an item boundary borrows one rather than creating one. *Phase 4a,
   decision 7.*
8. **An item can be ready before it is needed.** An item is opened and positioned at its start
   ahead of time, so switching to it costs a hand-off rather than an open and a decode walk. The
   player prepares the queue's next item, and the application can name another, such as the result
   under the cursor (#25). *Phase 4b, decision 8.*

## Public surface

Phase 1:

```csharp
namespace FrameFlow.Media;

// A source, where to start in it, and optionally where to stop.
public readonly record struct MediaClip
{
    public MediaClip(IMediaSource source, TimeSpan start = default, TimeSpan? end = null);
    public IMediaSource Source { get; }
    public TimeSpan Start { get; init; }    // not negative, before End
    public TimeSpan? End { get; init; }     // exclusive; null plays to the end of the source
}
```

```csharp
namespace FrameFlow.Player;

public interface IMediaPlayer
{
    Task<PlaylistItem> EnqueueAsync(MediaClip clip, CancellationToken cancellationToken = default);
    Task<PlaylistItem> SetNextAsync(MediaClip clip, CancellationToken cancellationToken = default);
    Task<PlaylistItem> AddAsync(MediaClip clip, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<PlaylistItem>>> ReplaceAsync(
        IEnumerable<MediaClip> clips, CancellationToken cancellationToken = default);
    // The source overloads stay, and mean a clip that starts at zero.
}

public interface IPlayerBuilder
{
    IPlayerBuilder WithMedia(MediaClip clip);
}
```

```csharp
namespace FrameFlow.Playback;

public sealed class PlaylistItem
{
    public PlaylistItem(MediaClip clip);
    public TimeSpan Start { get; }          // zero for an item built from a source
    public TimeSpan? End { get; }
}
```

A search-results view, for example:

```csharp
var video = MediaSource.FromFile(path);              // one source object per file
var items = await player.ReplaceAsync(
    hits.Select(h => new MediaClip(video, h.Start)));  // plays from the first hit
await player.JumpToAsync(items.Value[3]);             // the fourth hit, in the open file
```

Phase 3 added `MediaClip.End` and `PlaylistItem.End`, with no overload changes.

Phase 4b:

```csharp
namespace FrameFlow.Player;

public interface IMediaPlayer
{
    // Opens the item, positions it at its start and decodes toward it, while the current item
    // plays. Completes once it is open and decoding.
    Task<Result> PrepareAsync(PlaylistItem item, CancellationToken cancellationToken = default);
}

public interface IPlayerBuilder
{
    // Prepares the item the queue would advance to next, whenever an item starts.
    IPlayerBuilder WithNextItemPrepared(bool prepare = true);
}
```

A search-results view that prepares the hit under the cursor:

```csharp
void OnHover(PlaylistItem hit) => _ = player.PrepareAsync(hit);   // replaces the last one
async Task OnClick(PlaylistItem hit) => await player.JumpToAsync(hit);
```

## Semantics

- **File time.** `Position`, `Duration`, `SeekAsync` and the PTS a sink sees stay in the source's
  own time. A clip that starts at 83.2 s reports `Position` 83.2 s when it starts. A seek may go
  anywhere in the file, including before the clip's start.
- **Start past the end.** When the source reports a duration and the start is at or past it, the
  item fails to start, as an unopenable one does. Opening it would end it at once, and a clip
  under `RepeatMode.One` would then loop with no content.
- **A source that cannot seek.** A clip with a non-zero start on a source the demuxer cannot seek
  fails to start. `IMediaSource.IsSeekable` is not consulted, because `MediaSource.FromUri` reports
  every non-file URI as not seekable, an HTTP MP4 included.
- **Shared sources.** Two clips of one file should share one source object. Reuse is decided by
  reference (`PlaylistQueue.Take`). Two `MediaSource` records for the same path are equal as
  values but are different sources to the queue.
- **The end.** It is exclusive. No frame presented at or after it is shown. An audio buffer that
  starts before it plays whole, as a pass's range does it. A seek to or past the end lets that run
  play on to the end of the source. A loop or replay returns to the start and stops at the end
  again. An end past the source's end is reached at the source's end.
- **Before the start.** A run that starts past a keyframe decodes from the keyframe. What it
  decodes before its start is dropped where it is decoded: no frame reaches a configurator or the
  sink, and no audio reaches the audio sink. An audio buffer that crosses the start is cut at it.
  This holds for a seek as well as for a clip's start.
- **A prepared item.** One item is prepared at a time, and preparing another discards the first.
  It shares the sinks and the position clock with the item that is playing, and touches none of
  them until it becomes current. A jump, skip or advance to it uses it. Another clip of the current
  item's source is never prepared: the open item is repositioned instead (decision 5). An explicitly
  prepared item stays prepared until it is used, replaced or removed from the queue, and the
  player prepares nothing else automatically meanwhile.
- **An empty collection expression.** `ReplaceAsync([])` is ambiguous between the source and clip
  overloads. It always threw, and the breaking-changes log says what to write instead. The builder
  takes a single clip only, so `WithMedia([])`, which builds a player with nothing loaded, still
  compiles.

## Phases

1. **Start positions.** `MediaClip`, the overloads above, `PlaylistItem.Start`. The item runtime
   repositions at open, before its first warm-up. Replays rewind to the start. The failure guard
   measures from the start.
2. **Reuse across clips of one file, in every state.** A jump or skip to another clip of the
   current source repositions the open item while paused, and before the current item has
   played.
3. **End positions.** `MediaClip.End`. The item ends at its end position.
4. **a. Hardware devices kept across items.** A per-backend cache, on by default (decision 7).
   **b. An item prepared ahead** (#25): the queue's next item automatically, and an explicit
   prepare for one the application chooses. One prepared item at a time (decision 8).

## Known limitations

- **The loop-stall watchdog watches the source's duration.** A clip under `RepeatMode.One` that
  loops short of the end of its source never reaches that duration. A loop that wedges at the
  clip's end is reported only once the position passes the source's end, later than it would be
  for a whole-file loop. Nothing is reported falsely. #666.
- **A prepared item can stall short of its start.** It decodes behind closed gates. Audio from its
  start waits at the audio gate, and once the audio queue is full the demuxer stops. When a file's
  audio is muxed further ahead of its video than that queue holds, about 2.7 s of 48 kHz AAC, the
  video decoder stops short of the start, and the jump decodes the rest.

## Open questions

- How should a clip be shown while paused, without playing, for a results grid or hover preview?
  Today a seek while paused presents nothing until Play. That belongs with #544.
