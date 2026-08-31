# ADR-0019: SDL Native Bootstrap Seam

**Status:** Proposed
**Date:** 2026-03-31
**Supersedes:** None
**Related:** ADR-0002 (FFmpeg bootstrap strategy), ADR-0014 (native binary packaging), ADR-0018 (SDL presenter and audio adapter), ADR-0046 (native runtime acquisition strategy)

**Amendment 2026-05-15:** The §"OpenAL" forward-looking note below has been superseded — the trigger fired, but the chosen solution was the upstream `Silk.NET.OpenAL.Soft.Native` package rather than a custom `OpenAlBootstrapper`. See §"OpenAL" for the updated record and [ADR-0046](ADR-0046-native-runtime-acquisition-strategy.md) for the decision framework that drove that choice.

## Context

ADR-0002 established that native library resolution must be explicit, diagnosable, and isolated from playback logic. `FrameFlow.Native` implements this for FFmpeg: a dedicated `FrameFlowBootstrapper` resolves binary paths, loads libraries, and registers a `DllImportResolver` for its own assembly.

`FrameFlow.Sdl` has no equivalent. SDL2 is loaded implicitly when `Silk.NET.SDL.Sdl.GetApi()` is called, relying on the OS library loader to find it. In development builds this works because SDL2.dll lands next to the executable. In a self-contained single-file publish it fails.

### Why single-file publish breaks SDL loading

When `PublishSingleFile=true` and `IncludeNativeLibrariesForSelfExtract=true`, the .NET AppHost bundles all native libraries into the single executable and extracts them to a temporary hash directory at startup. The extraction path depends on each file's relative location in the original publish output:

- **FFmpeg DLLs** are placed in `runtimes/{rid}/native/` by `Directory.Build.targets`'s explicit `Link=` metadata and extract to `{hash}/runtimes/{rid}/native/`.
- **SDL2.dll** is placed at the publish output root by Silk.NET's own `.targets` file (it flattens native assets when a RID is active) and extracts to `{hash}/SDL2.dll` — the hash directory root, not a `runtimes/` subdirectory.

`AppContext.BaseDirectory` points to the directory containing the executable. In single-file mode the executable directory contains no loose DLLs. `NativeLibrary.Load("SDL2")` searches the executable directory and PATH — neither includes the extraction hash directory. `Sdl.GetApi()` therefore throws `FileNotFoundException`.

### Why the FFmpeg fix does not apply here

`FrameFlowBootstrapper` uses `NativeLibrary.SetDllImportResolver` to intercept P/Invoke calls for the `FrameFlow.Native` assembly and redirect them to the resolved library path. This works because FrameFlow owns the P/Invoke declarations.

SDL2's P/Invoke declarations live inside `Silk.NET.SDL`, a third-party package. FrameFlow cannot register a `DllImportResolver` for an assembly it does not own. Silk.NET instead exposes an `INativeContext` abstraction — a per-instance interface that controls how a given API object resolves its native function pointers. This is the correct integration seam.

### Current workaround and why it must not stay

The investigation in `docs/investigations/2026-03-31-single-file-publish-bootstrap-logging.md` added a `PreloadNativeLibraries` helper to `SdlPlayer/Program.cs` that:

1. Enumerates bundle extraction hash directories.
2. Locates `SDL2.dll` by walking up from the FFmpeg search path.
3. Pre-loads it via `NativeLibrary.TryLoad(fullPath)` to seed the OS module cache.
4. Relies on the Windows module cache behaviour that a subsequent `NativeLibrary.Load("SDL2")` returns the already-loaded handle.

This is application-level code that encodes knowledge of extraction layout internals. Every SDL-based consumer must either copy this helper or accept that SDL fails in single-file publish. It also relies on OS module cache side-effects rather than an explicit integration contract. Both properties violate the ADR-0002 principle that native concerns must be isolated from consuming code.

## Decision

`FrameFlow.Sdl` will own SDL2 library resolution through a dedicated `SdlBootstrapper` class that follows the same shape as `FrameFlowBootstrapper` but integrates with Silk.NET through a custom `INativeContext` implementation rather than a `DllImportResolver`.

