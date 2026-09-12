# ADR-0071: What the audio clock is allowed to believe

## Status

Accepted (2026-09-12).

Amends [ADR-0003](ADR-0003-audio-master-sync-policy.md). That record makes the audio
device the master clock when audio is present, on the grounds that it is "the most stable
continuous time source". It is — while it is playing. This decision says what the clock
does when it is not, which ADR-0003 left to the implementation and the implementation
answered by trusting the device unconditionally.

Builds on [ADR-0057](ADR-0057-pull-based-master-clock.md), which made the clock a pull:
every read goes to the device. Nothing here changes that. It changes what a read is
allowed to return.

Resolves defect 2 of issue #127.

### What shipped

| | |
| --- | --- |
| Pause latches the position; reads return it | `src/FrameFlow.Audio.OpenAL/OpenAlAudioSink.cs` |
| Resume re-anchors the origin onto that position | `AudioClockState.RebaseOnResume`, `src/FrameFlow.Media/AudioClockState.cs` |
| The counter may not outrun elapsed playing time | `src/FrameFlow.Media/AudioClockRateLimit.cs` |
| Both anchors dropped together at every reseat | `InvalidateClockAnchorsUnderLock`, the sink |
| Endpoint named at open; `ALC_CONNECTED` polled | `SharedOpenAlContext`, the sink (ADR-0058's device) |

## Context

### "Processed" is not "played"

The clock is `BaseSourceTime + (ProcessedSamplesPerChannel + deviceSampleOffset) / rate`.
`ProcessedSamplesPerChannel` advances in `RecycleProcessedBuffers`, which credits every
buffer OpenAL reports as processed.

`AL_BUFFERS_PROCESSED` means the device has let go of the buffer. It does not mean the
device turned it into sound. Two states make those diverge, and the sink could
distinguish neither:

- **The queue is discarded.** The endpoint goes away underneath the open context and
  OpenAL Soft marks everything queued as processed at once.
- **The source is started but not producing.** Every newly queued buffer comes back
  processed immediately, so the counter advances at the rate the decoder can feed it.

The second was already known. `ActivateAsync` has carried a defence against it since the
silent-loops-2+ fix — SourceStop, drain the residual queue, SourceRewind — with a comment
naming the behaviour exactly: OpenAL Soft "marks subsequent queued buffers as processed
without the device actually playing them". That defence is on the re-activation path and
nowhere else, so the same state reached any other way was not defended against at all.

### What it cost

Paused at 4.34 s, held 27 minutes, resumed at 5.43 s. The clock gained 1.09 s while
playback was stopped, and a four-minute file then reached `Ended` 1.08 seconds after Play.

The 1.09 s is the whole buffer pool, to within 2 %. Staging flushes at 6144 interleaved
samples — 3072 per channel, 69.66 ms at the file's 44.1 kHz — and `BufferPoolSize` is 16.
Sixteen buffers is 1114 ms against the 1090 ms measured. The entire queue was credited at
once, with the pause a third of the way into the head buffer.

The runaway after it is the other state: 209 s of clock in 176 ms is the counter moving at
decode rate. Every decoded frame came due at once, the presenter ran at 750 ticks/s, and
the decoder shed 4635 packets to backpressure.

The trigger was a Remote Desktop session. Connecting with audio redirection makes Remote
Audio the default render endpoint; disconnecting removes it. An endpoint that is removed
invalidates the stream an application already has open — which is why *demoting* an
endpoint does not reproduce this, and a paused source on a stable endpoint does not drain
over fifteen minutes on any machine tried.

### Why nothing caught it

Nothing was watching. A guard existed for three days: the publish ticker clamped
`min(audioTime, sessionElapsed)`, described as a guard "against the audio counter briefly
running ahead". It was removed, correctly. The two values are different coordinates —
`audioTime` is source-stream PTS seated on a seek target or a first buffer's PTS, while
elapsed playing time restarts at zero — so after a seek the min clamped the clock back to
~0 and froze video for the length of the seek. [ADR-0057](ADR-0057-pull-based-master-clock.md)
then removed the ticker entirely, and the clock became an unguarded device read.

The removal took the only bound on the counter with it. That is the piece this record
puts back, in the shape that survives a seek.

## Decision

**The device is the master clock while it is playing, and only then.**

Three rules, each independently sufficient for a different failure.

### 1. A stopped clock is latched, not read

`PauseAsync` takes one last live reading and stores it. Every read while paused returns
that value and does not touch the device. `ResumeAsync` seats the origin back onto it via
`AudioClockState.RebaseOnResume`, which subtracts the live counters so the clock continues
from the pause position whatever the queue did meanwhile.

A queue that survived the pause and a queue that was discarded land on the same position,
because the surviving queue's offset is already inside the subtraction. There is no case
analysis and nothing has to detect which happened.

This is narrower than it looks: it says a paused device's counters are not evidence about
where playback is. They are not evidence about anything — the device was asked to stop.

### 2. The counter may not outrun elapsed playing time

A playing device consumes one second of audio per second. `AudioClockRateLimit` compares
each reading's **advance** against the advance of `_sessionClock` over the same interval;
a reading beyond that by more than a tolerance is audio the device dropped rather than
played, so the clock publishes what real time allows and re-anchors there.

The excess is discarded, not recovered on later reads. A device that never recovers
therefore yields a clock that keeps real time, rather than one that races a file to its
end in a second.

**On the delta rather than the value.** This is the whole difference between this guard
and the one that was deleted. Absolute values live in different coordinate systems and a
seek moves them apart; advances live in the same one and a seek moves both sides together.
The cost is an obligation: the anchor has to be dropped wherever the origin is reseated,
or the reseat itself reads as a device outrunning real time. That is the same set of
transitions that already drops the interpolation anchor, so both now go through one
`InvalidateClockAnchorsUnderLock` — six sites: first-buffer capture, resume, the resume
rebase, deactivate, activation seating, and `SeekBaseline` — rather than two assignments
to remember at each.

Tolerance is `DefaultSlack`, 250 ms. The counter moves in mixing-period steps and a buffer
boundary can land inside any read interval, so a reading may legitimately lead elapsed
time by up to a buffer; the failures this catches overshoot by whole seconds. Host-versus-
device clock drift does not accumulate, because every accepted reading re-anchors and the
comparison is always over one read interval.

### 3. A dead endpoint is named, not inferred

The sink reads `ALC_CONNECTED` once per flush and latches the first false reading into
`DeviceDisconnected`, logging one error naming the endpoint. The endpoint's name is
captured when the device is opened and carried in the start log.

Deliberately not folded into the underrun check beside it: the two never co-occur. A dead
endpoint keeps reporting buffers processed, so the source never starves and no underrun is
ever observed. An underrun is the device running out of audio; this is the device ceasing
to be a place audio goes.

This reports. It does not recover. Reopening the endpoint means reopening the device the
whole process shares under [ADR-0058](ADR-0058-shared-openal-device-and-context.md), and
that is a larger decision than this one.

## Consequences

**The clock is honest under conditions where it used to be confidently wrong.** Its
failure mode changes from "races ahead by an unbounded amount" to "keeps real time while
the audio is silent". Silent playback at the right speed is still a fault, and
`DeviceDisconnected` is how a consumer sees it.

**ADR-0003 is unchanged in substance.** Audio still masters the clock when audio is
present; wall-clock is still the fallback when it is not. What is new is that "when audio
is present" now means present *and playing*, and the sink can tell the difference.

**Rule 2 makes the wall clock a bound on the audio clock.** ADR-0003 chose the device over
the wall clock because the device is more stable. It is, in the direction that matters —
the device may lag real time freely, and does, because that is what a buffer is. The bound
is one-sided by construction: only readings that run *ahead* are limited.

**A discontinuity that forgets to drop the anchor degrades to a clamped clock.** The
obligation in rule 2 is real, and the failure is quiet rather than loud — the clock would
advance at wall rate instead of jumping to the new origin. `AudioClockRateLimitTests`
pins both halves: the seek case with the anchor dropped passes through, and the same seek
with the anchor retained is clamped. The second test exists to state the obligation, so a
transition added later fails there rather than in the field.

**Three device calls per flush, where there were two.** `ALC_CONNECTED` joins the source
state read, at once per flush rather than once per clock read. A flush is roughly one per
three decoded audio frames.

**The pure core carries the proof; the device tests do not.** `AudioClockRateLimit` and
`RebaseOnResume` are total functions over scalars and are exhaustively tested. The
device-gated sink tests are regression guards: a healthy endpoint does not drop its queue,
so they pass with or without the fix. **The `ALC_CONNECTED` path has never been exercised
against a real endpoint removal** — it is a two-line read of a documented token, but it
rests on the extension reporting what its specification says, which no test here has
confirmed.

## Alternatives Considered

**Leave the clock alone and fix the trigger.** The trigger is a Windows audio endpoint
being removed. It is not reachable from this codebase, it is not rare, and a media library
running on a remote session or a laptop whose headphones are unplugged will meet it.
A clock that is correct only while the hardware behaves is not a clock.

**Detect the disconnect and use it to gate the clock.** Make rule 3 the mechanism and drop
rules 1 and 2. Rejected on ordering and on coverage: the detection is once per flush while
the clock is read every ≤50 ms, so a disconnect is credited before it is noticed; and
`ALC_CONNECTED` is one cause of a counter that lies. The `ActivateAsync` comment documents
another, on a connected device. Rules 1 and 2 are about the counter's claims and hold
whatever the cause; rule 3 is a name for one cause.

**Clamp the absolute value, as the deleted guard did.** Restoring it would restore the
freeze-after-seek bug it was deleted for. Included here because the record of *why* that
version failed is the argument for this one's shape, and without it the obvious
simplification looks like an oversight.

**Stop crediting processed buffers and derive the clock from `AL_SAMPLE_OFFSET` alone.**
The offset is per-buffer and resets as the queue advances, so the cumulative count is what
makes the clock monotonic across buffers. This trades a bounded overshoot for a clock that
cannot express a position beyond the current buffer.

**Unqueue the drained buffers at resume and subtract their samples.** An earlier shape of
rule 1. It needs the sink to work out what drained versus what played, which is exactly
the distinction the device stopped providing. Re-anchoring onto the latched position
reaches the same answer without asking the question.
