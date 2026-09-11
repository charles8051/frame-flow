# ADR-XXXX: Stream-backed media sources, via a custom AVIO context

## Status

Proposed (2026-09-11). Draft pending number assignment.

Revised (2026-09-11) after an independent review of the first draft. That
review found a defect that would have corrupted this document's own gating
experiment, plus two false claims about the codebase and a mechanism gap that
made one rejected alternative unjustifiable. What changed, and why, is recorded
in *Revision history* at the end — the superseded reasoning is kept because it
is what three of the open questions were about.

Resolves the substantial half of
[#108](https://github.com/charles8051/frame-flow/issues/108). The three small
items in that issue are settled in *Decision §7*.

**Nothing here is implemented.** Two decisions rest on behaviour not measured on
this codebase; both are acceptance conditions in *Not settled here*.

## Context

### There is no way to hand FrameFlow bytes

`IMediaSource` exposes `Uri`, `FilePath` and `IsSeekable`. Nothing else. A
consumer holding an in-memory clip, an embedded resource, a decrypted blob, or
a byte range inside a larger archive has one route: write it to a temporary
file and pass the path.

That needs a writable filesystem, doubles storage for content already resident,
leaves the plaintext of a decrypted blob on disk, and makes the caller
responsible for deleting a file whose lifetime now belongs to a player they do
not own.

`IMediaSource`'s own doc comment already names the missing case:

```csharp
/// The URI identifying the media resource, or <see langword="null"/> for sources
/// that are not URI-addressable (e.g. in-memory streams).
Uri? Uri { get; }
```

The interface was written with streams in mind. The plumbing was never built.

### The production open path is one call site

`DemuxSessionFactory.OpenAsync` is the only place in `src/` that calls
`avformat_open_input`, and it reaches the source through one private helper
(`DemuxSessionFactory.cs:207-220`):

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

A stream source is exactly the case that falls through to the `throw`. The seam
exists; it has a wall behind it.

Not the only call site in the *repository*:
`tests/FrameFlow.Decoding.Tests/BootstrapDiagnosticTests.cs` calls
`avformat_open_input` directly eleven times. Those tests exercise an open path
the new branch will not cover, which is worth knowing when this is implemented
but does not change the shape of the change.

### `IsSeekable` informs nothing

A repository-wide search for `IsSeekable` outside its own declaration finds:

| Where | What it does |
|---|---|
| `MediaSource.FromFile` / `FromUri` | sets it |
| `MediaSourceTests` | asserts the value the factory just set |
| two test stubs | return `true` |

**No production code reads it.** `SeekAsync` does not consult it and the seek
bar does not gate on it. That is tolerable while every source is a local file.
A stream source is the first case where non-seekable is both common and
knowable up front, so this design cannot treat the property as a mechanism it
plugs into.

### There is no input-side AVIO interop

`FrameFlow.Native` has the output half — `avio_open`, `avio_closep`, and a `Pb`
accessor on `AvOutputFormatContextAccessor` — added for the MP4 muxer. The
input-side `AvFormatContextAccessor` is read-only and has no `pb` at all, so a
writable input accessor is part of this work. There is no
`avformat_alloc_context`, no `avio_alloc_context`, no `avio_context_free`, no
`av_malloc`/`av_free`, and no `AVERROR(EINVAL)`/`ENOSYS` constants.

There is also no `[UnmanagedCallersOnly]` or `GetFunctionPointerForDelegate`
anywhere in `src/`. Managed callbacks invoked from native code are new ground
for this repository.

## Decision

### 1. `MediaSource.FromStream`, returning `IMediaSource`

```csharp
public static IMediaSource FromStream(
    Stream stream,
    string displayName,
    bool leaveOpen = false);
```

It returns `IMediaSource`, not `MediaSource`, and the instance is an
`internal sealed class StreamMediaSource : IMediaSource` in `FrameFlow.Media`.
Two reasons, both of which the first draft got wrong by leaving the mechanism
open (see Alternative C):

- `MediaSource` is a `public sealed record`. It cannot be subclassed, and a
  positional record has value equality — two `FromStream` sources with the same
  display name and null `Uri`/`FilePath` would compare **equal** while wrapping
  different streams. A class with reference identity is the correct shape for a
  handle to a one-shot resource, and it also removes the question of what
  `with`-cloning such a record would mean.
- Keeping the stream off the public record is the point of Alternative C's
  rejection. An internal type is how that is achieved rather than asserted.

`FrameFlow.Media` grants `InternalsVisibleTo` to Sdl, Native, Playback,
Audio.OpenAL and Media.Tests — **not** Decoding. It gains Decoding, so
`DemuxSessionFactory` can type-test for `StreamMediaSource`.

`IsSeekable` is read from `stream.CanSeek`. It is not a parameter: that is the
authoritative answer and one the caller cannot get wrong.

**The first cut accepts only streams whose synchronous reads are bounded** —
in practice `MemoryStream`, `UnmanagedMemoryStream`, `FileStream`, and
wrappers over them. The reason is §6: a blocking `Read` cannot be interrupted
by anything, so an unbounded one hangs playback and teardown with no recourse.
Enforced by documentation rather than a type check, because no property on
`Stream` answers the question and no list of allowed subclasses is right — a
`FileStream` over a dead SMB share blocks exactly like a socket.

Documentation alone leaves a silent hang, so it is paired with a **read
watchdog**: the callback records the start time, and a read outstanding longer
than a configured threshold raises on `ErrorOccurred` with
`ErrorCategory.Io` and surfaces in the diagnostics snapshot. This does not
unblock the thread — nothing can — but it converts "playback stopped and the
process will not exit" into a logged, observable cause, which is the
difference between a bug report that can be answered and one that cannot.
The repository already takes this shape for loop stalls.

This bounds **blocking**, not **seeking**. The two are orthogonal and the
first revision conflated them: a `DeflateStream` over a `MemoryStream` is
bounded and forward-only, so §3's non-seekable path ships with the first cut
rather than being deferred alongside it.

### 2. The open sequence

The first draft said "assign `pb` before `avformat_open_input`", which is not
executable — there is no context yet to assign it on.

```
ctx = avformat_alloc_context()
ctx->pb = avio_alloc_context(av_malloc(size), size, 0, opaque, read, seek_or_null, NULL)
avformat_open_input(&ctx, url: NULL, NULL, NULL)
```

`avformat_open_input` sets `AVFMT_FLAG_CUSTOM_IO` itself when `pb` is non-NULL
on entry, and `avformat_close_input` reads that flag to skip closing `pb`. That
is what makes §5's teardown safe rather than a double free, and it is stated
here because every line of that table depends on it.

`FFAvFormat.avformat_open_input` currently declares a non-nullable
`string url`; it needs `string?`, as the mux-side declarations already use.

### 3. Seekability: two properties, two jobs

The first draft claimed `MediaInfo.CanSeek` was "`IsSeekable`'s first
consumer", which is a contradiction — a new property cannot be a consumer of
the old one. Both exist, and they answer different questions:

| Property | Question | Read by |
|---|---|---|
| `IMediaSource.IsSeekable` | can the *source* produce bytes out of order? | the AVIO setup in §2 |
| `MediaInfo.CanSeek` | can the *opened container* seek? | `SeekAsync`, the seek bar |

`IsSeekable` decides **whether a seek callback is installed at all**. This is
the correction that matters most:

> FFmpeg derives seekability from whether the seek function pointer is non-NULL,
> not from what it returns. `avio_alloc_context` sets
> `s->seekable = seek ? AVIO_SEEKABLE_NORMAL : 0`, and the header says the seek
> callback "may be NULL".

So the first draft's "return `AVERROR(EINVAL)` when `CanSeek` is false" does
not make the input non-seekable. With a callback installed FFmpeg believes it
*is* seekable, takes the seekable demuxer paths, and never engages
`ffio_ensure_seekback` — the probe buffering that makes forward-only inputs
work at all, and which is gated on `!seekable`. Pass NULL.

`MediaInfo.CanSeek` is then read back from the opened context — but only as
far as the readback is trustworthy, which is less far than the first revision
implied.

`pb->seekable == 0` is a reliable **negative**: that input definitely cannot
seek. A non-zero value is not a reliable positive. It describes the AVIO layer
only, and a demuxer exposing a `read_seek` pointer does not guarantee a seek
will succeed on this particular input — nor does its absence rule out generic
index-based seeking. FFmpeg has no single flag that answers the question.

So `CanSeek` is `pb->seekable != 0`, and it is **advisory in the affirmative**.
A false positive degrades to a refused seek rather than a crash, because
ADR-0069 already makes a refusal an expected outcome carrying a category. The
UI gating in §4 is a courtesy that removes the common case, not a guarantee
that every surviving seek succeeds; the `Result` is the backstop and stays the
authority. A tighter derivation is an open question below.

### 4. Refusing a seek, everywhere seeks are issued

`MediaInfo.CanSeek` gates the seek path, and the gate cannot live only on
`IPlaybackController.SeekAsync`. Two internal paths bypass that method:

- **Loop rewind.** `PlaybackControllerCore.RunLoopRewindAsync` calls
  `StartSeekRunner(_session, TimeSpan.Zero, loopRewind: true)` directly
  (`PlaybackControllerCore.cs:944`). A non-seekable stream under
  `RepeatMode.One` would attempt the rewind and fail below the guard — the
  outcome the guard exists to prevent.
- **Playlist replay.** `PlaylistCoordinator.DecideNext` returns `Replay` for a
  single-clip `RepeatMode.All` wrap, serviced by `RewindToStartAsync`. Under
  `RepeatMode.All` the coordinator copies played items into `_loopBuffer` and
  replays the **same `IMediaSource` instances**, which for a stream means
  reopening one already consumed to EOF. The coordinator's own doc comment
  calls the single-clip loop "the canonical signage attract/panel loop", so
  this is the common case.

The guard goes on the session seek entry point that both routes reach, and
`SeekAsync` surfaces it as `Result.Fail(ErrorCategory.InvalidOperation, ...)`
per ADR-0069.

Whether a non-seekable source should additionally be *refused at load* under a
rewinding repeat mode, rather than failing at the first loop boundary, is in
*Not settled here*.

### 5. Callback and lifetime rules

The ways this corrupts, leaks or hangs, and the rule for each. These are
decisions, not implementation detail — the first draft deferred the exception
rule to a test, which was wrong because with `[UnmanagedCallersOnly]` an
escaping exception is a process fail-fast that no test can observe.

**Every callback body is a catch-all.** It returns an AVERROR and stashes the
exception in the session state. Nothing propagates through FFmpeg's stack
frames.

The stash is checked after **every** native call that can drive the callbacks,
not only the two obvious ones. `avformat_find_stream_info` reads ahead and so
runs them too; without a check there, a managed exception during probing
surfaces as a generic stream-info failure and the original cause is lost. The
list today is `avformat_open_input`, `avformat_find_stream_info`,
`av_read_frame` and `av_seek_frame`, and the rule is what governs rather than
the list — a new native call that can reach the stream gets a check.

**Read: `n == 0` maps to `AVERROR_EOF`, never 0.** The FFmpeg 7.1 header is
explicit: *"For stream protocols, must never return 0 but rather a proper
AVERROR code."* Returning 0 is read as "no data yet" and spins. The constant
already exists as `FFAvUtil.AvErrorEof`. `Stream.Read` may also return fewer
bytes than requested, which is legal and must not be treated as EOF.

**Seek: handle `AVSEEK_SIZE`.** `whence` may be `AVSEEK_SIZE` (0x10000),
optionally OR'd with `AVSEEK_FORCE` (0x20000). Both mov/mp4 and Matroska call
`avio_size()`, which routes through it. Mask `AVSEEK_FORCE`, answer
`AVSEEK_SIZE` with `Length` or a negative AVERROR when unknown, and only then
map SET/CUR/END. Casting `whence` straight to `SeekOrigin` yields
`(SeekOrigin)0x10000` and an `ArgumentException` inside a native frame.

**The delegates are reachable, not pinned.** A delegate cannot be pinned —
`GCHandle.Alloc(del, GCHandleType.Pinned)` throws on a non-blittable type. A
normal strong `GCHandle` plus `Marshal.GetFunctionPointerForDelegate`, or
`[UnmanagedCallersOnly]` statics. Released in the same `Dispose` that closes
the context.

**The `opaque` pointer is how a static callback finds its stream.** With
`[UnmanagedCallersOnly]` there is no other route. It carries a `GCHandle` to
the session state.

**The AVIO buffer is FFmpeg's.** Header, verbatim: *"It may be freed and
replaced with a new buffer by libavformat. AVIOContext.buffer holds the buffer
currently in use, which must be later freed with av_free()."* Free
`avioContext->buffer` as it stands at teardown, not the pointer originally
passed. The realloc is not hypothetical here: `ffio_ensure_seekback` is what
does it, on exactly the forward-only probe path §3 designs for.

**The context is freed with `avio_context_free`**, not `av_free`.
`avio_alloc_context` allocates an internal `FFIOContext` with the
`AVIOContext` as its first member; `av_free` happens to work today only because
`avio_context_free` is currently `av_freep`. That is the class of assumption
this section exists to remove.

**Two teardown orders, because failure is not success reversed.**

| | Success | `avformat_open_input` failed |
|---|---|---|
| 1 | `avformat_close_input` | *nothing* — it freed the context already |
| 2 | `av_free(avio->buffer)` | `av_free(avio->buffer)` |
| 3 | `avio_context_free(&avio)` | `avio_context_free(&avio)` |
| 4 | release `GCHandle` | release `GCHandle` |
| 5 | dispose stream unless `leaveOpen` | dispose stream unless `leaveOpen` |

The header states that *"a user-supplied AVFormatContext will be freed on
failure"*, so calling `avformat_close_input` on that path is a double free,
while the AVIO context, its buffer, the handle and the stream are all still
ours. A malformed stream is the most likely failure, so this is the path that
leaks in production. `find_stream_info` failure is the success column with
`formatCtx.Dispose()` in row 1 — today that call
(`DemuxSessionFactory.cs:139-152`) leaves the AVIO context and buffer behind.

A `SafeHandle` per resource, in the shape `FormatContextHandle` already
establishes, rather than a `finally` that has to get five steps right.

### 6. Ownership, cancellation, and the stream's position

**One thread.** FFmpeg calls back from whichever thread drives the demuxer.
`Stream` is not thread-safe and neither is `AVFormatContext` —
`FormatContextHandle` already documents the latter
(`FormatContextHandle.cs:24-26`). The stream inherits that rule. No lock in the
callback: it would sit on the packet-read path to protect an invariant the
caller can simply keep.

**The caller gets the stream back at disposal.** §5 row 5 is the point at which
it is the caller's again. `leaveOpen: false` is the default because the common
case is a `MemoryStream` the caller built for this purpose; a caller who owns
the stream beyond the player passes `true`.

**Cancellation: `AVIOInterruptCB`, and an honest limit on it.** ADR-0013 is
Accepted and says a consumer token on `OpenAsync` means "abort this
operation". `DemuxSessionFactory.OpenAsync` checks the token once and then
blocks inside `avformat_open_input`, which with custom IO blocks inside a
managed read callback with no token in scope.
`AVFormatContext.interrupt_callback` bridges the token and is part of this
work.

It does not close the hole. FFmpeg polls the interrupt callback *between* IO
operations; while control is synchronously inside our read callback it polls
nothing. A `Stream.Read` that blocks is therefore uninterruptible from the
FFmpeg side, and `Stream.Read` takes no token to honour on ours. Cancellation
and teardown both wait for it to return.

That is why §1 bounds what `FromStream` accepts rather than taking any
`Stream`.

**The stream is read from its current position, and offsets are absolute from
it.** FFmpeg seeks absolutely from byte 0 of what it is given. A stream handed
over mid-way must therefore have its origin rebased in the callbacks, or the
"byte range inside a larger archive" scenario silently reads the wrong bytes.
`FromStream` records the position at construction and treats it as byte 0.

### 7. The rest of #108

**`FromUri(Uri uri, bool? isSeekable = null)`** — **declined**, reversing both
earlier revisions of this document.

Its whole justification was that §3 gives `IsSeekable` teeth. §3 gives it teeth
*for stream sources*, where it decides whether a seek callback is installed on
an AVIO context we build. A URI source has no AVIO context of ours: FFmpeg's
own protocol handler opens it and decides its own seekability. So the override
would feed nothing — a caller marking an HTTP URL seekable would change no
behaviour, while a caller marking a file URL non-seekable would be ignored by
the thing that actually seeks.

Worse, it would advertise a capability contradicting the opened context, which
is the defect this ADR spends §3 removing. Adding a parameter that changes
nothing observable is precisely the class of drift #108 exists to clean up.

The underlying complaint in #108 is real — `IsSeekable: uri.IsFile` does
mislabel a range-serving origin. The fix is `MediaInfo.CanSeek`, read from the
opened context, which reports what the protocol actually supports without
asking the caller to guess. `IMediaSource.IsSeekable` stays what it is for a
URI source: a hint that nothing operative reads.

**XML docs on `MediaSource`, `IMediaSource`, `MediaInfo`, `VideoStreamInfo`,
`AudioStreamInfo`** — adopted. None has a doc comment, and they are the first
types a consumer touches.

**`implicit operator MediaSource(string)`** — **declined.** A `string` that is
a path and a `string` that is a URL are indistinguishable at the call site, and
the conversion would route both through `FromFile`, turning
`Open("https://example.com/a.mp4")` into a path relative to the working
directory. The saving is eight characters; the cost is a wrong answer that
looks like a missing file.

## Alternatives considered

### A. Spill to a temporary file inside `FromStream`

Copy the stream to a temp file, open the path, delete on dispose. No interop.

Rejected. It is the workaround the issue exists to remove, behind a nicer name.
Retained as the fallback if acceptance condition 1 fails, since a spill makes
any stream seekable.

### B. A named pipe or loopback socket

Rejected. Never seekable, so it forecloses §3's seekable case entirely; costs a
thread and a full copy of every byte; and a Windows named pipe versus a Unix
FIFO is a second portability surface.

### C. Put the stream on `IMediaSource`

Add `Stream? Content { get; }` beside `Uri` and `FilePath`.

Rejected as the **public** shape. `IMediaSource` describes where media lives; a
`Stream` is a live, stateful, disposable resource with thread affinity, and
putting one on a record consumers construct invites reusing a source across two
players and getting a half-consumed stream.

The first draft stopped there and called the internal mechanism "an
implementation question this ADR does not settle". That was not a tenable
position: rejecting C is only valid if some other route exists, and none of the
obvious three did — `OpenAsync` takes `IMediaSource`, `MediaSource` is a sealed
record, and `FrameFlow.Media` did not grant `InternalsVisibleTo` to Decoding.
§1 settles it.

## Consequences

### Good

- The motivating scenarios work without a filesystem.
- `IsSeekable` acquires its first real consumer (§3), and `SeekAsync` gains a
  structured refusal instead of an attempt that fails below.
- `FrameFlow.Native` gains input-side AVIO interop, the prerequisite for any
  future custom protocol.
- `interrupt_callback` makes `OpenAsync` honour its token for FFmpeg's own
  waits, which ADR-0013 promised and nothing has delivered.

### Bad

- New unmanaged-callback interop on the packet-read path — the highest-risk code
  in this repository. A lifetime mistake is heap corruption at a distance.
- **A blocking read is uninterruptible, by anything.** FFmpeg polls its
  interrupt callback between IO operations, not during ours, and `Stream.Read`
  takes no token. Cancellation and teardown both wait. §1 bounds the accepted
  shapes for that reason, and the bound is documentation rather than a
  compiler-enforced contract — a caller who passes a network stream anyway gets
  a hang, and the honest position is that the API cannot stop them.
- Consequently the network-backed case, which is a reasonable thing to want, is
  **not** served by this design and needs an async read path that does not
  exist. Named as future work rather than quietly implied. The watchdog in §1
  makes an ignored restriction diagnosable; it does not make it survivable.
- `MediaInfo` gains a member: breaking for anyone constructing one. It also
  moves the public API surface recorded in `PublicAPI.Unshipped.txt`, so
  `FromStream` and `MediaInfo.CanSeek` each need an entry, and the break goes
  in
  [docs/BREAKING-CHANGES.md](../BREAKING-CHANGES.md).

### Neutral

- **The seek-bar latch stays.** The first draft said §3 makes it redundant. It
  does not: `FrameFlowSeekBar`'s latch de-duplicates refusal *reports* across
  every `ErrorCategory`, not only non-seekability, and carries a
  `_playerGeneration` guard for rebinding. Removing it would restore the warning
  burst for wrong-state refusals, cancelled seeks and source errors during a
  scrub. Disabling the bar on `CanSeek` removes one cause of refusals, not the
  latch's job.

## Not settled here

Two acceptance conditions, neither measured. Do not accept this without both.

1. **Probing a forward-only stream.** Open a fragmented MP4 and a Matroska file
   over a `CanSeek == false` wrapper — **with no seek callback installed**, per
   §3 — and confirm `MediaInfo` comes back complete. Running this the way the
   first draft described would have measured a configuration in which FFmpeg's
   probe buffering can never engage, produced a failure, and concluded that
   forward-only streams are unsupportable. If it fails when run correctly,
   Alternative A returns as the fallback for that case.
2. **Teardown under a mid-read failure.** A test that faults the read callback
   and asserts no leak of buffer, context or `GCHandle`, against both columns of
   §5's table. §5 now fixes the exception rule, so this measures the rules
   rather than discovering them.

Open, and deliberately not decided:

- **Whether a non-seekable source is refused at `LoadAsync` under a rewinding
  repeat mode**, or allowed to fail at the first loop boundary. Refusing is
  honest and breaks the signage loop for stream sources; allowing it defers the
  failure to a point where the cause is less obvious. §4 guards the seek either
  way.
- **Whether `MediaInfo.CanSeek` is the right home**, versus a capability on the
  controller alongside `IsActivelyPresenting`.
- **Whether this should ship at all before an async read path exists.** The
  strongest objection to this design, raised three times in review and not
  answered by anything above. §1's bound is documentation; a caller who
  ignores it hangs playback *and* teardown, and no mechanism on either side
  can interrupt a synchronous `Stream.Read`. The watchdog makes that
  diagnosable, not survivable, and an allow-list of `Stream` subclasses does
  not work — a `FileStream` over a dead SMB share blocks like a socket.

  The case for shipping: the motivating scenarios are `MemoryStream`-shaped,
  where reads cannot block, and deferring leaves them on the temp-file
  workaround indefinitely. The case against: an unenforceable contract on a
  public API eventually meets a caller who did not read it, and "the process
  will not exit" is an expensive way to learn.

  This is a judgement about acceptable risk on a public surface, which is the
  maintainer's to make rather than this document's. It is stated here so the
  decision is taken deliberately and not by default.
- **A tighter derivation for `MediaInfo.CanSeek`.** §3 settles for
  `pb->seekable != 0`: reliable as a negative, advisory as a positive. A better
  answer probably means probing — attempting a seek to the current position
  during open and reading the result — which costs an IO round trip on every
  load and may fail for reasons unrelated to capability. Worth measuring before
  adopting; the `Result` backstop makes the current answer safe, only imprecise.
- **An async read path**, for the network-backed streams §1 excludes. A
  different design: FFmpeg's callback is synchronous, so it means either a pump
  thread feeding a ring buffer the callback reads without blocking, or a
  pre-buffered window. Neither belongs in this ADR.
- **`ADR-0048` step 4.** `DemuxSession.SeekAsync` pairs `av_seek_frame` with
  `avformat_flush`, whose documentation notes it does not flush the AVIOContext.
  The demuxer's internal `avio_seek` is believed to reset the AVIO buffer, so
  this is likely benign — but ADR-0048's audit table has a row for
  `DemuxSession` libavformat read-ahead whose meaning a custom AVIO changes, and
  its stated rule is that a new session-lifetime stateful component updates that
  table in the same commit.
- **Duration on a forward-only stream.** `DemuxSession.BuildMediaInfo` maps a
  non-positive `fmtCtx.duration` to `TimeSpan.Zero`. What the seek bar's
  `Maximum`, the position label and the loop-stall watchdog do with a zero
  duration is unexamined.

## Revision history

**2026-09-11, after independent review.** The review is the reason this document
is worth more than the first draft, and three findings changed the decision
rather than the prose:

- §3's seek callback returning `AVERROR(EINVAL)` was the wrong mechanism.
  FFmpeg reads seekability off the pointer, not the return, so the draft both
  described an input that was still seekable and would have invalidated its own
  acceptance condition 1 — producing a failure and, per the draft's own
  fallback rule, reinstating the temp-file spill the issue exists to remove.
- Alternative C's rejection had no surviving mechanism behind it. §1 now
  settles the internal shape, which also resolves the record-equality problem
  the draft never noticed.
- The seek guard sat only on `IPlaybackController.SeekAsync`, which loop rewind
  and playlist replay both bypass. §4 now puts it where both routes reach it.

Corrected factual claims: `avformat_open_input` is called in tests as well as
production; `FrameFlowPlayer.Open` already calls `MediaSource.FromFile`, so §7's
decline no longer prescribes something already true; the seek-bar latch is not
made redundant by §3.

**2026-09-11, second review pass.** Three more, two of them the document
claiming a problem was solved when it was not:

- `MediaInfo.CanSeek` "read back from the opened context (`pb->seekable` plus
  the demuxer's own capability)" named no such combined capability, because
  FFmpeg has none. §3 now claims only what is true — a reliable negative, an
  advisory positive — and leans on ADR-0069's refusal path for the rest.
- `AVIOInterruptCB` was called "the bridge for the token". It bridges FFmpeg's
  own waits and cannot interrupt a synchronous managed `Read`, so an unbounded
  stream hangs playback and teardown regardless. §1 now bounds the accepted
  shapes, which also settles the standing first-cut question: the limit is on
  blocking, not on seeking, so §3's forward-only path ships with the first cut.
- The exception stash was checked after two native calls;
  `avformat_find_stream_info` drives the callbacks too, and a managed exception
  during probing was being lost behind a generic failure.

**2026-09-11, third review pass.** Two, one of which reverses a decision both
earlier revisions had made:

- The bounded-read restriction in §1 is documentation and cannot be enforced —
  no property on `Stream` answers the question and no allow-list of subclasses
  is right. That stands, but it left a silent hang, so §1 now pairs it with a
  read watchdog that raises on `ErrorOccurred` and shows in diagnostics. It
  cannot unblock the thread; it makes the cause observable.
- `FromUri(Uri, bool?)` is **declined**, having been adopted twice. Its
  justification was that §3 gives `IsSeekable` teeth, and §3 does so only for
  stream sources — a URI source is opened by FFmpeg's own protocol handler,
  which decides its own seekability, so the override would change nothing while
  advertising a capability that could contradict the opened context. #108's
  underlying complaint is answered by `MediaInfo.CanSeek` instead.
