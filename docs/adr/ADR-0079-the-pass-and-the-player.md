# ADR-0079: The pass and the player: a clock decides the entry

## Status

Accepted (2026-09-17). Proposed the same day; numbered and accepted once the implementation
landed. **Implemented**; *As implemented* says how.

This record decides that the unpaced runtime gets its own entry point rather than a second
terminal on the player's builder, what it is called, that it takes one source and no queue, and
what that lets the builder surface drop.

It supersedes **[One builder, two terminals](one-builder-two-terminals.md)**, whose decision was
that one chain ends in either a `MediaPass` or a player. The reasoning that record gives for
folding `MediaPlayer.CreateAsync`'s options into a fluent chain stands. What changes is where the
two terminals live.

Related: [ADR-0077](ADR-0077-one-player-type.md),
[ADR-0044](ADR-0044-sink-ownership-and-disposal.md),
[ADR-0032](ADR-0032-pull-shape-playback-controller.md),
[ADR-0072](ADR-0072-tests-do-not-depend-on-elapsed-time.md).
Issues #99, #125.

## Context

### The two terminals do not differ on what their names say

`IPlayerBuilder` offers two terminals. `BuildPlayerAsync` returns a player: pause, resume, seek,
repeat, position, diagnostics, and the queue. `BuildAsync` returns a `MediaPass`, which the
README describes as "open a file and play it to the end".

That reads as a difference of transport surface. It is not the difference that matters.

`MediaPass.RunToCompletionAsync` builds `source → configurator → sink` and runs it. It holds
no `IPlaybackClock`. `ClockSelectVideoSink` and `PaceUntil`, the two types that hold a frame until
its presentation time, are constructed only by `SubstrateSession`, on the controller path. A
`MediaPass` never touches either.

So video through `BuildAsync` is presented as fast as the sink accepts it. A caller who reaches for
the terminal the README points them at, because they only want to play a file through once, gets a
file that flashes past. Nothing in the name, the return type or the documentation says so.

Both in-repo users are audio-only. `FrameFlow.Examples.AudioOnlyPlayer` and
`FrameFlow.Examples.HostedServicePlayer` attach an `OpenAlAudioSink` and no video sink, and an
audio device applies backpressure at its own rate. The timing is supplied by the device rather than
by the pipeline, which is why the gap has not shown up.

### Why `MediaPass` exists, and why the reason is spent

Its own summary says it:

> The full pause/resume/seek/repeat surface stays on `IPlaybackController` until its port to the
> substrate lands.

That port landed in ADR-0077. `PlaybackControllerCore` drives `PlaylistSession` over
`SubstrateSession`, which is the substrate. The reason `MediaPass` was written is answered.

The runtime it provides is not. An unpaced run is worth having on its own terms, and ADR-0032 §
*Deferred* asked for one: "a raw/unpaced accessor variant". `MediaPass` supplies that variant
by accident, under a name that suggests the opposite.

### An analysis pass wants decode speed

A consumer opening a file, running every frame through an inference chain and closing it does not
want to wait real time. Today the shape is:

```csharp
await using var sink = new HeadlessVideoSink();
await using var session = await FrameFlowPass.Create(path)
    .WithVideoSink(sink)
    .ConfigureVideo(chain => chain.Then(detect))
    .BuildAsync();

await session.RunToCompletionAsync(ct);
```

This is the right runtime and reads as the wrong one. Every word in it — `Player`, `BuildAsync`,
`PlayToCompletion` — is about playing.

### A third surface does the same job

`PlaybackGraph` is public, wires caller-supplied decoders to sinks, and runs to EOS. Its own
summary calls it a "lean Phase-3 sibling" and says the "full-controller port can build on" it. That
port landed. It had no users outside its own tests, and #271 removed it. This record records the
removal as part of the map and does not repeat its argument.

### What the last change left behind

[Breaking change 24](../BREAKING-CHANGES.md) moved media from `FrameFlowPlayer.Create` to
`WithMedia`, because a player is a queue and a queue can be empty. One entry serving both terminals
then means media arrives after the entry, so `Create().BuildAsync()` compiles and throws at run
time. The one-builder record closed exactly this class of hole by narrowing, and this one reopened
it.

