# ADR-0041: Deployment Automation and Publish RID Discipline

## Status

Accepted

## Context

FrameFlow is intended to support both local development and automated deployment flows. A recent inspection against Roam-style deploy automation surfaced a sharp distinction between those two concerns:

- local build/run on the same machine works well with the current repository-local FFmpeg layout
- automated publish/deploy across machines or across runtime identifiers is more constrained

The current state has three important properties:

1. FrameFlow currently checks in no `.pubxml` publish profiles.
2. `Directory.Build.targets` copies FFmpeg binaries from `runtimes/{rid}/native/` into build output for development-time convenience.
3. The RID used by that target is inferred from the build host OS/architecture, not from the publish target RID when cross-publishing.

This is acceptable for the narrow case of:

- building on Linux for Linux
- building on Windows for Windows
- building on macOS for macOS

It is not sufficient for cross-RID deployment automation such as:

- build on Linux, publish for `win-x64`, deploy to Windows
- build on macOS, publish for `linux-arm64`, deploy to Linux
- any tool that assumes publish output already represents the intended target runtime shape

During inspection, `dotnet publish` of `FrameFlow.Examples.ConsoleMediaInspector` produced these results:

- framework-dependent publish included FFmpeg native libraries under `runtimes/linux-x64/native/`, but still required a machine-installed .NET runtime
- self-contained `linux-x64` publish included the Linux FFmpeg native libraries and ran successfully
- self-contained `win-x64` publish from a Linux host still carried `runtimes/linux-x64/native/` content rather than Windows FFmpeg DLLs

That last point is the critical incompatibility. Deployment tooling such as Roam should not need FrameFlow-specific binary rewriting logic. Publish output should already contain the correct native assets for the intended target runtime.

This ADR clarifies the packaging discipline needed for deployment tooling compatibility.

## Decision

### 1. Publish output is the deployment contract

Deployment automation should treat `dotnet publish` output as the complete artifact boundary. FrameFlow will not require deployment tools to understand or transform FFmpeg native assets after publish.

If published output is correct, a deploy tool may simply sync the publish directory to the target machine.

### 2. Native FFmpeg asset selection must follow the publish target RID

When `$(RuntimeIdentifier)` is set during publish, native FFmpeg asset selection must be driven by that target RID, not by the build host RID.

In other words:

- explicit publish RID wins
- build host RID is only a fallback for local development builds where no publish RID is provided

This applies to any MSBuild targets, packaging targets, or helper logic that choose which files from `runtimes/{rid}/native/` are copied into build or publish output.

### 3. Explicit publish profiles are the preferred deploy surface

FrameFlow will prefer checked-in named `.pubxml` publish profiles for deployable applications and examples.

Those profiles should make target intent explicit, including where relevant:

- `RuntimeIdentifier`
- `SelfContained`
- configuration
- any other publish settings required for deterministic deployment

This keeps deployment shape declarative and consumable by automation tools without inventing a FrameFlow-specific publish schema.

### 4. Local-development convenience targets must not define cross-deploy semantics

`Directory.Build.targets` may continue to support development-time copying from repo-local `runtimes/{rid}/native/`, but that convenience mechanism must not be the only path that determines deployment correctness.

If a target is only appropriate for local same-machine development, that limitation should be explicit in comments and documentation.

### 5. Roam and similar deploy tools are consumers of publish correctness, not compensators for publish mistakes

Deployment tools should not contain FrameFlow-specific logic such as:

- rewriting `runtimes/{rid}/native/` after publish
- swapping FFmpeg binaries based on target host after artifacts are produced
- patching output directories to compensate for host-RID-based packaging

The correct place to solve those problems is the FrameFlow publish pipeline itself.

## Consequences

### Positive

- deployment automation becomes straightforward: publish, sync artifacts, run
- cross-RID publish correctness becomes an explicit engineering requirement
- packaging assumptions become testable in CI rather than discoverable only during remote deploy attempts
- local development convenience remains possible without letting it silently define deployment correctness

### Negative

- `Directory.Build.targets` and related packaging logic may need restructuring
- examples and deployable applications should eventually add and maintain checked-in publish profiles
- CI/publish verification should grow RID-aware checks to enforce this discipline

### Neutral / clarifying

- this ADR does not require any specific deployment tool
- this ADR does not require self-contained deployment in every scenario
- this ADR does require that whatever publish mode is chosen produces native assets matching the intended deployment RID

## Alternatives considered

### 1. Keep using build-host RID for native asset selection

Rejected.

This makes same-machine development easy, but it breaks the moment publish output is expected to represent a different target runtime than the build machine.

### 2. Let deployment tools repair the output after publish

Rejected.

That pushes packaging correctness downstream into every deploy tool and every deployment script. It duplicates logic, hides the real boundary, and makes publish output itself unreliable.

### 3. Introduce a FrameFlow-specific deploy manifest separate from publish output

Rejected for now.

That would add another packaging concept without solving the more basic issue: publish output should already be correct and self-describing for the chosen RID.

### 4. Rely only on local development layout and defer deploy correctness until later

Rejected.

The project is explicitly building toward reusable playback components and application hosts that should be publishable and deployable. Deferring deployment-shape correctness too long would harden the wrong assumptions into the bootstrap path.

## Follow-up work

1. Update `Directory.Build.targets` so FFmpeg/native asset selection prefers `$(RuntimeIdentifier)` when present.
2. Keep host-RID fallback only for local development scenarios with no explicit publish RID.
3. Add checked-in publish profiles for deployable examples/apps where target intent should be explicit.
4. Add verification that a `win-x64` publish contains Windows native assets, a `linux-x64` publish contains Linux native assets, and so on.
5. Keep bootstrap probing aligned with published layout, but do not rely on probing to compensate for incorrect packaging.