### Resolution order

SDL2 library resolution follows the same priority as FFmpeg (ADR-0002):

1. `SdlNativeOptions.CustomSdlLibraryPath` — explicit full path to the SDL2 shared library, takes priority over all other sources.
2. App-relative bundled path — `{AppContext.BaseDirectory}/runtimes/{rid}/native/{sdlFileName}` (standard NuGet runtime layout, dev builds).
3. App-relative root — `{AppContext.BaseDirectory}/{sdlFileName}` (regular self-contained publish, non-single-file).
4. Bundle extraction probe — enumerate `{DOTNET_BUNDLE_EXTRACT_BASE_DIR}/{appName}/*/` hash directories and look for `{sdlFileName}` at each hash directory root (single-file publish layout).
5. System library — bare library name passed to `NativeLibrary.TryLoad` with no path, relying on the OS loader as a final fallback.

### `INativeContext` integration with Silk.NET

`SdlBootstrapper.Initialize()` resolves the library path and loads it via `NativeLibrary.Load(resolvedPath)`, storing the handle. `SdlBootstrapper.CreateSdlApi()` wraps that handle in an internal `SdlNativeContext` implementation and constructs the `Sdl` API instance:

```csharp
public Sdl CreateSdlApi()
{
    if (!IsInitialized || _sdlHandle == 0)
        throw new InvalidOperationException(
            "SdlBootstrapper must be successfully initialized before creating API instances.");
    return new Sdl(new SdlNativeContext(_sdlHandle));
}
```

`SdlNativeContext` implements `INativeContext` by delegating `TryGetProcAddress` to `NativeLibrary.TryGetExport` on the stored handle:

```csharp
internal sealed class SdlNativeContext : INativeContext
{
    private readonly nint _handle;

    internal SdlNativeContext(nint handle) => _handle = handle;

    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
        => NativeLibrary.TryGetExport(_handle, proc, out addr);

    // SDL2 has no unload concept; the handle is kept for the process lifetime.
    public void Dispose() { }
}
```

This means Silk.NET never independently searches for SDL2. Every `Sdl` instance produced by `CreateSdlApi()` is backed by an already-resolved handle, regardless of publish mode or PATH state.

Consuming code creates a `Sdl` instance through the bootstrapper rather than calling `SdlApi.GetApi()` directly:

```csharp
// Before
var sdl = SdlApi.GetApi();

// After
var sdl = sdlBootstrapper.CreateSdlApi();
```

### Shared extraction utility in `FrameFlow.Media`

The bundle extraction probe (enumerate `{extractBase}/{appName}/*/` hash directories) is relevant to any native library, not only FFmpeg. The enumeration logic will be extracted from `FrameFlowBootstrapper` into a small internal utility in `FrameFlow.Media`:

```csharp
// FrameFlow.Media
internal static class BundleExtractionHelper
{
    /// <summary>
    /// Enumerates bundle extraction hash directories for the current process,
    /// ordered by last-write time descending (most recent extraction first).
    /// Returns an empty sequence if no extraction directory exists.
    /// </summary>
    public static IEnumerable<string> EnumerateHashDirectories() { ... }
}
```

Both `FrameFlowBootstrapper` and `SdlBootstrapper` call this utility and then apply their own file/path predicates. `FrameFlowBootstrapper` looks for `{hashDir}/runtimes/{rid}/native/` (a subdirectory). `SdlBootstrapper` looks for `{hashDir}/{sdlFileName}` (a file at the hash directory root). Neither bootstrapper knows about the other's extraction layout.

### New types in `FrameFlow.Sdl`

```
src/FrameFlow.Sdl/
  Bootstrap/
    SdlNativeOptions.cs               — options: CustomSdlLibraryPath, UseBundledLibrary, ProbeSystemLibrary
    SdlBootstrapResult.cs             — record: IsSuccess, ResolvedLibraryPath, Message
    ISdlBootstrapper.cs               — public interface: IsInitialized, Initialize(), CreateSdlApi()
    SdlBootstrapper.cs                — implementation
    SdlNativeContext.cs               — internal INativeContext wrapping a pre-loaded handle
    SdlHostedService.cs               — IHostedService for eager startup initialization
    SdlServiceCollectionExtensions.cs — AddFrameFlowSdl() and AddHostedSdlBootstrap()
```

