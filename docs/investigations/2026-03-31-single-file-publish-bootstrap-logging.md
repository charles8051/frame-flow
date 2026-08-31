# Investigation: Single-File Publish Bootstrap Logging and SDL Native Resolution

**Date:** 2026-03-31
**Trigger:** Debugging bootstrapping failures when publishing examples as self-contained apps.

---

## Problem statement

The SdlPlayer example crashed on startup when published as a self-contained single-file executable. Diagnosing the failure was difficult because the bootstrapper emitted no logs — it was constructed without a logger — and the static path-resolution methods had no visibility into which paths were being probed.

Two separate issues were found:

1. Logging infrastructure was not correctly wired through the bootstrap stack.
2. Silk.NET's SDL2 / OpenAL native libraries were extracted to a path the OS loader couldn't find automatically.

---

## Issue 1: Logging not wired into the bootstrap stack

### Root cause

`FrameFlowBootstrapper`'s public constructor accepted `ILogger<FrameFlowBootstrapper>`, but `SdlPlayer/Program.cs` called the no-arg overload:

```csharp
var bootstrapper = new FrameFlowBootstrapper(new FrameFlowNativeOptions());
```

This fell back to `NullLogger`, so no bootstrap output was produced regardless of the host's log configuration. Even when a logger was passed, `FfmpegNativeLibraryLoader` received that same `ILogger<FrameFlowBootstrapper>` instance, meaning its own log entries appeared under the wrong category name — a violation of ADR-0010's `ILogger<T>` requirement.

Additionally, `ResolveBundledPath()` and `ProbeBundleExtractionDirectory()` were `private static` methods, preventing them from using `_logger` at all.

### Fixes applied

**`FrameFlowBootstrapper`** — replaced the `ILogger<FrameFlowBootstrapper>` public constructor with an `ILoggerFactory` overload:

```csharp
public FrameFlowBootstrapper(FrameFlowNativeOptions options, ILoggerFactory loggerFactory)
{
    _logger  = loggerFactory.CreateLogger<FrameFlowBootstrapper>();
    _loader  = new FfmpegNativeLibraryLoader(loggerFactory.CreateLogger<FfmpegNativeLibraryLoader>());
}
```

The internal three-arg test-seam constructor (`ILogger<FrameFlowBootstrapper>` + `IFfmpegLibraryLoader`) was kept unchanged.

**`FfmpegNativeLibraryLoader`** — changed the constructor parameter from `ILogger` to `ILogger<FfmpegNativeLibraryLoader>`, giving its entries the correct category name in any log provider.

**`FrameFlowNativeServiceCollectionExtensions`** — DI registration updated to resolve `ILoggerFactory` instead of `ILogger<FrameFlowBootstrapper>`:

```csharp
var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
return new FrameFlowBootstrapper(opts, loggerFactory);
```

**`ResolveBundledPath` / `ProbeBundleExtractionDirectory`** — converted from `private static` to instance methods so they can emit `Debug` logs at every probe step.

**`SdlPlayer/Program.cs`** — updated to pass `loggerFactory` to the bootstrapper and added a category filter so `FrameFlow.*` logs at `Debug` while everything else stays at `Warning`:

```csharp
builder
    .SetMinimumLevel(LogLevel.Warning)
    .AddFilter("FrameFlow", LogLevel.Debug)
    ...

var bootstrapper = new FrameFlowBootstrapper(new FrameFlowNativeOptions(), loggerFactory);
```

### Log output added

Every meaningful decision point in the bootstrap path now emits a structured `Debug` log:

