# ADR-0014: Native Binary Packaging and Distribution

## Status

Accepted

## Context

ADR-0002 defines how FrameFlow resolves FFmpeg binaries at runtime: custom path → bundled → system. ADR-0004 scopes v1 to Windows, macOS, and Linux. ADR-0011 pins the interop surface to a single FFmpeg major version (7.x).

None of these decisions address how FFmpeg binaries get into a consumer's application in the first place. There are several upstream questions:

- How are platform-specific FFmpeg shared libraries distributed to consumers?
- Where do the binaries come from during development and CI?
- Should binaries be tracked in source control?
- How does `UseBundledBinaries()` actually work at the packaging level?

FFmpeg shared libraries are large (50–150 MB per platform for the five libraries FrameFlow uses), platform-specific, and subject to licensing constraints. They should not live in the main git repository.

The .NET ecosystem has a well-established pattern for distributing native dependencies: RID-specific NuGet packages with MSBuild `.targets` files that copy the correct binaries to the output directory. This is used by SkiaSharp, libgit2sharp, ImageSharp, and many others.

## Decision

### RID-specific NuGet runtime packages

FFmpeg binaries will be distributed as separate NuGet packages per runtime identifier:

| Package | Contents |
|---------|----------|
| `FrameFlow.Native.runtime.win-x64` | Windows x64 FFmpeg shared libraries |
| `FrameFlow.Native.runtime.win-arm64` | Windows ARM64 FFmpeg shared libraries |
| `FrameFlow.Native.runtime.linux-x64` | Linux x64 FFmpeg shared libraries |
| `FrameFlow.Native.runtime.linux-arm64` | Linux ARM64 FFmpeg shared libraries |
| `FrameFlow.Native.runtime.osx-x64` | macOS x64 FFmpeg shared libraries |
| `FrameFlow.Native.runtime.osx-arm64` | macOS ARM64 FFmpeg shared libraries |

Each runtime package contains the native libraries under the standard `runtimes/{rid}/native/` layout and a `.targets` file that ensures the correct binaries are copied to the output directory.

A meta-package `FrameFlow.Native` (or the main `FrameFlow` package) can reference the appropriate runtime packages so consumers get the right binaries automatically.

### Package layout

Each runtime package follows the NuGet native library convention:

```
FrameFlow.Native.runtime.win-x64/
├── FrameFlow.Native.runtime.win-x64.nuspec
├── build/
│   └── FrameFlow.Native.runtime.win-x64.targets
└── runtimes/
    └── win-x64/
        └── native/
            ├── avformat-61.dll
            ├── avcodec-61.dll
            ├── avutil-59.dll
            ├── swscale-8.dll
            └── swresample-5.dll
```

The `.targets` file ensures binaries are copied to the output directory and included in publish output:

```xml
<Project>
  <ItemGroup>
    <None Include="$(MSBuildThisFileDirectory)..\runtimes\win-x64\native\**"
          Link="%(Filename)%(Extension)"
          CopyToOutputDirectory="PreserveNewest"
          Visible="false" />
  </ItemGroup>
</Project>
```

### Binaries are not tracked in source control

FFmpeg shared libraries will not be committed to the git repository, not even via Git LFS. The `runtimes/` directory at the repository root is gitignored.

Rationale:

- FFmpeg binaries are 50–150 MB per platform; six platforms means 300–900 MB of binary content
- Git LFS would work technically but incurs bandwidth costs on every clone and CI run
- Binaries are build artifacts, not source — they belong in artifact storage, not version control
- The .NET ecosystem convention is to distribute natives via NuGet, not via the source repo

### Development workflow

Developers obtain FFmpeg binaries locally through one of:

