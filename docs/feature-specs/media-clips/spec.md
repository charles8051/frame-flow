# Media clips: playlist items that start at a position

**Status:** Phase 1 (start positions) built. Phases 2 to 4 open. Living document, rewritten as
the feature changes.

**Date:** 2026-10-12.

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

## Requirements

1. **An item opens at its start.** The first frame presented is the first at or after the clip's
   start. Nothing before the start is presented, including while the item waits for Play.
   `Position` reads the start when the item becomes current. *Phase 1.*
2. **Opening at a start costs what a seek costs.** No warm-up decode at zero, no second graph
   build, and no command round trip between the item starting and its seek. *Phase 1.*
3. **A replay returns to the start.** A loop under `RepeatMode.One`, a wrap to the only item under
   `RepeatMode.All`, and a replay from `Ended` all play from the clip's start. *Phase 1.*
4. **Clips that share a source reuse the open file.** Moving from one clip to another of the same
   source object repositions the open item instead of reopening the file. Today this happens only
   while playing, after the current item has played, and always to zero. *Phase 1 sets the
   target to the next clip's start. Phase 2 extends the reuse to a paused player and to an item
   that has not played.*
5. **Failure accounting measures from the start.** `PlaylistFailureGuard` counts progress from
   the clip's start and sizes the progress it needs from what remains after the start, so a clip
   late in a file that faults at once is not counted as having played. *Phase 1.*
6. **A clip can end at a position.** The item ends when playback reaches the clip's end, as at
   end of stream: it advances, loops or ends the queue by the repeat mode. *Phase 3.*
7. **The next clip can be ready before the current one ends.** The next item is opened and
   positioned at its start while the current one plays, so the switch costs a hand-off rather than
   an open (#25). *Phase 4.*

## Public surface

Phase 1:

```csharp
namespace FrameFlow.Media;

// A source and where to start in it.
public readonly record struct MediaClip
{
    public MediaClip(IMediaSource source, TimeSpan start = default);
    public IMediaSource Source { get; }
    public TimeSpan Start { get; init; }    // not negative
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
}
```

A search-results view, for example:

```csharp
var video = MediaSource.FromFile(path);              // one source object per file
var items = await player.ReplaceAsync(
    hits.Select(h => new MediaClip(video, h.Start)));  // plays from the first hit
await player.JumpToAsync(items.Value[3]);             // the fourth hit, in the open file
```

Phase 3 adds `MediaClip.End` as an `init` property, so no overload changes.

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
4. **The next clip prepared ahead** (#25), and a decision on whether the player lends itself one
   hardware device by default.

## Open questions

- Should a player create and lend itself one hardware device by default? It is the largest single
  cost of a switch. ADR-0064 and the borrowed-device decision keep it opt-in today.
- How should a clip be shown while paused, without playing, for a results grid or hover preview?
  Today a seek while paused presents nothing until Play. That belongs with #544.
