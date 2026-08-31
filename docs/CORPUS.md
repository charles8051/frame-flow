# Sample Media Corpus

This document defines the test media corpus strategy for FrameFlow. ADR-0007 identifies sample corpus validation as the fifth layer of the testing strategy.

## Purpose

A media player library must handle a wide variety of containers, codecs, resolutions, channel layouts, and edge cases. A curated test corpus provides:

- **Reproducibility** — the same files produce the same results across machines and CI runs
- **Edge case coverage** — deliberate inclusion of unusual or broken media that real-world usage will encounter
- **Regression testing** — new changes can be validated against a known set of expected behaviors
- **Format breadth** — exercises the full range of container and codec combinations the library claims to support

## Public test media sources

### FFmpeg FATE samples

**https://samples.ffmpeg.org/**

The FFmpeg project's own conformance test suite. Contains thousands of files covering nearly every container and codec combination, plus edge cases, broken files, and format corner cases. This is the gold standard for media test corpora.

FrameFlow should selectively pull relevant samples rather than mirroring the entire collection.

### Matroska test files

**https://github.com/Matroska-Org/matroska-test-files**

Official Matroska container test suite with various codec and feature combinations. Useful for MKV and WebM container testing.

### Xiph.org test media

**https://media.xiph.org/**

Free, high-quality test media from the Xiph.org foundation. Includes uncompressed and losslessly compressed reference media useful for codec conformance testing. Contains the well-known "Big Buck Bunny," "Tears of Steel," and other open-source film clips.

### Blender open movies

**https://studio.blender.org/films/**

Creative Commons licensed short films rendered at various resolutions and frame rates. Useful for real-world playback testing with complex scene content: "Big Buck Bunny" (1080p, 4K), "Sintel," "Tears of Steel," "Cosmos Laundromat."

### Netflix open content

**https://opencontent.netflix.com/**

High-quality test sequences in various codecs and resolutions. Includes challenging content for codec testing: high motion, film grain, dark scenes. Available under Creative Commons.

### ITU-T / ISO test sequences

Standard conformance test sequences for H.264, H.265, and other codecs. Available from ITU-T and used by codec implementors worldwide for bitstream conformance verification.

## Format coverage matrix

### Containers

| Container | Extension | Priority | Notes |
|-----------|-----------|----------|-------|
| MP4/MOV | .mp4, .mov | **P0** | Most common container for H.264/H.265 |
| Matroska | .mkv | **P0** | Flexible container, common for high-quality media |
| WebM | .webm | **P1** | VP8/VP9/AV1 in Matroska subset |
| AVI | .avi | **P1** | Legacy but still encountered |
| MPEG-TS | .ts, .mts | **P1** | Broadcast and streaming transport |
| FLV | .flv | **P2** | Legacy Flash container |
| OGG | .ogg, .ogv | **P2** | Xiph container family |
| WAV | .wav | **P0** | Uncompressed audio reference |

### Video codecs

| Codec | Priority | Notes |
|-------|----------|-------|
| H.264 / AVC | **P0** | Most widely used video codec |
| H.265 / HEVC | **P0** | 4K and HDR content |
| VP9 | **P1** | WebM, YouTube |
| AV1 | **P1** | Next-generation, growing adoption |
| VP8 | **P2** | Legacy WebM |
| MPEG-2 | **P2** | DVD, broadcast |
| MJPEG | **P2** | Webcams, some cameras |

### Audio codecs

| Codec | Priority | Notes |
|-------|----------|-------|
| AAC | **P0** | Most common lossy audio in MP4 |
| MP3 | **P0** | Legacy but ubiquitous |
| Opus | **P1** | Modern, high quality, WebM |
| FLAC | **P1** | Lossless, common in MKV |
| Vorbis | **P2** | OGG container |
| AC-3 / E-AC-3 | **P2** | Surround sound, Blu-ray |
| PCM | **P0** | Uncompressed reference |

## Edge case test files

These files should be included in the corpus to test robustness and error handling:

### Structural edge cases

- **Truncated file** — file ends mid-packet or mid-frame
- **Missing moov atom** — MP4 with moov at end, then truncated before moov
- **Corrupt headers** — container headers with invalid field values
- **Zero-length streams** — container with a stream that contains no packets
- **Multiple video streams** — file with 2+ video tracks
- **No audio stream** — video-only file
- **No video stream** — audio-only file in a video container
- **Very short file** — sub-second duration
- **Very long duration metadata** — file claiming hours of content with little actual data

### Timing edge cases

- **Variable frame rate (VFR)** — frames with non-uniform PTS intervals
- **Non-monotonic PTS** — timestamps that go backward (common in some capture devices)
- **Large PTS gaps** — significant timestamp discontinuities mid-stream
- **PTS/DTS mismatch** — B-frame reordering with significant decode/presentation time differences
- **Zero-start offset** — PTS starting at 0 vs. large initial offset

### Resolution and format edge cases

- **Odd dimensions** — width or height not divisible by 2 (e.g., 1921×1081)
- **Very small** — 1×1, 16×16 pixel video
- **Very large** — 8K (7680×4320) if feasible for testing
- **Non-square pixels** — SAR ≠ 1:1 (anamorphic content)
- **Unusual pixel formats** — YUV 4:2:2, YUV 4:4:4, 10-bit, 12-bit

### Audio edge cases

- **Multichannel** — 5.1, 7.1 channel layouts
- **High sample rate** — 96 kHz, 192 kHz
- **Sample rate mismatch** — audio at 48 kHz in container marked as 44.1 kHz
- **Channel layout changes** — mid-stream channel configuration change
- **Silent audio** — all-zero samples