### `ISdlBootstrapper`

```csharp
public interface ISdlBootstrapper
{
    bool IsInitialized { get; }

    /// <summary>
    /// Resolves and loads the SDL2 native library.
    /// Must be called before <see cref="CreateSdlApi"/>.
    /// Does not throw; all failure information is in the returned result.
    /// </summary>
    SdlBootstrapResult Initialize();

    /// <summary>
    /// Creates a <see cref="Silk.NET.SDL.Sdl"/> API instance backed by the resolved SDL2 library.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="Initialize"/> has not been called or returned a failure result.
    /// </exception>
    Silk.NET.SDL.Sdl CreateSdlApi();
}
```

### DI registration

`AddFrameFlowSdl()` registers `ISdlBootstrapper` as a singleton and registers `Silk.NET.SDL.Sdl` as a singleton whose factory calls `ISdlBootstrapper.CreateSdlApi()`:

```csharp
services.AddFrameFlowSdl();
// ISdlBootstrapper and Sdl are now injectable without path knowledge in the consumer.

services.AddFrameFlowSdl().AddHostedSdlBootstrap();
// Eager initialization at startup; Sdl is safely injectable into any hosted component.
```

### Boundary constraint

`SdlBootstrapper` must not reference `FrameFlow.Native`. The two bootstrappers are independent subsystems. Both call `BundleExtractionHelper` from `FrameFlow.Media`; neither knows about the other's resolved path or loading strategy. Application-level composition of the two (e.g., initializing FFmpeg before SDL) is the consumer's concern, not a library dependency.

### Impact on example applications

The `PreloadNativeLibraries` helper in `SdlPlayer/Program.cs` is removed once `SdlBootstrapper` is implemented. The app call site becomes:

```csharp
var sdlBootstrapper = new SdlBootstrapper(new SdlNativeOptions(), loggerFactory);
var sdlResult = sdlBootstrapper.Initialize();
if (!sdlResult.IsSuccess)
{
    Console.Error.WriteLine($"SDL bootstrap failed: {sdlResult.Message}");
    return 1;
}

var sdl = sdlBootstrapper.CreateSdlApi();
```

### OpenAL

**Original note (2026-03-31):** The same pattern applies to `FrameFlow.Audio.OpenAL`. `OpenAL32.dll` is currently system-resolved; if a future self-contained publish requires it to be bundled, an `OpenAlBootstrapper` following this same seam is the right approach. That work is out of scope for this ADR.

**Update (2026-05-15):** The trigger fired earlier than the original note anticipated — and for a different reason. The break surfaced on a fresh Windows 11 install (no prior game / SDK / Oculus / etc.), not in a self-contained single-file publish: `ALContext.GetApi()` in `OpenAlAudioSink.ActivateAsync` threw `FileNotFoundException("Could not load from any of the possible library names!")` because no machine-wide OpenAL was discoverable. The implicit "OpenAL32 is on most Windows machines" assumption from the 2026-03-31 investigation doc does not hold for clean installs.

The chosen solution is **not** a custom `OpenAlBootstrapper`. Silk.NET ships a maintained companion package, `Silk.NET.OpenAL.Soft.Native`, that bundles the OpenAL Soft 1.23 binaries under the standard `runtimes/{rid}/native/` NuGet convention for Windows (x64/arm64), Linux (x64/arm/arm64), and macOS (x64/arm64). Adding a single `PackageReference` to `FrameFlow.Audio.OpenAL.csproj` resolves the issue cleanly across every target RID — no bootstrapper code, no fetch script, no PreloadNativeLibraries helper, no manifest. The managed wrapper's resolver picks up the bundled binary via the standard NuGet runtime-asset mechanism.

