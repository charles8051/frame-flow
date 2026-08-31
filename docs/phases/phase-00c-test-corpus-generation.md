# Phase 00c — Test Corpus Generation

## Status

**Done.** `scripts/generate-test-corpus.cs` generates the corpus into the gitignored `tests/corpus/files/`, with `tests/corpus/test-expectations.json` as the manifest consumed by `FrameFlow.Integration.Tests`.

## Goal

Use FFmpeg to generate a baseline corpus of synthetic test media files so that bootstrapping, decoding, playback, and edge-case handling can be validated from the earliest phases onward.

This phase materializes the synthetic generation strategy described in `docs/CORPUS.md` into a repeatable, scriptable process that produces files checked into the test infrastructure (or downloaded/generated on demand).

## Dependencies

- Phase 00 (API shape informs what metadata and format properties tests will assert against)
- FFmpeg available on the development machine (the runtimes DLLs are for library use; corpus generation uses the FFmpeg CLI)

## In scope

- a single-file `dotnet run` C# generation script (`scripts/generate-test-corpus.cs`) that produces all synthetic test files
- a manifest file (`tests/corpus/manifest.json`) listing each generated file with expected properties (codec, container, dimensions, duration, sample rate, channel count, etc.)
- generation of the P0 format matrix files from `CORPUS.md`:
  - H.264 + AAC in MP4 (1080p, 30fps, 10s)
  - H.265 + AAC in MP4 (1080p, 30fps, 10s)
  - H.264 + AAC in MKV
  - PCM audio in WAV (stereo, 44.1kHz, 16-bit)
  - MP3 audio in MP4 container
- generation of key edge-case files:
  - video-only (no audio stream)
  - audio-only (no video stream, in MP4 container)
  - odd dimensions (1921x1081)
  - very short duration (sub-second)
  - variable frame rate (VFR)
  - truncated file (generated then byte-truncated)
  - multichannel audio (5.1)
  - silent audio (all-zero samples)
- a verification step in the script that confirms each generated file exists and has non-zero size
- `.gitignore` entry for generated corpus files (they should not be committed; they are regenerated or downloaded)

## Out of scope

- downloading public test media from external sources (that is the download script's concern, or a future CI step)
- integration with CI pipelines (deferred to Phase 08)
- test code that consumes the corpus (that belongs to each phase's testing sub-phase)

## Deliverables

- `scripts/generate-test-corpus.cs` — the generation script (`dotnet run scripts/generate-test-corpus.cs`), cross-platform
- `tests/corpus/manifest.json` — machine-readable file listing each corpus entry with expected properties
- `tests/corpus/.gitignore` — ignores generated media files but tracks scripts and manifest
- updated `docs/CORPUS.md` if the generation process reveals corrections to the documented FFmpeg commands

## Script design

The generation script should:

1. Check that `ffmpeg` is available on PATH and report its version
2. Create the output directory (`tests/corpus/files/`)
3. Generate each file using FFmpeg with explicit, documented parameters
4. Verify each output file exists and is non-trivially sized
5. Report a summary of generated files with pass/fail status
6. Be idempotent — re-running overwrites existing files cleanly

### Example generation commands

```powershell
# Standard 1080p H.264 + AAC
ffmpeg -y -f lavfi -i testsrc2=size=1920x1080:rate=30:duration=10 `
       -f lavfi -i sine=frequency=440:duration=10 `
       -c:v libx264 -preset fast -pix_fmt yuv420p `
       -c:a aac -b:a 128k `
       "$outDir/test-1080p-h264-aac.mp4"

# Audio-only in MP4
ffmpeg -y -f lavfi -i sine=frequency=440:duration=30 `
       -c:a aac -b:a 128k `
       "$outDir/test-audio-only.mp4"

# Video-only (no audio)
ffmpeg -y -f lavfi -i testsrc2=size=640x480:rate=24:duration=10 `
       -c:v libx264 -pix_fmt yuv420p -an `
       "$outDir/test-video-only.mp4"

# Odd dimensions
ffmpeg -y -f lavfi -i testsrc2=size=1921x1081:rate=30:duration=5 `
       -c:v libx264 -pix_fmt yuv420p `
       "$outDir/test-odd-dimensions.mp4"

# Sub-second duration
ffmpeg -y -f lavfi -i testsrc2=size=640x480:rate=30:duration=0.5 `
       -c:v libx264 -pix_fmt yuv420p `
       "$outDir/test-subsecond.mp4"

# PCM WAV
ffmpeg -y -f lavfi -i sine=frequency=440:duration=10 `
       -ar 44100 -ac 2 -c:a pcm_s16le `
       "$outDir/test-pcm-stereo.wav"
```

## Risks

- FFmpeg CLI version differences may produce slightly different output across machines — pin to explicit codec parameters to minimize variance
- Some edge-case files (e.g., VFR, truncated) may require post-processing steps beyond a single FFmpeg command
- Generated files should be small (seconds, not minutes) to keep test runs fast

## Validation

- each generated file can be probed with `ffprobe` and matches its manifest entry
- the manifest covers at least the P0 entries from the `CORPUS.md` format matrix
- edge-case files are genuinely edge cases (e.g., the odd-dimension file actually has odd dimensions, the VFR file actually has variable frame rate)

## Exit criteria

- the generation script runs cleanly on a Windows development machine with FFmpeg on PATH
- a manifest file accurately describes all generated files
- the corpus is sufficient to support Phase 01 bootstrap validation and Phase 02+ decode testing
- the FFmpeg Expert Agent has reviewed the generation commands for correctness

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

This phase specifically requires **FFmpeg Expert Agent review** of all generation commands to ensure they produce files that exercise real decode paths and are representative of the claimed formats.
