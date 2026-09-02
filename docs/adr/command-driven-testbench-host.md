# ADR-XXXX: A command-driven test-bench host, separate from the examples

## Status

Proposed (2026-08-31). Draft pending number assignment.

Supersedes nothing. It narrows the scope of `examples/`, adds an instrument
alongside the five testing layers in
[ADR-0007](ADR-0007-testing-and-validation-strategy.md) without becoming one of
them, and gives the diagnostics surfaces in
[ADR-0034](ADR-0034-diagnostics-surfaces.md) their first dedicated consumer.

## Context

### The examples are doing two jobs

`examples/` exists to show the minimum wiring for an API. That is not the only
thing the examples are being used for, and the code records it.

`examples/FrameFlow.Examples.AvaloniaPlayer/Properties/launchSettings.json`
carries a profile named `Repro-Signage-NoAudio-GPU`, pinned to
`tests/corpus/files/test-1080p-h264-aac.mp4` with `--presenter gpu --no-audio`.
That is a saved reproduction, stored in an example's IDE configuration because
there is nowhere else to put it.

It is also wrong, and the storage is why. `test-1080p-h264-aac.mp4` is
1920x1080 at 30 fps for 3.0 seconds
([generate-test-corpus.cs:274](../../scripts/generate-test-corpus.cs)). The
signage symptom is choppiness that builds over a long unattended run. The
profile plays three seconds of `testsrc2` once and stops, so it cannot show the
thing it is named after. Nothing about a `commandLineArgs` string invites the
question of whether the fixture is long enough; writing the same run as a script
with a rate assertion over a named window surfaced it immediately.

A census of the argument surface across the eleven runnable example projects
(`FrameFlow.Examples.Common` is a shared library with no entry point):

| Flag | Projects parsing it | What it is for |
| --- | --- | --- |
| `--log-file` | 11 of 11 | capture output to a file, and on `WinExe` the only way to see it |
| `--exit-after` | 7 | self-terminate so an unattended run ends |
| `--hw-mode` | 2 | A/B hardware decode selection |
| `--break-yolo` | 3 | fault injection |
| `--presenter`, `--gpu` | 3 | A/B presenter selection |
| `--left-clock`, `--right-clock` | 1 (DualPlayer) | A/B clock mastering |

`--break-yolo` skips `Yolov8Detector.CreateAsync` so a broken pane can be
observed not to take down its neighbours. `--left-clock` / `--right-clock` runs
two players side by side under different clock masters. Neither demonstrates an
API to a newcomer. Both are diagnostic rigs living in demonstration code.

`TextBoxLoggerProvider.cs` exists three times, copied between AvaloniaPlayer,
DualPlayer, and LiveCaptioning, because each example needed its own log window.

### The current workflow is open-loop

`examples/FrameFlow.Examples.Camera.Multicast/App.axaml.cs:41` states the method
in a comment: `--auto-pick` "pairs with `--exit-after` for autonomous diagnostic
runs: the agent launches the app, lets it capture frames into the log for N
seconds, then has it self-terminate."

Launch, wait, read the log. Every question costs a full cycle, and any question
the existing flags do not answer costs a code change first. There is no way to
seek and then ask what the counters did, no way to change one variable while the
pipeline stays up, and no way for a run to fail. A log is read by a human, so
nothing regresses loudly.

### Where it hurts most is cross-platform

`.github/workflows/build.yml` is manual dispatch over a windows / linux / macos
matrix, and it builds and runs unit tests. The code that breaks per platform is
the code unit tests cannot reach: `CompositionInteropVideoView`,
`D3D11Nv12SharedConverter`, `OpenAlAudioSink`. Those three also sit at the top
of the repository's bug-fix history.

Diagnosing them today means a person sits at the machine and clicks. There is no
way to run the same sequence of operations on Windows and on Linux and compare
what the counters say.

### There is a working precedent in the workspace

`popcorn` is a sibling application that consumes FrameFlow as a package. Its GUI
holds the window and the player and listens on a named pipe; a separate CLI
binary sends one JSON line and prints the reply. It has driven a running
FrameFlow player from a terminal since 2026-08-27.

Two parts of it are relevant here, and only one of them is the transport.

`Popcorn.Gui/Services/PlaybackProbe.cs` polls `IMediaPlayer.PollDiagnostics()`
once a second and turns counter deltas into sentences that separate a bad file
from a struggling presenter. `DecodeErrors` rising means a corrupt packet or a
hardware-transfer failure. `PacketsDroppedForBackpressure` rising means the
player is shedding compressed video, which presents as a freeze on the last good
frame. `VideoFramesDroppedForSync` rising means frames were discarded to keep
A/V lock. `VideoSink.FramesDropped` rising means the render thread lagged.

That mapping is knowledge about FrameFlow's own counters, derived from ADR-0034
and the decoder snapshot documentation, and it currently lives in a downstream
application.

## Decision

Add `tools/FrameFlow.TestBench`: a console host that builds a real player from
the public API, reads commands from stdin, and writes replies and log lines to
stdout.

### Decision 1: one process with a stdin loop, not a pipe and a second binary

Popcorn splits into two processes because its GUI is the product and its CLI is
an operator tool reaching a window that is already running. The bench has no
such constraint. It starts when the session starts and ends when it ends.

A single process gives one interleaved stream: the command, the reply, and every
log line the pipeline emitted in between, in the order they happened. That
ordering is the artifact worth pasting into an issue. Two processes writing two
logs cannot produce it without clock correlation.

`--script <file>` runs a command file non-interactively and sets the exit code
from the first failed assertion, so a bench session can run in CI or over SSH.
(What a script *is* is under challenge — see the proposed revision at the head of
Decision 6. If assertions move to C#, `--script` reduces to reading commands from
a file instead of the console, and the exit code comes from the C# repro rather
than from the bench.)

