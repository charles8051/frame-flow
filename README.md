# FrameFlow

FrameFlow is a .NET media playback library built around a clean, headless FFmpeg-based core with platform presenters and audio backends at the edges. It is pre-1.0 with no external consumers yet, so its public surface is still free to change.

The design goals are:

- strong separation between playback core and UI
- composition over inheritance
- modern .NET usability with DI, options, and host-friendly patterns
- software-first correctness before hardware acceleration
- a reusable core that can support multiple presenters beyond Avalonia

## Current status

FrameFlow plays real media today. The FFmpeg bootstrap, demuxing, decoding,
headless playback orchestration (A/V sync, seeking, pause/resume, repeat), and
presentation are implemented across the 21 library projects under `src/`, with
a substrate processing graph (`FrameFlow.Graph`) carrying frames from the
decoder to the sinks.

What works:

- a headless playback core driven by a pull-shape controller, exposed to
  consumers through `MediaPlayer.CreateAsync(...)` and the
  `FrameFlowPlayer.Open(...)` fluent builder (`FrameFlow.Player`)
- software decode plus a hardware-decode path, and a zero-copy Windows
  presenter (`FrameFlow.Avalonia.Windows`) that hands GPU frames straight to a
  composition-interop surface
- OpenAL cross-platform audio output that doubles as the master clock
- Avalonia and SDL presenters, camera capture, an H.264 → MP4 encoder, and
  optional DirectML / CUDA inference (YOLO detection, Whisper captioning)
- **11 runnable example apps** under `examples/` (plus a shared
  `FrameFlow.Examples.Common` helper library) that exercise these paths against
  real files and live camera/multicast sources

The architecture is still pre-1.0 and has no external consumers, so public
surface and internal contracts change freely. Durable decisions are recorded
as ADRs in `docs/adr/` — the series currently runs through **ADR-0067**.

## Quick start (consumer construction)

FrameFlow exposes three construction surfaces, each aimed at a different
scenario. Pick by what you need from playback, not by preference — they layer
from "full player" down to "raw state machine":

| Your scenario | Start from | Returns | Example |
|---|---|---|---|
| **App / host playback** (seek, pause, repeat, observables) | `MediaPlayer.CreateAsync(...)` | `IMediaPlayer` | DualPlayer, AvaloniaPlayer, Multicast, LiveCaptioning, ZeroCopyInterop |
| **Minimal "open + play to EOS"** (no seek/pause) | `FrameFlowPlayer.Open(...).BuildAsync()` | `PlayerSession` | AudioOnlyPlayer, HostedServicePlayer |
| **Driving the state machine yourself** (custom event loop, transition subscriptions) | `PlaybackController.Create(...)` | `IPlaybackController` | SdlPlayer |

### Default: `MediaPlayer.CreateAsync`

The dominant entry point across the examples — give it a source and the sinks
you want, then drive playback through the returned `IMediaPlayer`. The snippet
below mirrors `examples/FrameFlow.Examples.DualPlayer` (audio mastered to the
OpenAL clock, video to an Avalonia surface):

```csharp
using FrameFlow;                 // HardwareDecodeMode
using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using FrameFlow.Player;

// OpenAlAudioSink is the production audio sink; it also implements
// IClockSource, so it becomes the playback master clock.
var audioSink = new OpenAlAudioSink();

await using var player = await MediaPlayer.CreateAsync(
    source: MediaSource.FromFile(path),
    videoSink: videoSink,   // e.g. an Avalonia video surface's sink, or null for audio-only
    audioSink: audioSink,
    hardwareDecodeMode: HardwareDecodeMode.Auto,
    initialRepeatMode: RepeatMode.Off);

await player.PlayAsync();
```

`using FrameFlow;` is easy to miss and the examples will not remind you.
`HardwareDecodeMode` lives in the root `FrameFlow` namespace while the rest of
the types here are in `FrameFlow.Media` / `FrameFlow.Player`, and every example
in this repository declares a namespace under `FrameFlow.*` — so C# walks up
the enclosing namespaces and resolves it without the `using`. Your own code,
in your own namespace, gets no such lookup.

### Minimal: the `FrameFlowPlayer.Open(...)` fluent builder

When you only need "open a file and play it to the end" — no seek, pause, or
repeat — the fluent builder produces a leaner `PlayerSession`. This is the shape
`examples/FrameFlow.Examples.AudioOnlyPlayer` uses:

```csharp
await using var player = await FrameFlowPlayer.Open(path)
    .WithAudioSink(audioSink)   // .WithAvaloniaVideoView(view) / .WithOpenAlAudio() also available
    .BuildAsync();

await player.PlayToCompletionAsync(ct);
```

### Host / DI composition: the `services.AddFrameFlow…()` builder

