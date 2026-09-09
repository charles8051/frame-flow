# ADR-XXXX: A video-only pipeline that falls behind skips decode work, rather than losing time

## Status

Proposed (2026-09-08). Draft pending number assignment.

The prerequisite is **confirmed** (2026-09-09): `skip_frame` suppresses frame
output on the D3D11VA path. On the 2160p60 fixture, lag falls from ~15 s to
~0.26 s at `AVDISCARD_NONKEY` — about 58x — over three runs at each level.
Measurements and conditions in Decision §4.

The second acceptance condition — that escalation has a usable middle on content
without B-frames — is **also settled** (2026-09-09), and settling it changed the
decision twice over. The middle rung is not a `skip_frame` level: it is a
proportional skip of the GPU-to-CPU readback, which is where the cost actually is
on the pipeline this ADR was built around. And the two mechanisms are one ordered
path rather than two levers needing a classification the runtime cannot make.
Decision §2 carries the measurements, including that the readback rung's cadence
is exactly even.

Narrows the video-only fallback [ADR-0003](ADR-0003-audio-master-sync-policy.md)
left open, and implements the drop responsibility ADR-0003 already assigns to the
playback layer. Leaves [ADR-0060](ADR-0060-video-send-backpressure-policy.md)'s
backpressure policy in place.