The pipe is not ruled out. If driving a long-lived window from a second terminal
turns out to matter, popcorn's `PipeAddress` and line protocol are roughly 120
lines to add behind a flag. Nothing here blocks that.

### Decision 2: console subsystem, not `WinExe`

Every Avalonia example is `WinExe`, which on Windows means no console. That
explains most of the `--log-file` parsers but not all of them: `AudioOnlyPlayer`,
`HostedServicePlayer`, and `SdlPlayer` are already `<OutputType>Exe</OutputType>`
and hand-roll the parser anyway, because a log on disk is wanted for reasons
unrelated to the subsystem. Console output is still the right default, and it is
what makes an interleaved command/reply/log stream possible at all — it just
buys less example cleanup than "eleven of twelve" implies. The bench sets
`<OutputType>Exe</OutputType>` and writes to stdout. A window still opens when a
presenter needs one; the console is attached alongside it.

`--log-file` stays available for a copy on disk. It stops being the only way to
see anything.

### Decision 3: the bench uses the public API and nothing else

No `InternalsVisibleTo`, no project reference reaching past the packaged
surface. Anything the bench needs that the public API does not offer is a gap in
the surface defined by
[ADR-0024](ADR-0024-playback-controller-as-public-api-surface.md) and
[ADR-0027](ADR-0027-public-api-surface-cleanup.md), to be filed as one rather
than worked around locally.

The bench then doubles as a standing ergonomics check. It breaks when the public
surface breaks, which is the intended behaviour.

#### Which public type, and why it is not `IMediaPlayer`

The bench builds on `IPlaybackController`, plus the audio sink's
`IVolumeControl` for the `volume` command. Not `IMediaPlayer`, despite the
citation in the Context section — and this needs saying, because the obvious
reading of "builds a real player" is the wrong one.

`IMediaPlayer` has no load. Its source is fixed at construction by
`MediaPlayer.CreateAsync`, which fuses the source with the graph configuration
(`hardwareDecodeMode`, `yieldHardwareFrames`, `configureVideo`, `configureAudio`).
That is correct rather than an oversight: a load on that facade would have to
re-answer all of them per call, since the next source might be audio-only or need
a frame memory domain the sink was not built for. Load-as-a-command lives one
level down, on `IPlaybackController`, which is where ADR-0024 put it precisely
because the state machine outlives the demuxer.

Neither type alone covers Decision 6's command set: `IPlaybackController` has
`LoadAsync`/`UnloadAsync` and no volume; `IMediaPlayer` has volume and no load.
The bench composes both, which is what `MediaPlayer` does internally when it
type-tests the audio sink for `IVolumeControl`.

**This is a semantic choice, not just a plumbing one.** `load` has three possible
implementations with different diagnostics:

| Implementation | What resets |
| --- | --- |
| `IPlaybackController.LoadAsync` | the session; sink counters keep climbing |
| dispose + `MediaPlayer.CreateAsync` | everything, sink included |
| `IMediaPlaylistPlayer.SetNextAsync` + `SkipToNextAsync` | decode source only; warm presenter |

Decision 5's premise — sink counters "owned by the consumer's long-lived sink,
keep climbing" — holds for the first and third and is false for the second. The
bench's `load` is the first. A future `next` command would be the third, and
would need its own note here.

### Decision 4: `--headless` runs with no window

`NullVideoSink` already ships in `src/FrameFlow.Media/NullVideoSink.cs`, so the
sink half is free. Headless mode lets the same script run over SSH and on a
machine with no display.

`NullVideoSink` disposes each frame on arrival and counts nothing, so a headless
run as it stands measures demux, decode, and clock, and says nothing about
presentation. The bench needs a counting sink with an optional synthetic present
cost, or headless numbers will be optimistically wrong in the one direction that
matters. That sink is part of this work, not a follow-up.

**Landed** as `HeadlessVideoSink` in
[`src/FrameFlow.Media/HeadlessVideoSink.cs`](../../src/FrameFlow.Media/HeadlessVideoSink.cs),
beside `NullVideoSink` rather than inside the bench — see *Not settled here*,
which this resolves. Three details are load-bearing and easy to get wrong:

- **The frame is held for the whole cost**, not disposed first. The pool slot
  stays occupied for the duration, so the cost propagates back through the frame
  pool as real backpressure. Disposing first and then sleeping bills wall-clock
  time while the decoder runs unimpeded, which measures nothing.
- **The wait is high-resolution.** On the system timer a 5 ms synthetic cost
  bills ~15.6 ms ([ADR-0067](ADR-0067-high-resolution-pacing-timers.md)), which
  trades optimistically wrong for pessimistically wrong.
- **The pool is required, not created by the sink.** A bounded pool is what
  makes the decoder block when frames are in flight; `NullVideoSink`'s unbounded
  one is a second way a headless run comes back faster than the machine can go.

It never reports a drop of its own: with no render tick there is nothing to fall
behind, so loss under a heavy synthetic cost appears upstream as `sync.dropped`
rather than `sink.dropped`, in one of two places depending on where the pressure
lands. A bounded pool blocks the decoder once frames are in flight, which fills
the channel and shows up as `video.shed`; frames that do get through and arrive
late are discarded by the pacing chain as `sync.dropped`. A heavy cost can move
either counter, and a headless script watching for loss has to watch both.