For a Generic Host app, the `services.AddFrameFlow()` builder family registers
the engine's **environment** pieces against `IServiceCollection`: the OpenAL
audio backend (`AddFrameFlowOpenAlAudio()`), the FFmpeg bootstrap as a hosted
service (`AddHostedBootstrap()`), the Avalonia video sink
(`AddFrameFlowAvaloniaVideoSink()`), and FrameFlow options. Per the
architecture's lifecycle split (see `docs/ARCHITECTURE.md`,
"Hosted lifecycle integration"), the **playback session itself stays an
explicitly created runtime object** — you resolve the registered sinks from the
container and hand them to one of the construction surfaces above, rather than
resolving a player singleton:

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

`examples/FrameFlow.Examples.HostedServicePlayer` is the runnable reference for
this path end-to-end.

## Solution structure

Projects under `src/`:

**Substrate**
- `FrameFlow.Native` — FFmpeg binary resolution and bootstrap
- `FrameFlow.Media` — shared contracts: metadata models, decoded payload types, enums

**Graph / pipeline**
- `FrameFlow.Graph` — processing graph and node pipeline abstraction. Forked
  in-tree from a first-party predecessor substrate; ADRs that cite "Crossbar"
  mean that predecessor, which is not published — see
  [docs/adr/README.md](docs/adr/README.md#a-note-on-the-crossbar-citations)

**Decode / encode**
- `FrameFlow.Decoding` — FFmpeg demux, decode, and resampling
- `FrameFlow.Encoding` — H.264 → MP4 encoder terminal (ADR-0053)
- `FrameFlow.MotionClip` — camera-tracked motion-clip capture, packaged as the `motionclip` dotnet tool

**Playback**
- `FrameFlow.Playback` — headless playback session: A/V sync, queues, clocks
- `FrameFlow.Player` — player-level composition on top of the playback core

**Camera / video**
- `FrameFlow.Camera` — camera capture
- `FrameFlow.Video` — video frame processing

**Audio**
- `FrameFlow.Audio` — audio pipeline and processing contracts
- `FrameFlow.Audio.OpenAL` — OpenAL audio backend adapter

**Inference**
- `FrameFlow.Inference.Abstractions` — shared inference contracts
- `FrameFlow.Inference.Ort` — ONNX Runtime inference session and provider bootstrap
- `FrameFlow.Inference.Cuda` — CUDA inference backend
- `FrameFlow.Inference.Dml` — DirectML inference backend
- `FrameFlow.Yolo` — YOLO object detection (ADR-0050, ADR-0051)
- `FrameFlow.Face` — face detection
- `FrameFlow.Whisper` — Whisper speech-to-text (ADR-0039)

**Presenters**
- `FrameFlow.Avalonia` — Avalonia UI presenter adapter
- `FrameFlow.Avalonia.Windows` — zero-copy Windows presenter: GPU frames to a D3D composition-interop surface (ADR-0061, ADR-0063, ADR-0064)
- `FrameFlow.Sdl` — SDL presenter adapter

## Documentation structure

Design and decision docs live under `docs/`.

- `docs/ARCHITECTURE.md` — long-lived architectural vision and the reasoning behind the layering
- `docs/adr/` — architectural decision records, the authority on what was decided and why (currently through ADR-0067)
- `docs/ROADMAP.md` — the phase model; all thirteen phases are complete, so it now reads as delivery history
- `docs/phases/` — per-phase execution docs, likewise historical
- `docs/AGENT_MATRIX.md` — subsystem ownership and review gates
- `docs/issues/` — open review findings and their phase gates
- `docs/investigations/` — dated write-ups of specific bugs and perf work
- `docs/patterns/` — implementation patterns (playback statechart, video sink and frame pool)
- `docs/archive/` — superseded surveys and audits, each bannered with what replaced it; history, not guidance
- `docs/DEFERRED_WORK.md` — described-but-unscheduled work, and what would unblock each item

Recommended usage:

1. keep `ARCHITECTURE.md` relatively stable
2. record important irreversible or high-impact choices in ADRs
3. treat `ROADMAP.md` and `docs/phases/` as the record of how the project got here, not as a live plan
4. file review findings in `docs/issues/` and deferred work in `docs/DEFERRED_WORK.md`

## Build

FrameFlow needs FFmpeg shared libraries on disk before anything
decoding-related works. They are gitignored, so prime them once per clone:

```bash
dotnet run scripts/fetch-ffmpeg.cs
```

That writes into `runtimes/{rid}/native/`, which the repo-root
`Directory.Build.targets` copies into every project's output directory. Then
build:

```bash
dotnet build ./FrameFlow.slnx --nologo
```

**Six projects do not restore from a fresh public clone**, and building the
whole `.slnx` will fail on them:

```
error NU1100: Unable to resolve 'FrameFlow.Native.Runtime (>= 0.1.1-alpha)'.
PackageSourceMapping is enabled, the following source(s) were not considered:
nuget.org.
```

They are `FrameFlow.MotionClip` and the `AvaloniaPlayer`,
`Camera.Inference.Dml`, `DualPlayer`, `Multicast.Dml` and `ZeroCopyInterop`
examples. All six take a `PackageReference` on `FrameFlow.Native.Runtime`,
which is not published to nuget.org yet, so the mapping in `nuget.config`
leaves them with no source they can reach. Everything else — all 21 libraries
and the rest of the tests and examples — restores from nuget.org alone.

Until that package is published, build a project or a test project directly
rather than the whole solution:

```bash
dotnet build ./src/FrameFlow.Playback/FrameFlow.Playback.csproj --nologo
```

The other dev-time scripts (`fetch-cuda.cs` for the CUDA execution provider,
`generate-test-corpus.cs` for the integration-test media) are documented in
[scripts/README.md](scripts/README.md).

## Test

19 test projects live under `tests/`. The integration suite needs the FFmpeg
runtimes and the generated corpus:

```bash
dotnet run scripts/generate-test-corpus.cs
```

Then run everything:

```bash
dotnet test ./FrameFlow.slnx --nologo
```

`scripts/run-tests.sh` is the faster path — it fans one `dotnet test` process
out per project and requires a prior `dotnet build`.

### Visual SDL tests

A handful of tests open a real SDL window and are skipped unless
`FRAMEFLOW_VISUAL_TESTS` is set to `1`. Nothing sets it: neither the commands
above nor CI passes `-settings frameflow.runsettings`, and the gate in
`FfmpegBootstrapFixture` skips when the variable is absent. **They are not
covered by any automated run**, so presenter and windowing regressions will not
be caught by normal validation. Run them deliberately, on a machine with a
display:

```bash
FRAMEFLOW_VISUAL_TESTS=1 dotnet test ./tests/FrameFlow.Integration.Tests --nologo
```

`frameflow.runsettings` carries the run timeouts and pins the gate to `0`. It
injects that into the test host, so `-settings frameflow.runsettings` overrides
an ambient `FRAMEFLOW_VISUAL_TESTS=1` and the visual tests still skip. Use one
or the other, not both.

## Implementation path

The build followed this dependency order, and the bulk of it has shipped:

1. consumer API and foundational contracts
2. FFmpeg bootstrap and probe
3. demux and metadata inspection
4. video decoding
5. audio decoding
6. playback orchestration
7. synchronization and seeking
8. the Avalonia adapter
9. diagnostics and usability
10. optional acceleration and more presenters (zero-copy Windows presenter, SDL, DirectML/CUDA inference)

See `docs/ROADMAP.md` for the current state and remaining work.

## Design stance

FrameFlow is built around:

- explicit subsystem boundaries
- decoupled lifecycle and processing logic
- fluent configuration where it improves usability
- simple runtime loops rather than overly abstract runtime pipelines

Because the project is pre-1.0 with no external consumers, the bias is "make it
right" over "make it gradual": breaking changes to public surface and internal
contracts are acceptable when the new shape is better.

## Contributing

**Not accepting contributions.** Pull requests will not be reviewed or merged.

[CONTRIBUTING.md](CONTRIBUTING.md) is still the place to start for build
prerequisites, the test corpus, the commit convention, and when a change needs an
ADR — worth reading if you are evaluating the project or working from a fork.

Bug reports are welcome in the issue tracker, with no promise of a reply.

Security problems go through
[the private advisory form](https://github.com/charles8051/frame-flow/security/advisories/new),
not the issue tracker — see [SECURITY.md](SECURITY.md).

## License

FrameFlow is released under the [PolyForm Small Business License 1.0.0](LICENSE.md).

It is **source-available, not open source**: the license is not OSI-approved,
though it does carry the SPDX identifier `PolyForm-Small-Business-1.0.0`. In
short: you may use, modify and distribute FrameFlow for
any purpose *provided* your company has fewer than 100 people and less than
USD 1,000,000 (2019, inflation-adjusted) in prior-year revenue. Personal,
noncommercial, educational and evaluation use are permitted regardless of company
size. `LICENSE.md` is the authority; this paragraph is not.

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

The pinned FFmpeg build is a prebuilt LGPL archive from BtbN/FFmpeg-Builds,
not a build this repository configures. What makes it LGPL is that neither
`--enable-gpl` nor `--enable-nonfree` is present in its `ffmpeg -buildconf`;
the absence of `libx264` and `libx265` follows from that rather than proving
it. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full
reasoning. Two test-corpus fixtures need those GPL encoders and so cannot be
produced; `dotnet run scripts/generate-test-corpus.cs` reports which and
explains the workaround.

Attribution, the pinned FFmpeg build identity, and where to obtain its
corresponding source are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md),
which ships inside every package.

Two packages actually pack FFmpeg's binaries — `FrameFlow.Native` and
`FrameFlow.Native.Runtime` — and both ship the operative licence texts alongside:
LGPL-3.0, the GPL-3.0 it incorporates by reference, Apache-2.0 for the OpenCORE
codecs inside `avcodec`, and LGPL-2.1 as the record of the upstream grant.
`FrameFlow.MotionClip` packs no natives but receives them at publish time, so it
ships the same texts. `FrameFlow.Audio.OpenAL` receives OpenAL Soft the same way
and ships the LGPL-2.1 text, which is the version that governs it.
