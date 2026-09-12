# ADR-0072: Tests do not depend on elapsed time

## Status

Accepted (2026-09-12).

Extends [ADR-0007](ADR-0007-testing-and-validation-strategy.md). That record asks for
"deterministic seams where timing and synchronization behavior matter" and says nothing about
what a test may do once the seams exist. This one says what a test may depend on, which suites
are exempt and why, and how the rule is kept.

Closes issue #153.

### What shipped

| | |
| --- | --- |
| The ban, as a build error | `tests/Directory.Build.targets`, `tests/BannedSymbols.txt` |
| Opt-outs, one per project, each with its reason | the six test `.csproj` files that still set `FrameFlowBanWallClockInTests` |
| One time abstraction for wall time | #156, retiring `ITimeSource` for `TimeProvider` |
| One fake for it | #157, `FakeTimeProvider` in place of two hand-rolled doubles |
| Production timers on an injected provider | #158, the settle backstop and wait caps in `ClockSelectVideoSink` |
| A worker that tells the test it is done | #159, the park signal on `ClockSelectVideoSink` |
| Assert the selection, not its speed | #155, `WallClockSource`'s provider |

## Context

### The flakes were not where they looked

Two tests had been filed as flaky. #148 timed fifteen real 16.67 ms waits and asserted a median.
#78 slept 120 ms and asserted nothing had been delivered, against a production timer that fired
at 250 ms. Both read as "this test depends on the wall clock", and the reflex answer is to inject
a clock.

In `ClockSelectVideoSinkTests` the clock was already injected. It was a `FakeClock` with no wall
time in it at all, and the file still carried thirteen `Task.Delay` calls and a 5 ms polling
loop. The code under test ran a delivery loop on its own task. Advancing the fake clock returned
immediately, and the loop reacted on another thread at a time nobody controlled. Every assertion
was made across that gap. The positive ones polled for the outcome; the negative ones, which
have no outcome to poll for, slept and hoped.

So there were two separate dependencies wearing one name:

- **Time as a value.** "What time is it", "has this timer fired". Solved by injecting a clock.
- **Progress.** "Has the worker finished reacting to what I just did". Not touched by injecting a
  clock, and the actual source of most of the flakes.

An injected clock is necessary and nowhere near sufficient.

### The abstraction had forked

Before #156 wall time was reached three ways: `ITimeSource` in `FrameFlow.Playback`, `TimeProvider`
in five other places, and raw `Stopwatch` and `DateTime.UtcNow` throughout. Tests had matching
doubles — a hand-rolled `ManualTimeSource`, a hand-rolled advancing `ManualTimeProvider`, and
Microsoft's `FakeTimeProvider` in one project. The hand-rolled advancing provider fired timers in
registration order rather than due order and did not fire a zero-due-time timer until the next
advance. Neither had bitten, because every test using it registered one wait at a time, and its
file described it as the pattern to copy.

### Nothing kept any of it

Sixteen of twenty-one test projects contained no sleep, no delay, no stopwatch and no wall-clock
read. That was not a rule anyone had written down. It was where the tree happened to be, and the
five that were not had got there one reasonable-looking test at a time. #78 and #148 were filed
months apart against tests that read as fine when written.

This repository already had a working answer to exactly that shape of problem. An undeclared
public member fails the build through `PublicAPI.Unshipped.txt`, so the surface cannot move
without someone writing a line and a reviewer seeing it. Test timing had no equivalent.

## Decision

### 1. Wall time is `TimeProvider`, and it is faked with `FakeTimeProvider`

Code that needs the current time, a timer, or a delay takes a `TimeProvider`, defaulting to
`TimeProvider.System` or to `HighResolutionTimeProvider.Preferred` where the frame rate depends on
it (ADR-0067). Tests pass `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`,
which every test project now references.

A test does not hand-roll a `TimeProvider` that advances. The ordering rules for due timers are
easy to get wrong in ways that do not show until a second timer exists. A double that only
*records* a request — a frozen clock whose timers never fire, used to assert the interval a caller
asked for — is fine, because it simulates nothing.