## Decision

### 1. Two entries, a clock apart

`FrameFlowPlayer.Create()` builds a player. A second entry builds the unpaced runtime. Which one a
consumer gets is chosen at the entry, in the first call they write, not by which terminal they
reach for at the end of a chain.

```csharp
// Paced. Honours a clock, and a sink sees a frame at its presentation time.
await using var player = await FrameFlowPlayer.Create().WithMedia(path)
    .WithVideoSink(view)
    .BuildPlayerAsync();

// Unpaced. Runs the content through once, as fast as the sinks accept it.
await using var pass = await FrameFlowPass.Create(path)
    .WithVideoSink(sink)
    .ConfigureVideo(chain => chain.Then(detect))
    .BuildAsync();

await pass.RunToCompletionAsync(ct);
```

The type confusion the split removes is the smaller half. `MediaPass` carries only `Info` and
`RunToCompletionAsync`, so a caller who wanted a player fails to compile on their next line. What
the split removes is the silent half: a consumer no longer picks the unpaced runtime by reaching
for the terminal whose name sounded simpler.

### 2. `MediaPass`, and `RunToCompletionAsync`

`MediaPass` is renamed `MediaPass` and `RunToCompletionAsync` becomes
`RunToCompletionAsync`. The runtime is unchanged.

A pass is one traversal of the content. The word claims that and claims nothing about rate, which
is what the name has to do: the distinction this record draws is pacing, and a name that says
"play" asserts the opposite. `RunToCompletionAsync` drops the same implication from the method.

`Pass` beats `Processor`, which undersells the audio case — both current users play an audio file
to a device at device rate, and that is playback. It beats `Graph`, which is the substrate's word
(`FrameFlow.Graph`, `Graph`, `GraphChain`, `GraphTopology`) and is part of why `PlaybackGraph`
reads as a layering mistake.

### 3. The pass takes one source, at its entry, and has no queue

`FrameFlowPass.Create` takes a path or an `IMediaSource`. There is no plural overload and no
`WithMedia`.

**No queue.** The thing worth reusing across files is the sink. An inference sink holds a loaded
model, and under ADR-0044 the caller owns it and the pass does not dispose it, so one sink serves
any number of passes and the model stays resident. What a queue would add is reuse of the graph
between items, which is a construction cost of milliseconds against a file that takes seconds to
decode. The playlist's warm presenter exists for a boundary a viewer sees (ADR-0062); a pass has no
viewer and no gapless requirement.

A queue would also mean per-item runtimes, which is `PlaylistSession` composing `SubstrateSession`.
Rebuilding that on the pass side to save a graph construction is the wrong trade, and it is the
machinery ADR-0077 spent five changes unifying.

**Media at the entry.** A pass without a source has nothing to do, so the argument is required, and
a required argument belongs in the call that cannot be skipped. That closes the run-time hole
breaking change 24 opened: there is no `Create()` on the pass that can reach a terminal with
nothing to open.

The player keeps `WithMedia`, and keeps accepting none, because a player with an empty queue is a
thing a host wants (ADR-0077, amended).

**A pass never reads a clock, and a seam proves it.** That is the decision: no presentation time
is waited on, and nothing in the pass consults one. The builder carries no clock option, because a
consumer has nothing to supply.

An internal seam accepts a clock all the same, and exists for one caller: a test that hands the
pass a clock which throws on every read, and asserts the run completes anyway. The seam is how the
decision is falsifiable, not an exception to it — a pass that used what it was given would fail the
test that the seam exists to make possible. `MediaPlayer.CreateCoreAsync` already keeps a clock off
the public surface this way, there for the fluent builder rather than for a test. See
*Validation*.