1. **System install** — `apt install ffmpeg`, `brew install ffmpeg`, `winget install ffmpeg`, or equivalent. The bootstrap resolver finds them via system paths.
2. **Download script** — a repository-provided script downloads pre-built FFmpeg shared libraries for the current platform into a local `runtimes/` directory (gitignored). This is the recommended approach for consistent versions. Run it from the repository root:

   ```bash
   dotnet run scripts/fetch-ffmpeg.cs
   ```

   It is a single-file C# app with no project file, so it needs the **.NET 10 SDK or newer** — file-based `dotnet run` does not exist on earlier SDKs. `global.json` pins that floor, so an older SDK fails with a message naming the required version rather than an obscure parse error.
3. **Manual placement** — download binaries from a trusted source and configure `options.FFmpegPath` to point at them.

The download script will:

- detect the current RID
- download the correct FFmpeg build from a known source (e.g., BtbN/FFmpeg-Builds on GitHub for Windows/Linux, or evermeet.cx for macOS)
- verify checksums
- place libraries in `runtimes/{rid}/native/`
- be idempotent (skip download if checksums match)

### CI workflow

CI builds obtain FFmpeg binaries via the same download script or a cached artifact. The workflow is:

1. Run `dotnet run scripts/fetch-ffmpeg.cs` for the runner's platform
2. Cache the `runtimes/` directory by checksum for fast repeat runs
3. Build and test with bundled binaries available

For packaging (producing the runtime NuGet packages), CI will:

1. Fetch binaries for all target RIDs (or use a matrix build)
2. Pack each runtime package using the corresponding `.nuspec`
3. Publish to a NuGet feed

### Relationship to UseBundledBinaries()

When a consumer installs a FrameFlow runtime NuGet package, the `.targets` file copies the native libraries next to the application assembly. The bootstrap resolver's "bundled binaries" search path looks in the application's base directory (and `runtimes/{rid}/native/` relative to it). No additional configuration is needed — `UseBundledBinaries = true` (which should be the default) just means "look next to the app first."

### FFmpeg version pinning

Each set of runtime packages corresponds to a specific FFmpeg version. The package version should encode the FFmpeg version in some way (e.g., `FrameFlow.Native.runtime.win-x64` version `1.0.0+ffmpeg7.1.1`). This makes it clear which FFmpeg build is bundled.

When FrameFlow upgrades its target FFmpeg version, new runtime packages are published. Consumers upgrade by updating the package reference.

## Consequences

### Positive

- Follows the established .NET ecosystem pattern for native dependency distribution
- Repository stays lean — no large binaries in git history
- Each platform's binaries are independently versioned and distributable
- Development works with system-installed FFmpeg (zero setup) or a download script (consistent version)
- CI can cache binaries effectively
- Consumers get automatic binary deployment via NuGet without manual file management

### Negative

- Requires building and maintaining six runtime NuGet packages (plus the meta-package)
- The download script must track upstream FFmpeg build sources, which may change URLs
- Developers who want the exact CI version must run the download script rather than using whatever system FFmpeg they have
- NuGet package size may be large; consumers who only target one platform still resolve the meta-package (though only the relevant RID package is restored)

## Alternatives considered

### Git LFS for FFmpeg binaries

Rejected because LFS bandwidth costs scale with clone and CI frequency. For 300–900 MB of binaries across platforms, this becomes expensive quickly. LFS is better suited for assets that are tightly coupled to source changes (e.g., test fixtures), not for third-party build artifacts that change infrequently.

### Commit binaries directly to the repository

Rejected for obvious reasons — it would bloat the repository permanently and make cloning impractical.

### Require consumers to install FFmpeg themselves

Rejected as the only option because it creates a poor out-of-box experience. However, system-installed FFmpeg remains a supported resolution path (ADR-0002), and for development it is the simplest approach. The runtime NuGet packages provide the "it just works" bundled path.

### Single fat NuGet package with all platforms

Rejected because it would force every consumer to download 300+ MB regardless of which platform they target. RID-specific packages let NuGet restore only the relevant platform's binaries.

### vcpkg or other native package managers

Rejected for distribution to consumers because it adds a non-.NET toolchain requirement. However, vcpkg could be used internally to build FFmpeg from source for packaging — that is a CI implementation detail, not a distribution decision.
