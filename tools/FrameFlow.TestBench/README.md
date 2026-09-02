# FrameFlow.TestBench

A console host that builds a real pipeline from the public API, keeps it warm, and takes
typed commands.

```
dotnet run --project tools/FrameFlow.TestBench -- clip.mp4
> play
ok    Playing 00:00.000/00:05.000
> seek 4s
ok    Playing 00:02.031/00:05.000 [SeekInProgress]
> diag
  state     Playing 00:04.921/00:05.000  gen=1
  demux     packets=300 bytes=226054 seeks=1 eof=True
  video     decoded=220 errors=0 shed=0 backend=D3D11Va
  audio     decoded=0 errors=0 active=False
  sink      presented=89 dropped=0 sync-dropped=1
  headless  abandoned=0
  since last diag:
    [Info] Demuxer seeked once.
```

The pipeline stays up between questions. That is the whole point: state that took thirty
seconds to reach is not thrown away to ask the next one.

## Commands

| | |
|---|---|
| `load <path>` | build a session on this source |
| `unload` | tear the session down |
| `play`, `pause` | |
| `seek <duration>` | |
| `volume <level>` | 0 is silent, 1 is unity; above 1 is legal and may distort |
| `mute on\|off` | |
| `repeat off\|one\|all` | |
| `status` | one line: state, position, duration |
| `diag [--all]` | counters, and what moved since the last `diag` |
| `wait <duration>` | a sleep |
| `quit` | |

Durations are a number and a unit: `250ms`, `1.5s`, `2m`, `1h`. The unit is required — a
bare number would have to mean seconds or milliseconds by convention, and a script that
meant the other one is off by a thousand without saying so.

`#` starts a comment. Blank lines are fine.

## Scripts

`--script <file>` reads those same commands from a file.

```
0   every command succeeded
1   a command failed
2   a line did not parse, and nothing ran
```

The whole file parses before the first command runs, and every bad line is reported at
once. A typo on line 40 is not worth discovering after a thirty-second run, and five
typos are not worth five runs.

## The bench does not assert

There is no `expect`, no `require`, no metric namespace, and no operators. A
reproduction that asserts is a C# file-based app under `scripts/repro/`, which takes
`#:package FrameFlow.Player`, builds its own sinks, and returns its own exit code. See
the resolution at the head of Decision 6 in
[the ADR](../../docs/adr/command-driven-testbench-host.md), and
[`scripts/repro/`](../../scripts/repro/README.md) for two that exist.

## Presenters

`--presenter headless|cpu|gpu`, default `headless`.

| | |
|---|---|
| `headless` | no window; counts frames, with an optional synthetic present cost |
| `cpu` | a window, presenting through `WriteableBitmap` |
| `gpu` | a window, presenting through the zero-copy compositor path |

**The bench reports the presenter it resolved, not the one you asked for.** `gpu` falls
back to `cpu` off Windows and the flag still reads `gpu`, so a bench that echoed the
request would produce a transcript claiming a pipeline the run did not measure:

```
presenter cpu — requested gpu, the zero-copy compositor surface is Windows-only
```

`committed` follows the same rule. `VideoSink.FramesCommitted` is populated only by the
compositor presenter, and every other sink leaves it at `0` — which cannot be told apart
from a compositor that committed nothing. The bench cannot fix the field, but it knows
which surface it built:

```
--presenter gpu    committed 58  last=10:24:16.941
--presenter cpu    committed n/a — only the gpu presenter populates it (this run is cpu)
```

A window has no chrome: no transport bar, no seek bar. The bench is driven from the
console, and a control that could also drive it would make a transcript an incomplete
record of what happened to the session.

## Headless is not the same run

`--presenter headless` presents to `HeadlessVideoSink`. Different sink, no compositor,
no vsync. It records what the software path does and **will not reproduce a DXGI or
DComp defect**. A green headless run is not evidence that the presenter is fine — that
is what `--presenter gpu` is for, and it needs Windows and a display.

`--present-cost <duration>` gives each frame a synthetic cost so a headless run is not
optimistically fast. The frame is held for the whole cost, so the pool slot stays
occupied and the cost propagates back as real backpressure rather than billing
wall-clock time while the decoder runs unimpeded.

Where the loss shows up is the part worth knowing before reading the numbers. The
headless sink never reports a drop of its own — with no render tick there is nothing to
fall behind — so `sink dropped` stays at zero however badly the run goes. Loss appears
upstream instead, measured on the same clip:

```
--present-cost 0ms    presented=90  dropped=0  sync-dropped=1
--present-cost 60ms   presented=49  dropped=0  sync-dropped=40
```

A script watching for loss has to watch `sync-dropped` and `shed`, not `dropped`.

Frames abandoned at shutdown or to cancellation are counted separately again, on
`headless abandoned`, so `dropped` keeps its "the render path is the bottleneck"
meaning.

## Options

| | |
|---|---|
| `--script <file>` | run commands from a file instead of the console |
| `--presenter <kind>` | `headless` (default), `cpu`, or `gpu` |
| `--no-audio` | build no audio sink; `volume` and `mute` then fail |
| `--present-cost <dur>` | synthetic per-frame cost for the headless sink |
| `--pool-capacity <n>` | frame pool slots, default 3 |
| `--log-file <file>` | also write the session to this file |

`--log-file` is a copy on disk rather than the only way to see anything. That is the
difference between this and the eleven examples that parse the same flag: the bench is a
console application, so the command, the reply, and every log line the pipeline emitted
in between arrive on stdout in the order they happened. That ordering is the artifact
worth pasting into an issue.

## Public API only

No `InternalsVisibleTo`, and no project reference reaching past the packaged surface.
Anything the bench needs that the public API does not offer is a gap in that surface, to
be filed as one rather than worked around here. The bench then doubles as a standing
ergonomics check: it breaks when the public surface breaks, which is intended.

It is in `FrameFlow.slnx` with `IsPackable=false` rather than outside the solution like
`spikes/`. CI builds and tests the solution and nothing else, so a bench outside it
would never be compiled and that alarm would never fire.

`OutputType` is `Exe`, not `WinExe`, even though it opens a window for the `cpu` and
`gpu` presenters. `FrameFlow.MotionClip` is the precedent. The cost is a console window
beside the video one; the gain is that the command, the reply, and every log line the
pipeline emitted in between arrive in one stream.