`sink.dropped` itself stays at zero throughout, so any assertion that it stayed
at zero passes regardless of how badly the run went — the windowed form Decision 6
requires for a `count`, and the absolute inequalities it also permits. Frames abandoned at shutdown or to cancellation are on the sink's
own `AbandonedCount`, deliberately outside `sink.dropped` so that metric keeps
its "the render path is the bottleneck" meaning.

### Decision 5: the diagnostics interpretation moves into the library

Popcorn's delta-to-sentence mapping moves next to the ADR-0034 snapshot types in
`FrameFlow.Playback.Diagnostics`, as a function from two snapshots to a list of
observations. The bench formats what the library interprets, and so does every
other consumer.

Reading `PollDiagnostics()` correctly requires knowing which counter blames the
file and which blames the presenter. Shipping the snapshot without shipping that
knowledge means each consumer rediscovers it. Popcorn is the evidence that they
do.

#### A snapshot pair has to be known-comparable

The interpretation is a function of two snapshots, and two snapshots are not
always subtractable. `load` builds a fresh session
([`PlaybackControllerCore.cs:809`](../../src/FrameFlow.Playback/PlaybackControllerCore.cs)),
so demux and decoder counters restart at zero while sink counters, owned by the
consumer's long-lived sink, keep climbing. Any poll straddling a `load` is
comparing two different sessions on half its fields.

Invalidating the bench's named marks does not cover this. A 1 s probe poll has
no marks, and neither does any other consumer of the interpretation function.

So `PlaybackDiagnosticsSnapshot` carries a session generation, incremented on
each `CreateSession`, and the interpretation function returns an explicit
`Reset` result for a pair whose generations differ rather than a list of
observations.

Neither available shortcut is acceptable:

- **Subtracting anyway** produces negative deltas, and reports a session restart
  as an error or drop burst.
- **Only reporting increases**, which is what popcorn's `ReportDeltas` does
  today, avoids the false alarm by accident and buys a false negative: after a
  `load` the new session's counters climb from zero back toward the old
  session's values, and every genuine error in that first interval is silently
  swallowed until the count passes the previous session's high-water mark.

Making the reset a value the caller has to handle is what stops both. It also
gives the bench the error text for `since` across a `load`, so the rule under
Decision 6 and the library's behaviour are the same rule rather than two.

**Landed.** `PlaybackDiagnosticsSnapshot.SessionGeneration` carries the
generation, and `DiagnosticsInterpreter.Compare` in
[`src/FrameFlow.Playback/Diagnostics`](../../src/FrameFlow.Playback/Diagnostics)
returns either observations or an explicit reset.

Three notes for whoever writes the bench against it. The generation advances on
**both** session boundaries — creation and teardown — so a reload moves it twice
and a poll landing between the two is comparable to neither side. An earlier
revision advanced it only on creation, which meant a pair straddling an unload or
a fatal error compared as subtractable while every counter had silently gone to
zero.

The generation is published with the session as one reference rather than as a
separate counter: reading
them apart lets a `load` land between the two reads, and both interleavings
produce a snapshot whose counters and generation disagree, which defeats the
reset for the exact poll-straddles-load case it exists to catch.

And the observation set is positive deltas and rising edges only. "Nothing
decoded" and "nothing reached the screen" are absent because whether that is a
freeze or a normal gap between two fast polls depends on the interval, and the
*interpretation function* has no clock — it is documented as pure and total, and
should stay that way.

The bench is not so constrained, and an earlier revision of this section
overstated the limit. `VideoSinkDiagnosticsSnapshot` carries `LastPresentedAtUtc`
and `LastCommittedAtUtc`, both reachable on every poll and both missing from the
namespace table below. A bench that already timestamps its own marks for the `/s`
rule can compare either against now, which is a presenter-liveness assertion. Two
table rows would make presenter freezes assertable; they should be added.

For the loop the answer is simpler still: `loop.stalled` is on the snapshot and
carries its own timeout. `PresenterStallWatchdog` genuinely is unreachable — it is
a host-side component that raises events, and nothing it decides reaches
`PlaybackDiagnosticsSnapshot` — but that closes one route, not the question.

### Decision 6: the script language is declarative and has no control flow

