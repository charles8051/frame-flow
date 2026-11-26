# ADR (draft): FFmpeg 9, shipped as one runtime package for every RID

**Status:** Proposed. Records decisions already in the tree; numbered at merge.
**Supersedes, in part:** [ADR-0011](ADR-0011-ffmpeg-interop-binding-approach.md) (the FFmpeg 7.x
pin), [ADR-0014](ADR-0014-native-binary-packaging-and-distribution.md) (one runtime package per
RID, the RID list, the version-encoding scheme and system FFmpeg as a development source) and
[ADR-0046](ADR-0046-native-runtime-acquisition-strategy.md) (its FFmpeg 7.1 and Homebrew
premises).
**Related:** ADR-0002 (FFmpeg bootstrap strategy), ADR-0017 (struct access via generated
bindings), ADR-0070 (the resolver installs itself on first native use)

## Context

Three accepted records still describe FFmpeg as it was planned rather than as it ships:

- **ADR-0011** pins the interop to "a single FFmpeg major version (7.x at time of writing)". The
  single-major rule stands. The major moved to 9 in #351, when the struct bindings moved to
  `FFmpeg.AutoGen.Abstractions` 9.0.1.1.
- **ADR-0014** decides one NuGet package per runtime identifier,
  `FrameFlow.Native.runtime.<rid>`, each with its own `.nuspec` and `.targets`, for six RIDs
  including osx-x64, and a package version that encodes the FFmpeg version in build metadata. None
  of those packages was ever built. It also lists a system install (`apt install ffmpeg`,
  `brew install ffmpeg`) as a development source.
- **ADR-0046** reaffirms the custom FFmpeg pipeline on the premise of "version 7.1 specifically"
  and a Homebrew-based macOS path.

What ships instead is one package, `FrameFlow.Native.Runtime`, built from
`nuget/FrameFlow.Native.Runtime.csproj` and published to nuget.org. The stale records matter
beyond the archive: the loader's own failure message carried the "7.x" forward until #864, and a
reader following ADR-0014 would install a distribution FFmpeg that FrameFlow cannot load.

## Decision

### 1. FrameFlow pins FFmpeg 9

The interop targets FFmpeg 9.0: libavutil 61, libswresample 7, libswscale 10, libavcodec 63 and
libavformat 63. ADR-0011's rule is unchanged: one major, no runtime adaptation across majors.

The pin has one source, the `FFmpeg.AutoGen.Abstractions` package reference in
`src/FrameFlow.Native/FrameFlow.Native.csproj`. The ABI check reads its expected majors from that
package's constants (`FfmpegAbiCheck.ExpectedMajor`), and the load-failure message is built from
the same constants, so neither can name a release the loader would refuse. The versioned file
names written elsewhere in the tree are held to it by `SonameConsistencyTests`.

### 2. One runtime package carries every RID

`FrameFlow.Native.Runtime` carries the five FFmpeg libraries FrameFlow P/Invokes for win-x64,
win-arm64, linux-x64, linux-arm64 and osx-arm64, under the standard `runtimes/<rid>/native/`
layout, so NuGet's own runtime-asset resolution places them and no `.targets` file is needed. It
replaces ADR-0014's per-RID package table.

- osx-x64 is not supported.
- `FrameFlow.Native`'s own package carries the win-x64 libraries as well, so a Windows x64 app
  runs without the runtime package. Every other RID needs it.
- The runtime package's version is its own SemVer, not an encoding of the FFmpeg version. The
  repository pins the version its own projects use exactly (`FrameFlowNativeRuntimeVersion` in
  `Directory.Build.props`), because a floating range could resolve to a package with an older
  FFmpeg major, which the loader refuses.

### 3. A system FFmpeg is not a supported source

A distribution or Homebrew FFmpeg is supported only when it is the pinned major, and in practice
it rarely is. FrameFlow resolves the versioned names (`libavcodec.so.63`), so another major is not
found, and the ABI check refuses one that is loaded by another name. The supported sources are
the runtime package, `scripts/fetch-ffmpeg.cs` for development, and an FFmpeg 9 build named by
`FrameFlowNativeOptions.CustomFfmpegPath`. Every RID, macOS included, fetches a pinned and hashed
artifact; no Homebrew keg is involved.

## Consequences

- A Linux, macOS or Windows Arm64 app must reference `FrameFlow.Native.Runtime`. The repository's
  `FFNATIVE001` publish check enforces that only for projects built inside this repository, so a
  consumer learns it from the README or from the load-failure message, which names the package.
- One package carries every RID's libraries, so every consumer restores all of them. That is the
  cost of not maintaining per-RID packages, and it is accepted.
- An FFmpeg major bump moves the binding package, the versioned names the consistency test
  checks, the runtime package's contents and the repository's exact runtime-package pin together.

## Alternatives considered

- **Build ADR-0014's per-RID packages.** Smaller restores per app, at the cost of five packages,
  five version streams and a metapackage or documentation to choose between them. Not pursued:
  nothing about the single package has caused a problem that per-RID packages would solve.
- **Rewrite ADR-0011, ADR-0014 and ADR-0046 in place.** Accepted records are superseded, not
  rewritten, so the reasoning that led to the original choices stays readable.