- which binary source strategy was selected (`CustomPath` / `Bundled` / `System` / `Unknown`) and why
- the RID and `AppContext.BaseDirectory` being used
- each candidate path being probed for the bundled layout
- the `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, app extraction dir, and each hash subdirectory enumerated
- each `NativeLibrary.TryLoad` candidate per library — both hits (resolved) and misses
- `Warning` when all candidates for a library are exhausted

Example output from a working single-file run:

```
dbug FrameFlow.Native.FrameFlowBootstrapper  Binary source resolved to Bundled (UseBundledBinaries=true).
dbug FrameFlow.Native.FrameFlowBootstrapper  Resolving bundled FFmpeg path. RID='win-x64', AppBase='...\bin\Publish\win-x64\'
dbug FrameFlow.Native.FrameFlowBootstrapper  Probing primary bundled path: '...\bin\Publish\win-x64\runtimes\win-x64\native'
dbug FrameFlow.Native.FrameFlowBootstrapper  Primary bundled path not found. Probing single-file bundle extraction directory.
dbug FrameFlow.Native.FrameFlowBootstrapper  Probing bundle extraction directory. ExtractBase='%TEMP%\.net', AppName='FrameFlow.Examples.SdlPlayer', AppExtractDir='%TEMP%\.net\FrameFlow.Examples.SdlPlayer'
dbug FrameFlow.Native.FrameFlowBootstrapper  Checking extraction hash dir: '...IvN1ApPC-ir5\runtimes\win-x64\native'
dbug FrameFlow.Native.FrameFlowBootstrapper  Found native layout in bundle extraction directory: '...IvN1ApPC-ir5\runtimes\win-x64\native'
info FrameFlow.Native.FrameFlowBootstrapper  FrameFlow native bootstrap starting. BinarySource=Bundled, SearchPath=...IvN1ApPC-ir5\runtimes\win-x64\native
dbug FrameFlow.Native.FfmpegNativeLibraryLoader  Registering DllImportResolver for FrameFlow.Native assembly.
dbug FrameFlow.Native.FfmpegNativeLibraryLoader  Resolved avutil via '...IvN1ApPC-ir5\runtimes\win-x64\native\avutil-59.dll'
...
info FrameFlow.Native.FfmpegNativeLibraryLoader  FFmpeg avutil version 59.39.100 confirmed via version probe
info FrameFlow.Native.FrameFlowBootstrapper  FrameFlow native bootstrap completed successfully. BinarySource=Bundled, AvutilVersion=59.39.100
```

---

## Issue 2: SDL2 not found after single-file extraction

### Root cause

`IncludeNativeLibrariesForSelfExtract=true` bundles ALL native libraries into the single-file exe and extracts them at startup. However, the extraction path depends on where each file lived in the original publish output:

| Library source | Publish output layout | Extracted path |
|---|---|---|
| FFmpeg (via `Directory.Build.targets` `Link=`) | `runtimes/win-x64/native/*.dll` | `{hash}/runtimes/win-x64/native/*.dll` |
| SDL2 (via Silk.NET's RID-flatten at publish) | root of output dir (`SDL2.dll`) | `{hash}/SDL2.dll` |
| OpenAL32 (system-installed) | not in publish output | not extracted |

The .NET `AppHost` extracts files preserving their relative paths. SDL2.dll lands at the hash dir root, not in `runtimes/win-x64/native/`. `AppContext.BaseDirectory` points to the exe's directory (the publish output dir, which is empty of loose DLLs in single-file mode). `NativeLibrary.Load("SDL2")` searches the exe dir and PATH but not the temp extraction dir, so Silk.NET's `Sdl.GetApi()` threw:

```
System.IO.FileNotFoundException: Could not load from any of the possible library names!
   at Silk.NET.SDL.Sdl.CreateDefaultContext(String[] n)
   at Silk.NET.SDL.Sdl.GetApi()
```

### Fix applied

A `PreloadNativeLibraries` helper was added to `SdlPlayer/Program.cs` that runs immediately after the FFmpeg bootstrap succeeds. It pre-loads SDL2.dll and OpenAL32.dll by full path before Silk.NET attempts name-based resolution. Once a DLL is in the process's loaded-module cache (Windows: `LoadLibraryW` by full path registers it under its module name), subsequent `NativeLibrary.Load("SDL2")` calls return the cached handle.

The candidate search covers all three build/publish layouts:

1. **Same dir as FFmpeg** — dev builds place SDL2 in `runtimes/{rid}/native/` alongside FFmpeg.
2. **Ancestor dirs** — single-file extraction puts SDL2 at the hash root, 3 levels above `runtimes/{rid}/native/`. The helper walks up to 3 levels.
3. **`AppContext.BaseDirectory`** — regular self-contained publish (non-single-file) puts SDL2 next to the exe.

```
dbug Preload  PreloadNativeLibraries: not found at '...IvN1ApPC-ir5\runtimes\win-x64\native\SDL2.dll'
dbug Preload  PreloadNativeLibraries: not found at '...IvN1ApPC-ir5\runtimes\win-x64\SDL2.dll'
dbug Preload  PreloadNativeLibraries: not found at '...IvN1ApPC-ir5\runtimes\SDL2.dll'
dbug Preload  PreloadNativeLibraries: pre-loaded 'SDL2.dll' from '...IvN1ApPC-ir5\SDL2.dll' handle=0x7FF...
warn Preload  PreloadNativeLibraries: could not pre-load 'OpenAL32.dll' from any candidate path. Silk.NET will attempt its own resolution.
```

OpenAL32 is a Windows system component on most machines (redistributed by many games/apps) and is found by Silk.NET's own resolver via the system PATH.

---

## Single-file publish profile

A publish profile was created at:

```
examples/FrameFlow.Examples.SdlPlayer/Properties/PublishProfiles/win-x64-single-file.pubxml
```

Publish command:

```
dotnet publish examples\FrameFlow.Examples.SdlPlayer\FrameFlow.Examples.SdlPlayer.csproj -p:PublishProfile=win-x64-single-file
```

Output: a single `FrameFlow.Examples.SdlPlayer.exe` in `bin\Publish\win-x64\` containing the .NET runtime, all managed assemblies, and all native libraries (FFmpeg 7.x, SDL2, OpenAL). On first launch the AppHost extracts native libraries to `%TEMP%\.net\FrameFlow.Examples.SdlPlayer\{hash}\`. Subsequent runs reuse the cached extraction directory.

---

## Files changed

| File | Change |
|---|---|
| `src/FrameFlow.Native/FrameFlowBootstrapper.cs` | Replace `ILogger<T>` public constructor with `ILoggerFactory` overload; convert static path methods to instance methods; add Debug logging throughout |
| `src/FrameFlow.Native/FfmpegNativeLibraryLoader.cs` | Change `ILogger` → `ILogger<FfmpegNativeLibraryLoader>`; add per-candidate and failure logging in `TryLoadLibrary`; log DllImportResolver registration |
| `src/FrameFlow.Native/FrameFlowNativeServiceCollectionExtensions.cs` | Inject `ILoggerFactory` instead of `ILogger<FrameFlowBootstrapper>` |
| `examples/FrameFlow.Examples.SdlPlayer/Program.cs` | Pass `loggerFactory` to bootstrapper; add `FrameFlow` Debug filter; add `PreloadNativeLibraries` helper |
| `examples/FrameFlow.Examples.SdlPlayer/Properties/PublishProfiles/win-x64-single-file.pubxml` | New: single-file self-contained publish profile |
| `tests/FrameFlow.Native.Tests/BootstrapperIntegrationTests.cs` | Update `NullLogger<T>` → `NullLoggerFactory.Instance` |
| `tests/FrameFlow.Native.Tests/FrameFlowBootstrapperTests.cs` | Update null-guard test to use `ILoggerFactory` cast; add `Microsoft.Extensions.Logging` using |
| `tests/FrameFlow.Decoding.Tests/FfmpegBootstrapFixture.cs` | Update `NullLogger<T>` → `NullLoggerFactory.Instance` |

---

## Potential follow-up

- **`FrameFlow.Sdl` bootstrapper seam**: see detailed design below and `docs/adr/ADR-0019-sdl-native-bootstrap-seam.md`.
- **OpenAL extraction path**: OpenAL is currently system-resolved. If this needs to be self-contained, the same bootstrapper pattern described below applies equally to `FrameFlow.Audio.OpenAL`.
- **Cross-platform**: the `PreloadNativeLibraries` helper handles Windows, macOS, and Linux file extensions but has only been tested on Windows.

---

## Design: `FrameFlow.Sdl` bootstrapper seam

### Why the current approach is a workaround

The `PreloadNativeLibraries` helper in `Program.cs` is application-level code that knows about `.NET` single-file extraction internals. Every app that uses `FrameFlow.Sdl` must either copy this helper or accept that SDL won't load in single-file publish. That's knowledge that should belong to the library, not the consumer.

The FFmpeg side already has the right shape: `FrameFlowBootstrapper` owns path resolution, loads libraries, and apps just call `bootstrapper.Initialize()`. SDL needs the same treatment.

### Why SDL is different from FFmpeg

`FrameFlowBootstrapper` uses `NativeLibrary.SetDllImportResolver` to intercept all P/Invoke calls for the `FrameFlow.Native` assembly and redirect them to the resolved path. This works because FrameFlow owns the P/Invoke declarations.

SDL2 is different: the P/Invoke declarations live inside `Silk.NET.SDL`, a third-party package. FrameFlow can't register a `DllImportResolver` for an assembly it doesn't own. Instead, Silk.NET uses its own `INativeContext` abstraction — a pluggable interface that controls how each API instance resolves its library handle.

This actually gives us a cleaner seam: rather than patching the OS DLL search path or relying on module-cache side effects, the bootstrapper can own a concrete `INativeContext` implementation that loads SDL2 from the resolved path and hands it directly to Silk.NET:

```csharp
// Instead of the module-cache approach:
NativeLibrary.TryLoad(fullPath, out _);   // side effect: seeds the cache
var sdl = SdlApi.GetApi();                // Silk.NET's default context searches PATH

// Bootstrapper approach:
var sdl = sdlBootstrapper.CreateSdlApi(); // gives Silk.NET a context that already holds the handle
```

### Path resolution for SDL2

SDL2.dll ends up in different places depending on the publish mode. The bootstrapper needs to cover all three layouts:

| Layout | SDL2.dll location |
|---|---|
| Dev / debug build | `{appBase}/runtimes/{rid}/native/SDL2.dll` (standard NuGet runtime layout) |
| Self-contained publish (non-single-file) | `{appBase}/SDL2.dll` (RID-flattened to output root) |
| Single-file publish | `{hash}/SDL2.dll` (at the hash dir root, NOT under `runtimes/`) |
| System-installed | resolved by OS loader — no path needed |

Note that the single-file extraction layout for SDL2 differs from FFmpeg. FFmpeg is placed in `runtimes/{rid}/native/` by `Directory.Build.targets`' explicit `Link=` metadata, so it extracts to `{hash}/runtimes/{rid}/native/`. SDL2 comes from Silk.NET's NuGet package, whose targets flatten native assets to the publish root, so it extracts to `{hash}/SDL2.dll` — the hash dir root directly.

This means the extraction probe for SDL2 must search for a specific file at the hash dir root, not for a subdirectory path. The existing `ProbeBundleExtractionDirectory` in `FrameFlowBootstrapper` is hardcoded to look for `runtimes/{rid}/native/` — it can't be reused as-is.

### Shared extraction utility

The bundle extraction probe logic (enumerate `{DOTNET_BUNDLE_EXTRACT_BASE_DIR}/{appName}/*/`) is relevant to any native library, not just FFmpeg. The cleanest move is to extract it to `FrameFlow.Media` as a small internal utility:

```csharp
// FrameFlow.Media — shared by all bootstrappers
internal static class BundleExtractionHelper
{
    /// <summary>
    /// Enumerates candidate bundle extraction subdirectories for the current process,
    /// ordered by last-write time descending (most recent run first).
    /// </summary>
    public static IEnumerable<string> EnumerateHashDirectories()
    {
        var extractBase = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")
            ?? Path.Combine(Path.GetTempPath(), ".net");

        var processPath = Environment.ProcessPath;
        if (processPath is null) yield break;

        var appName    = Path.GetFileNameWithoutExtension(processPath);
        var appExtract = Path.Combine(extractBase, appName);

        if (!Directory.Exists(appExtract)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(appExtract)
                                     .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)))
            yield return dir;
    }
}
```

`FrameFlowBootstrapper` would call this and look for `{hashDir}/runtimes/{rid}/native/` inside. `SdlBootstrapper` would call it and look for `{hashDir}/SDL2.dll` (or the platform equivalent) inside. Each bootstrapper interprets the hash dirs according to its own file layout without sharing any logic beyond the enumeration.

### Proposed types in `FrameFlow.Sdl`

```
src/FrameFlow.Sdl/
  Bootstrap/
    SdlNativeOptions.cs               — options (CustomSdlPath, UseBundledBinaries, ProbeSystem)
    SdlBootstrapResult.cs             — record (IsSuccess, ResolvedLibraryPath, Message)
    ISdlBootstrapper.cs               — public interface (IsInitialized, Initialize, CreateSdlApi)
    SdlBootstrapper.cs                — implementation
    SdlNativeContext.cs               — internal INativeContext impl for Silk.NET
    SdlHostedService.cs               — IHostedService for eager startup init
    SdlServiceCollectionExtensions.cs — AddFrameFlowSdl() / AddHostedSdlBootstrap()