> **PROPOSED REVISION — the script language should not exist.**
>
> Everything below this block describes a bespoke assertion language. This block
> argues it should be replaced by C#, and that the interactive command set should
> survive unchanged. The reasoning below is left in place because it is sound
> reasoning *about a language*; the question is whether the language earns its
> place. That is an open call, not a settled one.
>
> #### What splits apart
>
> The decision fuses three things that are separable:
>
> | | Verdict |
> | --- | --- |
> | A console host that keeps the pipeline warm and takes typed commands | Keep. Nothing else does this — a run config cannot seek-then-ask, and a test tears down between cases |
> | A file that drives a sequence non-interactively | Keep, and it is nearly free (see below) |
> | A declarative assertion grammar with kinds, windows, operators and gates | Drop |
>
> The transport commands — `load`, `play`, `pause`, `seek`, `volume`, `repeat`,
> `status`, `diag` — are typed by a human at a prompt. Words are right for that,
> and a file of those same words is stdin redirection rather than a language.
> `--script` becomes "read commands from this file instead of the console",
> which needs no grammar the REPL does not already have.
>
> The assertions are the part that needs a language, and they are the part C#
> already does.
>
> #### The evidence against the grammar
>
> **Every defect found in review has been in the language, not the pipeline.**
> Operators were never enumerated while a shipped script used `!=`. `count`
> admitted contradictory rules in two sections. Exit code 2 means both "did not
> parse" and "wrong invocation". Eight metrics read zero when their subsystem is
> absent, which required inventing a third parse-time constraint. None of these
> are problems with driving a player; all of them are problems with having built
> a language to do it.
>
> **The rejection of embedded C# does not cover the actual alternative.** The
> *Alternatives considered* section rejects "Lua, or C# scripting via Roslyn" on
> the grounds that the bench "inherits a language runtime, a packaging problem".
> True of Roslyn scripting. Not true of .NET file-based apps, where the script
> *is* a program and there is no host runtime to inherit.
>
> The repo already does this. `scripts/generate-test-corpus.cs` — cited by line
> number elsewhere in this ADR — opens with `#!/usr/bin/env dotnet` and
> `#:property TargetFramework=net10.0`, as do `fetch-cuda.cs` and
> `fetch-ffmpeg.cs`. The convention for "a runnable, reviewable, diffable file of
> logic" is established and it is C#.
>
> #### What the grammar's hard problems become
>
> | Grammar concept | In C# |
> | --- | --- |
> | The metric namespace | property access, with IDE completion |
> | Metric kinds constraining operators | the type system |
> | Nullable metrics (`drift`, `sink.last-pts`) | `TimeSpan?` |
> | Capability gating (`sink.committed`, `out.*`) | a nullable field, or an explicit check |
> | `mark` / `since` | a local holding a snapshot, and `DiagnosticsInterpreter.Compare` |
> | `require presenter gpu` | an `if` over the resolved sink, and `return 2` |
> | "Parse first, run second" | compilation, which catches strictly more |
>
> The capability problem is the sharpest case. It exists only because a dotted
> string cannot express "this metric may be unavailable". The nullable option was
> rejected because null-as-capability reads wrong *in the grammar*; in C# there is
> no ambiguity, because a nullable field is checked rather than compared.
>
> #### The argument for the grammar, and why it does not hold
>
> Non-Turing-complete is deliberate: it stops the bench "growing into a second
> test framework with its own debugger". A C# repro can grow logic, and that risk
> is real.
>
> It is a review norm, not a language property, and this ADR already relies on
> review norms for the same class of drift — "the line to hold is that it drives,
> observes, and asserts". A hand-rolled parser is an expensive way to enforce a
> convention that a reviewer enforces anyway.
>
> #### How a C# repro takes its dependency — settled by spike
>
> This was the one unknown: .NET file-based apps support `#:package`, but no
> script in `scripts/` had exercised the directive, so whether a single `.cs`
> file could consume FrameFlow *including its FFmpeg natives* was untested.
>
> `spikes/package-directive-repro.cs` tests exactly that and nothing else. It
> declares `#:package FrameFlow.Player` and `#:package FrameFlow.Native`,
> references no project, and is shaped like a repro would be — drive, observe,
> assert, exit non-zero. On Windows against the local feed:
>
> ```
> PASS  managed assemblies resolved from the package
> PASS  FFmpeg natives loaded — FFmpeg avutil 59.39.100 loaded from Bundled.
> PASS  video stream present — h264 320x240, 00:05.000
> PASS  seek moved the position — immediately 00:00.000, settled 00:01.000
> PASS  frames reached the sink — presented 61, abandoned 0
> ```
>
> `Bundled` is the decisive word: the loader resolved the package's own
> `runtimes/{rid}/native` payload rather than a system FFmpeg. The hardware
> probe ran and selected D3D11Va, `HeadlessVideoSink` counted frames, and
> `PollDiagnostics()` returned the full snapshot including `SeeksPerformed = 1`.
> A repro can therefore consume the packaged surface, which sharpens Decision
> 3's ergonomics check rather than weakening it.
>
> One finding that cuts the other way, and belongs here rather than buried.
> `Position` is clock-driven: immediately after `SeekAsync` returns it still
> reads `00:00.000`, and only settles to the target once the pipeline presents
> at the new point. A repro has to poll for it. That is precisely what the
> grammar's `wait` command exists to express, so `wait` is a real need and not
> an artefact of having built a language — whatever the repro is written in, the
> concept survives.
>
> #### What it collapses
>
> Adopting this closes, without further design: the operator table, the exit-code
> split, capability gating, the metric-kind rules, `set`, and what kind the
> wallclock fields get. Those are six of the open questions below and in this
> section.
>
> ---
>
> The superseded design follows.


Three groups of statement: the transport commands (`load`, `play`, `pause`,
`seek`, `volume`, `repeat`, `status`, `diag`), the assertions (`mark`, `wait`,
`expect`), and the header forms (`require`, `set`). No variables, no arithmetic,
no conditionals, no loops.

`repeat` is both a command and a metric. Statement position separates them: a
line beginning `repeat one` sets the mode, and `repeat` inside an `expect` or a
`wait` reads it.

A script that needs those is an integration test in C# and belongs in
`tests/FrameFlow.Integration.Tests`. Keeping the language non-Turing-complete is
what stops the bench from growing into a second test framework with its own
debugger.

#### `require`: the configuration a script cannot set itself

`--presenter gpu` and `--no-audio` choose which sinks get built. They are not
runtime commands, and a script cannot issue them: the surface is constructed
before the script is read, and Avalonia will not swap a composition-interop
presenter for a `WriteableBitmap` one underneath a live player.

So a reproduction is a pair — an invocation and a script — and the half that
lives on the command line is exactly the half that got lost when these were
launch profiles. `require presenter gpu` and `require audio off` are assertions
against the bench's own startup configuration. A script run under the wrong
invocation exits 2 without playing anything, rather than producing a green run
that measured the wrong pipeline.

**`require` checks the resolved configuration, not the flag string**, which means
it runs after startup rather than in the parse pass. The distinction is
load-bearing: `--presenter gpu` silently falls back to the CPU surface off
Windows and the flag still reads `gpu`, so a string check would let through
exactly the failure this rule exists to prevent. Evaluating it against the sink
the bench actually constructed still happens before the first command, so the
exit-2-without-playing contract is unchanged.

