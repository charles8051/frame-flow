# ADR-0046: Native Runtime Acquisition Strategy

**Status:** Accepted
**Date:** 2026-05-15
**Supersedes:** None
**Related:** ADR-0002 (FFmpeg bootstrap strategy), ADR-0014 (native binary packaging), ADR-0019 (SDL native bootstrap seam)

## Context

This repository has accumulated three native dependencies — FFmpeg, SDL2, and OpenAL — and today (2026-05-15) settled into **two different acquisition patterns** across them. This ADR records the tension surfaced by that divergence and codifies a decision framework so future native deps don't relitigate the question from scratch.

### What we have today

| Dependency | Bindings | Native bundling |
|---|---|---|
| **FFmpeg** | `FFmpeg.AutoGen.Abstractions` (structs/enums only) + hand-rolled P/Invoke in `FrameFlow.Native/Interop/` | Custom: [`scripts/fetch-ffmpeg.cs`](../../scripts/fetch-ffmpeg.cs) downloads BtbN's LGPL build to `runtimes/{rid}/native/`; `FrameFlowBootstrapper` resolves at startup; `FrameFlow.Native.Runtime.csproj` packages the bundle for downstream consumers |
| **SDL2** | `Silk.NET.SDL` (third-party) | Custom: `SdlBootstrapper` (per ADR-0019) integrates via `INativeContext` to handle single-file extraction layouts |
| **OpenAL** | `Silk.NET.OpenAL` (third-party) | Upstream: `Silk.NET.OpenAL.Soft.Native` ships OpenAL Soft 1.23 binaries under the standard `runtimes/{rid}/native/` NuGet convention — three lines of csproj, no custom code |

The OpenAL choice landed today after the AvaloniaPlayer example broke on a fresh Windows install. Pre-2026-05-15, OpenAL was system-resolved (per ADR-0019 §"OpenAL"), relying on the implicit assumption that OpenAL32.dll exists on most Windows machines via prior game/SDK installs. That assumption breaks on a clean install.

### The tension

When the OpenAL fix proved trivial (one PackageReference) compared to the FFmpeg infrastructure we maintain (a fetch script with a manifest, a bootstrapper with three resolution modes, an opt-in runtime-bundle NuGet, and an ADR per non-trivial subsystem), the natural question was: **why did we build all this custom FFmpeg infrastructure when the OpenAL pattern is cheaper and cleaner?**

