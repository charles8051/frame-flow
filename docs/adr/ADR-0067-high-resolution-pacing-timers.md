# ADR-0067: Pacing clocks sleep on high-resolution timers, not the platform timer queue

## Status

Accepted. Supersedes the timer-resolution deferral in
[ADR-0057](ADR-0057-pull-based-master-clock.md) and the consumer guidance in
[ADR-0018](ADR-0018-sdl-presenter-and-audio-adapter.md).

## Context

Both master clocks pace by sleeping (ADR-0057): `WaitUntilAsync` recomputes the
remaining time to the target from the live clock and sleeps that long, in a loop.
`WallClockSource` does it directly, `OpenAlAudioSink` does it against the OpenAL
sample counter.

That sleep is `Task.Delay`, which on Windows goes to the platform timer queue.
The queue fires on the system tick, ~15.625 ms by default. A 60 fps frame period
is 16.67 ms — just over one tick — so a sleep for one frame is rounded up to two.
Measured on the development machine, 60 samples per arm:

| sleep source | mean | median | max | implied rate |
| --- | --- | --- | --- | --- |
| `TimeProvider.System` | 28.89 ms | 30.79 ms | 32.99 ms | 34.6 fps |
| high-resolution waitable timer | 16.39 ms | 16.38 ms | 16.69 ms | 61.0 fps |

The pacing loop is identical in both rows. Which timer supplies its sleep is the
entire difference, and it is a hard ceiling: no amount of decode or presenter
headroom gets a frame delivered before the clock releases it.

This surfaced three times. #128 read as a presenter ceiling, #145 as a demux
problem, and #152 as a report that a downstream host saw ~50 fps where the
reference example saw 60. In #152 the two arms turned out to differ only by
whether the host process had called `timeBeginPeriod(1)`.

The existing answer was to make that the host's job. ADR-0018 tells consumers
"targeting high-frame-rate content on Windows" to call `timeBeginPeriod(1)` at
startup. ADR-0057 deferred a high-resolution timer on the grounds that "its
natural home is the playback session/host, not a sink". The reference example
followed its own advice and called `timeBeginPeriod(1)` in `Main`.

That is a poor contract. It is invisible — nothing fails, playback just runs at
two thirds rate — it has to be repeated in every top-level application, and it
is the kind of thing that gets discovered by filing an issue.

## Decision

FrameFlow supplies its own high-resolution timers, and both clocks default to
them.

`HighResolutionTimeProvider` (in `FrameFlow.Media`) is a `TimeProvider` whose
`CreateTimer` returns a Windows waitable timer created with
`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`. `WallClockSource` and `OpenAlAudioSink`
default their injected provider to `HighResolutionTimeProvider.Preferred`
instead of `TimeProvider.System`. Nothing else about either clock changes: the
`TimeProvider` seam and the pacing loops were already there.

`Preferred` is the system provider wherever high-resolution timers are not
available — anything that is not Windows, and Windows before 10 1803 — so the
default needs no platform check at the call site. Support is settled by creating
a timer with the flag once at startup, because the flag fails outright on older
Windows rather than being ignored, and a version check would not cover a
container or emulation layer that claims 1803 without implementing it.

The reference example's `timeBeginPeriod` call and its `--no-hi-res-timer`
opt-out flag are deleted.

### Why this is the library's call and `timeBeginPeriod` was not

ADR-0057's deferral was right about `timeBeginPeriod` and does not generalise to
this.

`timeBeginPeriod` raises the timer interrupt rate for the whole process, and
historically for the whole system. It affects every timer any component owns,
costs power, and is a policy decision about the process that a library embedded
in someone else's application has no standing to make.

`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` is a property of one timer object. It is
the facility Windows added so an application could get a precise timer *without*
the process-wide cost. A FrameFlow clock's own sleep becomes precise; no other
timer in the host changes. The reason for deferring does not apply, so neither
does the deferral.

### Consequence: the constructor parameter is now the opt-out

