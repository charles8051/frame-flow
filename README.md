# FrameFlow

Cross-platform FFmpeg-based media playback for .NET, with a UI-agnostic core.

FrameFlow decodes and plays audio and video on Windows, Linux and macOS. The
playback core has no UI dependency: A/V sync, seeking, and buffering happen
behind interfaces, and presenters attach at the edges. Avalonia and SDL
presenters ship in the box, and you can write your own.

> **Pre-1.0.** Public surface and internal contracts change freely between
> releases. The bias is toward getting the shape right rather than staying
> compatible. Each change is listed with
> its fix in [docs/BREAKING-CHANGES.md](docs/BREAKING-CHANGES.md).

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

`FrameFlowPlayer.Open` builds a player. Two terminals:

| You want | Terminal | Returns |
|---|---|---|
| Playback you drive — play, pause, seek, repeat, observables | `.BuildPlayerAsync()` | `IMediaPlaylistPlayer` |
| Open a file and play it to the end | `.BuildAsync()` | `PlayerSession` |

### `BuildPlayerAsync` — the full player

```csharp
using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using FrameFlow.Player;

await using var player = await FrameFlowPlayer.Open(path)
    .WithOpenAlAudio()          // also implements IClockSource, so it becomes the master clock
    .WithAvaloniaVideoView(view)
    .WithHardwareDecode(HardwareDecodeMode.Auto)
    .WithRepeatMode(RepeatMode.All)
    .BuildPlayerAsync();

var played = await player.PlayAsync();
if (!played.IsSuccess)
    Console.Error.WriteLine($"{played.Error.Category}: {played.Error.Message}");
```

### `BuildAsync` — play to end of stream

When you only need "open a file and play it to the end", with no seek, pause,
or repeat:

```csharp
await using var player = await FrameFlowPlayer.Open(path)
    .WithAudioSink(audioSink)   // .WithAvaloniaVideoView(view) / .WithOpenAlAudio() also available
    .BuildAsync();

await player.PlayToCompletionAsync(ct);
```

`WithRepeatMode`, `WithClock`, `WithHardwareFrames` and `WithAudioActivation`
mean nothing to a `PlayerSession`, so setting one and then asking for a session
is a compile error rather than a dropped setting.

### Playlists

Every player is a queue, so sources can be added while it plays:

```csharp
await using var player = await FrameFlowPlayer.Open([first, second])
    .WithVideoSink(videoSink)
    .WithAudioSink(audioSink)
    .WithRepeatMode(RepeatMode.All)
    .BuildPlayerAsync();

await player.PlayAsync();
await player.AddAsync(third);      // joins the loop
await player.EnqueueAsync(once);   // plays once, then leaves
await player.SkipToNextAsync();
```

`Open(path)` builds the same player over a queue of one, so the transport above
is there whether you opened one file or twenty. The sinks stay warm across every
item, so nothing is rebuilt at a boundary.

`MediaPlayer.CreateAsync(...)` is the positional form of `BuildPlayerAsync`, for
callers who would rather not chain. `PlaybackController.Create(...)` sits below
both and returns the raw `IPlaybackController` state machine. Use it only when
that state machine is what you are building around.

### Generic Host and DI

`services.AddFrameFlow()` registers the engine's *environment* pieces: the
OpenAL backend, the FFmpeg bootstrap as a hosted service, the Avalonia video
sink, and options. The playback session itself stays an explicitly created
runtime object — resolve the registered sinks and hand them to the builder
rather than resolving a player singleton:

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

### Errors

Transport commands return `Result` rather than throwing. A command the state
machine refuses — a seek on a non-seekable source, a play on a disposed player —
is an expected outcome, and `Result.Error` carries an `ErrorCategory`:

```csharp
var seeked = await player.SeekAsync(TimeSpan.FromSeconds(30));
if (!seeked.IsSuccess && seeked.Error.Category == ErrorCategory.InvalidOperation)
    DisableTheSeekBar();
```

Construction is the exception to that: a null sink, a bad argument or a source
that cannot be opened or decoded throws. A failure that arises mid-playback rather than in
answer to a command surfaces on `IMediaPlayer.ErrorOccurred`.

See [ADR-0069](docs/adr/ADR-0069-one-error-model-across-the-playback-stack.md).

## What works

- playlists: one player, one warm presenter, items added and reordered as it plays
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

Any package here works on its own: the FFmpeg resolver installs itself on the
first native call (ADR-0070). Bootstrap explicitly — `AddHostedBootstrap()`, or
`new FrameFlowBootstrapper(options).Initialize()` — to choose which binaries
load or to read the hardware-decode capabilities, before the first decode call.

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

The other scripts — CUDA provider, test corpus — are documented in
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

A few tests open a real SDL window and are skipped unless
`FRAMEFLOW_VISUAL_TESTS=1`. Nothing sets it, CI included, so presenter and
windowing regressions need a deliberate run on a machine with a display:

```bash
FRAMEFLOW_VISUAL_TESTS=1 dotnet test ./tests/FrameFlow.Integration.Tests --nologo
```

## Documentation

- [docs/BREAKING-CHANGES.md](docs/BREAKING-CHANGES.md) — what moved in each release and what to write instead
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
