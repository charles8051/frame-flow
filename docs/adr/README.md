# ADRs

This folder stores Architectural Decision Records for FrameFlow.

Use an ADR when a decision:

- affects multiple phases
- changes public shape or project structure
- constrains future implementation choices
- would be hard to rediscover from code alone

Good ADR candidates include:

- FFmpeg binary resolution strategy
- chosen audio backend(s)
- synchronization master clock policy
- presenter abstraction boundaries
- hardware acceleration strategy
- public DI and options registration surface

## A note on the Crossbar citations

Many ADRs here cite **Crossbar** — `crossbar ADR-0014`, "the Crossbar substrate",
`crossbar/Directory.Build.props`, and similar. Those references cannot be
followed, and this note exists so that is a known fact rather than a puzzle.

Crossbar was a first-party library by the same author: a Holoscan-class
processing-graph substrate that FrameFlow consumed as a NuGet package. In
2026-05 it was forked into this repository — a one-time verbatim copy that
became `src/FrameFlow.Graph`, with `using Crossbar;` becoming
`using FrameFlow.Graph;` throughout. [ADR-0049](ADR-0049-frameflow-graph-fork-from-crossbar.md)
records that decision and its rationale in full.

Three consequences worth stating plainly:

- **There is no Crossbar dependency.** No `PackageReference`, no `using`. Every
  mention in this repository is history, not a live edge. `FrameFlow.Graph` is
  the substrate, in-tree and diverged.
- **The Crossbar repository is not published**, so its ADR numbers and file paths
  are cited as the record of a decision rather than as links. They are named so
  that someone with access can find them.
- **The forked code ships under FrameFlow's licence, not Crossbar's.** Crossbar
  was MIT; `src/FrameFlow.Graph` is covered by `LICENSE.md` like the rest of this
  repository. That is a change of terms, not a continuation of them. The
  licensing note at the top of [ADR-0049](ADR-0049-frameflow-graph-fork-from-crossbar.md)
  records what is known about the fork's provenance and what remains a question
  for the maintainer; this index does not settle it.

Where an ADR's reasoning genuinely depends on a Crossbar document, the ADR says
so at the point of citation. Where it is only provenance — "this mirrors
Crossbar's shape" — it can be read as a historical aside.

## A note on "the kiosk"

Many ADRs and investigations here say **the kiosk** — "the kiosk's Intel HD
620", "on-kiosk A/B", "the confirmed kiosk choppiness". Like the Crossbar
citations above, this note exists so the term is a known referent rather than a
puzzle.

The kiosk is a downstream first-party application by the same author: a
single-window digital-signage player that consumes FrameFlow. It is not
published, and it is not this repository. It appears in these documents because
it was the deployment most of the performance and stability work was measured
against, and naming the machine is what makes those measurements checkable.

Its properties are the ones that keep showing up in the reasoning:

- **A weak Intel iGPU.** Most of the hardware-decode, zero-copy, and
  colour-conversion decisions turn on what is slow or broken there, not on what
  a discrete GPU does. Individual ADRs name the exact part wherever a
  measurement depends on it.
- **Single monitor, fullscreen, one window.** Several diagnostics are
  process-wide precisely because there is only ever one active sink.
- **Long uptime, unattended.** Resource leaks that a desktop app would never
  notice are load-bearing failures over weeks of runtime.
- **Offline / locked-down.** No first-run network egress, which is why model
  and native-binary acquisition are pre-seeded rather than downloaded
  ([ADR-0051](ADR-0051-model-acquisition-strategy.md)).

Two consequences worth stating plainly:

- **Measurements citing it are records, not reproduction steps.** "Measured on
  the kiosk" means it was measured on that hardware, and you cannot re-run it
  here. Where a result is reproducible on ordinary hardware, the ADR says so.
- **It is one consumer's shape, not a constraint on yours.** FrameFlow does not
  assume a kiosk. Where a decision was made *because* of that deployment and
  another consumer might reasonably want the opposite, the ADR records it as a
  trade-off rather than a rule.

Unrelated: `src/FrameFlow.MotionClip/scripts/install-kiosk-task.ps1` and its
paired uninstaller use "kiosk" in the ordinary sense — an auto-login machine
running one application at logon. Those scripts are a supported deployment mode
of MotionClip and have nothing to do with the specific deployment above.

## A note on the investigation and commit citations

Two more kinds of reference in this repository cannot be followed. Like the
Crossbar note above, this exists so that is a known fact rather than a puzzle.

**Three investigations are cited but not published.** Comments and ADRs refer
to them by date, usually with a section number:

| cited as | what it is |
|---|---|
| `investigation 2026-06-12`, often `§6` or `§9` | the composition-interop presenter teardown deadlock, and the live-playback `VideoProcessorBlt` hang found on the same deploy |
| `perf survey`, usually `§A1`, also `§A3` / `§A4` / `B5` | a 2026-06-11 survey of pacing-clock cadence, the held-lease coupling, and thread pressure |
| `2026-06-06 investigation` | the DirectComposition / MPO overlay presenter measurements behind [ADR-0061](ADR-0061-dcomp-overlay-video-surface.md) |

They are wholly about debugging a downstream deployment. Each cites paths and
line numbers in a repository that is not published, a host profile that is not
published, and crash-dump locations on a specific machine. Generalising them
line by line would leave documents that no longer describe anything, so they
are kept outside this repository rather than rewritten into it.

**Every citation to them is provenance, not a dependency.** The comment or ADR
section that carries one already states the mechanism it is describing; the
date is there to record where the finding came from. Nothing in this repository
requires reading those documents to be understood, and where a decision
genuinely turned on one, the ADR says so at the point of citation — see
[ADR-0057](ADR-0057-pull-based-master-clock.md) §Amendment,
[ADR-0061](ADR-0061-dcomp-overlay-video-surface.md),
[ADR-0063](ADR-0063-nv12-pixel-shader-color-conversion.md) and
[ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md), each of which
names the investigation and states plainly that it is not published here.

**Commit hashes do not resolve either.** ADRs and `docs/DEFERRED_WORK.md` cite
short hashes — `04ab378`, `aeec5dc`, `a193260` and others — as the record of
when something landed. This repository was published as a fresh tree without
its development history, so none of them resolve against the published remote
regardless of whether they were valid before. Read a cited hash as a date-stamp
on a claim, not as a link. Hashes qualified as Crossbar's (`Crossbar dcee5f1`)
were never in this repository's history in the first place.

## Naming

`ADR-<4-digit number>-<kebab-case-slug>.md`, matching the existing series:

- `ADR-0001-api-first-foundation-sequencing.md`
- `ADR-0002-ffmpeg-bootstrap-strategy.md`
- `ADR-0003-audio-master-sync-policy.md`

Numbers are assigned at merge, not at authoring — see "Drafts pending number assignment" below.

## Cross-repo references

**Always qualify an ADR reference from another repository with the repo name:
`Crossbar ADR-0014`, never bare `ADR-0014`.**

FrameFlow's series and Crossbar's series collide on number. FrameFlow's
ADR-0014 is native binary packaging; Crossbar's ADR-0014 is the substrate
migration whose "Phase 3 / Phase 4" milestones a lot of this codebase was
written against. FrameFlow's ADR-0010 is logging and diagnostics; Crossbar's
ADR-0010 is consumer-function unification, which is what the
`FrameConsumer<TFrame>` ownership comments mean. Unqualified citations sent
readers to the wrong document in both cases (issue #97).

When the claim also has a FrameFlow ADR that covers it, cite both — for example
`(ADR-0030; Crossbar ADR-0014 Phase 4)` on the sink dataflow contracts.

## Suggested template

Each ADR should capture:

1. status
2. context
3. decision
4. consequences
5. alternatives considered

## Drafts pending number assignment

ADRs are numbered at merge, not at authoring, so parallel branches never collide
on the same number. Drafts in flight live here under a slug filename until they
land.

- [A command-driven test-bench host, separate from the examples](command-driven-testbench-host.md)
  — a console host under `tools/` that drives a real player from stdin and from
  script files, so reproductions stop living in example launch profiles and the
  same command sequence can be run and compared across platforms. Also moves
  popcorn's diagnostics delta interpretation into `FrameFlow.Playback.Diagnostics`.

(Most recently, the pacing-clock timer-resolution decision landed as
[ADR-0067](ADR-0067-high-resolution-pacing-timers.md), moving the Windows high-resolution timer
from a thing every host had to ask for into the library's own default and superseding the
consumer guidance in ADR-0018 and the deferral in ADR-0057; before it, the zero-copy converter
decode-device identity / ownership decision landed as
[ADR-0064](ADR-0064-zero-copy-converter-device-ownership.md), fixing the warm-sink player-swap
presenter freeze (Decision 1) and then — Decision 2, implemented 2026-06-21 — making the swap
gapless by giving the converter its own D3D11 device so it rebinds in place instead of rebuilding,
plus splitting enqueued-vs-committed present observability.)