**At least one sink.** A pass with no sink has nowhere to put what it decodes, so the terminal
refuses it and names the call that is missing. This is what `RunToCompletionAsync` already does
("No sinks attached"), moved to the terminal so the refusal arrives before the demuxer opens the
file rather than after. It stays a run-time check: video-only and audio-only sources each need a
different one of the two sinks, and which streams a file carries is not known until it is opened.
The narrowing that makes the player's options a compile-time matter cannot reach this.

### 4. The player's builder loses its narrowing

`IPlayerBuilder` and `IMediaPlayerBuilder` declare the same thirteen options twice over. The second
exists so that a chain which has set a player-only option — repeat mode, an injected clock,
hardware-frame yield, audio activation — can no longer reach `BuildAsync`, making a meaningless
combination a compile error instead of a dropped setting. That is the one-builder record's central
mechanism.

With two entries there is no chain that can reach the wrong terminal. The player's builder has one
terminal and every option means something to it. The two interfaces collapse into one.

The pass's builder is its own interface and carries what a pass can honour: the sinks, the two
configurators, hardware-decode policy, and the logger.

So the option declarations stay at two copies, as they are today, and the reason for the second
copy changes from "these four are meaningless here" to "this is a different runtime". The count is
the same. The story is one a reader can hold.

### 5. `PlaybackGraph` is gone

Recorded here because this record's map of the construction surface would be wrong without it. The
deletion landed in #271 while this record was being written, and its reasoning is there.

## As implemented

- **The pass.** `FrameFlowPass.Create(path)` and `Create(IMediaSource)` return `IPassBuilder`,
  whose options are the two sinks, the two configurators, hardware-decode policy and the logger,
  and whose one terminal is `BuildAsync`. `PassBuilder` holds the demux and decoder construction
  that `PlayerBuilder` used to, and `PlayerSession` is `MediaPass` with
  `RunToCompletionAsync`.
- **The sink rule.** `PassBuilder.RequireASink` refuses at the terminal, before the bootstrap and
  the open. `MediaPass.RunToCompletionAsync` keeps the same check, now unreachable through the
  public path because only `PassBuilder` constructs a pass; it stays as a guard on the internal
  constructor.
- **The clock seam.** `PassBuilder.WithClock` is internal and `MediaPass.Clock` holds what it is
  given. Nothing reads it.
  `MediaPassIntegrationTests.APass_NeverReadsTheClockItIsGiven` hands a pass a clock that throws
  on every member and asserts the run presents frames and completes. With
  `Clock?.Start(TimeSpan.Zero)` added to the run, it fails with
  "A pass read the clock (Start)".
- **The fold.** `IMediaPlayerBuilder` is deleted and every `IPlayerBuilder` option returns
  `IPlayerBuilder`. `WithOpenAlAudio` and `WithAvaloniaVideoView` keep two overloads, the second
  now on `IPassBuilder`.
- **`PlaybackGraph`.** Removed in #271, before this landed.

Breaking changes 25, 26 and 27.

## What this does not decide

- **Whether a pass yields hardware frames.** `WithHardwareFrames` is a player-only option today and
  `MediaPass` has no equivalent. A GPU inference sink is exactly the consumer that wants
  GPU-resident frames, so the answer is probably yes, but it is a change to the pass's runtime
  rather than a rename and it needs its own pass over `SubstrateSession`'s yield path. Deferred.
- **Whether `MediaPlayer` survives.** After #269 it is a strict subset of the builder. Deleting it
  is a separate change with its own migration, and it is orthogonal to this split.
- **Whether a pass later gets a queue.** If batch-over-a-warm-graph turns out to be wanted, it
  arrives as a loop helper or a queue on the pass. It does not become a second reason for the
  split.

## Migration

| Before | After |
|---|---|
| `FrameFlowPass.Create(path)….BuildAsync()` | `FrameFlowPass.Create(path)….BuildAsync()` |
| `MediaPass` | `MediaPass` |
| `MediaPass.RunToCompletionAsync(ct)` | `MediaPass.RunToCompletionAsync(ct)` |
| `IMediaPlayerBuilder` | `IPlayerBuilder`, which no longer narrows |
| `PlaybackGraph` | removed in #271 |

