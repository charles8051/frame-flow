# ADR-0002: FFmpeg Bootstrap Strategy

## Status

Accepted

## Context

FrameFlow depends on FFmpeg native binaries, and those binaries may come from:

- a custom user-provided path
- bundled application/package assets
- system-installed FFmpeg

Different platforms resolve native library dependencies differently, and the presence of a file path alone does not guarantee that the process can actually call the bindings successfully.

The bootstrap strategy needs to be:

- explicit
- diagnosable
- testable
- independent from playback logic

## Decision

FrameFlow will centralize native setup in a dedicated bootstrap layer owned by `FrameFlow.Native`.

The resolution order will be:

1. custom path
2. bundled binaries
3. system-installed binaries

Bootstrap responsibilities will include:

- determining runtime identifier and candidate search paths
- configuring binding resolution
- probing the FFmpeg environment with at least one callable FFmpeg function
- returning a structured bootstrap result with binary source and diagnostics

Bootstrap will be separate from playback session creation and separate from demux/decode logic.

## Consequences

### Positive

- native concerns remain isolated
- failures become more actionable
- packaging and deployment decisions stay decoupled from playback logic
- lower layers can assume FFmpeg is already available once bootstrap succeeds

### Negative

- bootstrap becomes a critical subsystem that must be tested carefully across platforms
- extra structure is required before any media logic can run

## Alternatives Considered

### Let decoder creation implicitly trigger FFmpeg loading

Rejected because it hides environment failures in the wrong layer.

**Amended by [ADR-0070](ADR-0070-resolver-installs-itself-on-first-native-use.md).** The
loading half of this alternative was adopted: a P/Invoke that finds nothing loaded runs a
default bootstrap once. While the bootstrap was reachable only from `FrameFlow.Player`,
every entry point below it threw `Unable to load DLL 'avformat'` — an environment failure
wearing a packaging error's clothes, which is the outcome this rejection was written to
avoid. ADR-0070 keeps the rejection's substance by making the remaining failure carry this
layer's own diagnostic instead.

### Only support bundled binaries

Rejected because it reduces flexibility for development and deployment.

### Central bootstrap with structured probing

Accepted because it isolates native concerns and supports multiple deployment strategies cleanly.