**Consequence for the shipped scripts.** Both `scripts/repro/*.bench` open with
`require presenter gpu`, and the zero-copy presenter is Windows-only, so under
this rule they exit 2 on Linux and macOS. That is correct, and an uncomfortable
fit with the cross-platform motivation in the Context section: the reproductions
most worth comparing across platforms are the ones that cannot be. A
cross-platform script has to skip `require presenter gpu` and confine itself to
metrics the software path populates. Nothing here fixes that — it is a limit of
what a GPU reproduction can mean on a machine with no GPU path.

#### The metric namespace

Every assertable value is a dotted path over the ADR-0034 snapshot tree.
`diag --all` prints the namespace with current values, so a name never has to be
guessed.

The Source column names the leaf field, not the full path. Decode rows sit under
`Pipeline.Stream.` (`DecodedMediaStreamDiagnosticsSnapshot`), sink and sync rows
under `Pipeline.`, and the rest on the snapshot root.

| Path | Kind | Source |
| --- | --- | --- |
| `state` | enum | `PlaybackState` |
| `seek.state` | enum | `SeekingState` |
| `repeat` | enum | `RepeatMode` |
| `position` | time | `Position` |
| `duration` | time | `Duration` |
| `drift` | time? | `AvSyncDrift` |
| `loop.stalled` | bool | `LoopStalled` |
| `loop.overrun` | time? | `LoopOverrun` |
| `demux.packets` | count | `Demux.PacketsRead` |
| `demux.bytes` | count | `Demux.BytesRead` |
| `demux.seeks` | count | `Demux.SeeksPerformed` |
| `demux.eof` | bool | `Demux.EndOfStreamReached` |
| `video.decoded` | count | `VideoDecoder.FramesDecoded` |
| `video.errors` | count | `VideoDecoder.DecodeErrors` |
| `video.shed` | count | `VideoDecoder.PacketsDroppedForBackpressure` |
| `video.backend` | enum? | `VideoDecoder.HardwareBackend` (null is software) |
| `video.depth` | gauge | `VideoChannelDepth` |
| `audio.decoded` | count | `AudioDecoder.BuffersDecoded` |
| `audio.errors` | count | `AudioDecoder.DecodeErrors` |
| `audio.synthetic-pts` | bool | `AudioDecoder.UsedSyntheticPts` |
| `audio.depth` | gauge | `AudioChannelDepth` |
| `sink.presented` | count | `VideoSink.FramesPresented` |
| `sink.dropped` | count | `VideoSink.FramesDropped` |
| `sink.committed` | count | `VideoSink.FramesCommitted` |
| `sink.last-pts` | time? | `VideoSink.LastPresentedPresentationTime` |
| `sync.dropped` | count | `VideoFramesDroppedForSync` |
| `out.underruns` | count | `AudioSink.UnderrunCount` |
| `out.backpressure` | count | `AudioSink.BackpressureEvents` |
| `out.blocks` | count | `AudioSink.BlocksWritten` |
| `out.time` | time | `AudioSink.PresentationTime` |
| `out.rate` | gauge | `AudioSink.SampleRate` |
| `out.channels` | gauge | `AudioSink.Channels` |
| `out.active` | bool | `AudioSink.IsActive` |

#### Metrics that read zero because the thing does not exist

`sink.committed` is only populated by the zero-copy compositor presenter. Every
other sink reports `0`, which cannot be told apart from "committed nothing", so
`expect sink.committed == 0` is green and meaningless on the CPU and SDL paths.

That is not one metric's problem. `SubstrateSession.GetPipelineDiagnostics`
substitutes a zero-valued snapshot whenever a subsystem is absent — `?? AudioSinkDiagnosticsSnapshot.Empty`
for the audio sink, `_videoPacer?.DroppedLate ?? 0` for the sync counter — so
under `--no-audio` **all seven `out.*` metrics are structurally frozen** at zero
or false. With `sink.committed` that is eight metrics in this table that can read
zero for a reason unrelated to the run.

`sync.dropped` looks like a ninth and is not. Its backing read is
`_videoPacer?.DroppedLate ?? 0`, and the pacer is constructed whenever a video
sink is, which the bench always provides — headless included. Zero there means
zero.

`scripts/repro/signage-gpu-noaudio.bench` already contains the proof: it declares
`require audio off` and then asserts `expect out.active == false`, which cannot
fail. The dangerous direction is the mirror — in a script *with* audio,
`expect since m1 out.underruns == 0` goes green when the audio sink was never
constructed at all. Windowing does not save it; zero minus zero is zero.

**Resolution: capability gating, as a third parse-time constraint beside kind and
operator.** A metric resolves only in a script that carries the `require` its
subsystem depends on. `sink.committed` needs `require presenter gpu`; the `out.*`
prefix needs a new `require audio on`. Anywhere else the path is a parse error,
exit 2, nothing runs. Two metrics and one prefix is the whole table today — if it
grows past that, the mapping wants to move onto the snapshot rather than staying
in the bench. This is a better message than a nullable value could give —
it names the missing `require`, at parse time, instead of reporting "not yet
available" twelve seconds into a thirty-second run.

It rejected the `expect out.active == false` line in
`scripts/repro/signage-gpu-noaudio.bench`, which is the demonstration that the
rule works. That line has been removed: `require audio off` already asserts what
it was reaching for, and does it against the resolved configuration rather than a
counter that cannot move.

The operators are `==`, `!=`, `>`, `>=`, `<`, `<=`. Ordering comparisons are legal
on `count`, `gauge`, and `time`; `enum` and `bool` admit only `==` and `!=`, since
there is no order to compare on. (`!=` was in use in
`scripts/repro/signage-gpu-noaudio.bench` before it appeared in this list.)

