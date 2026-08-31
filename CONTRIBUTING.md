# Contributing to FrameFlow

**FrameFlow is not accepting contributions.** Pull requests will not be reviewed
or merged.

That is a decision about the project's capacity, not about any particular change.
Accepting contributions well means reviewing them, settling licensing, and then
supporting what was merged — and doing that badly is worse than not doing it at
all. This may change later. If it does, this file changes with it.

**The rest of this file is still worth reading.** It is how to build the
repository, run its tests, and find the reasoning behind its shape — useful
whether you are evaluating FrameFlow, reading the code, or working from a fork
under the licence.

**Bug reports are welcome** in the issue tracker, with no promise of a reply or a
fix. Anything with a security impact goes through [SECURITY.md](SECURITY.md)
instead — never a public issue.

## Licence

FrameFlow is **source-available, not open source**, under the
[PolyForm Small Business License 1.0.0](LICENSE.md). Read it before building on
top of this: it restricts commercial use by company size.

If you open a pull request anyway,
[GitHub's Terms of Service §D.6](https://docs.github.com/en/site-policy/github-terms/github-terms-of-service#6-contributions-under-repository-license)
licenses what **you** contribute to a public repository under the licence that
repository carries, and there is no separate CLA or DCO on top of it. That is a
default, not an invitation — the pull request still will not be merged.

§D.6 is also only as good as the rights you actually hold. It is your grant, so
it cannot convey what is not yours to give: it does not cover code your employer
owns, or third-party material under other terms. If you are working on an
employer's time or equipment, that is usually their decision rather than yours.
Do not submit either.

Every **C# source file** under `src/` opens with a two-line SPDX header, and a
fork should keep it:

```csharp
// Copyright <year> <copyright holder>
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
```

The SPDX line is fixed. The copyright line names whoever holds copyright in that
file — every existing file reads `// Copyright 2026 Charles Lee` because the
maintainer wrote them.

The header is a C# convention only. Project, props and targets files under `src/`
do not carry one, and neither do tests, examples or spikes.

The dependency rule, for anyone working from a fork: anything GPL or AGPL is a
hard no. The FFmpeg build is pinned to LGPL for exactly this reason, and the YOLO
weights are fetched at runtime rather than redistributed.
`THIRD-PARTY-NOTICES.md` records what ships.

## Prerequisites

- **.NET SDK 10.** Every project targets `net10.0`, and the solution is a `.slnx`,
  which needs SDK 9.0.200 or newer to parse at all. The dev-time scripts under
  `scripts/` are single-file C# apps run with `dotnet run scripts/<name>.cs`,
  which needs SDK 10. `global.json` pins 10.0.100 with
  `rollForward: latestFeature`, so any 10.0.x feature band works and an
  older SDK fails with a message naming the version it wanted. It also
  means a future major on its own is not enough: CI builds on `10.0.x`, so
  the pin holds local builds to the same major rather than letting them
  drift ahead of it. If you have moved on to a newer major, keep a .NET 10
  SDK installed alongside it.
- **FFmpeg shared libraries**, primed once per clone. They are gitignored:

  ```bash
  dotnet run scripts/fetch-ffmpeg.cs
  ```

  That writes `runtimes/{rid}/native/`, which `Directory.Build.targets` copies into
  every project's output. Nothing decoding-related works before you run it.

**Seven projects cannot restore from a public clone yet.** They reference
`FrameFlow.Native.Runtime`, which packages the FFmpeg binaries and has only ever
been published to a private feed:

```
error NU1100: Unable to resolve 'FrameFlow.Native.Runtime (>= 0.1.1-alpha)' for 'net10.0'.
PackageSourceMapping is enabled, the following source(s) were not considered: nuget.org.
```

nuget.org is excluded deliberately. Nobody has registered any `frameflow.*` id
there, so letting it serve that pattern would let a stranger's package win the
floating version range instead.

`FrameFlow.MotionClip` and its tests, and the `AvaloniaPlayer`,
`Camera.Inference.Dml`, `DualPlayer`, `Multicast.Dml` and `ZeroCopyInterop`
examples. Everything else — all 21 libraries and the rest of the tests and
examples — restores from nuget.org alone. Build a project or a test project
directly rather than the whole `.slnx` if you hit this.

It goes away when that package reaches nuget.org.

## Build and test

```bash
dotnet build ./FrameFlow.slnx --nologo
```

The integration suite needs generated media on disk:

```bash
dotnet run scripts/generate-test-corpus.cs
dotnet test ./FrameFlow.slnx --nologo
```

`scripts/run-tests.sh` is the faster path — it fans one `dotnet test` process out
per project, and expects a prior `dotnet build`.

`frameflow.runsettings` carries the timeouts and the `FRAMEFLOW_VISUAL_TESTS` gate.
The SDL tests open a real window and stay skipped unless you set it to `1`.

Two corpus fixtures cannot be produced by the pinned FFmpeg build, because x264 and
x265 are disabled as GPL. `generate-test-corpus.cs` reports which and explains the
workaround.

The default corpus does not cover everything. A few regressions need a clip longer
than any 3 s fixture, and those tests report as **skipped** — with the command to
fix it — rather than quietly passing. To run them:

```bash
dotnet run scripts/generate-test-corpus.cs -- --include-benchmarks
```

Read the skip list at the end of a run before treating it as a full pass.

## Continuous integration

`Build and Test` is **`workflow_dispatch` only** — a cross-platform matrix is
expensive in runner minutes, so it does not fire on push or pull request. It is
dispatched by hand against a chosen branch.

Worth knowing when reading the repository: a quiet checks list on a commit does
not mean CI passed on it. It usually means CI never ran. The local suite is the
real gate.

## Style

`.editorconfig` is the whole of it: UTF-8, LF, four-space indent in C#, two in
XML/YAML, final newline, no trailing whitespace. There is no separate formatter to
install and no CI format check.

Beyond that, match the file you are editing. The codebase leans on explicit
subsystem boundaries, composition over inheritance, and simple runtime loops over
abstract pipelines.

## Commit convention

This is how the history here is kept, documented for anyone reading it or working
from a fork. Commit subjects follow Conventional Commits, scoped to the
subsystem:

```
fix(playback): discard the frames between the keyframe and the seek target
feat(decoding): report the video shed rate instead of only counting it
docs: explain the Crossbar citations once, rather than sweep 92 files
```

Work lands squash-merged, so the pull-request title becomes the commit subject,
and each one carries a single concern.

Bodies are written for someone who was not there: what changed, why this shape,
and how it is known to work. That is why the log is unusually long-form — it is
the closest thing to a design record for changes too small to earn an ADR.

## Architecture decisions

`docs/adr/` holds 67 decision records, indexed in
[docs/adr/README.md](docs/adr/README.md). Read the relevant one before changing a
subsystem's shape — several explain why an obvious-looking simplification was
already tried and rejected.

Write a new ADR when you change a boundary, a threading or ownership rule, or a
dependency policy. Do not renumber or rewrite an existing record; supersede it and
say what replaced it. Ordinary bug fixes and features need no ADR.

Many ADRs cite **Crossbar**, a first-party predecessor substrate that is not
published. Those references cannot be followed;
[docs/adr/README.md](docs/adr/README.md#a-note-on-the-crossbar-citations) explains
why they are there.

## Reporting problems

Bug reports go to GitHub issues and are read, with no commitment to reply or to
fix. A report that names a version, gives a media file or corpus entry that
reproduces it, and says what you expected instead is worth far more than one that
does not.

Feature requests are unlikely to be actioned while contributions are closed.

For anything with a security impact, do not open an issue — see
[SECURITY.md](SECURITY.md).
