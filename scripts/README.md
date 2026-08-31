# scripts/

Dev-time helpers. Not shipped in any NuGet package; not assumed by
CI. Each script is a single-file `dotnet run` C# 10 app — no
project, no restore, just `dotnet run scripts/<name>.cs`.

The two native-payload scripts (`fetch-ffmpeg.cs`, `fetch-cuda.cs`)
write into the repo's gitignored `runtimes/` directory. The repo-root
`Directory.Build.targets` glob then copies those payloads into every
project's output dir at build time, so test/example projects find
them via `AppContext.BaseDirectory/runtimes/{rid}/native/` with no
extra wiring.

`generate-test-corpus.cs` is not one of them: it writes test media to
`tests/corpus/files/` and `tests/corpus/subs/`, both gitignored, and
the tests read them from there by repo-relative path.

## `fetch-ffmpeg.cs` — FFmpeg native libraries

Downloads the FFmpeg 7.x shared-build DLLs from BtbN's GitHub
release archive into `runtimes/{rid}/native/`. Required on a fresh
clone before anything decoding-related works.

See the script header for full usage; the most common invocation is
just `dotnet run scripts/fetch-ffmpeg.cs` (current platform, LGPL
build).

## `fetch-cuda.cs` — CUDA Toolkit + cuDNN

Downloads the full set of CUDA Toolkit + cuDNN DLLs that ORT 1.26's
CUDA execution provider links against — cudart, cuBLAS, cuFFT,
cuRAND, cuSPARSE, cuSOLVER, nvJitLink, cuDNN — from NVIDIA's public
redist endpoint into `runtimes/win-x64/native/` at the repo root.
Same on-disk layout as `fetch-ffmpeg.cs`, same MSBuild glob picks
them up. Total payload ≈ 3 GB cached, one-time per developer.

`CudaDllResolver` (in `src/FrameFlow.Inference.Cuda/Bootstrap/`) probes
`AppContext.BaseDirectory/runtimes/{rid}/native/` first, before
`%CUDA_PATH%` and the canonical install root. So after running this
script + a rebuild, examples that touch ORT's CUDA EP — LiveCaptioning's
YOLOv8 detector, for one — work without a system-wide CUDA install.

### Usage

```bash
# Default — CUDA 12.9.1 + cuDNN 9.22.0
dotnet run scripts/fetch-cuda.cs

# CUDA only — skip the 1.8 GB cuDNN archive
dotnet run scripts/fetch-cuda.cs -- --no-cudnn

# Pin different versions (e.g., to match a future ORT-Gpu bump)
dotnet run scripts/fetch-cuda.cs -- --cuda-version 12.9.1 --cudnn-version 9.22.0

# Force re-download (ignores cached archives in %TEMP%)
dotnet run scripts/fetch-cuda.cs -- --force
```

### Why this exists (vs. requiring the system CUDA install)

LiveCaptioning's YOLO detector now gracefully falls back to
captioning-only mode when CUDA isn't available, so this script is
opt-in convenience rather than a hard requirement. Without the
script you still have a working captioning demo; with the script
you also get GPU object detection.

The layout the resolver expects is per-app
(`AppContext.BaseDirectory`), so the redists have to land in each
app's own output directory rather than somewhere shared — which is
why this is a script run per clone rather than a package
dependency.

Win-x64 only today.

### What this script does NOT handle

- **GPU driver.** Kernel-mode, never redistributable.
- **ORT native + EP wrappers.** Already shipped via
  `Microsoft.ML.OnnxRuntime.Gpu`.
- **FFmpeg.** That's `fetch-ffmpeg.cs`.
- **System-wide install.** This script writes to the repo only.
  Does not modify `%CUDA_PATH%`, `%PATH%`, the registry, or any
  system folder.

## `generate-test-corpus.cs` — synthetic test media

Generates the canonical test corpus (small AV files in various
codecs/containers) under `tests/corpus/files/`. Run after a fresh
clone or whenever a new corpus entry is added; idempotent
(skip-if-present per file).

Needs FFmpeg on PATH (or pass `--ffmpeg <path>`). The bundled
LGPL FFmpeg fetched by `fetch-ffmpeg.cs` lacks libx264/libx265, so
this script wants a system FFmpeg with GPL codecs enabled —
`winget install BtbN.FFmpeg.GPL` is the easy path on Windows.
