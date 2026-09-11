# ADR-XXXX: Stream-backed media sources, via a custom AVIO context

## Status

Proposed (2026-09-11). Draft pending number assignment.

Resolves the substantial half of
[#108](https://github.com/charles8051/frame-flow/issues/108). The three small
items in that issue are settled here too, in *Decision §5*, because they are the
same type's surface and one of them is being declined.

**Nothing in this document is implemented.** Two of its decisions rest on
behaviour that has not been measured on this codebase, and both are called out
as acceptance conditions in *Not settled here*. Read it as a design under
review.

## Context

### There is no way to hand FrameFlow bytes

`IMediaSource` exposes `Uri`, `FilePath` and `IsSeekable`. Nothing else. A
consumer holding an in-memory clip, an embedded resource, a decrypted blob, or
a byte range inside a larger archive has exactly one route: write it to a
temporary file and pass the path.

That route is not merely inelegant. It requires a writable filesystem, it
doubles the storage for content that is already resident, it leaves the
plaintext of a decrypted blob on disk, and it makes the caller responsible for
deleting a file whose lifetime is now coupled to a player they do not own.

`IMediaSource`'s own doc comment already acknowledges the gap:

```csharp
/// The URI identifying the media resource, or <see langword="null"/> for sources
/// that are not URI-addressable (e.g. in-memory streams).
Uri? Uri { get; }
```

The interface was written with streams in mind and the plumbing was never
built.

### The open path is one call site

This is the part that makes the change tractable.
`DemuxSessionFactory.OpenAsync` is the only place in the repository that calls
`avformat_open_input`, and it reaches the source through a single private
helper:

```csharp
private static string ResolveUrl(IMediaSource source)
{
    if (!string.IsNullOrEmpty(source.FilePath))
        return source.FilePath;

    if (source.Uri is not null)
        return source.Uri.ToString();

    throw new ArgumentException(
        $"Media source '{source.DisplayName}' has neither a file path nor a URI. ...");
}
```

A stream source is precisely the case that falls through to the `throw`. The
seam already exists; it currently has a wall behind it.

### `IsSeekable` informs nothing

Worth stating plainly, because a stream design would otherwise assume
otherwise. A repository-wide search for `IsSeekable` outside its own
declaration finds:

| Where | What it does |
|---|---|
| `MediaSource.FromFile` | sets it `true` |
| `MediaSource.FromUri` | sets it `uri.IsFile` |
| `MediaSourceTests` | asserts the value the factory just set |
| two test stubs | return `true` |

**No production code reads it.** It is a property that describes the source to
nobody. `IPlaybackController.SeekAsync` does not consult it, the seek bar does
not disable itself on it, and `#118` added a latch to the seek bar's scrub
dispatcher precisely because a refusing source was otherwise indistinguishable
from a working one.

That is tolerable while every source is a local file. A stream source is the
first case where non-seekable is both common and knowable up front, so this
design cannot treat `IsSeekable` as an existing mechanism it plugs into. It has
to give the property its first consumer.

### There is no input-side AVIO interop

`FrameFlow.Native` has the output half — `avio_open`, `avio_closep`, and a `pb`
accessor on the format context — added for the MP4 muxer. It has none of the
input half: no `avio_alloc_context`, no `av_malloc`/`av_free`, no delegate
marshalling for read/seek callbacks.

So this is new interop, in the assembly whose stated job is to keep FFmpeg
pointers and allocation rules isolated behind a small surface
(ARCHITECTURE.md §4).

## Decision

### 1. `MediaSource.FromStream`, backed by a custom `AVIOContext`

```csharp
public static MediaSource FromStream(
    Stream stream,
    string displayName,
    bool leaveOpen = false);
```

`IsSeekable` is **not** a parameter. It is read from `stream.CanSeek`, which is
the authoritative answer and one the caller cannot get wrong.

`DemuxSessionFactory` gains a branch: a source carrying a stream allocates an
`AVIOContext` over managed read/seek callbacks and assigns it to the format
context's `pb` before `avformat_open_input`, which is then passed `null` for
the URL.

### 2. The stream is owned by one thread, and the contract says so

FFmpeg calls the read callback from whichever thread is driving the demuxer.
`Stream` is not thread-safe, and neither is `AVFormatContext` —
`FormatContextHandle` already documents the latter:

> The underlying `AVFormatContext` operations are not thread-safe; callers must
> ensure that only one thread operates on this handle at a time.

The stream inherits that rule rather than getting a new one. `FromStream`'s
documentation states that the stream is consumed by the demux thread and must
not be touched by the caller after the source is handed to a player. No
locking is added inside the callback: a lock there would sit on the packet-read
path, and the invariant it would protect is one the caller can simply keep.

### 3. Non-seekable is a first-class outcome, not a failure

A forward-only stream is the expected case for the motivating scenarios, so it
gets a designed path rather than an error:

- The seek callback returns `AVERROR(EINVAL)` when `stream.CanSeek` is false.
  FFmpeg treats that as "this input cannot seek" and copes.
- `MediaInfo` gains a `CanSeek` flag, read from the opened format context
  rather than from the source's claim, because a seekable stream in a container
  that cannot seek is still not seekable.
- `IPlaybackController.SeekAsync` consults it and returns
  `Result.Fail(ErrorCategory.InvalidOperation, ...)` rather than attempting the
  seek. This is `IsSeekable`'s first real consumer, and it arrives as a
  `Result` because ADR-0069 made that the answer to a refused command.
- `FrameFlowSeekBar` disables itself when the bound player reports it, which
  replaces the latch `#118` added with the signal that latch was approximating.

### 4. Buffer and lifetime rules, written down once

The three ways this leaks or crashes, and the rule for each:

**The callback delegates must outlive the context.** FFmpeg stores raw function
pointers. A `GCHandle` pinned in the session's state, released in the same
`Dispose` that closes the format context, and never a lambda captured into a
local.

**The AVIO buffer is FFmpeg's, not ours.** It is allocated with `av_malloc`,
FFmpeg may reallocate it, and the pointer to free at teardown is
`avioContext->buffer` as it stands then — not the pointer originally passed in.
Freeing the original is a heap corruption that will not reproduce on a small
file.

**Teardown order is fixed**: `avformat_close_input` first, then `av_free` the
AVIO buffer, then `av_free` the AVIO context, then release the `GCHandle`, then
dispose the stream if `leaveOpen` is false. A `SafeHandle` per resource, in the
shape `FormatContextHandle` already establishes, rather than a `finally` block
that has to get five steps right.

### 5. The rest of #108

**`FromUri(Uri uri, bool? isSeekable = null)`** — adopted. The current
`IsSeekable: uri.IsFile` reports a range-serving HTTP origin as non-seekable,
and once §3 gives the property teeth that becomes a seek bar disabled on a
source that seeks fine. `null` keeps the current inference.

**XML docs on `MediaSource`, `IMediaSource`, `MediaInfo`, `VideoStreamInfo`,
`AudioStreamInfo`** — adopted. These are the first types a consumer touches and
none has a doc comment.

**`implicit operator MediaSource(string)`** — **declined.** A `string` that
looks like a path and a `string` that is a URL are indistinguishable at the
call site, and the conversion would silently route both through `FromFile`,
turning `player.Open("https://example.com/a.mp4")` into a path relative to the
working directory. The saving is the eight characters of `FromFile`, and the
cost is a wrong answer that looks like a missing file. `FrameFlowPlayer.Open`
takes the shortcut internally today; it should call `MediaSource.FromFile`
explicitly rather than have the language do it invisibly.

## Alternatives considered

### A. Spill to a temporary file inside `FromStream`

Keep the public shape, copy the stream to a temp file, open the path, delete on
dispose. No interop at all.

Rejected. It is the workaround the issue exists to remove, moved behind a nicer
name — it still needs a writable filesystem, still doubles storage, and still
writes a decrypted blob to disk. It is also worse than the caller doing it,
because the caller at least knows a file appeared.

Worth keeping in mind as a fallback if the probing condition in *Not settled
here* fails for forward-only streams, since a spill makes any stream seekable.

### B. A named pipe or loopback socket

Write the stream into a pipe and hand FFmpeg a `pipe:` URL. No custom AVIO, and
FFmpeg's own protocol handler does the work.

Rejected. It is never seekable, so it forecloses §3's seekable-stream case
entirely; it costs a thread and a full copy of every byte; and the platform
differences between a Windows named pipe and a Unix FIFO are a second
portability surface for a library that already carries one.

### C. Extend `IMediaSource` with the stream

Add `Stream? Content { get; }` beside `Uri` and `FilePath`.

Rejected as the *public* shape, though something like it is needed internally.
`IMediaSource` is a description of where media lives; a `Stream` is a live,
stateful, disposable resource with thread affinity, and putting one on a
record that consumers construct invites them to reuse a source across two
players and get a half-consumed stream. The stream should be reachable only
through the factory that understands its lifetime. The internal mechanism is an
implementation question this ADR does not settle.

## Consequences

### Good

- The motivating scenarios work without a filesystem: in-memory clips, embedded
  resources, decrypted blobs, byte ranges inside an archive.
- `IsSeekable` acquires a consumer, and the seek bar stops guessing.
- `SeekAsync` on a non-seekable source becomes a refusal with a category rather
  than an attempt that fails somewhere below.
- `FrameFlow.Native` gains input-side AVIO interop, which is also the
  prerequisite for any future custom protocol.

### Bad

- New unmanaged-callback interop on the packet-read path. It is the highest-risk
  kind of code in this repository: a lifetime mistake produces heap corruption
  at a distance, not an exception.
- A blocking read inside the callback blocks the demux thread. For a
  `MemoryStream` that is free; for a network-backed `Stream` it is a stall the
  pacing chain cannot see or account for. The design does not address this, and
  it is the strongest argument for restricting the first cut to streams that are
  already resident.
- `MediaInfo` gains a member, which is a breaking change for anyone
  constructing one. Pre-1.0, and it goes in
  [docs/BREAKING-CHANGES.md](../BREAKING-CHANGES.md) like the rest.

### Neutral

- `#118`'s seek-bar latch becomes redundant once §3 lands. It should be removed
  then rather than left as a second mechanism for the same signal.

## Not settled here

Two acceptance conditions. Neither has been measured on this codebase, and the
decision should not be accepted until both are.

1. **Probing a forward-only stream.** `avformat_find_stream_info` reads ahead to
   identify streams, and on a seekable input it rewinds afterwards. FFmpeg
   buffers probe data for non-seekable inputs, but how much, and whether it is
   enough for the containers FrameFlow cares about, is not something this
   document knows. The condition: open a fragmented MP4 and a Matroska file over
   a `CanSeek == false` wrapper and confirm `MediaInfo` comes back complete. If
   it does not, alternative A returns as the fallback for that case.

2. **Teardown under a mid-read failure.** The rules in §4 are correct as written
   and the ordering is the part that goes wrong in practice. The condition: a
   test that throws from inside the read callback and asserts the session tears
   down without leaking the buffer, the context or the `GCHandle` — which means
   deciding first how an exception crossing the unmanaged boundary is caught,
   since letting one propagate through FFmpeg's stack frames is undefined.

Also open, and deliberately not decided:

- **Whether the first cut restricts itself to `CanSeek` streams.** That would
  sidestep condition 1 entirely and cover the in-memory cases, which are the
  motivating ones. It would also mean `FromStream` initially rejects exactly the
  forward-only sources §3 designs for, so the two sections need reconciling one
  way or the other before this is accepted.
- **Whether `MediaInfo.CanSeek` is the right home**, versus a capability on the
  controller alongside `IsActivelyPresenting`.