```

#### `SdlNativeOptions`

```csharp
public sealed class SdlNativeOptions
{
    /// <summary>Explicit path to SDL2 shared library. Takes priority over all other sources.</summary>
    public string? CustomSdlLibraryPath { get; set; }

    /// <summary>
    /// Whether to search the standard bundled locations (app base dir and bundle extraction dir).
    /// Default: true.
    /// </summary>
    public bool UseBundledLibrary { get; set; } = true;

    /// <summary>Whether to fall back to OS-level library search if bundled resolution fails.</summary>
    public bool ProbeSystemLibrary { get; set; } = true;
}
```

#### `SdlBootstrapResult`

```csharp
public sealed record SdlBootstrapResult(
    bool IsSuccess,
    string? ResolvedLibraryPath,
    string Message);
```

#### `ISdlBootstrapper`

```csharp
public interface ISdlBootstrapper
{
    bool IsInitialized { get; }

    /// <summary>
    /// Resolves and loads SDL2. Must be called before <see cref="CreateSdlApi"/>.
    /// Does not throw; all failure information is in the returned result.
    /// </summary>
    SdlBootstrapResult Initialize();

    /// <summary>
    /// Creates a <see cref="Silk.NET.SDL.Sdl"/> API instance backed by the resolved SDL2 library.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="Initialize"/> has not been called or returned failure.
    /// </exception>
    Silk.NET.SDL.Sdl CreateSdlApi();
}
```

#### `SdlNativeContext` (internal)

This is the key integration point with Silk.NET. Rather than patching PATH or the module cache, the bootstrapper holds the loaded library handle and hands it to Silk.NET via a custom context:

```csharp
internal sealed class SdlNativeContext : INativeContext
{
    private readonly nint _handle;