`WallClockSource` and `OpenAlAudioSink` still take a `TimeProvider`, and passing
`TimeProvider.System` explicitly restores the previous behaviour. The seam that
existed for testing is what a host with a reason to want coarse timers uses.

## Consequences

### Positive

- **The default configuration is the correct one.** A host that does nothing
  gets 60 fps on 60 fps content. Verified end-to-end on the 1080p60 fixture with
  no host timer call anywhere in the process: presenter tick rate went from a
  49.7/s median to 59.8/s, scheduler gap from 19.4 ms to 16.3 ms.
- **No process-wide side effect**, which is what the host-side fix cost and the
  reason it was pushed onto consumers in the first place.
- **One less undocumented requirement.** ADR-0018's consumer guidance and the
  example's `Main` both described a workaround for a defect that is now fixed.

### Negative / trade-offs

- **A kernel handle and a wait registration per pacing sleep** — roughly one per
  frame, where `TimeProvider.System` created none. Measured: 14.4 us for a
  create-register-arm-dispose cycle, 0.09% of the 16.67 ms frame it paces. Under
  load the full cycle including the fire and the wait-thread dispatch costs more:
  16 concurrent 60 Hz pacers held 61.2 fps each while the process spent 1.17 s of
  CPU over 14.7 s of wall, about 8% of one core of 24, or ~0.5% of a core per
  playing session.

  Reusing one timer per clock instead of one per sleep would remove that, and was
  not built. It needs either the clocks to abandon `Task.Delay` for a
  hand-managed `ITimer` they re-`Change`, or a pool inside the provider with
  generation-counted rejection of signals from a previous renter. Half a percent
  of a core per playing session does not buy that complexity today, and the
  `TimeProvider` seam means it can be built later without touching either clock.

  The handle also has to be released promptly rather than at finalization. Pinned
  by a test that creates and disposes 300 timers and asserts the process handle
  count does not follow.
- **Callbacks run on a thread pool wait thread**, not a queued work item,
  because queueing one would give back the scheduling delay this exists to
  remove. A wait thread serves up to 63 handles, so a callback that blocks
  delays other timers — the same constraint `System.Threading.Timer` documents.
  FrameFlow's own uses are `Task.Delay` completions.
- **A precise timer keeps the CPU out of its deepest idle states** while
  playback is running, in exchange for playing at the source's frame rate. It
  stops when playback does, which `timeBeginPeriod` at process startup did not.
- **`FrameFlow.Media` now contains a Win32 P/Invoke.** It is the assembly both
  clocks already reference and the one that ships, so the alternatives were
  worse; it is still a new kind of thing for that project.

### Neutral

- Reading the clock is unchanged. `GetTimestamp` and `GetUtcNow` are
  `TimeProvider`'s inherited defaults, which are the same QPC and system-clock
  reads `TimeProvider.System` makes. Only *when a sleep wakes* moved.
- Non-Windows platforms are unaffected in either direction. Their timers were
  never quantized this way.

## Alternatives considered

- **Keep telling hosts to call `timeBeginPeriod(1)`.** The status quo. It works,
  and #152 is the evidence for what it costs: a silent two-thirds-rate failure
  that took a bug report to find, in a downstream application whose author had
  no reason to know the requirement existed. Rejected.
- **Call `timeBeginPeriod(1)` from inside FrameFlow.** Fixes it with no consumer
  action, and imposes a process-wide timer policy from inside a library.
  Rejected for the reason ADR-0057 gives.
- **Spin the last millisecond.** Precise, and burns a core per playing stream to
  save one timer handle. Rejected.
- **Put the provider in `FrameFlow.Graph`,** where `IClockSource` and the rest
  of the clock vocabulary live. It is the better semantic home, but Graph is the
  internal Crossbar fork (ADR-0049) — not packaged, and reached only
  transitively by `FrameFlow.Audio.OpenAL`. `FrameFlow.Media` is referenced
  directly by both clocks and ships, so a host can name the type to opt out.
