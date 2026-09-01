# FrameFlow

Cross-platform FFmpeg-based media playback for .NET, with a UI-agnostic core.

FrameFlow decodes and plays audio and video on Windows, Linux and macOS. The
playback core has no UI dependency: A/V sync, seeking, and buffering happen
behind interfaces, and presenters attach at the edges. Avalonia and SDL
presenters ship in the box, and you can write your own.

> **Pre-1.0.** Public surface and internal contracts change freely between
> releases. There are no external consumers yet, so the bias is toward getting
> the shape right rather than staying compatible.

## Install

Packages are on nuget.org. You need the library plus the native FFmpeg
binaries, which ship as a separate runtime package:

```bash
dotnet add package FrameFlow.Player --prerelease
```

```bash
dotnet add package FrameFlow.Native.Runtime --prerelease
```

Requires .NET 10. Add a presenter and an audio backend for the platform you
target — `FrameFlow.Avalonia`, `FrameFlow.Sdl`, `FrameFlow.Audio.OpenAL`. The
full list is under [Packages](#packages).

## Quick start

Three construction surfaces, layered from "full player" down to "raw state
machine". Pick by what you need from playback:

| Your scenario | Start from | Returns |
|---|---|---|
| App or host playback — seek, pause, repeat, observables | `MediaPlayer.CreateAsync(...)` | `IMediaPlayer` |
| Open a file and play it to the end | `FrameFlowPlayer.Open(...).BuildAsync()` | `PlayerSession` |
| Driving the state machine yourself | `PlaybackController.Create(...)` | `IPlaybackController` |

### `MediaPlayer.CreateAsync`

The usual entry point. Give it a source and the sinks you want, then drive
playback through the returned `IMediaPlayer`:

```csharp
using FrameFlow;                 // HardwareDecodeMode lives here
using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using FrameFlow.Player;

// OpenAlAudioSink also implements IClockSource, so it becomes the master clock.
var audioSink = new OpenAlAudioSink();

await using var player = await MediaPlayer.CreateAsync(
    source: MediaSource.FromFile(path),
    videoSink: videoSink,   // an Avalonia video surface's sink, or null for audio-only
    audioSink: audioSink,
    hardwareDecodeMode: HardwareDecodeMode.Auto,
    initialRepeatMode: RepeatMode.Off);

await player.PlayAsync();
```

Do not drop `using FrameFlow;`. `HardwareDecodeMode` is in the root `FrameFlow`
namespace while the other types here are not. The examples in this repository
compile without it only because they declare namespaces under `FrameFlow.*`.

### `FrameFlowPlayer.Open` — the fluent builder

When you only need "open a file and play it to the end", with no seek, pause,
or repeat:

```csharp
await using var player = await FrameFlowPlayer.Open(path)
    .WithAudioSink(audioSink)   // .WithAvaloniaVideoView(view) / .WithOpenAlAudio() also available
    .BuildAsync();

await player.PlayToCompletionAsync(ct);
```

### Generic Host and DI

`services.AddFrameFlow()` registers the engine's *environment* pieces: the
OpenAL backend, the FFmpeg bootstrap as a hosted service, the Avalonia video
sink, and options. The playback session itself stays an explicitly created
runtime object — resolve the registered sinks and hand them to one of the
surfaces above rather than resolving a player singleton:

```csharp
builder.Services
    .AddFrameFlow()
    .AddFrameFlowOpenAlAudio()   // registers IAudioSink (container-owned)
    .AddHostedBootstrap();       // FFmpeg bootstrap runs at host startup

// …then, inside an IHostedService, resolve IAudioSink and build the session:
await using var player = await FrameFlowPlayer.Open(path)
    .WithAudioSink(resolvedAudioSink)
    .BuildAsync(ct);
```

## What works

- software decode and a hardware-decode path
- a zero-copy Windows presenter that hands GPU frames straight to a D3D
  composition-interop surface
- OpenAL audio output on all three platforms, doubling as the master clock
- Avalonia and SDL presenters
- camera capture and an H.264 to MP4 encoder
- optional DirectML and CUDA inference: YOLO detection, Whisper captioning

11 runnable example apps under `examples/` exercise these against real files
and live camera and multicast sources.

## Packages

| Area | Packages |
|---|---|
| Substrate | `FrameFlow.Native` (FFmpeg resolution and bootstrap), `FrameFlow.Media` (shared contracts) |
| Pipeline | `FrameFlow.Graph` (processing graph and node pipeline) |
| Decode / encode | `FrameFlow.Decoding`, `FrameFlow.Encoding` |
| Playback | `FrameFlow.Playback` (A/V sync, queues, clocks), `FrameFlow.Player` (composition on top) |
| Camera / video | `FrameFlow.Camera`, `FrameFlow.Video` |
| Audio | `FrameFlow.Audio`, `FrameFlow.Audio.OpenAL` |
| Presenters | `FrameFlow.Avalonia`, `FrameFlow.Avalonia.Windows`, `FrameFlow.Sdl` |
| Inference | `FrameFlow.Inference.Abstractions`, `.Ort`, `.Cuda`, `.Dml`, `FrameFlow.Yolo`, `FrameFlow.Face`, `FrameFlow.Whisper` |

`FrameFlow.Native.Runtime` carries the FFmpeg binaries. The libraries do not
reference it — add it yourself, or supply the natives another way.

`FrameFlow.MotionClip` is a camera-tracked motion-clip capture tool. It is not
on nuget.org; take the self-contained binary from
[Releases](https://github.com/charles8051/frame-flow/releases).

## Building from source

FrameFlow needs FFmpeg shared libraries on disk. They are gitignored, so prime
them once per clone:

```bash
dotnet run scripts/fetch-ffmpeg.cs
```

That writes into `runtimes/{rid}/native/`, which `Directory.Build.targets`
copies into every project's output. Then:

```bash
dotnet build ./FrameFlow.slnx --nologo
```

The whole solution restores from nuget.org alone. Six projects take a
`PackageReference` on `FrameFlow.Native.Runtime` for self-contained publish —
`FrameFlow.MotionClip` and the `AvaloniaPlayer`, `Camera.Inference.Dml`,
`DualPlayer`, `Multicast.Dml` and `ZeroCopyInterop` examples. That package is
mapped to nuget.org by exact id in `nuget.config`; the `FrameFlow.*` prefix is
deliberately not mapped there, so a new FrameFlow `PackageReference` needs its
id added.

`scripts/fetch-cuda.cs` (CUDA execution provider) and
`scripts/generate-test-corpus.cs` (integration-test media) are documented in
[scripts/README.md](scripts/README.md).

## Tests

19 test projects live under `tests/`. The integration suite needs the FFmpeg
runtimes and a generated corpus:

```bash
dotnet run scripts/generate-test-corpus.cs
```

```bash
dotnet test ./FrameFlow.slnx --nologo
```

`scripts/run-tests.sh` is faster — it fans one `dotnet test` process out per
project, and needs a prior `dotnet build`.

A handful of tests open a real SDL window and are skipped unless
`FRAMEFLOW_VISUAL_TESTS=1`. Nothing sets it, including CI, so presenter and
windowing regressions are not caught by normal validation. Run them
deliberately, on a machine with a display:

```bash
FRAMEFLOW_VISUAL_TESTS=1 dotnet test ./tests/FrameFlow.Integration.Tests --nologo
```

`frameflow.runsettings` pins the gate to `0` and injects it into the test host,
so passing `-settings frameflow.runsettings` overrides an ambient
`FRAMEFLOW_VISUAL_TESTS=1`. Use one or the other.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the layering and the reasoning behind it
- [docs/adr/](docs/adr/) — architectural decision records, the authority on what was decided and why
- [CONTRIBUTING.md](CONTRIBUTING.md) — build prerequisites, test corpus, commit convention

The other directories under `docs/` are project history. `ROADMAP.md` and
`phases/` record how the project got here, `investigations/` holds dated bug
and perf write-ups, and `archive/` holds superseded material.

## Contributing

**Not accepting contributions.** Pull requests will not be reviewed or merged.

Bug reports are welcome in the issue tracker, with no promise of a reply.

Security problems go through
[the private advisory form](https://github.com/charles8051/frame-flow/security/advisories/new),
not the issue tracker — see [SECURITY.md](SECURITY.md).

## License

FrameFlow is released under the [PolyForm Small Business License 1.0.0](LICENSE.md).

It is **source-available, not open source**: the license is not OSI-approved,
though it does carry the SPDX identifier `PolyForm-Small-Business-1.0.0`. In
short, you may use, modify and distribute FrameFlow for any purpose *provided*
your company has fewer than 100 people and less than USD 1,000,000 (2019,
inflation-adjusted) in prior-year revenue. Personal, noncommercial, educational
and evaluation use are permitted regardless of company size. `LICENSE.md` is
the authority; this paragraph is not.

If your company is over those thresholds, contact the maintainer about a
commercial license.

### Third-party components

FrameFlow's own license does not extend to the components it builds on. The
significant ones:

| Component | License | How it is distributed |
|---|---|---|
| FFmpeg (LGPL build) | LGPL-3.0-or-later | Native libraries, fetched at build time by `scripts/fetch-ffmpeg.cs`; **not** committed to this repository |
| OpenAL Soft (via `Silk.NET.OpenAL.Soft.Native`) | LGPL-2.1 | NuGet package dependency |
| ONNX Runtime, DirectML, CUDA/cuDNN | vendor terms | NuGet package dependencies; CUDA redistributables are not published with the package |
| YOLO / Ultralytics weights | AGPL-3.0 | **Not redistributed.** Models are fetched at runtime into a local cache; you are responsible for your own use of them |

The pinned FFmpeg build is a prebuilt LGPL archive from BtbN/FFmpeg-Builds, not
a build this repository configures. What makes it LGPL is that neither
`--enable-gpl` nor `--enable-nonfree` is present in its `ffmpeg -buildconf`.
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full reasoning,
the pinned build identity, and where to obtain its corresponding source. It
ships inside every package.

Two packages pack FFmpeg's binaries — `FrameFlow.Native` and
`FrameFlow.Native.Runtime` — and both ship the operative licence texts
alongside: LGPL-3.0, the GPL-3.0 it incorporates by reference, Apache-2.0 for
the OpenCORE codecs inside `avcodec`, and LGPL-2.1 as the record of the
upstream grant. `FrameFlow.MotionClip` packs no natives but receives them at
publish time, so it ships the same texts. `FrameFlow.Audio.OpenAL` receives
OpenAL Soft the same way and ships the LGPL-2.1 text that governs it.
