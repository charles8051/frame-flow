# ADR-0070: The FFmpeg resolver installs itself on first native use

## Status

Accepted (2026-09-11).

Amends [ADR-0002](ADR-0002-ffmpeg-bootstrap-strategy.md). That record rejected the
alternative "let decoder creation implicitly trigger FFmpeg loading" because it hides
environment failures in the wrong layer. This decision adopts the loading half of that
alternative and keeps the rejection's substance by making the failure louder, not
quieter.

Resolves issue #124 and issue #55.

### What shipped

| | |
| --- | --- |
| Resolver registered from a module initializer | `src/FrameFlow.Native/FfmpegNativeLibraryLoader.cs` |
| Search directory read from a static, not captured at registration | same |
| Default bootstrap run once on a resolver miss | same |
| `DllNotFoundException` carrying the bootstrap's diagnostic | same |
| Cold-start regression tests | `tests/FrameFlow.ColdStart.Tests/` |

## Context

### The bootstrap was a convention, and conventions are not reachable from a package graph

`NativeLibrary.SetDllImportResolver` was called in one place, inside
`FfmpegNativeLibraryLoader.TryLoad`, reached only from `FrameFlowBootstrapper.Initialize()`.
Four call sites reached that: `MediaPlayer`, `MediaPlaylistPlayer`, `PlayerBuilder`, and
the `AddHostedBootstrap` DI extension. Three of the four are in `FrameFlow.Player`.

`FrameFlow.Decoding` ships as its own package and depends on `FrameFlow.Native`, so the
resolver is already in its dependency closure. Nothing installed it. A project that
references `FrameFlow.Decoding` has no dependency on `FrameFlow.Player` at all, so there
is nothing in its package graph to suggest that the thing which makes its first P/Invoke
work lives there.

The result, on a fresh console app with `FrameFlow.Decoding` and
`FrameFlow.Native.Runtime` and nothing else:

```
System.DllNotFoundException: Unable to load DLL 'avformat' or one of its dependencies:
The specified module could not be found. (0x8007007E)
   at FrameFlow.Native.Interop.FFAvFormat.avformat_open_input(...)
   at FrameFlow.Decoding.DemuxSessionFactory.OpenAsync(...)
```

The binaries were present the whole time, in `runtimes/win-x64/native/`. Nothing mapped
`avformat` to `avformat-61.dll` at that path, which is the resolver's entire job. A
consumer reading that message concludes the runtime package failed and debugs packaging.

### The same defect, twice, one layer apart

Issue #55 is this defect at the playback layer: `PlaybackController.Create` does not
bootstrap while `MediaPlayer.CreateAsync` does. Issue #124 is the decoding layer.

Both were introduced the same way. The DI path made the bootstrap structural — a player
could not be resolved without the hosted service having run. The explicit-construction
surfaces replaced that guarantee with a convention that each new surface has to remember.
Adding a bootstrap call to each public surface needs a new call site every time one is
added, which is how both of these were missed.

## Decision

### The resolver is installed by the module, not by a caller

`FfmpegNativeLibraryLoader.InstallResolver` is a `[ModuleInitializer]`. It runs before
the first P/Invoke in `FrameFlow.Native` regardless of what the consumer called, and it
registers a `DllImportResolver` and nothing else. No library is loaded, no path is
probed, no FFmpeg function is called.

This is the part that closes both issues. Registration is what maps `avformat` to
`avformat-61.dll` under `runtimes/{rid}/native/`, and it is now unconditional.

### The resolver reads its search directory instead of capturing it

Registration used to happen inside `TryLoad`, so the resolver closed over that call's
`searchPath`. A module initializer has no options to close over, and the guard that makes
registration once-per-process would have pinned the resolver to `null` forever — an
explicit `Initialize()` with `CustomFfmpegPath` would have registered nothing and its
path would have been silently dropped.

The search directory is a static that `TryLoad` writes on every attempt. A bootstrap that
falls back from bundled to system resolution moves the resolver with it, which the
captured version did not do either.

### A resolver miss runs a default bootstrap, once

When a P/Invoke asks for a library that no explicit bootstrap has loaded, the resolver
runs `new FrameFlowBootstrapper(new FrameFlowNativeOptions { SkipHardwareProbe = true })`
and initializes it, once per process, then re-checks the handle table.

Three details make that safe:

