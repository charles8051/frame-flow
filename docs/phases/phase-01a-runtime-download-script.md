# Phase 01a — Runtime Download Script

## Status

**Done.** Delivered as `scripts/fetch-ffmpeg.cs`, a single-file `dotnet run` script driven by `scripts/runtime-manifest.json` (ADR-0046).

## Goal

Write a download script that primes the `runtimes/` directory cache with the correct FFmpeg native DLLs for develop-time use, so that developers can bootstrap the project without manually sourcing or placing binaries.

## Dependencies

- Phase 00 (options model defines how the native path is configured)
- Phase 01 (bootstrap logic defines which libraries are required and how they are resolved)

## In scope

- a single-file `dotnet run` C# script (`scripts/fetch-ffmpeg.cs`) that downloads pre-built FFmpeg shared libraries for the current platform
- a manifest file (`scripts/runtime-manifest.json`) listing each required library with its download URL, expected SHA-256 checksum, target RID directory, and version metadata
- support for the `win-x64` runtime identifier as the initial target
- checksum verification of every downloaded file
- placement into the correct `runtimes/{rid}/native/` directory structure
- skip-if-present logic: do not re-download files that already exist with matching checksums
- clear console output indicating what was downloaded, verified, and placed
- cross-platform operation from the same script (`--rid <rid>` / `--all`) rather than a separate shell script per platform

## Out of scope

- building FFmpeg from source
- NuGet packaging of native binaries (deferred to Phase 08 or Phase 14/packaging)
- CI-specific caching strategies (deferred; the script itself is CI-friendly by design)
- multi-RID simultaneous download (one RID at a time; parameterized for future expansion)

## Runtime manifest format

```json
{
  "version": "7.1",
  "description": "FFmpeg shared libraries for FrameFlow develop-time use",
  "runtimes": {
    "win-x64": {
      "libraries": [
        {
          "name": "avcodec-61.dll",
          "sha256": "<checksum>",
          "url": "<download-url>",
          "target": "runtimes/win-x64/native/avcodec-61.dll"
        },
        {
          "name": "avformat-61.dll",
          "sha256": "<checksum>",
          "url": "<download-url>",
          "target": "runtimes/win-x64/native/avformat-61.dll"
        },
        {
          "name": "avutil-59.dll",
          "sha256": "<checksum>",
          "url": "<download-url>",
          "target": "runtimes/win-x64/native/avutil-59.dll"
        },
        {
          "name": "swresample-5.dll",
          "sha256": "<checksum>",
          "url": "<download-url>",
          "target": "runtimes/win-x64/native/swresample-5.dll"
        },
        {
          "name": "swscale-8.dll",
          "sha256": "<checksum>",
          "url": "<download-url>",
          "target": "runtimes/win-x64/native/swscale-8.dll"
        }
      ]
    }
  }
}
```

## Script behavior

1. **Parse manifest** — read `runtime-manifest.json` from the script's directory
2. **Detect RID** — default to current platform RID, allow override via parameter (`-RuntimeId win-x64`)
3. **Check existing files** — for each library in the manifest, check if the target file exists and matches the expected SHA-256
4. **Download missing/mismatched** — download files that are missing or have wrong checksums
5. **Verify downloads** — compute SHA-256 of each downloaded file and compare to manifest
6. **Place files** — move verified files to the target path under `runtimes/`
7. **Report** — print summary showing each library's status (cached/downloaded/failed)

### Error handling

- fail clearly if no internet connection and files are missing
- fail clearly if a downloaded file does not match its checksum (do not silently use it)
- warn if the manifest references a RID that is not the current platform

## Deliverables

- `scripts/fetch-ffmpeg.cs` — the download script (`dotnet run scripts/fetch-ffmpeg.cs`)
- `scripts/runtime-manifest.json` — version-pinned manifest with checksums
- documentation of the first-run step in `README.md` and `scripts/README.md`

## Risks

- download URLs for pre-built FFmpeg binaries may change or disappear — the manifest should reference stable, versioned sources
- checksum mismatches due to upstream rebuilds — pin to specific build artifacts, not rolling latest
- library dependency chains: FFmpeg DLLs may depend on other DLLs (e.g., `avcodec` depends on `avutil`) — the manifest must include all transitive dependencies
- Windows Defender or corporate proxies may interfere with downloads

## Validation

- run the script on a clean machine (no existing runtimes) and verify all DLLs are placed correctly
- run the script again and verify it skips already-cached files
- tamper with one DLL and verify the script re-downloads it
- verify that the bootstrap path from Phase 01 can successfully load the downloaded DLLs

## Exit criteria

- the download script reliably primes `runtimes/win-x64/native/` with all required FFmpeg DLLs
- checksums are verified for every file
- the script is idempotent and safe to re-run
- the FFmpeg Expert Agent has reviewed the library list for completeness (no missing transitive dependencies)
- the Architecture Hawk has reviewed the script's integration with the bootstrap resolution strategy from ADR-0002

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

This phase specifically requires **FFmpeg Expert Agent review** of the library dependency chain and version compatibility.
