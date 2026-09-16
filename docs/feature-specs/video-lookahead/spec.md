# Video lookahead

**Status:** Draft. Not implemented. Living document, rewritten as the feature changes.

**Date:** 2026-09-15

**Decision record:**
[Frame-pool ownership for buffered decoded video](../../adr/frame-pool-ownership.md) supplies
the frames this surface buffers. Without it the depth cannot exceed the decoder's spare pool
slices, and this feature has nothing to sell.

## What

A consumer asks for a lead: the media time by which decoding runs ahead of the displayed
picture. Operators placed ahead of the pacer then have that long to finish work on a frame
before it is shown, and a late wakeup costs a buffered frame rather than a dropped one.

Today the lead is 3 frames, because `SubstrateSession` never passes a capacity to
`ClockSelectVideoSink`.

## Requirements

1. **`WithVideoLookahead(TimeSpan)` on the player builder.** States the lead the consumer
   wants. Absent, behaviour is what it is today.

2. **The request is resolved, not obeyed.** A pure function takes the requested lead, the
   backend's pool model, the frame size, the frame rate and a byte budget, and returns the
   effective depth, how it is backed, and why. Values in, values out, so it is exercised as a
   table with no hardware. `ReadAheadCapacity` already derives the decoder's packet read-ahead
   from frame rate and argues against time- and byte-derived bounds; this either follows that
   reasoning or states where it departs.

3. **A byte budget bounds the depth.** One second of 4K60 NV12 is about 746 MB at 8 bit, and
   `P010` doubles it. Over budget, the depth is reduced to fit and one warning names the
   requested and effective leads. A request that cannot be met is never a failed build.

4. **Both leads are reported.** Diagnostics carry the lead configured and the lead actually
   reached. They differ whenever decode does not outrun realtime, which makes "I asked for a
   second and got two frames" visible rather than mysterious.

5. **The decision repeats per playlist item.** Frame size and rate change between items, so
   the resolve runs per item. That needs the per-item media info #216 proposes handing to the
   configurator.

## Affected layers

| Layer | Change |
| --- | --- |
| `FrameFlow.Player` | The builder method, carried through `PlayerBuilder` into the session |
| `FrameFlow.Playback` | `SubstrateSession` passes a resolved capacity to `ClockSelectVideoSink` instead of the default |
| `FrameFlow.Decoding` | The pool model per backend, and the copy-out pool from the decision record |
| Diagnostics | Configured and achieved lead on the playback snapshot |

## Open questions

- **Does a lead help an overlay at all without a presented-PTS signal?** An operator that
  finishes early still posts its result when the graph runs it, not when the frame reaches the
  screen, so a deeper lead moves overlays further ahead of the picture rather than closer to
  it. A sink-side signal carrying the presented PTS is proposed in a comment on #218. If that
  is the real fix, this surface serves throughput cases only, and requirement 1 waits.
- **Which consumers want it.** LiveCaptioning is the candidate, and it can only use a lead
  once its fork and join terminate in an open chain (#218).
- **The default budget, and whether it is per player or process-wide.**