- **Re-entrancy.** The bootstrap's own version probe is a P/Invoke, so it re-enters the
  resolver. A `[ThreadStatic]` flag makes the re-entrant call fall through to the handle
  table, which by then holds `avutil`.
- **Lock order.** The implicit bootstrap's lock is always taken outside the loader's lock,
  never the reverse. A second thread that arrives mid-bootstrap waits rather than racing
  ahead to a probe that would fail.
- **The hardware probe is skipped.** Its result is reachable only through a
  `FrameFlowBootstrapResult`, which an implicit bootstrap never hands to anyone, and
  `av_hwdevice_ctx_create` is not something to run from inside a resolver callback.

### A failed implicit bootstrap says so

Returning zero from the resolver produces `Unable to load DLL 'avformat'`, which is the
message that sent consumers after the runtime package. When the implicit bootstrap ran and
failed, the resolver throws `DllNotFoundException` carrying the bootstrap's own diagnostic:
the candidate paths it searched, and the options to configure.

This is the part that answers ADR-0002's rejection. The environment failure is not hidden
by loading implicitly — it is reported in more detail than before, in the same exception
type any existing `catch` already handles.

### What did not change

`Initialize()` is unchanged and still wins when it runs first: it populates the handle
table before any P/Invoke can reach the resolver, so the implicit path is never entered.
It remains the only way to select binaries, and the only way to obtain
`HardwareDecodeCapabilities`. Nothing about the public API moved.

## Consequences

### Positive

- `FrameFlow.Decoding` and `FrameFlow.Playback` work as the packages they are advertised
  as. Their first P/Invoke is not a failure.
- A new public construction surface cannot reintroduce this. There is no per-surface call
  site to forget.
- The failure that remains — no FFmpeg anywhere on the machine — names the environment
  rather than the DLL.

### Negative

- **An implicit bootstrap uses default options.** A consumer who sets `CustomFfmpegPath`
  but touches a decoding API before calling `Initialize()` gets bundled or system binaries
  instead, silently as far as the call goes. `TryLoad` logs a warning naming both paths
  when it sees a second, different request, but a consumer with no logger configured will
  not see it. The ordering requirement is unchanged from before this decision; what
  changed is that getting it wrong no longer throws.
- **Hardware-decode capability discovery still needs an explicit `Initialize()`.** A
  process that only ever bootstraps implicitly gets `HardwareDecodeCapabilities.Empty`,
  and `HardwareDecodeMode.Auto` falls through to software decode. This matches what such a
  process could have had before, since it had no result object to read capabilities from.
- **CA2255 is suppressed.** The rule warns against `[ModuleInitializer]` in libraries. The
  suppression carries its justification: installing a `DllImportResolver` is the case the
  rule's own guidance allows, and no consumer call site can guarantee the ordering it needs.
- **Bootstrap work can now happen on a thread that did not ask for it**, inside a resolver
  callback, the first time any thread P/Invokes. It is bounded — five `NativeLibrary.TryLoad`
  calls and one version probe — and happens once.

### Neutral

- `tests/FrameFlow.ColdStart.Tests` exists for one reason: every other test project
  bootstraps from a collection fixture, so a cold-start regression is invisible in all of
  them regardless of where the assertion is placed. Keep that project free of a bootstrap
  call and of a reference to any project that makes one.

## Alternatives Considered

### Add a bootstrap call to each public construction surface

Rejected. It is the fix that was already in place for `MediaPlayer.CreateAsync` and is
exactly how both #124 and #55 happened: it needs a new call site every time a surface is
added, and nothing fails when one is missed until a consumer hits it in a process that
never went through the player layer.

### Keep the loading behaviour and only improve the message

Throw an exception naming the missing bootstrap instead of surfacing a DLL-load error —
the second option offered in #55. Rejected as insufficient. It makes a `FrameFlow.Decoding`
consumer's first experience a failure with instructions, when the library has everything it
needs to succeed. It was adopted for the case where it is the truth: the implicit bootstrap
ran and could not find FFmpeg.

### Restore the DI-only guarantee

Make the hosted bootstrap the only supported path again, so resolution is structural.
Rejected: the explicit-construction surfaces are documented entry points, and three of
them are in the README. Removing them to fix a loading bug is not a trade worth making.

### Register the resolver from a static constructor on the interop types

Rejected. It puts the guarantee on each interop type rather than on the module, so it has
the same shape of gap as the per-surface bootstrap call — a new interop type without the
static constructor is a new hole. The module initializer covers the assembly by
construction.