`CancelAfter` on an existing `CancellationTokenSource` always uses the platform timer queue,
whatever provider the surrounding code holds. A timeout that has to be fakeable is
`new CancellationTokenSource(delay, timeProvider)`, linked in.

### 2. Media time is `IClockSource`, and it is a different axis

`FrameFlow.Graph.IClockSource` is a position on the presentation timeline. It seeks, pauses, and
is mastered by the audio device when there is one. `TimeProvider` cannot express any of that, and
neither substitutes for the other. A type can need both — `ClockSelectVideoSink` paces against an
`IClockSource` and arms its caps on a `TimeProvider` — and that is two dependencies, not one
done twice.

### 3. A test about a background worker waits on a signal from the worker

When the code under test does its work on another task, the test does not guess how long that
work takes. The worker exposes an observable point meaning "I have consumed everything available
and am waiting", and the test waits for it.

`ClockSelectVideoSink` is the reference. It increments a park generation each time its delivery
loop suspends on one of its waits. A test captures the generation, acts, and waits for a higher
one — edge triggered, because the loop was probably already parked when the test called.

Two properties matter, and both were found by getting them wrong first:

- **Only the worker's own waits count.** A producer blocked on backpressure is a different thread;
  counting it lets a blocked producer satisfy a question about delivery.
- **A park is published only for a wait that suspends.** A wait that completes synchronously is
  the worker carrying on, and publishing a park for it releases a waiter mid-iteration.

The signal only answers for actions that wake the worker. An action that changes state the worker
will read on its next pass without asking for one — pausing a loop parked for want of input,
releasing a hold scoped to a superseded run — never moves the generation. For those, park the
worker first with an action that does wake it, then act, then assert synchronously: a parked
worker cannot do anything until woken. Where the property under test is synchronous state, expose
it and assert it directly. `IsSettleHeld` replaced a 120 ms wait to see whether a frame came out.

### 4. Assert the choice, not its consequence

When a behaviour follows from a decision the code makes, pin the decision. #148 measured playback
speed through a runner's scheduler to establish that `WallClockSource` had selected the
high-resolution provider. The selection is one assignment, the same on every platform, and
observable without a clock; asserting it fails deterministically on the regression it exists to
catch, where the timed version could only fail on some runs of one leg. What the selection causes
is then asserted where it is defined — `HighResolutionTimeProviderTests` for the provider's speed —
and the arithmetic in between is asserted against a frozen provider.

### 5. Health gates assert counts and conservation

A test asserting that a run was healthy does not bound a value derived from elapsed time.
`VideoPresentationLag` is out, and so is `VideoFramesDroppedForSync == 0`: a runner descheduled for
longer than a frame interval makes the clock pass a queued frame and the pipeline correctly counts
a drop, on a tree with nothing wrong in it.

What holds at any speed is accounting. Every frame in the file is decoded, shed, or dropped to GOP
resync. Every decoded frame is presented or dropped for sync. A decode shortfall has a counter
against it. #134 was every counter correct and nothing comparing them; conservation is the check
that would have caught it. Where a floor is needed as well — a run that dropped everything for sync
satisfies conservation exactly — it sits far enough from both sides that a slow runner does not
reach it.

### 6. Two places may read the wall clock, and nothing else may

- **`FrameFlow.Integration.Tests`.** It drives real FFmpeg decode through the playback stack and
  asserts real durations: that a 3 s clip plays in about 3 s, that a loop restarts on time. Elapsed
  time is what it measures. It is exempt permanently.
- **A type whose defining property is timing.** `HighResolutionTimeProviderTests` measures that a
  16.67 ms delay costs roughly 16.67 ms, because that is what the type is for and no wiring
  assertion can stand in for it.

Everything else follows rules 1 to 5. A measurement that is wanted as evidence rather than as a
gate belongs behind an explicit opt-in, as `FRAMEFLOW_VISUAL_TESTS` already does for tests that
open real windows.

### 7. The build enforces it

`Microsoft.CodeAnalysis.BannedApiAnalyzers` runs in every project under `tests/`, wired from
`tests/Directory.Build.targets` in the same shape as the public-API baseline in `src/`. `RS0030` is
an error. `tests/BannedSymbols.txt` bans:

- `Thread.Sleep`
- the `Task.Delay` overloads that do **not** take a `TimeProvider`
- `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`
- `new Stopwatch()`, `Stopwatch.StartNew`, `Stopwatch.GetTimestamp`, `Stopwatch.GetElapsedTime`

Each entry's message names the replacement and cites this record, so the error explains itself.

#### What the ratchet deliberately leaves alone: timeouts that bound a failure

`Task.WaitAsync(TimeSpan)`, `new CancellationTokenSource(TimeSpan)` and `CancelAfter` are not
banned, and that is a decision rather than a gap. Tests use them overwhelmingly as a safety net —
`new CancellationTokenSource(TimeSpan.FromSeconds(30))` around a playback run, `WaitAsync(5 s)` on
a task that should complete in milliseconds. Those do not make a passing test depend on how fast
the machine is. A correct run finishes far inside them, and they only expire when the test has
already failed, where they turn a hang into a named failure instead of a stuck CI job. Banning
them would break correct tests in projects that are otherwise clean and make every real failure
worse.

The line is whether the duration **bounds** a failure or **is** the assertion:

> Would the test still be correct if this timeout were ten times longer?

If yes, it is a safety net and it stays. If no — the test only passes because the time runs out —
it is a sleep in disguise and falls under rules 3 to 5. The common disguise is proving a negative
through a timeout: `await Assert.ThrowsAsync<TimeoutException>(() => task.WaitAsync(100 ms))` to
show something did not complete. That is `Task.Delay(100); Assert.False(task.IsCompleted)` with
different spelling, and it is not allowed.

The analyzer cannot tell those two apart, because they are the same call. So this boundary is held
by review and by this record rather than by the build. That is the one part of the policy the
ratchet does not enforce, and it is named here so nobody concludes from a green build that it was
checked.

A project opts out by setting `FrameFlowBanWallClockInTests` to `false` in its own `.csproj`, with a
comment saying why. The opt-out list is the six projects that had violations when this landed.
It only shrinks. A project comes off when its last violation is gone, and a new test project starts
on the right side of the ban without anyone having to decide to put it there — which is the whole
point, because the previous state of the tree was reached by nobody deciding anything.

## Consequences

### Positive

- A new wall-clock dependency in a test fails the build with a message naming what to use instead,
  rather than surfacing months later as a flake on one CI leg.
- Every exception is a line in a `.csproj` with its reason next to it, and the set of exceptions is
  a grep.
- `ClockSelectVideoSinkTests` runs in 0.7 s where it took 3 s, with no sleeps, and was run 25
  consecutive times clean.
- Hand-rolled time doubles cannot quietly reappear, since the replacement is already referenced by
  every test project.

### Negative

- Five projects are opted out temporarily and will stay that way until someone does the work. The
  analyzer holds the line; it does not move it.
- The park signal is internal API on a production type, and adding one to the next background
  worker is work rather than a helper call. It is also the only approach found that makes a
  negative assertion about a worker mean anything.
- `HighResolutionTimeProviderTests` is still a wall-clock median assertion on a hosted runner — the
  same hazard #148 was filed about. It is permitted under rule 6 because it is the type's defining
  property. If it starts failing on CI it moves behind an opt-in, not into a retry.

## Alternatives considered

### Retry flaky tests

Rejected. A retry makes a flake invisible without making it stop, and the class of defect that
looks exactly like a flake — a race in the code under test — is the one a retry hides best.

### Write the policy down and rely on review

Rejected as sufficient, kept as necessary. This record is the policy. But the state it describes
was reached through tests that each looked reasonable to whoever wrote and reviewed them, so review
had already been tried, by default, and had produced #78 and #148.

### A deterministic scheduler

Taking over the task scheduler, as Coyote does, would remove the progress dependency without a park
signal and explore interleavings a hand-written test never reaches. It is issue #143 and it is
complementary rather than a replacement: it explores schedules of code that is already
deterministic about time, and it needs assertions that fail when an invariant breaks. Rules 1 to 5
are what make both of those true.