    public SdlNativeContext(nint preloadedHandle)
    {
        _handle = preloadedHandle;
    }

    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
        => NativeLibrary.TryGetExport(_handle, proc, out addr);

    // Handle is kept for the process lifetime — SDL has no unload concept.
    public void Dispose() { }
}
```

`SdlBootstrapper.Initialize()` would load the library and store the handle. `CreateSdlApi()` would wrap it:

```csharp
public Sdl CreateSdlApi()
{
    if (!IsInitialized || _sdlHandle == 0)
        throw new InvalidOperationException(
            "SDL bootstrap must succeed before creating an API instance.");
    return new Sdl(new SdlNativeContext(_sdlHandle));
}
```

#### `SdlBootstrapper.Initialize()` resolution order

```
1. CustomSdlLibraryPath (if set)
2. {appBase}/runtimes/{rid}/native/{sdlFileName}     — dev build NuGet layout
3. {appBase}/{sdlFileName}                           — regular self-contained publish
4. Bundle extraction probe:
     for each hashDir in BundleExtractionHelper.EnumerateHashDirectories():
         try {hashDir}/{sdlFileName}
5. OS loader (bare library name, no path)            — system-installed fallback
```

### How consuming apps change

**Before (with workaround in `Program.cs`):**

```csharp
var bootstrapResult = bootstrapper.Initialize();
PreloadNativeLibraries(bootstrapResult.ResolvedPath, logger); // app knows about extraction paths
var sdl = SdlApi.GetApi();
```

**After (with bootstrapper seam):**

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

**With DI and hosted bootstrap:**

```csharp
// Registration
services
    .AddFrameFlowNative()
    .AddHostedBootstrap()
    .AddFrameFlowSdl()          // registers ISdlBootstrapper, Sdl (lazy), SdlNativeOptions
    .AddHostedSdlBootstrap();   // eager init at startup via IHostedService