The answer is that the patterns are not interchangeable — they sit at different points on a tradeoff curve. Custom infrastructure costs ongoing maintenance (today's avdevice/avfilter bug in `fetch-ffmpeg.cs` is exactly the kind of regression an upstream package would not have) but buys control (license selection, version pinning, platform-specific patching). Upstream packages cost flexibility (you get whatever build the maintainer ships) but buy zero maintenance.

The decision today is to **keep both patterns** but make the choice deliberate and documented, not implicit.

### Available upstream FFmpeg packages

For completeness, real options exist if we ever decide to migrate FFmpeg off the custom path:

- **`Sdcb.FFmpeg.runtime.{windows-x64,linux-x64,osx-x64,...}`** — well-maintained, ships natives via `runtimes/{rid}/native/`, follows the exact pattern `Silk.NET.OpenAL.Soft.Native` uses. Pairs with `Sdcb.FFmpeg` bindings.
- **`FFmpeg.AutoGen.Bindings.*`** ecosystem variants — same Abstractions root we already use.
- Smaller community packages (varying quality).

None of these are currently right for FrameFlow because they ship **GPL builds**, follow **their own version cadence**, and lack the **macOS dylib install_name patching** our script does. But "currently right" can change; the trigger conditions below name what would tip the balance.

## Decision

### Decision framework for new native dependencies

When adding a new native dependency to this repository, evaluate against these criteria in order. The **first NO** flips the decision to "custom":

1. **Does a maintained upstream package exist that ships the binaries via `runtimes/{rid}/native/`?**
   - If no maintained option exists → custom.
2. **Does the upstream package's build configuration meet our requirements?**
   - License: LGPL-only (no GPL) for the substrate libraries.
   - Version: within the major.minor range we need to support.
   - RID coverage: all platforms we target (today: win-x64/arm64, linux-x64/arm64, osx-x64/arm64).
   - Build flags: include the features we use (codecs, transports, etc.).
3. **Is the consumer-facing experience clean without a custom resolver?**
   - The binding library's own resolver must find DLLs in `{AppContext.BaseDirectory}/runtimes/{rid}/native/` without our intervention.
   - We're not also solving a single-file-publish extraction problem for this lib (if we are, custom infrastructure is needed regardless — per ADR-0019).
4. **Is the maintenance and reachability story acceptable?**
   - Recently updated (last 12 months).
   - Active issue tracker.
   - Reasonable download count / community size — proxy for "won't disappear."

If all four are YES → **use upstream**. If any is NO → **roll custom**, scoped to the specific deficiencies. Document the deficiencies in an ADR so the choice can be revisited.

### Current dependency reaffirmations

Applying the framework to today's three deps:

- **OpenAL**: all four YES. Already on upstream (`Silk.NET.OpenAL.Soft.Native`) as of today. **Confirmed.**
- **SDL2**: criterion 3 fails — SDL2 has a single-file-publish extraction problem that no upstream package solves; a custom `INativeContext` integration is required per ADR-0019. **Custom confirmed.**
- **FFmpeg**: criteria 2 (LGPL-only, version 7.1 specifically, macOS dylib patching) and 4 (we want predictable LGPL builds for the whole 0.x cycle, not at the mercy of an upstream maintainer's GPL switch) both fail. **Custom confirmed.**

No migrations triggered today. The point of this ADR is to make the **status quo deliberate** and the **future-trigger conditions explicit**, not to change anything that's working.

### Triggers to revisit FFmpeg → upstream

The framework's criteria 2 and 4 are the moving pieces. Migrate FFmpeg to a maintained upstream package (most likely `Sdcb.FFmpeg.runtime.*`, with bindings adjusted accordingly) when **any** of these is true:

- An upstream package starts shipping LGPL-only builds for all six target RIDs we use.
- The maintenance cost of `fetch-ffmpeg.cs` exceeds two repo-resident bugs per six months (today's avdevice/avfilter bug is **one**; the macOS dylib install_name handling already contains one tricky failure mode).
- A major FFmpeg version upgrade requires more than one hour to handle in `fetch-ffmpeg.cs` (RID-list churn, archive-format churn, new lib enumerations).
- The macOS path's reliance on Homebrew becomes untenable (e.g., ffmpeg@7 deprecated, breaking dep changes upstream).
- A consumer wants a self-contained NuGet experience and the LGPL constraint can be relaxed.

Until at least one fires, keep the custom path. When one fires, evaluate the upstream options with this framework's criteria 1–4 again and write a migration ADR if the move clears the bar.

### Triggers to revisit OpenAL → custom

Less likely, but for symmetry:

- `Silk.NET.OpenAL.Soft.Native` becomes unmaintained (no release in 18+ months) and a security-relevant CVE in OpenAL Soft goes unaddressed.
- A single-file-publish extraction problem surfaces for OpenAL (analogous to SDL2's) that the upstream package cannot solve.
- We need a fork or patched build of OpenAL Soft for a specific reason (latency tuning, custom backend, etc.) — note this would also reopen the binding question.

### Recording rationale in csproj

For each dependency that uses an upstream native package, the csproj's `PackageReference` line carries an XML comment naming **why** the upstream package is the right choice (or the legacy fact that motivated the switch). For dependencies that use custom infrastructure, the csproj or the fetch-script's source documents the specific upstream gap that justifies the custom path. The goal: a developer reading either file can answer "why is this here / why not the other pattern?" without consulting this ADR. This ADR is the framework; the per-csproj comment is the application.

Example for OpenAL (already applied in `FrameFlow.Audio.OpenAL.csproj`):

```xml
<!--
  Silk.NET.OpenAL is a managed wrapper only — it does not bundle the
  OpenAL Soft native runtime. On a fresh machine with no OpenAL
  installation, ALContext.GetApi() fails with "Could not load from any
  of the possible library names." Silk.NET.OpenAL.Soft.Native ships
  the OpenAL Soft binaries under runtimes/{rid}/native/ so the
  managed wrapper can resolve them without a system-wide install.
-->
<PackageReference Include="Silk.NET.OpenAL.Soft.Native" Version="1.23.*" />
```

## Consequences

### Positive

- Future native-dep additions follow a documented decision framework, not ad-hoc reasoning.
- The status-quo divergence (custom for FFmpeg/SDL, upstream for OpenAL) is no longer accidental — it's a deliberate choice driven by criteria, with each option's tradeoffs explicit.
- Trigger conditions are concrete enough to recognize when met. We will not relitigate the FFmpeg-upstream question every six months; we will revisit it when one of the named triggers fires.
- The csproj-comment convention surfaces the rationale where developers actually read code, not buried in an ADR.

### Negative

- Two patterns means two maintenance surfaces. New contributors must learn both to navigate the codebase.
- The framework's criteria 2 and 4 are judgement calls (what counts as "acceptable maintenance"?). Two reasonable engineers could disagree on whether a trigger has fired. This is unavoidable; the ADR's job is to make the question explicit, not to eliminate it.
- The "csproj comment names the rationale" convention adds friction to adding/removing package references — easy to forget if not enforced in review.

### Unchanged

- `FrameFlow.Native.Runtime.csproj` continues to exist and ships FFmpeg natives as its own NuGet (this repository's home-grown equivalent to `Silk.NET.OpenAL.Soft.Native`). Today's decision doesn't affect that artifact's role for downstream consumers.
- ADR-0014's native-binary-packaging conventions still apply to whatever lives in `runtimes/{rid}/native/`, regardless of how it got there (upstream package or custom fetch).
- ADR-0019's `INativeContext` seam still applies to libraries whose managed wrappers can't resolve adjacent natives (today: SDL2; potentially OpenAL in a single-file-publish scenario).

## Alternatives Considered

### Migrate everything to upstream packages now

Considered. Concretely: switch FFmpeg to `Sdcb.FFmpeg` + `Sdcb.FFmpeg.runtime.*`, switch SDL2 to its upstream equivalent (no clean one exists for the single-file-publish case anyway).

Rejected for FFmpeg because of the LGPL constraint — `Sdcb.FFmpeg.runtime.*` ships GPL builds. Switching means a license-compatibility audit and a downstream-consumer-facing change for anyone consuming FrameFlow's LGPL guarantee. The audit is the bigger blocker; not done lightly.

Rejected for SDL2 because no upstream package solves the single-file-publish extraction problem ADR-0019 addresses; switching loses real capability.

### Migrate everything to custom infrastructure (build an OpenAlBootstrapper too)

Considered, mostly as the dual of the above. Rejected because it spends engineering time on a problem the upstream package already solves — the OpenAlBootstrapper would replicate the work `Silk.NET.OpenAL.Soft.Native` does for free. There's no specific deficiency in the upstream package that custom infrastructure would address today.

### Maintain a single bootstrapper abstraction that wraps either pattern

Considered. A `NativeDependencyBootstrapper` base class with two strategies — "upstream NuGet" (no-op, the package handles resolution) and "custom fetch" (the FFmpeg/SDL2 path) — would unify the call sites.

Rejected as premature abstraction. The two patterns don't have meaningfully shared behaviour at call time: upstream packages need no resolver intervention (the binding library does it), while custom-fetch deps run a startup probe + `DllImportResolver` or `INativeContext` integration. Wrapping "do nothing" alongside "do a lot" in one abstraction provides no value over keeping them separate.

### Skip the framework, decide ad-hoc per dependency

Considered. The current state arrived ad-hoc and works fine. The cost of writing this ADR is paid; the benefit (less re-derivation when a future native dep arrives) is small.

Rejected because re-deriving the FFmpeg question every six months is what we want to avoid — particularly when the trigger conditions are subtle (LGPL vs GPL, RID coverage, macOS dylib patching) and easy to overlook in a code review of "should we just switch to Sdcb.FFmpeg?". Writing the framework once is the cheap option.

## References

- [ADR-0002] — FFmpeg bootstrap strategy. The original "explicit, diagnosable, isolated" principle that motivates the custom path for FFmpeg today.
- [ADR-0014] — Native binary packaging. The `runtimes/{rid}/native/` convention both patterns target.
- [ADR-0019] — SDL native bootstrap seam. The single-file-publish concern that keeps SDL on the custom path and the (now-superseded by today's PackageReference fix) note that OpenAL would follow the same seam.
- `scripts/fetch-ffmpeg.cs` — the FFmpeg fetcher; today's avdevice/avfilter bug fix lives here.
- `src/FrameFlow.Audio.OpenAL/FrameFlow.Audio.OpenAL.csproj` — the OpenAL csproj with the upstream-package rationale comment.

[ADR-0002]: ADR-0002-ffmpeg-bootstrap-strategy.md
[ADR-0014]: ADR-0014-native-binary-packaging-and-distribution.md
[ADR-0019]: ADR-0019-sdl-native-bootstrap-seam.md