## Synthetic test file generation

For edge cases not covered by public corpora, FrameFlow can generate synthetic test files using FFmpeg:

```bash
# Generate a 10-second 1080p test pattern with AAC audio
ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=30:duration=10 \
       -f lavfi -i sine=frequency=440:duration=10 \
       -c:v libx264 -preset fast -c:a aac -b:a 128k \
       test-1080p-h264-aac.mp4

# Generate VFR content (variable frame rate)
ffmpeg -f lavfi -i "testsrc2=size=1280x720:rate=1:duration=10" \
       -vf "setpts='if(mod(N,3),PTS,PTS+0.5/TB)'" \
       -c:v libx264 -vsync vfr \
       test-vfr.mp4

# Generate odd-dimension video
ffmpeg -f lavfi -i testsrc2=size=1921x1081:rate=30:duration=5 \
       -c:v libx264 test-odd-dimensions.mp4

# Generate audio-only in MP4
ffmpeg -f lavfi -i sine=frequency=440:duration=30 \
       -c:a aac -b:a 128k test-audio-only.mp4

# Generate video-only (no audio)
ffmpeg -f lavfi -i testsrc2=size=640x480:rate=24:duration=10 \
       -c:v libx264 -an test-video-only.mp4

# Generate truncated file (write then truncate)
ffmpeg -f lavfi -i testsrc2=size=1280x720:rate=30:duration=10 \
       -c:v libx264 temp-full.mp4
# Then truncate to 50% of file size in PowerShell:
# $bytes = [System.IO.File]::ReadAllBytes("temp-full.mp4")
# [System.IO.File]::WriteAllBytes("test-truncated.mp4", $bytes[0..($bytes.Length/2)])

# Generate multichannel audio (5.1)
ffmpeg -f lavfi -i "sine=frequency=440:duration=10" \
       -af "channelmap=0|0|0|0|0|0:5.1" \
       -c:a aac test-5.1-audio.mp4
```

## As built

The strategy below was written before the corpus existed. What shipped is
option 2 without the downloads: the corpus is generated locally, not fetched.

- **Generator:** `scripts/generate-test-corpus.cs`, run as
  `dotnet run scripts/generate-test-corpus.cs` (add `-- --force` to regenerate).
  It drives FFmpeg's `testsrc2` and `sine` lavfi sources, so it needs FFmpeg on
  `PATH` or in `runtimes/{rid}/native/` — run `scripts/fetch-ffmpeg.cs` first.
- **Output:** `tests/corpus/files/`, gitignored. Nothing media-shaped is
  committed, so Git LFS was never needed.
- **`tests/corpus/manifest.json`:** checked in. A flat JSON array of 24 entries
  describing what the generator should produce — `filename`, `category`
  (`basic-video`, `basic-audio`, `combined-av`, `pixel-format`, `edge-case`,
  `benchmark`),
  `container`, `videoCodec`, `pixelFormat`, `width`, `height`, `frameRate`,
  `durationSeconds`, `description`. Not the URL-and-checksum shape sketched
  below, because nothing is downloaded.
- **`tests/corpus/test-expectations.json`:** written by the generator, and the
  file `FrameFlow.Integration.Tests` asserts against.
- **`category: benchmark` is opt-in.** Those entries are generated only by
  `dotnet run scripts/generate-test-corpus.cs -- --include-benchmarks`, because
  they are large and slow (~81 MB and about a minute each, against <1 MB and ~1s
  for everything else) and no conformance test needs them. They exist to measure
  throughput under a realistic decode load — `testsrc2` encodes to almost nothing
  and understates it by roughly 30%. When the flag is absent they contribute no
  `test-expectations.json` entry either, so the default corpus is unaffected.
- **Absence is a skip, not a failure.**
  `FfmpegBootstrapFixture` resolves the corpus directory and skips corpus-backed
  tests when it is empty.

The public sources listed above (FATE, Matroska, Xiph, Blender) remain the
reference for broadening coverage; none of them are wired into the build.

## Corpus management (original strategy)

### Git LFS or download script

Test media files should not be committed directly to the repository. Options:

1. **Git LFS** — track media files with Git LFS for transparent versioning
2. **Download script** — a PowerShell/bash script that fetches files from known URLs and verifies checksums
3. **Hybrid** — small synthetic files in Git LFS, large public files via download script

The download script approach is recommended for CI, as it avoids LFS bandwidth costs and keeps the repository lean. A manifest file (`tests/corpus/manifest.json`) should list each file with its URL, SHA-256 checksum, and expected properties.

### Corpus manifest format

```json
{
  "files": [
    {
      "name": "test-1080p-h264-aac.mp4",
      "source": "synthetic",
      "sha256": "abc123...",
      "properties": {
        "container": "mp4",
        "videoCodec": "h264",
        "audioCodec": "aac",
        "width": 1920,
        "height": 1080,
        "duration": 10.0,
        "fps": 30
      }
    },
    {
      "name": "big-buck-bunny-1080p.mp4",
      "source": "https://test-videos.co.uk/bigbuckbunny/mp4-h264/1080/30",
      "sha256": "def456...",
      "properties": {
        "container": "mp4",
        "videoCodec": "h264",
        "audioCodec": "aac",
        "width": 1920,
        "height": 1080
      }
    }
  ]
}
```

### CI integration

The CI workflow should:

1. Check for cached corpus files
2. Download missing files using the manifest
3. Verify checksums
4. Run corpus-based tests

This keeps CI fast on repeat runs while ensuring the corpus is always available.