// Injection — SDL API instance available directly, no path knowledge required
public class MyPresenter(Sdl sdl, ILogger<MyPresenter> logger) { ... }
```

The `AddFrameFlowSdl()` registration would use `TryAddSingleton<Sdl>` with a factory that calls `ISdlBootstrapper.CreateSdlApi()`, so the `Sdl` instance itself is injectable without any of the bootstrap machinery leaking to consumers.

### What the `SdlVideoPresenter` change looks like

Currently `SdlVideoPresenter` receives an `Sdl` instance from the caller (constructor injection). That shape doesn't need to change — the bootstrapper just becomes the thing that produces the correctly-configured `Sdl` instance, whether created manually or resolved from DI.

### Boundary note

The `SdlBootstrapper` should live entirely in `FrameFlow.Sdl` and should not reference `FrameFlow.Native`. The two bootstrappers are independent; they happen to use the same bundle extraction enumeration utility from `FrameFlow.Media`. Neither should know about the other's resolved path. If an application wants to derive the SDL extraction root from the FFmpeg result (as the current workaround does), that's application-level composition, not a library dependency.

### Comparison with `FrameFlowBootstrapper`

| Concern | `FrameFlowBootstrapper` | `SdlBootstrapper` |
|---|---|---|
| Library | 5 FFmpeg DLLs in dependency order | 1 SDL2 DLL |
| P/Invoke ownership | FrameFlow owns the interop declarations | Silk.NET owns the declarations |
| Integration mechanism | `NativeLibrary.SetDllImportResolver` for `FrameFlow.Native` assembly | Custom `INativeContext` passed to `new Sdl(context)` |
| Extraction layout | `{hash}/runtimes/{rid}/native/` | `{hash}/SDL2.dll` |
| Probe verification | calls `avutil_version()` | calls `SDL_GetVersion()` |
| Options type | `FrameFlowNativeOptions` | `SdlNativeOptions` |
| Result type | `FrameFlowBootstrapResult` | `SdlBootstrapResult` |
| Hosted service | `FrameFlowHostedService` | `SdlHostedService` |