Answers [frame-flow#82](https://github.com/charles8051/frame-flow/issues/82).

## Context

### The policy question

When a video-only pipeline cannot keep up there are two defensible answers:

- **Stay realtime, drop frames.** What a player should do.
- **Play everything, run slow.** What an offline analysis pass wants.

The pump runs the second. The clock and the reported position run the first, and
nothing reconciles them. That contradiction is #82: on a 45 s file the position
reads `00:30.011` while the picture is at 16.95 s, and on a 30 s file the same run
passes its own duration while still `Playing`.

**The decision is realtime.** The rest of this ADR is about the only mechanism
that delivers it.

### What the failure actually is

`bench-2160p60-h264-aac.mp4` through the headless sink, which runs with
`yieldHardwareFrames` false and so copies every hardware frame GPU to CPU. 30 s
window, RTX 3080 Ti:

```
  state     Playing 00:30.011/00:45.000  gen=1
  video     decoded=1017 errors=0 shed=0 backend=D3D11Va
  sink      presented=1016 dropped=0 sync-dropped=1
  lag       00:13.077  — position is ahead of the picture
```

The consumer is below realtime, so the pipeline is below realtime. Nothing is
lost — letting the run continue reaches EOF with 2609 of 2700 decoded — but for
thirteen seconds the player reports a position no viewer is looking at.

### It is not about resolution, and it is about being upstream of the ring

`--present-cost` makes the consumer slow at a chosen point in the chain:

| bottleneck | decoded / 30 s | sync-dropped | lag |
| --- | --- | --- | --- |
| 1080p60, none | 1809 | 0 | `00:00.012` |
| 1080p60, slow **presenter** (`--present-cost 30ms`) | 1808 | **811** | `00:00.020` |
| 2160p60, slow **decoder** (readback) | 1017 | 1 | **`00:13.077`** |

A slow presenter backs frames up into the clock-select ring, so `Select` has
something older to discard and the clock holds. A slow decoder means the ring
never holds more than one frame — nothing droppable, every frame presented late,
clock walks away.

So the failure is not "a slow consumer" — a slow presenter is a slow consumer and
it behaves. It is a bottleneck **upstream of the ring**, where nothing compares
frames against the clock. Readback is one way to be there; software decode on a
weak box, or inference run before the sink, are others. Resolution only matters
because it sets how many bytes each readback moves.

Naming that precisely matters for the mechanism: Decision §2 turns on *which*
upstream cost is binding, and the answer decides which lever applies.

### Dropping packets at the queue cannot fix this

This is the part that decides the mechanism, and it is measured rather than
argued. **The audio path already runs packet-level dropping** —
`DropNewestWhenQueueFull` is true whenever audio shares the pump — so it shows
what that policy achieves on this workload:

```
  video     decoded=1008 errors=0 shed=704 backend=D3D11Va
  lag       00:09.219  — position is ahead of the picture
```

704 packets shed, and still **9.2 seconds behind**. Packet dropping is running at
full tilt and the position is nowhere near the picture.

Two structural reasons, both already documented in this repository:

**The decoder trails the pump by a queue's worth.**
[`ReadAheadCapacity`](../../src/FrameFlow.Decoding/ReadAheadCapacity.cs) says so
directly — "the decoder trails the pump by a queue's worth of packets" — and sizes
the video queue to `DefaultVideoReadAhead`, **12 seconds**. That number is not
free to lower: the queue must hold more *time* than the audio queue so audio is
the stream that fills first and paces the pump. Lower it and ADR-0060's throttle
inverts. So a 9.2 s lag is not a malfunction of packet dropping; it is the read-
ahead the design requires, observed from the other end.

**Drop-newest discards at the wrong end.** A full queue sheds the packet demux
just offered, while the decoder reads from the head. The queue front stays where
the decoder is, and nothing moves the decoder toward the clock.

Neither is fixed by shedding harder, and neither is fixed by pacing the reader —
a reader gate bounds how far *demux* runs ahead, not how far *decode* falls
behind. See *Alternatives, F*, which is the version of this ADR that got that
wrong.

### Where lateness is already handled correctly

The 1080p slow-presenter row is the one with near-zero lag, and the mechanism
behind it is the model for this decision. `ClockSelectBuffer.Select` compares
frame PTS against the master clock and discards what is already past. It is a
**lateness test**, not a fullness test, and it is the only thing in the pipeline
that holds position and picture together.

It cannot solve the decode-bound case on its own — with one frame in a ring of
three there is nothing to choose between, and by then the decode cost is already
paid. The lesson is the comparison it makes, not the place it makes it.

## Decision

**A video-only pipeline that falls behind stays realtime by skipping decode
work.** It does not play slowly, and it does not report a position the picture
has not reached.

### 1. The playback layer drives a discard level from measured lateness

[ADR-0003](ADR-0003-audio-master-sync-policy.md) already assigns "when to drop or
skip a late frame" to the playback orchestration layer. This implements that
rather than adding to it.

Playback observes lateness — it already computes
`PipelineDiagnosticsSnapshot.VideoPresentationLag` — and sets a discard level on
the decoder. The decoder obeys and holds no timing policy of its own.

This is deliberately the direction that keeps the clock out of the decoding
layer. ADR-0009 put timing outside it, and a decoder that read the master clock
to make its own skip decisions would widen that boundary for no gain: the layer
that already owns the policy also already has the number.

**The setter is synchronous and takes the decoder's existing lock.**
`AVCodecContext` is not a thread-safe control surface and the decode worker is on
it continuously, but that problem is already solved: `VideoDecoder` serialises
`avcodec_send_packet`, `avcodec_receive_frame` and `Flush` under `_codecSync`, so
a setter taking the same lock writes the field while no decode call is in it. The
level is in effect when the call returns.

An earlier draft specified a posted command applied between packets, and an
asynchronous-by-contract setter to go with it. That was machinery for a race the
existing lock already prevents. See Decision §3.

### 2. One escalation path, ordered by damage

An earlier version of this section split the policy into two levers and asked the
implementation to classify a pipeline as readback-bound or decode-bound. There is
no observable that makes that call: `YieldHardwareFrames == false` proves a copy
happens, not that the copy is what binds, and a hardware decoder feeding a CPU
consumer on a weak device can be limited by either. Escalation would have had to
guess.

It does not need to. The two mechanisms go on **one ordered path, cheapest damage
first**, and the policy walks it until lateness recovers:

| step | mechanism | what it costs |
| --- | --- | --- |
| 1 | readback 1 in 2, 3, 4, … | nothing decoded is lost; only the copy is skipped |
| 2 | `AVDISCARD_NONREF` | frames nothing references |
| 3 | `AVDISCARD_BIDIR` | B-frames |
| 4 | `AVDISCARD_NONKEY` | everything but keyframes |

Selection falls out of the walk. A readback-bound pipeline recovers in step 1 and
never reaches the rest. A decode-bound one does not — increasing N leaves lateness
where it was, because every frame is still decoded — so the policy keeps going and
reaches the `skip_frame` levels that shrink decode work. A pipeline limited by
both recovers partway through and stops there. **Nothing has to know which kind it
is; not recovering is the signal to keep walking.**

The ordering is by damage, and it is not arbitrary. Skipping a readback discards a
copy. Skipping a decode discards a frame other frames may reference. The harmless
lever is exhausted before the destructive one is touched, and on the pipeline #82
was found on the destructive one is never reached.

#### The readback rung, measured

Skipping `BuildManagedFrame` for (N−1) of every N received frames — the frame is
still decoded, so the decoder's reference state is untouched:

| readback | received | read back | lag |
| --- | --- | --- | --- |
| 1 in 1 (today) | 837 | 836 | `16.097` |
| 1 in 3 | **1747** | 581 | `0.972` |

`received` counts `avcodec_receive_frame` returning a frame; `read back` counts
frames that reached `BuildFrame`, which is `_framesDecoded` and sits after the
copy. The two columns are the point: at 1 in 3 the codec produces 1747 frames in
30 s — 58 fps, essentially the source rate — while 581 are copied. The deficit was
the copy, and the earlier sweep shows it moving continuously rather than in a
step:

| readback | read back | lag |
| --- | --- | --- |
| 1 in 1 | 978 | `13.703` |
| 1 in 2 | 724 | `5.899` |
| 1 in 3 | 600 | `0.029` |
| 1 in 4 | 450 | `0.023` |
| 1 in 6 | 300 | `0.028` |
| GPU presenter, no readback at all | — | `0.003` |

(Two sweeps on different runs; the 1-in-1 and 1-in-3 lag figures differ between
them by the ~15 % run-to-run spread Decision §4 records. The shape is what is
being claimed, not the third decimal.)

**1 in 3 reaches the lag `NONKEY` reaches while keeping roughly five times the
frames** — ~600 against 114 over the same window. That is the argument for putting
it first.

#### Its cadence is even, measured rather than assumed

A count and an end-of-window lag cannot tell an even 20 fps from the same frames
delivered in bursts. Tracing the PTS of every kept frame settles it:

| run | kept frames | PTS deltas |
| --- | --- | --- |
| 1 in 1 | 838 | 837 intervals, **all** exactly 256 |
| 1 in 3 | 583 | 582 intervals, **all** exactly 768 |

768 is exactly three frame intervals, and not one of the 582 deviates. The skip is
a modulo counter over frames arriving at a constant rate, so even spacing is what
it produces.

**With one caveat this corpus cannot close.** Decode order equals presentation
order here because the stream has no B-frames. On reordered video a modulo counter
over *received* frames is not evenly spaced in *presentation* time. Keying the
skip to presentation timestamps instead would fix that, and is the obvious shape,
but it is unmeasured for the same reason the `skip_frame` middle rungs are: the
pinned LGPL FFmpeg cannot produce a B-frame fixture. Recorded in *Validation*.

#### The `skip_frame` steps, and their limit

`AVDISCARD_NONREF` first among them, because non-reference frames are the ones
nothing else decodes from. On content with no B-frames those two steps discard
nothing at all — measured, one run each:

| level | read back | lag |
| --- | --- | --- |
| `NONE` | 908 | `00:14.902` |
| `NONREF` | 917 | `00:14.749` |
| `BIDIR` | 997 | `00:13.408` |
| `NONKEY` | **114** | **`00:00.265`** |

`ffprobe` over the first 10 s finds 564 P-frames and 36 I-frames and no B-frames,
so the two middle rows sit inside the run-to-run spread. `NONKEY` matches its
arithmetic — 36 I per 10 s is ~108 in 30 s, against 114, repeating at exactly 114
across three runs.

So on such content steps 2 and 3 are no-ops and step 4 is a cliff. That is
accepted rather than solved: there is no proportional way to decode part of a
reference chain. What makes it tolerable is the ordering — a pipeline only reaches
step 4 after the harmless rung has failed to recover it, which on a readback-bound
pipeline never happens.

#### Hysteresis

Escalate while lateness exceeds the threshold, de-escalate as it recovers, with
hysteresis wide enough that a pipeline sitting near a boundary does not oscillate
across it. The path is walked in one direction at a time; the steps are ordered, so
"escalate" and "de-escalate" are a single index moving.

### 3. No binding work is required

An earlier draft of this ADR said `AVCodecContext.skip_frame` needed binding. It
does not. `FFmpeg.AutoGen.Abstractions` already exposes the field and all four
`AVDiscard` levels, and the write compiles against the project unchanged, in the
same shape `VideoDecoder.HwAccel` already uses for `hw_device_ctx`:

```csharp
ref AVCodecContext ctx = ref Unsafe.AsRef<AVCodecContext>((void*)ctxPtr);
ctx.skip_frame = level;
```

Nor does the setter need the command-and-apply-between-packets machinery an
earlier draft specified. `VideoDecoder` already serialises every codec-context
call — `avcodec_send_packet`, `avcodec_receive_frame`, `Flush` — under
`_codecSync`. A setter taking that lock is safe, synchronous, and needs no queue.

### 4. What the skip saves, and what is not guaranteed

`skip_frame` has two effects and they carry different weight.

**Guaranteed:** the frames are not returned from the decoder API. Everything
downstream of it — the GPU-to-CPU readback on the hardware path, pool traffic,
the pacer, the sink — does not run for a skipped frame, on any decoder,
regardless of who did the decoding. In the failure this ADR exists for, that
readback *is* the bottleneck, so the guaranteed half is the half that matters.

**Not guaranteed:** that the decode work itself shrinks. With D3D11VA the GPU
does the decoding and a driver may honour `skip_frame` fully, partially, or not
at all. A pipeline that is genuinely decode-bound rather than downstream-bound
therefore may not recover, and that is observable: lag stays high after
escalation reaches `AVDISCARD_NONKEY`.

The ADR does not assume the second, and it does not merely note the first either.
The guarantee rests on a specific claim about libavcodec — that `skip_frame` is
enforced where frames are output, not delegated to the hardware decoder, so a
frame matching the level is not returned whoever did the work. That claim is what
makes the readback saving hold on D3D11VA, and it is the load-bearing one.

**This was held as a prerequisite, and it is now confirmed.** Measured on the
D3D11VA path this failure was found on, with `skip_frame` pinned and everything
else unchanged:

Three runs at each level, alternating, same process configuration:

| `skip_frame` | decoded / 30 s | lag |
| --- | --- | --- |
| `NONE` | 863, 906, 901 | `15.641`, `14.925`, `15.014` |
| `NONKEY` | 114, 114, 114 | `0.263`, `0.259`, `0.257` |

**About 58x less lag**, and eight times fewer frames returned. `NONKEY` is
deterministic to the frame across runs, which is what decoding exactly the
keyframes looks like.

**Why this shows suppression at the decoder and not dropping downstream.**
`decoded` is `VideoDecoder._framesDecoded`, incremented in `BuildFrame` — which
returns the frame `ReceiveFrame` stashed, *after* the hardware readback that
`ReceiveFrame` performs. It is a post-readback counter. 114 rather than ~900
therefore means ~900 readbacks did not happen, not that they happened and the
frames were discarded later. Downstream dropping would leave `decoded` at its
baseline and show up in `dropped` / `sync-dropped`, which stayed at 0 and 1.

**Run conditions.** RTX 3080 Ti, driver 32.0.16.1047, Threadripper PRO 5945WX,
Windows 11. Headless presenter, default pool capacity, and the decoder must
resolve to D3D11VA — that is the whole point of the measurement, and `diag`
reports it as `backend=D3D11Va` on the `video` line. A run that falls back to
software, or to a different hwaccel, is measuring something else and its numbers
do not belong beside these. `decoded` and `lag` come from the single `diag` at the
end of the 30 s window, so each row is one sample of a cumulative counter and one
instantaneous lag reading, not an average.

**Reproducing it.** The fixture is generated, not committed, and its input is
seeded (`all_seed=12345`), so it is the same pixels on any machine:

```bash
dotnet run scripts/fetch-ffmpeg.cs
dotnet run scripts/generate-test-corpus.cs -- --include-benchmarks
```

The script, `p.bench`, is four lines — `play`, `wait 30s`, `diag`, `quit`:

```bash
FRAMEFLOW_PROBE_SKIP_FRAME=NONKEY dotnet run --project tools/FrameFlow.TestBench   -c Release -- tests/corpus/files/bench-2160p60-h264-aac.mp4   --no-audio --script p.bench
```

That env var does not exist in the tree. `skip_frame` was pinned by a temporary
probe in `VideoDecoder`'s constructor, reverted afterwards because a debug hook in
a decoder hot path is not worth committing for a one-off. It is fifteen lines, and
this is all of it:

```csharp
var probe = Environment.GetEnvironmentVariable("FRAMEFLOW_PROBE_SKIP_FRAME");
if (!string.IsNullOrWhiteSpace(probe))
{
    var level = probe.Trim().ToUpperInvariant() switch
    {
        "NONREF" => AVDiscard.AVDISCARD_NONREF,
        "BIDIR" => AVDiscard.AVDISCARD_BIDIR,
        "NONKEY" => AVDiscard.AVDISCARD_NONKEY,
        _ => AVDiscard.AVDISCARD_NONE,
    };
    unsafe
    {
        ref AVCodecContext c = ref Unsafe.AsRef<AVCodecContext>(
            (void*)codecCtx.DangerousGetHandle()
        );
        c.skip_frame = level;
    }
}
```

Dropped in after `_codecCtx = codecCtx;`, it reproduces every row above. It is
also the whole of the `skip_frame` plumbing this decision needs, which is the
other thing it demonstrates.

**On the baseline moving.** Context above records 1017 decoded for the same
nominal scenario, against 863–906 here. Different worktree and a machine that had
just generated the corpus; the decode-bound baseline is sensitive to load in a way
`NONKEY` is not. Both are the same phenomenon and the spread is roughly 15%, which
is why the comparison is drawn against the three runs in this table rather than
across sessions. The effect being claimed is 8x in frames and 58x in lag, so it
survives that spread by a wide margin — but it is measured on one machine and one
fixture, and nothing here establishes the factor generalises.

The decode-side saving is still not separated from the downstream one by this
measurement, and does not need to be: on a readback-bound pipeline the downstream
saving is the whole of it. That split stays a tuning question.

### 5. Nothing changes in the pump or the queue

No read-ahead gate, no change to `DropNewestWhenQueueFull`, no change to
`ReadAheadCapacity`. A decoder that skips forward drains its queue, which lets
ADR-0060's existing backpressure pace the pump at realtime again on the video-only
path, and reduces shedding on the audio path.

The whole change is one ordered escalation path, a policy that walks it from
measured lateness, and a switch to turn the policy off. No binding and no new
synchronisation — both already exist, as Decision §3 records — and the readback
step is a third arm on a branch that is already there.

## Consequences

### Positive

- Attacks the cost that is actually over budget, and picks the lever by which
  cost that is. On a readback-bound pipeline the copy shrinks and the decode does
  not; on a decode-bound one the reverse. Every mechanism that moves packets
  around leaves both untouched.
- The reported position describes content the pipeline has reached, on every
  configuration. `Position` and `Ended` become usable by a caller driving a
  playlist without a watchdog around them.
- Helps the audio path too, which today sheds 704 packets and still runs 9.2 s
  behind. The same escalation lets the decoder close that gap.
- Graduated where it counts, and self-selecting. The readback rung is proportional
  at any 1-in-N and its cadence is exactly even; 1 in 3 reaches the lag
  keyframes-only reaches while keeping five times the frames. Because the steps are
  ordered by damage and walked until lateness recovers, no classification of the
  pipeline is needed — not recovering is the signal to keep walking.
- No new coupling. Playback already owns the policy and already has the number;
  the decoder gains a setter.

### Negative

- **Video-only playback on a machine that cannot keep up now loses frames where
  it used to lose time.** The intended trade, and a behaviour change for anything
  relying on complete-but-slow.
- Analysis and transcode passes want every frame, and dropping cannot be the only
  contract available to them. **The policy is a session-level setting, realtime by
  default and switchable off**; a consumer that needs every frame turns it off and
  gets today's complete-but-slow behaviour, now as a stated choice rather than as
  the only behaviour. Deferring the knob was the earlier plan and it was wrong:
  making frame-dropping the default contract with no way out is a larger
  commitment than this decision needs, and naming the setting costs a property.
  What is still deferred is any richer non-realtime *mode* — rate control,
  completion guarantees — which stays out of scope.
- Discard levels are visible as motion artefacts before they are visible as
  stutter. `AVDISCARD_BIDIR` on high-motion content looks worse than its frame
  count suggests, and a readback skip at 1 in 6 is 10 fps however evenly spaced.
- The escalation path is longer than a single lever would be, and a reader has to
  hold four steps rather than one. The alternative was worse: one lever is either
  useless on a readback-bound pipeline or unavailable on a decode-bound one, and
  two levers behind a classification needs an observable that does not exist.
- Escalation is a feedback loop against a measurement that already lags reality.
  The hysteresis is what keeps it stable, and hysteresis chosen badly is its own
  failure mode.

### Neutral

- A pipeline that keeps up never leaves `AVDISCARD_NONE`, so a healthy run is
  bit-identical to today on every configuration.

## Alternatives considered

### A. The clock follows the video

Derive position from the last presented PTS, or reseat both clocks onto it
continuously — generalising what `RepositionAsync` already does at a seek.

Rejected. It makes the halves agree by adopting the *other* policy: video-only
playback becomes permanently slow, and a live or long-running source drifts
without bound. Wrong for the single-window signage shape ADR-0060 validated
against, where a dropped frame is cheaper than a growing offset.

### B. Clamp the reported position to the duration

Rejected: it replaces an obviously wrong number with a plausible one.
`00:32.007/00:30.000` announces that something is broken.
`00:30.000/00:30.000` over a picture at 17 s does not.

### C. Report the lag and change nothing else

The step before, landed in [#84](https://github.com/charles8051/frame-flow/pull/84).
It is the instrument this decision is measured with and fixes nothing alone.

### D. Stop the headless sink forcing a GPU-to-CPU copy

Rejected as a fix for this, though it may be worth doing anyway. `--present-cost`
reproduces a slower-than-realtime consumer at 1080p with no readback involved, so
the class is independent of the mechanism that triggered it here.

### E. Bound the decoder queue in time instead of in slots

Already done, and it does not address this.
[`ReadAheadCapacity`](../../src/FrameFlow.Decoding/ReadAheadCapacity.cs) sizes the
video queue from the stream's frame rate to a 12-second target, and records why a
per-packet time bound was rejected in favour of a capacity derived once. Shrinking
that target is not available: the video queue must hold more time than the audio
queue or ADR-0060's throttle inverts.

### F. Pace demux reads against the master clock, and drop-newest unconditionally

**This was the first draft of this ADR, and it was wrong.** Recorded in full
because the reasoning is easy to arrive at again.

The proposal was ADR-0060's own deferred alternative C: bound how far the pump
reads ahead of the master clock, then restore drop-newest for video-only since
blocking would no longer be the pacing mechanism.

It does not fix the failure. A read-ahead bound constrains how far *demux* runs
ahead, not how far *decode* falls behind, and the queue's depth is a design
requirement rather than an accident. The measurement that settles it is the audio
path, which already runs the proposed configuration — realtime-paced demux plus
unconditional drop-newest — and sits **9.2 seconds behind while shedding 704
packets**.

It would also have added clock plumbing to the decoding layer and a new pacing
policy to the pump, to deliver an outcome that configuration already demonstrably
does not deliver.

## Validation

Two items are done and struck through below with their results: the D3D11VA
prerequisite, and whether the ladder has a usable middle. Everything else is
unperformed, because this ADR proposes the change and does not record it. What
would have to hold:

- **2160p60 fixture, video-only, 30 s window:** lag falls from 13.1 s to inside
  the escalation threshold; presented frame count falls; position tracks content;
  the run reaches `Ended` at the file's duration.
- **2160p60 fixture with audio:** lag falls from 9.2 s. This case is the reason to
  believe the mechanism generalises, because it is already dropping packets and
  still behind.
- **1080p60 fixture, both paths:** unchanged, and the discard level never leaves
  `AVDISCARD_NONE`. A pipeline that keeps up must not pay anything for this.
- **A long-GOP source at `AVDISCARD_NONKEY`:** output stutters to the keyframe
  interval and does not corrupt. This is the case where the escalation is most
  destructive and it needs its own fixture; the corpus has none today.
- **Escalation as a pure function** of lateness, current level and hysteresis —
  testable without FFmpeg, a decoder, or a clock.
- ~~**That `skip_frame` suppresses frame output on the D3D11VA path.**~~
  **Done** — ~15 s to ~0.26 s at `NONKEY`, three runs each, Decision §4. On one
  machine and one fixture; the mechanism is established, the factor is not
  claimed to generalise.
- ~~**That escalation has a usable middle on B-frame-free content.**~~ **Done** —
  a proportional readback skip gives 13.7 s / 5.9 s / 0.029 s at 1-in-1 / 1-in-2 /
  1-in-3, against `NONKEY`'s single step to 0.265 s. Decision §2.
- ~~**That the readback skip spaces frames evenly, not in bursts.**~~ **Done** —
  PTS traced for every kept frame: 582 of 582 intervals exactly three frame
  durations at 1 in 3, zero deviation. Decision §2.
- **That it stays even on reordered video.** Decode order equals presentation order
  on this corpus because nothing in it has B-frames, so a modulo counter over
  received frames is trivially even here and would not be on a reordered stream.
  Keying the skip to presentation timestamps is the obvious answer and is
  unvalidated, blocked by the same missing B-frame fixture as the `skip_frame`
  middle steps.
- **That a decode-bound pipeline actually walks past the readback rung.** The
  ordering assumes increasing N leaves lateness unmoved when decode is the
  constraint. That follows from every frame still being decoded, but it is reasoned
  rather than measured — no decode-bound fixture has been run.
- **`NONREF` and `BIDIR` on content that actually contains those frames.** Not
  possible on this corpus: the pinned FFmpeg is an LGPL build without libx264, and
  libopenh264 emits no B-frames, so every fixture is all-reference P plus I. The
  generator already records two fixtures as unavailable for the same reason.
  Validating the ladder's middle needs a GPL FFmpeg, and until someone runs it
  those two rungs are unexercised rather than working.
- **How much decode work the hardware path actually saves**, separately from the
  downstream saving. Tuning — it changes how fast escalation recovers, not whether
  it does.
- **The policy switched off yields today's behaviour exactly** — every frame
  decoded, lag free to grow. The opt-out is only worth having if it is the
  complete-but-slow path and not a degraded version of it.
- **`RunDemuxPump_RealVideoDecoder_AudioDiscarded_LongClip_DoesNotPrematurelyEof`
  keeps passing**, unchanged. This decision does not touch the pump, so ADR-0060's
  regression test should be untouched by it; if it moves, something is wrong.

## Open questions

- **The threshold and the hysteresis.** Both are empirical and both go in this ADR
  when measured. `VideoPresentationLag` is how they get chosen.
- **How much of the saving is decode-side on hardware paths.** Output suppression
  is confirmed and is the half that matters on a readback-bound pipeline; this is
  the size of the other half. Tuning — it changes how fast escalation recovers,
  not whether it does.
- **What a richer non-realtime mode would offer** beyond the off switch in
  Consequences — rate control, completion guarantees. Out of scope; the switch is
  not.
- ~~**What the ladder does on content with no B-frames.**~~ **Settled** — the
  middle rung is a proportional readback skip, not a `skip_frame` level, and it
  applies to the pipeline #82 was found on. Decision §2. The `skip_frame` ladder
  keeps its cliff on the decode-bound path, accepted there rather than solved,
  because no proportional way exists to decode part of a reference chain.
- **What happens across a seek** while escalated. The level should reset with the
  run, on the same reasoning that `PresentationLag` reports null on a fresh run.

## References

- [ADR-0003](ADR-0003-audio-master-sync-policy.md) — audio-master policy; the
  video-only fallback this narrows, and the drop responsibility it implements.
- [ADR-0009](ADR-0009-threading-and-concurrency-model.md) — timing outside the
  decoding layer, which the direction of control here preserves.
- [ADR-0059](ADR-0059-discard-streams-with-no-consumer.md) — the existing
  `AVDiscard` use at the demuxer.
- [ADR-0060](ADR-0060-video-send-backpressure-policy.md) — backpressure policy,
  left in place.
- [frame-flow#82](https://github.com/charles8051/frame-flow/issues/82) — the
  defect.
- [frame-flow#84](https://github.com/charles8051/frame-flow/pull/84) — the lag
  metric this is measured with.
- `src/FrameFlow.Decoding/ReadAheadCapacity.cs` — the 12-second read-ahead and why
  it cannot shrink.
- `src/FrameFlow.Playback/ClockSelectVideoSink.cs` — `Select`, the lateness test
  this generalises.