This choice diverges from the SDL2 path because the SDL2 problem set was different: SDL2 needed bundle-extraction handling for single-file publish, an `INativeContext` integration to bypass Silk.NET's own resolution, and structured init logging. None of those concerns are at play for OpenAL — Silk.NET.OpenAL's own resolver finds DLLs adjacent to the executable just fine; the only missing piece was the DLL itself. Decision framework: [ADR-0046](ADR-0046-native-runtime-acquisition-strategy.md).

**What still applies from the original note:** if a future single-file publish exposes the same extraction-layout problem for OpenAL that SDL2 has today, a custom `OpenAlBootstrapper` following this seam *is* still the right approach for that specific failure mode. The two solutions are complementary, not mutually exclusive — `Silk.NET.OpenAL.Soft.Native` solves "binary is missing"; a hypothetical `OpenAlBootstrapper` would solve "binary is bundled but in an extraction directory Silk.NET's resolver can't see."

## Consequences

### Positive

- Consuming apps require no knowledge of `.NET` single-file extraction internals or SDL2 library paths.
- The `INativeContext` integration means SDL2 resolution works identically across dev, regular publish, and single-file publish on all platforms — there is no dependency on OS module cache side-effects.
- `SdlBootstrapper` can emit structured `Debug`-level logs at every resolution step, matching the diagnostic visibility added to `FrameFlowBootstrapper` in this investigation.
- `ISdlBootstrapper` is mockable, making `SdlVideoPresenter` tests easier to write without a real SDL2 binary.
- `SdlBootstrapper.Initialize()` can verify the loaded library by calling `SDL_GetVersion()`, providing an early failure signal equivalent to `avutil_version()` in the FFmpeg bootstrap.
- The `BundleExtractionHelper` extraction in `FrameFlow.Media` removes the only duplicated path-probing logic between bootstrappers.

### Negative

- Consuming apps can no longer call `SdlApi.GetApi()` directly if they want single-file publish to work. They must go through `ISdlBootstrapper.CreateSdlApi()`. This is a breaking change to the current (informal) usage pattern.
- `FrameFlow.Sdl` gains a new internal dependency on `Silk.NET.Core`'s `INativeContext` interface. If Silk.NET changes this interface in a major version, `SdlNativeContext` will need updating.
- The `SdlHostedService` and DI extension methods add surface area that must be maintained alongside the equivalent FFmpeg infrastructure.

## Alternatives Considered

### Keep `PreloadNativeLibraries` in example applications

Rejected. It encodes extraction layout knowledge that belongs in the library and relies on OS module cache side-effects that are not guaranteed to be stable across platforms or .NET versions. Any application that publishes as a single file must rediscover and reimplement the same logic.

### Register a `DllImportResolver` for `Silk.NET.SDL`'s assembly

Rejected. `NativeLibrary.SetDllImportResolver` can only be called once per assembly per process lifetime, and it must be called by code running in the target assembly's load context. FrameFlow does not own `Silk.NET.SDL` and cannot reliably be the first caller. Competing `DllImportResolver` registrations from the same process would silently win or lose based on call order.

### Manipulate `PATH` before calling `Sdl.GetApi()`

Rejected. Modifying the process environment is a global side-effect that affects all subsequent native library loads, not just SDL2. It is also fragile: on Linux, `LD_LIBRARY_PATH` must be set before the dynamic linker starts, meaning in-process modification has no effect on libraries already searched. The `INativeContext` approach is scoped to the specific API instance and has no global side-effects.

### Accept a `string sdlPath` parameter on `SdlVideoPresenter`

Rejected. It pushes path resolution into `SdlVideoPresenter`, which is a rendering adapter — path resolution is not its responsibility. It also means every consumer that constructs a presenter must independently solve the path resolution problem. The bootstrapper pattern keeps resolution concerns in one place.

### Derive SDL2 path from the FFmpeg bootstrap result

Rejected. It creates a runtime coupling between two independent subsystems. In the single-file layout SDL2 and FFmpeg happen to be extracted to the same hash directory, but this is a coincidence of the current `.targets` files, not a stable contract. The extraction root for each library should be discovered independently.
