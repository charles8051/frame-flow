# scripts/repro/

Saved reproductions. Each is one C# file that drives a pipeline, observes it,
asserts, and exits non-zero when a check fails.

```bash
dotnet run scripts/repro/signage-gpu-noaudio.cs
```

| File | Shape | Runtime |
|---|---|---|
| `signage-gpu-noaudio.cs` | GPU zero-copy presenter, no audio sink, one 3s clip | seconds |
| `signage-gpu-noaudio-soak.cs` | the same, looping a 45s clip, measured in two windows | ~6 minutes |

Both are **Windows only**. They pin the zero-copy compositor presenter, which is
`FrameFlow.Avalonia.Windows`, and exit 2 elsewhere rather than quietly measuring
the software path.

```
0   every check passed
1   a check failed
2   could not run at all (wrong platform, missing fixture)
```

## Why these are C# and not a script language

The draft ADR specified an assertion grammar — `expect`, `require`, `mark`,
`since`, a metric namespace, operators with kinds. Decision 6 dropped it. Every
defect review found in that design was in the language rather than in driving a
player, and the alternative it had rejected ("a language runtime, a packaging
problem") does not describe .NET file-based apps, where the script *is* a
program.

See the resolution at the head of Decision 6 in
[the ADR](../../docs/adr/command-driven-testbench-host.md).

The bench (`tools/FrameFlow.TestBench`) is still there and still useful — it
keeps a pipeline warm and takes typed commands. It just does not assert.

## Seven things worth knowing before writing another one

**Take a floating package version.** `#:package FrameFlow.Player@0.7.0-alpha.*`
resolves from the local feed and keeps working as MinVer advances. Record the
exact build you observed the behaviour on in a comment. Pinning it exactly
records the observation and breaks the file for everyone else.

**Attach the sink on the UI thread.** `AttachSink` initialises the compositor
surface. Calling it from the worker throws `"Call from invalid thread"` out of
Avalonia's dispatcher. Build on the thread that owns the window, drive from
another.

**Poll; do not assert straight after an action.** `PlayAsync` returns before a
frame arrives and `Position` is clock-driven — it settles to a seek target rather
than stepping to it. This is the one part of the deleted grammar that was a real
need rather than an artefact of having built a language.

**Level-triggered diagnostics need the edge-triggered observable.**
`PlaybackDiagnosticsSnapshot.LoopStalled` means "currently appears stalled". A
stall that recovers before your next poll leaves it `false`, so reading it once
at the end reports a clean run on exactly the symptom you were watching for.
Subscribe to `IMediaPlayer.LoopStalled` — it fires once on the rising edge — and
count.

**Distinguish an absent flag from a malformed one.** `--gap 20` has no unit.
Falling back to the default on a typo turns a twenty-second smoke test into a
five-minute soak that looks like it ran what you asked for. Exit 2 instead.

**Setup failure is exit 2, not exit 1.** A compositor that will not initialise
is "could not run at all", and it has to be caught: an exception out of the
`Opened` handler escapes and the process exits on whatever the host decides.

**The harness is inlined on purpose.** A file-based app is one file. A shared
helper would have to be a `#:project` reference, which makes a reproduction need
a checkout and gives up the thing that made `#:package` worth having — one file
plus the SDK is the whole reproduction. Sixty duplicated lines is the cheaper
side of that trade.

## Fixtures

Both need the generated corpus, which is gitignored:

```bash
dotnet run scripts/generate-test-corpus.cs
dotnet run scripts/generate-test-corpus.cs -- --include-benchmarks   # for the soak
```

The soak's fixture is `bench-1080p60-h264-aac.mp4`: 45s of noise at 15 Mbps,
1080p60, 85 MB. It is behind a flag because of the size, and it is noise rather
than `testsrc2` because `testsrc2` "encodes to almost nothing" and costs the
decoder nothing to play.

Running the generator rewrites `tests/corpus/test-expectations.json` from
whatever it produced that run, so a `--include-benchmarks` run leaves a diff.
That diff is local — do not commit it.
