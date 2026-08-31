# ADR-0065: Capability discovery on the player surface

> Drafted on branch `claude/sink-volume-control`; assigned ADR-0065 on merge to
> `main` per this repository's "number-at-merge" rule.

## Status

Accepted. Implemented for gain control: `AudioSinkCapabilities` deleted,
`IVolumeControl` extracted, `IMediaPlayer.SupportsVolumeControl` added.

**Date:** 2026-08-25
**Tracking:** `charles8051/frame-flow` #95, under the #92 sink-contract epic.
**Related:**
- ADR-0035 (master clock interface split) — the precedent this generalizes.
  `IMasterClock`, later `FrameFlow.Graph.IClockSource`, was the first capability
  moved off `IAudioSink` and onto a side-implemented interface.
- ADR-0044 (sink ownership and disposal) — unchanged; this ADR does not touch
  the dataflow or lifecycle facets.
- ADR-0006 (extension seams) — capability discovery is one of the seams that
  document anticipates.

## Context

`IAudioSink` carried an `AudioSinkCapabilities` record with four flags:
`SupportsPauseResume`, `SupportsPlaybackClock`, `PreferredChannels`, and
`SupportsVolumeControl`.

Every one of them was write-only. `OpenAlAudioSink` set all four to constants;
the only readers were tests asserting back the values the tests had just
constructed. No production code branched on any flag. Each was redundant with
something that already existed:

| Flag | Already answered by |
|---|---|
| `SupportsPauseResume` | `IAudioSink` declares `PauseAsync` / `ResumeAsync` |
| `SupportsPlaybackClock` | ADR-0035's `_audioSink is IClockSource` type test |
| `PreferredChannels` | `FrameFlowOptions.PreferredChannels`, which the DI path reads |
| `SupportsVolumeControl` | nothing — see below |

`SupportsVolumeControl` was the one flag describing something real, and its
documented contract was the problem. A sink whose flag was `false` "may still
expose this property as a no-op (silently dropping the write)." A runtime
boolean guarding a compile-time question, whose failure mode was a write that
looked like it worked.

The consequence was visible in the UI. `FrameFlowVolumeControl` is a slider
bound to `IMediaPlayer.Volume`. It had no way to distinguish "this sink has no
gain stage" from "volume is currently zero", so against a capture sink it
rendered as live and did nothing.

## Decision

### 1. Capabilities that exist at compile time are interfaces

Gain control moves to `IVolumeControl { float Volume; bool Muted; }`, in
`FrameFlow.Media`, side-implemented by sinks that own a mixer or device gain.
`AudioSinkCapabilities` is deleted.

`Muted` needs no capability of its own. A sink with no gain stage cannot mute
either, so one interface covers both members.

This is ADR-0035's shape applied a second time. A sink that cannot do something
does not implement the interface for it, and the compiler carries the fact.

### 2. The player surface exposes a `Supports…` property, not a capability record

A consumer holds an `IMediaPlayer`. It never sees the sink, so it cannot type-test
for `IVolumeControl` itself. `IMediaPlayer.SupportsVolumeControl` asks that
question on its behalf, and is defined as exactly the type test:

```csharp
_volumeControl = audioSink as IVolumeControl;
public bool SupportsVolumeControl => _volumeControl is not null;
```

**This is not the deleted flag moved up a layer.** The sink-level flag was
redundant with a type test that anyone holding a sink could perform. At the
player layer no type test is available, so the property is the only way to
surface the answer. In the final shape the fact is stated once, at the boundary
that cannot derive it.

Future capabilities on `IMediaPlayer` follow this shape: a `Supports…` property
backed by a type test on the composed object, never a capability record handed
up from below.

### 3. Unsupported writes are a documented no-op, and reads round-trip

When `SupportsVolumeControl` is `false`:

- **The setter does not throw.** A consumer that respects the flag never writes.
  A consumer that ignores it should not take down the application over a
  cosmetic control. The original defect was not that the write was dropped, it
  was that the drop was *undiscoverable*; with the capability exposed and
  documented, dropping it is coherent.
- **The getter returns the last value written**, defaulting to unity. A UI reads
  the value back to render a slider position and a speaker glyph. If setting
  `0.5` read back as something else, the glyph would lie. Note the previous
  implementation returned `0f` for a missing sink, which a level-bucketing glyph
  renders as muted.

Validation is unaffected, and applies on both paths. `IVolumeControl.Volume`
rejects negative and `NaN` gain with `ArgumentOutOfRangeException`, and the
player validates before storing a detached value too — otherwise
`IMediaPlayer.Volume` would be the one route by which a caller could read back
`NaN`. The no-op rule covers the absent capability, not invalid input.

### 4. UI gates on the capability

`FrameFlowVolumeControl` disables its slider and mute toggle when
`SupportsVolumeControl` is `false`. `FrameFlowPlayerChrome` ignores the `M`
shortcut in the same case, so the glyph cannot flip to a mute state the audio
never entered.

## Consequences

`IAudioSink` is now three things and nothing else: one dataflow method
(`PresentAsync`), a device transport (`ActivateAsync` / `PauseAsync` /
`ResumeAsync` / `DeactivateAsync`), and diagnostics. Gain and clock are both
side-implemented.

Eight test doubles dropped `Capabilities`, `Volume`, and `Muted` — 200-odd
characters of identical boilerplate each. None of them render audio, so none
implement `IVolumeControl`, which gives the type test real coverage rather than
a synthetic case.

`AudioSinkCapabilitiesTests` is deleted. Three capability-echo assertions in
`OpenAlAudioSinkTests` are replaced by two type tests that observe something the
compiler enforces.

**Breaking**, and permitted by the pre-1.0 stance: no external consumers,
no stability commitment. Any sink implementation
outside the tree loses `Capabilities`, `Volume`, and `Muted` from the interface
and must implement `IVolumeControl` to keep gain.

## Alternatives considered

**Keep `AudioSinkCapabilities`, read it somewhere.** Rejected. Three of four
flags duplicate information the type system already carries, and giving a record
a reader to justify its existence is backwards.

**`float? Volume` on `IMediaPlayer`, null meaning unsupported.** Rejected. It
conflates "unsupported" with "unknown", makes the common write awkward, and is a
worse binding target.

**`IVolumeControl? VolumeControl { get; }` on `IMediaPlayer`.** The cleanest in
the abstract: one property answers both "can I" and "how", and the unsupported
case becomes unrepresentable rather than silently ignored. Rejected for
ergonomics — it removes two flat properties in exchange for binding through a
nullable sub-object, and `FrameFlowVolumeControl` sets `IsEnabled` imperatively
anyway, so the safety it buys is not collected.

**Extract the audio transport to an optional interface too.** Rejected, and
recorded in #92 as an explicit non-goal. `ActivateAsync` and friends are called
at nine sites across `SubstrateSession`, `MediaPlayer`, and
`MediaPlaylistPlayer`. Making them optional converts nine direct calls into nine
type tests to buy symmetry with a concept — a video surface's transport — that
does not exist.