The kind constrains the language rather than merely describing it. It decides which operators
are legal, and an illegal pairing is rejected when the script parses:

- **`count` is monotonic within a session, so `==` against an absolute is
  rejected.** `expect video.errors == 0` is true only at session start and
  quietly stops meaning anything after the first `load`. Counters are asserted
  over a window instead, below.
- **`wait` on a `count` with `==` is rejected** for a second reason: a 50 ms poll
  steps over the exact value.
- **`gauge`, `enum`, and `bool` cannot take a window.** The delta of `state` is
  not a thing.
- **Nullable metrics compare against `null` explicitly.** `drift`,
  `video.backend`, `sink.last-pts`, and `loop.overrun` are null until the
  pipeline produces the underlying data. Any other comparison against a null
  fails as "not yet available" rather than treating it as zero, which is the
  mistake that makes a drift assertion pass on a stream that never produced
  timed audio.

#### Windows: `mark` and `since`

Counters accumulate over the session. The question is nearly always what they did
during one stretch of it. `mark` names a full snapshot; `since` reads a counter
as its delta from that mark.

```
load tests/corpus/files/test-1080p-h264-aac.mp4
play
wait state == Playing
wait sink.presented >= 30        # let the pipeline settle

mark warm
wait 10s
expect since warm sync.dropped == 0
expect since warm video.errors == 0
expect since warm sink.presented >= 55/s
```

A value carrying a `/s` suffix divides the delta by the mark's elapsed wall
time. That is one rule rather than a `rate()` function, and it keeps the
language free of call syntax. Against the same ten-second window,
`>= 55/s` and `>= 550` are both legal and mean different things.

Several marks can be live at once. `mark` with an existing name replaces it.

**`load` invalidates every live mark, and so does anything else that tears a
session down.** `PlaybackControllerCore` builds a fresh session per load
(`PlaybackActionKind.CreateSession` at
`src/FrameFlow.Playback/PlaybackControllerCore.cs:809`, with the previous one
disposed first), so the demux and decoder counters restart at zero while the
bench's own sink counters keep climbing. Teardown on its own is worse: once the
session is gone every counter reads zero, including the sink's. `Unload` from any
state and a fatal error from any non-terminal one both go through it. A `since` spanning a `load` would
subtract across that discontinuity and report a negative delta on some paths and
a plausible-looking wrong one on others. Rather than give the snapshot two reset
semantics and expect a script author to track which is which, `since` on a mark
taken before the current `load` is a run-time failure naming the mark and the
load that retired it.

#### Waits

```
wait <metric> <op> <value> [timeout <duration>]
wait <duration>
```

Everything the bench drives is asynchronous. `play` returns before a frame
arrives and `seek` returns before the first post-seek frame presents, so a
script without waits asserts against a pipeline that has not caught up. That is
the ordinary flaky test, and it is worse here than usual: the noise would be
attributed to the platform under investigation.

Polling is 50 ms, deliberately not the 1 s cadence of the diagnostics log line.
One is a heartbeat for a human, the other is a control loop.

Default timeout 10 s, changed with `set timeout <duration>`. A timeout is a
failure.

`wait <duration>` is a sleep, not a condition, so no timeout governs it. A soak
script waits four minutes on purpose and should not have to raise a timeout to
be allowed to.

#### Tolerance

The primitive is a `+-` suffix on the compared value:

```
expect duration == 3.0s +- 100ms
expect since m1 sink.presented == 60/s +- 3/s
```

`expect drift within 40ms` is sugar for `expect drift == 0ms +- 40ms`. Both are
legal on `time` and numeric metrics.

An earlier draft had `within` as the primitive, meaning `abs(value) <= bound`.
That covers drift, which is centred on zero, and nothing else: the first
non-drift use is a duration centred on three seconds. Making the two-sided
comparison general and keeping `within` as the zero-centred shorthand costs one
production and removes the trap.

#### Failure output is the deliverable

A failing assertion has to say what happened, not that it happened.

```
FAIL line 9: expect since warm sync.dropped == 0
  actual 42   (warm 0 -> now 42, window 10.01s)
```

A timeout has to separate "never started" from "started and stalled", so it
prints the trajectory of the waited metric and of the counters that explain it:

```
FAIL line 4: wait position >= 5s timeout 10s
  timed out after 10.00s
  position        1.284s -> 1.284s   (last change 8.7s ago)
  state           Playing
  sink.presented  38 -> 38
  video.decoded   41 -> 41
  demux.packets   512 -> 1980
```

That trace says the demuxer is still reading while the decoder stopped, which is
a different defect from the clock never starting. Reaching the same conclusion
from today's wall-clock log takes a person and a text editor.

#### Parse first, run second

The whole file parses, and every metric name and operator pairing resolves,
before the first command runs. A typo on line 40 is not worth discovering after
a 30 second run.

#### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | every assertion passed |
| 1 | an assertion failed or a wait timed out |
| 2 | the script did not parse; nothing ran |
| 3 | a command failed (the source would not open, the seek was refused) |

`--keep-going` runs past a failed assertion and still exits 1. Interactive mode
reports and continues; it never exits on a failed `expect`.

## Consequences

### Positive

- **The loop closes.** A question becomes a line typed at a prompt instead of a
  rebuild, a relaunch, and a log read. The pipeline stays up between questions,
  so state that took thirty seconds to reach is not thrown away to ask the next
  one.
