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

Downloads the FFmpeg 9.x shared-build DLLs from BtbN's GitHub
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

LiveCaptioning's YOLO detector falls back to the CPU when CUDA isn't
available, so this script is opt-in convenience rather than a hard
requirement. Without the script, object detection runs on the CPU;
with it, detection runs on the GPU.

The layout the resolver expects is per-app
(`AppContext.BaseDirectory`), so the redists have to land in each
app's own output directory rather than somewhere shared — which is
why this is a script run per clone rather than a package
dependency.

Win-x64 only today. On Linux, see the next section.

### Linux x64: CUDA 12 and cuDNN 9 from the system

`fetch-cuda.cs` does not download Linux libraries. On linux-x64,
`Microsoft.ML.OnnxRuntime.Gpu` 1.26 (through its `.Gpu.Linux` package)
ships `libonnxruntime.so` and `libonnxruntime_providers_cuda.so`, but not
the CUDA or cuDNN libraries that provider links against. A Linux host
needs:

| Layer | Libraries | Comes from |
| --- | --- | --- |
| Driver | `libcuda.so.1` | The NVIDIA driver. Never bundled with an app. |
| CUDA 12 runtime | `libcudart.so.12`, `libcublas.so.12`, `libcublasLt.so.12`, `libcufft.so.11`, `libcurand.so.10` | NVIDIA's CUDA apt repository, or a CUDA 12 Toolkit |
| cuDNN 9 | `libcudnn.so.9` and its `libcudnn_*.so.9` sub-libraries | NVIDIA's CUDA apt repository, or the cuDNN 9 for CUDA 12 archive |

CUDA 13 does not work with ORT 1.26, which is built against CUDA 12.

On Ubuntu or Debian, after adding NVIDIA's CUDA repository (the
`cuda-keyring` package from
`https://developer.download.nvidia.com/compute/cuda/repos/<distro>/x86_64/`):

```bash
sudo apt install cuda-cudart-12-9 libcublas-12-9 libcufft-12-9 libcurand-12-9 libcudnn9-cuda-12
```

Any CUDA 12 minor works in place of `12-9`. The driver is separate:
`sudo ubuntu-drivers install` on Ubuntu, or the distribution's NVIDIA
driver package. In a container, the host's driver is mounted in by the
NVIDIA Container Toolkit (`docker run --gpus all`).

Packages from NVIDIA's repository register their directories with the
loader. Libraries that are on disk but not found (a Toolkit under
`/usr/local/cuda-12.*/lib64`, an unpacked cuDNN archive) can be made
visible in one of three ways:

- list the directory in a file under `/etc/ld.so.conf.d/` and run
  `sudo ldconfig`;
- set `LD_LIBRARY_PATH` before the process starts. The loader reads it
  only at startup, so setting it from inside the process does nothing;
- copy the libraries into `runtimes/linux-x64/native/` under the app's
  base directory.

`CudaDllResolver` loads copies in `runtimes/linux-x64/native/` by
absolute path first, then the rest by soname through the loader's search
path. `CudaBootstrapper.Probe()` reports which layer did not load, with
`apt` package names in its instructions, and the loader's message for a
missing driver in `DriverFailure`. To see what the CUDA provider itself
cannot resolve:

```bash
ldd bin/<config>/net10.0/runtimes/linux-x64/native/libonnxruntime_providers_cuda.so | grep 'not found'
```

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