`FrameFlowPlayer.Create()` and `BuildPlayerAsync` are unchanged. A consumer building a player edits
nothing.

Every break is a compile error. The one that is not is a consumer who was using `BuildAsync` for
video and did not know it was unpaced: their code stops compiling, they read the new name, and they
find out. That is the point of the change.

## Validation

Each row names a test that has to fail on today's tree, for the stated reason, before the change is
claimed to work.

| # | Decision | Test | Fails today with |
|---|---|---|---|
| 1 | 3 | `FrameFlowPass.Create(path)` with no sink is refused at the terminal, naming the call that is missing | no such type |
| 2 | 1, 2, 3 | A pass given, through its internal seam, a clock that throws on every read presents every frame of a clip and completes | a paced implementation reads the clock to schedule the first frame, so the run fails with that exception; the assertions are on frames and completion |
| 3 | 3 | The pass builder has no `WithMedia` and no plural entry: a queue of two is not expressible | no such type |
| 4 | 3 | Two passes over the same caller-owned sink both run, and the sink is not disposed between them | nothing: this is carried-over coverage, not a new gate. `MediaPassIntegrationTests` holds it today and the test moves with the rename |
| 5 | 4 | A player chain sets every player-only option and still reaches `BuildPlayerAsync` on one interface | passes today through `IMediaPlayerBuilder`; the test pins that the fold kept it |
| 6 | 4 | `IMediaPlayerBuilder` is gone from `PublicAPI.Unshipped.txt` | the analyser is the test |

Rows 1 to 3 and 6 are gates: each has to fail on today's tree for the reason given. Rows 4 and 5
are not, and say so — they carry existing coverage across the rename and the interface fold, and
they pass before and after. A record that listed them as gates would be claiming evidence it does
not have.

Row 2 is the one that needs care, and two drafts of it were wrong. "Runs at decode speed" is a
claim about elapsed time, which ADR-0072 bans from the suite. A test that counted frames and
declared victory would pass on a paced implementation that happened to finish. A test that froze a
clock and waited for a timeout would be an elapsed-time test wearing a disguise, and it would hang
the suite on a regression.

What is left is to deny the implementation a usable clock and assert it does not miss one. The
pass is handed a clock that throws on every read, through the seam decision 3 records. An
implementation routed through `SubstrateSession` reads it to schedule the first frame and fails
with that exception, immediately and on any machine. Nothing waits, and nothing asserts a duration.

The rate itself belongs in an investigation with a recorded measurement, next to the soak in
[docs/investigations/2026-09-15-single-source-as-a-queue-of-one.md](../investigations/2026-09-15-single-source-as-a-queue-of-one.md).

## Alternatives

### A. Keep two terminals, rename the second

`BuildUnpacedAsync`, or a `.Unpaced()` step that narrows to a builder whose only terminal is the
pass. Cheaper: no new entry point, and the existing narrowing carries it.

Rejected. `.Unpaced()` adds a third builder interface to remove a footgun from a second, and the
consumer still starts every chain with `FrameFlowPlayer`, which is the word that misleads. The
choice is made at the end of the chain either way, which is where a reader has already stopped
thinking about which runtime they asked for.

### B. Delete `MediaPass` and give the player an unpaced mode

One entry, one type, pacing as an option: `FrameFlowPlayer.Create().Unpaced()…BuildPlayerAsync()`
returning a player whose clock is a no-op.

Rejected for now, and it is the shape to revisit. A player with no clock still carries seek,
repeat, position and a queue, and what those mean without a clock is a design question per member,
not a flag. It is a larger change than this record, and the split does not block it: if the answer
later is one runtime, `MediaPass` folds into the player and the second entry goes.

### C. Leave it

The runtime is correct and only the names mislead, so document the pacing and move on.

Rejected. The README row, the terminal name and the return type all say playback, and the
documentation fix is a sentence a reader has to find before they reach for the terminal that
sounds right. That three surfaces grew to do this job, `MediaPass`, `PlaybackGraph` and the raw
graph, is the evidence that a reader cannot currently tell which one they want.