- **A reproduction becomes a file.**
  [`scripts/repro/signage-gpu-noaudio.bench`](../../scripts/repro/signage-gpu-noaudio.bench)
  is the `Repro-Signage-NoAudio-GPU` profile written out, and
  [`signage-gpu-noaudio-soak.bench`](../../scripts/repro/signage-gpu-noaudio-soak.bench)
  is the same shape over a longer window. It is not yet on the right fixture: it
  loops a 10-second `testsrc2` clip ~36 times, and
  `scripts/generate-test-corpus.cs` argues against both halves of that — testsrc2
  "encodes to almost nothing" and reproduced #145 at ~30% severity, and looping a
  short file "introduces restart bursts that confound the counters".
  `bench-1080p60-h264-aac.mp4` (45s, `noise`, 15 Mbps) exists for exactly this and
  the soak should move to it and drop `repeat one`. Both are reviewable,
  diffable, and runnable by someone else. A launch profile in an example's
  `Properties/` is none of those. The two scripts predate the bench on purpose:
  they were written to test the grammar in Decision 6 against a real
  reproduction, and three of its rules changed as a result.
- **Cross-platform comparison becomes mechanical, for scripts written to be
  portable.** The same script on three operating systems, diffing the diagnostics
  line, is a thing a person can do in an afternoon and a thing CI could do later.
  A script is portable when it avoids `require presenter gpu` and confines itself
  to metrics the software path populates. Neither script in `scripts/repro/`
  qualifies — both are marked Windows-only — so this consequence is a capability
  the bench has, not one it currently demonstrates. See *Not settled here*.
- **The examples shrink back to examples.** `--break-yolo`, `--exit-after`,
  `--left-clock` / `--right-clock`, the repro launch profiles, and two of the
  three `TextBoxLoggerProvider` copies have somewhere else to live.
- **Every consumer gets the diagnostics interpretation**, not just this tree.

### Negative / trade-offs

- **Another project to keep green on three platforms.** It carries no XAML
  beyond a host window and consumes only the packaged surface, so the
  maintenance is a build leg and an API-drift alarm. The alarm is deliberate.
- **A pleasant bench attracts features.** The line to hold is that it drives,
  observes, and asserts. It does not acquire capabilities FrameFlow lacks. When
  it wants one, that is Decision 3 firing correctly.
- **Headless is not the same run.** Different sink, no compositor, no vsync. It
  records what the software path does and will not reproduce a DXGI or DComp
  defect. This has to be stated in the bench README, or a green headless run
  gets read as a green platform.
- **Moving flags out of the examples breaks saved launch profiles** and habits.
  One-time, and the replacement is a script file that survives being shared.

### Neutral

- **It is not a testing layer.** Nothing gates on it. ADR-0007's five layers are
  unchanged, and anything the bench finds that can be asserted without hardware
  belongs in `tests/FrameFlow.Integration.Tests` afterwards.
- **It does not ship.** Excluded from the pack and publish set. It is a
  development instrument, like `spikes/`.
- **The corpus is the natural input.** `tests/corpus/files` and
  [CORPUS.md](../CORPUS.md) already define the media a script would name.

## Alternatives considered

- **Keep using the examples.** The status quo, and the evidence against it is
  the flag census above: fault injection in demonstration code, a saved
  reproduction in a launch profile, and eleven hand-rolled `--log-file` parsers.
  The workflow it supports is open-loop and cannot fail. Rejected.
- **Port popcorn into the repository as it stands.** Its `AppState` /
  `AppEvent` / `AppReducer` / `Wire` core is roughly 1,100 lines of machinery
  whose purpose is to let two processes agree on state without sharing memory. A
  single-process bench does not have that problem. Rejected. The command
  vocabulary and `PlaybackProbe` are worth taking; the transport and the reducer
  are not.
- **Extend `tests/FrameFlow.Integration.Tests` instead.** It already has capture
  sinks and a harness, and it is the right home for anything assertable and
  hardware-independent. It is the wrong instrument for "this looks wrong on that
  iGPU", where the answer needs a person watching a real window and changing one
  variable at a time. Rejected as a replacement, kept as the destination for
  what the bench turns up.
- **A diagnostics panel inside `FrameFlowPlayerView`.** Puts the counters where
  the eyes already are. It is not scriptable, not diffable across platforms, and
  it grows a shipping control to serve a development need. Rejected.
- **Localhost HTTP instead of stdin**, which popcorn's own ADR-0001 also
  considered. Binds a port, needs a Windows firewall exception, and buys
  reachability from another machine that nothing here asks for. Rejected.

### On the script language specifically

- **An embedded general-purpose language** (Lua, or C# scripting via Roslyn).
  Every capability for free, and the bench inherits a language runtime, a
  packaging problem, and scripts that can be written badly enough to need
  debugging. The failure mode is that the bench becomes the place logic lives
  instead of the place the library is exercised. Rejected.
- **`rate(sink.presented)` and `delta(sink.presented, warm)` as functions.**
  The conventional shape, and it introduces call syntax into a language that is
  otherwise a sequence of words. The `since <mark>` prefix and the `/s` unit
  suffix cover both cases without it. Rejected.
- **An implicit window: every `expect` on a counter compares against the
  previous statement's snapshot.** No `mark`, shorter scripts. Rejected: the
  window becomes invisible, and inserting one `wait` silently changes what every
  following assertion means.
- **Absolute counter assertions with no window concept at all.** Simplest to
  build. It is also the thing that makes `expect video.errors == 0` look correct
  and mean nothing after the second `load`. Rejected as the defect the window
  exists to prevent.
- **Reusing the diagnostics probe's 1 s cadence for `wait` polling.** One timer
  instead of two. It puts up to a second of latency into every wait, and a
  ten-statement script pays it ten times. Rejected; 50 ms for control, 1 s for
  the human-readable heartbeat.

## Not settled here

**The largest open question is at the head of Decision 6**: whether the script
language should exist at all, or whether repro files should be C# and the bench
should keep only its interactive command set. Adopting that revision closes six
of the entries below and in Decision 6 — the operator table, the exit-code split,
capability gating, the metric-kind rules, `set`, and the wallclock kinds — so it
should be decided before any of them are. The one unknown that block recorded —
whether `#:package` gives a single `.cs` file FrameFlow *and* its natives — is
now answered yes by `spikes/package-directive-repro.cs`.

- ~~Whether the project lives at `tools/` or `testbench/`.~~ **Settled:** `tools/`.
  Three places outside this ADR already say so — `docs/adr/README.md` and the
  invocation line in both repro scripts. The layout is category-per-directory and
  every other top-level holds N projects, so a directory named after one project
  is the odd one out.
  <br>One correction to the reasoning that goes with it: the bench must be **in**
  `FrameFlow.slnx` with `<IsPackable>false</IsPackable>`, not outside it. The
  "like `spikes/`" framing below is misleading — `spikes/` is excluded from the
  solution, and `build.yml` builds and tests the solution and nothing else, so a
  bench outside it would never be compiled by CI and Decision 3's API-drift alarm
  would never fire. The `tests/` shape is the right one. Note also that
  `src/Directory.Build.props` claims projects outside `src/` are unpackable by
  default; that is false, which is why `examples/Directory.Build.props` exists.
- ~~Whether the counting sink belongs in the bench or beside `NullVideoSink` in
  `FrameFlow.Media`.~~ **Settled:** `FrameFlow.Media`. It keeps the bench thin,
  it is where the sink telemetry shell already lives, and it makes the sink
  testable without a bench. See Decision 4.
- ~~Whether `set` gains knobs beyond `timeout`, and whether `set` belongs in a
  script at all rather than on the command line.~~ **Settled:** it stays in the
  script, keeps `timeout`, and gains nothing, under this rule — *`set` may change
  only how the bench interprets the remainder of the script; never how the
  pipeline is built, and never how the run is reported.*
  <br>That admits `timeout` and rejects the three obvious proposals.
  `present-cost` is construction-time (`HeadlessVideoSink.PresentCost` is
  get-only, and the sink is built before the script is read), so it is a
  `require`, not a `set`. `--keep-going` changes the exit-code contract CI owns.
  `set poll` would let a script reintroduce the latency the Waits section already
  priced. Moving `timeout` to the command line loses the thing the pair principle
  exists to protect: `set timeout 30s` sits three lines above the fixture that
  needs it and is self-documenting there.
- ~~Whether `sink.committed` should be nullable.~~ **Settled: no** — capability
  gating instead, which covers all nine affected metrics rather than one. See
  *Metrics that read zero because the thing does not exist* under Decision 6. The
  reasoning below is kept because its diagnosis is right even though its proposed
  fix is not; note also that its stated cost is wrong — nothing in the tree reads
  `VideoSinkDiagnosticsSnapshot.FramesCommitted`, so "existing consumers" was
  never the obstacle. The real objection is that the existing nullable rule means
  null-as-*not-yet*, which resolves forward, while this would be
  null-as-*capability*, which never does.
  <br><br>The original entry: the field behind it,
  `VideoSinkDiagnosticsSnapshot.FramesCommitted`, is a `long` that only the
  zero-copy compositor presenter ever populates; every other sink reports `0`.
  Under Decision 6 that is indistinguishable from a presenter that enqueued
  nothing, so `expect sink.committed > 0` fails on the CPU and SDL paths for a
  reason that has nothing to do with the run. The metric-kind rules already
  handle this shape for `drift`, `video.backend`, `sink.last-pts`, and
  `loop.overrun` — a nullable metric compares against `null` explicitly and any
  other comparison fails as "not yet available" rather than being treated as
  zero. Making `FramesCommitted` a `long?` would put `sink.committed` in that
  group and let a script distinguish "this sink does not track commit" from
  "nothing committed". It is a change to an ADR-0034 snapshot record with
  existing consumers, so it belongs to whoever implements Decision 6 rather than
  being smuggled in ahead of it.
- What kind `LastPresentedAtUtc` and `LastCommittedAtUtc` get when they join the
  metric namespace. Decision 5 now says they should — they exist on
  `VideoSinkDiagnosticsSnapshot` and are what makes a presenter freeze assertable
  — but they are wallclocks, not media time, so the existing `time` kind is wrong
  for them. Comparing one against `now` is the only useful operation, and no kind
  in the table expresses that.
- Whether exit code 2 should split. It currently means both "the script did not
  parse" and "the invocation did not satisfy a `require`" — a script-author error
  and an operator error, which a CI job cannot tell apart. Related and also
  unspecified: how `--keep-going` composes with a command failure, and which code
  wins when both an assertion and a command fail.
- What the portable reproduction is. The cross-platform motivation in the Context
  section is the strongest argument for building this, and both shipped scripts
  are Windows-only because the presenter they pin is. A third script exercising
  the CPU or SDL path under `--headless`, asserting only on metrics the software
  path populates, is what would make that motivation actionable. Nothing about the
  design blocks it; it has simply not been written, and the ADR should not read as
  though the existing pair covers it.
- Whether a `next` command belongs in the grammar, for
  `IMediaPlaylistPlayer.SetNextAsync` + `SkipToNextAsync`. It is the warm-presenter
  path — the one that swaps decode source without rebuilding the sink — and the
  gapless signage case is what the repro scripts are about. It would need its own
  entry in the `load` semantics table under Decision 3.
