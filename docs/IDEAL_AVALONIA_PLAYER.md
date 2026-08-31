# The Ideal AvaloniaPlayer — A Target to Shoot For

**Date:** 2026-05-12
**Status:** Design target. Not implemented; serves as the call-site
shape we work back from when filling in the rest of the
Crossbar-shaping roadmap.

> **Update 2026-05-15 / 16 (Crossbar ADR-0010 / ADR-0012).** Code
> samples below reference `IFrameSink<IVideoFrame>` /
> `IFrameSink<PcmAudioBuffer>` as the substrate-side type. These
> have been deleted from Crossbar; the current substrate type is
> `FrameConsumer<TFrame>` (a delegate). FrameFlow's `IVideoSink` /
> `IAudioSink` no longer inherit anything from Crossbar — they're
> standalone `IAsyncDisposable` interfaces that expose a
> `FrameConsumer<TFrame> Consumer { get; }` property. The
> *consumer-facing call shape* this document idealizes is unchanged
> (`.ToSink(sink)` becomes `.ToSink(sink.Consumer)` in current code);
> the gap analysis tables still apply with that substitution.

This document rebuilds the AvaloniaPlayer example from first
principles, assuming an idealized Crossbar-esque pipeline API. Some
of the components don't exist yet; they're called out inline as
**[gap]** with a pointer to where on the roadmap they belong.

The point: *if the library API were perfect, what would the consumer
code look like?* That's the target. The roadmap is the route.

## Table of contents

1. [The ideal — what a player should be in three layers](#the-ideal)
2. [The ideal example, end to end](#the-ideal-example)
3. [What's already built](#whats-already-built)
4. [What's missing — gap inventory mapped to the roadmap](#gap-inventory)
5. [Reading list — the comparison sections](#comparison)

---

## The ideal

The ideal player has three layers, each Crossbar-shaped:

```
┌─────────────────────────────────────────────────────────────────┐
│  Layer 3 — Application                                          │
│  MainWindow.axaml.cs: builds the player, binds it to the UI.    │
│  Knows about file paths, controls, user gestures.               │
├─────────────────────────────────────────────────────────────────┤
│  Layer 2 — Player builder                                       │
│  FrameFlowPlayer.Open(path).WithVideo(...).WithAudio(...).Build().  │
│  Declarative description of the data flow. Compiles to an       │
│  IMediaPlayer that owns the underlying state + pipelines.       │
├─────────────────────────────────────────────────────────────────┤
│  Layer 1 — Substrate                                            │
│  IDecodedMediaStream, FramePipeline<T>, pipeline operators      │
│  (ConvertPixelFormat, Resize, PacedAgainst, ToSinkAsync).       │
│  Crossbar shape end-to-end.                                     │
└─────────────────────────────────────────────────────────────────┘
```

Layer 1 already exists (ADR-0036 / ADR-0037 / ADR-0038). Layer 2 is
the new shape we're missing. Layer 3 is the example we want to
write.

## The ideal example

### `Program.cs`

```csharp
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
```

(Unchanged. The Avalonia bootstrap is orthogonal.)

### `MainWindow.axaml.cs`

```csharp
using FrameFlow.Player;
using FrameFlow.Player.Diagnostics;

public partial class MainWindow : Window
{
    private IMediaPlayer? _player;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync();
        if (path is null) return;

        await TeardownAsync();

        // ── This is the heart of the example. ─────────────────────────
        // Declarative pipeline description. Each call returns a new
        // builder; nothing actually runs until BuildAsync() resolves.
        _player = await FrameFlowPlayer
            .Open(path)
            .WithVideo(video => video
                .ConvertPixelFormat(PixelFormat.Bgra32)
                .ToSink(VideoCanvas))                // [gap] AvaloniaVideoSink as IFrameSink
            .WithAudio(audio => audio
                .ToSink(OpenAlSink.Default))         // [gap] OpenAlSink.Default static
            .BuildAsync();

        // ── Reactive UI bindings — one observable per concern. ────────
        _player.State.Subscribe(s => StatusBadge.Text = s.ToString());
        _player.Position.Subscribe(p => SeekBar.Value = p.TotalSeconds);
        _player.Diagnostics
            .Sample(TimeSpan.FromMilliseconds(500))   // [gap] Diagnostics as IObservable<PlayerDiagnosticsSnapshot>
            .Subscribe(d => DiagnosticsPanel.Render(d));

        await _player.PlayAsync();
    }

    private async void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        if (_player.State.Current == PlaybackState.Playing)
            await _player.PauseAsync();
        else
            await _player.PlayAsync();
    }

    private async void OnSeek(object? sender, RangeBaseValueChangedEventArgs e) =>
        await (_player?.SeekAsync(TimeSpan.FromSeconds(e.NewValue)) ?? Task.CompletedTask);

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        e.Cancel = true;
        await TeardownAsync();
        Close();
    }

    private async Task TeardownAsync()
    {
        if (_player is not null)
        {
            await _player.DisposeAsync();
            _player = null;
        }
    }
}
```

That's it. ~50 lines.

**Things this example demonstrates the ideal API should make easy:**

1. **One line per concern.** Video pipeline, audio pipeline, state
   observable, position observable, diagnostics observable — each is
   one line.
2. **No DI ceremony for a simple app.** `FrameFlowPlayer.Open(path)` is
   the entry point; the builder pulls FFmpeg bootstrap, decoders,
   sinks lazily.
3. **Pipelines are composable in place.** The video pipeline is
   "convert pixel format then sink." Adding a resize or an overlay
   is `video.ConvertPixelFormat(...).Resize(...).ToSink(...)`.
4. **State is observable, not polled.** No `DispatcherTimer(500ms)
   { ... GetDiagnostics() ... }`. Subscribe to the stream; the
   library handles the cadence.
5. **Dispose works.** Cancels in-flight pipelines, releases native
   resources, returns the controls to a quiescent state. No bespoke
   `TeardownControllerAsync` in the example.

### What about advanced needs?

The simple example covers 80% of consumers. The remaining 20% want:

- **Multiple sinks** (broadcast / multicast — the AvaloniaMulticast
  example):
  ```csharp
  .WithVideo(video => video
      .ConvertPixelFormat(PixelFormat.Bgra32)
      .Broadcast(b => b
          .ToSink(MainCanvas)
          .ToSink(PreviewCanvas)
          .ToSink(RecordingFile)))      // [gap] Recording sink
  ```

- **Inference branching** (the OnnxInference example):
  ```csharp
  .WithVideo(video => video
      .Broadcast(b => b
          .Branch(p => p.ToSink(MainCanvas))
          .Branch(p => p
              .ResizeAndConvert(640, 640, PixelFormat.Rgba32)
              .DetectWith(yoloModel)    // [gap] Tier 3 operator
              .Observe(detections => UpdateOverlay(detections)))))
  ```

- **Captioning** (the LiveCaptioning example):
  ```csharp
  .WithAudio(audio => audio
      .Broadcast(b => b
          .ToSink(OpenAlSink.Default)
          .Branch(p => p
              .Resample(16_000, 1)
              .TranscribeWithWhisper(modelPath)   // [gap] Tier 3 operator
              .Observe(caption => DisplayCaption(caption)))))
  ```

- **GPU-resident inference** (future):
  ```csharp
  .WithVideo(video => video
      .OnGpu()                          // [gap] decoder.YieldHardwareFrames flag via builder
      .DetectWith(yoloGpuModel)
      .ToCpu()                          // ADR-0038 — exists
      .ToSink(MainCanvas))
  ```

- **Custom rendering** without the playback state machine (the
  captioning demo today, the inference demo arguably):
  ```csharp
  await using var media = await MediaStream.OpenAsync(path);  // [gap] convenience over IDecodedMediaStreamFactory
  await media.Audio.Resample(16_000, 1).TranscribeWithWhisper(...).RunAsync(ct);
  ```

In every case, the call site is declarative. The library makes the
state machinery, dispatch, and lifecycle disappear.

## What's already built

The Layer 1 substrate is in place after ADR-0036 / ADR-0037 /
ADR-0038:

| Component | Status | Where |
|---|---|---|
| `IDecodedMediaStream` — decode pipeline | ✓ | `FrameFlow.Decoding` |
| `FramePipeline<IVideoFrame>` / `FramePipeline<PcmAudioBuffer>` | ✓ | from Crossbar via `stream.Video` / `stream.Audio` |
| `ConvertPixelFormat`, `Resize`, `ResizeAndConvert` | ✓ | `FrameFlow.Video` (ADR-0037) |
| `PacedAgainst(clock, strategy)` | ✓ | `FrameFlow.Playback` (ADR-0036 Phase 2) |
| `ToCpu()`, `AsDomain(target)` | ✓ | `FrameFlow.Video` (ADR-0038 Phase A) |
| `GpuVideoFrame` + `YieldHardwareFrames` | ✓ | `FrameFlow.Decoding` (ADR-0038 Phase A) |
| `IVideoSink` implements `IFrameSink<IVideoFrame>` | ✓ | from ADR-0030 |
| `ToSinkAsync(IVideoSink)` | ✓ | Crossbar |
| `Broadcast` | ✓ | Crossbar |
| `Observe`, `Transform`, `Enrich` | ✓ | Crossbar |
| `Resample(rate, channels)` | ✓ | `FrameFlow.Audio` |

The decode → operator → sink call sites work today — what's missing
is the *fluent player layer* (Layer 2) that hides the
`IPlaybackController` / DI ceremony.

## Gap inventory

The idealized example calls these things that don't exist yet. Each
is sized and mapped to the roadmap.

### Layer 2 — the player builder

The biggest gap. Needs:

- `FrameFlowPlayer.Open(path)` static entry point.
- `IPlayerBuilder` with `.WithVideo(Func<...>)`, `.WithAudio(Func<...>)`,
  `.WithVideoSink(IVideoSink)`, `.WithAudioSink(IAudioSink)`,
  `.WithOptions(FrameFlowOptions)`.
- `IPlayerBuilder.BuildAsync()` → `IMediaPlayer`.
- `IMediaPlayer`: `PlayAsync`, `PauseAsync`, `SeekAsync`,
  `SetRepeatMode`, `State`, `Position`, `Duration`, `Diagnostics`,
  `LoopRestarted`, `ErrorOccurred`, `DisposeAsync`.

Internally `IMediaPlayer` composes the existing
`IPlaybackControllerFactory` / `IDecodedMediaStreamFactory`. The
*shape* is new, but the implementation reuses the substrate.

**Sizing.** ~400 lines for the builder + player; not on a tier
explicitly — it's the surface-layer cleanup the tiers feed into. Call
it ADR-0041 (future).

### Crossbar surface gaps

- **`IObservable<T>` integration with `FramePipeline<T>`.** The
  pipelines should expose state-event-shaped concerns
  (`player.State`, `player.Position`, `player.Diagnostics`) as
  `IObservable<T>`. Currently `IPlaybackController` exposes
  `IObservable<StateTransition<PlaybackState>>` etc. directly — the
  shape is fine; just needs to surface on `IMediaPlayer`.
- **`Sample(TimeSpan)` operator on `IObservable<T>`.** Either pull
  in System.Reactive or write a small implementation. Or expose
  diagnostics with a built-in cadence option.

### Tier 3 operators

The advanced examples want operators that consolidate today's
manual wiring:

- **`audio.TranscribeWithWhisper(modelPath, options)`** —
  `FramePipeline<PcmAudioBuffer>` → `FramePipeline<Caption>`. Folds
  the `WhisperAsrWorker` + bridge channel out of the captioning
  example. **Tier 3, ADR-0039 candidate.**
- **`frames.DetectWith(yoloModel)`** — `FramePipeline<IVideoFrame>`
  → `FramePipeline<DetectedFrame>` (packets enriched with detection
  metadata via Crossbar's `Enrich`). Folds the inference example's
  `Yolov8InferencePreview` rent-infer-draw class. **Tier 3.**
- **`audio.MeasureLoudness()`** — for VU meters and gain control.
  Lower priority.

### Tier 4

- **`IAudioSink` implements `IFrameSink<PcmAudioBuffer>`.** Lets
  `audio.ToSinkAsync(audioSink, ct)` work directly. Today the audio
  pump in `PipelineController` does `Observe(p =>
  audioSink.WriteAsync(p.Frame, ct))` as an adapter. The 50-line
  symmetry win the roadmap describes.

### Tier 5

- **Capture sources.** `Camera.OpenStream(deviceId)` →
  `FramePipeline<IVideoFrame>`. `Microphone.OpenStream(...)` →
  `FramePipeline<PcmAudioBuffer>`. Periphery already enumerates
  devices; FrameFlow consumes them.
- **Encoder + muxer terminals.**
  `pipeline.EncodeTo(H264).MuxInto(Mp4, path)`. Lets the example
  add a "record" sink alongside the renderer.
- **`Broadcast` with recording sink.** Folding the multicast +
  recording use case once encode terminals exist.

### Sink-side conveniences

- **`OpenAlSink.Default`** static, **`AvaloniaVideoSink` as
  `IFrameSink<IVideoFrame>` accepting an Avalonia control directly.**
  Today the OpenAL DI helper hides this; we need it to surface as a
  simple `.ToSink(view)` shape.
- **Sink lifecycle subsumed by the pipeline.** Today
  `IAudioSink.ActivateAsync` / `DeactivateAsync` is orchestrated by
  the playback session. The fluent player should activate / pause /
  deactivate the sinks at the right state transitions transparently.

## Comparison

For reference, the **current AvaloniaPlayer's `MainWindow.axaml.cs`
is 800+ lines** with significant ceremony around:

- DI registration with `services.AddFrameFlow().AddFrameFlowDecoding()
  .AddFrameFlowPlayback().AddFrameFlowAvalonia().AddFrameFlowOpenAlAudio()`.
- Factory resolution: `IPlaybackControllerFactory.CreateController()`.
- Explicit load + play sequence: `await controller.LoadAsync(MediaSource.FromFile(path))`,
  `await controller.PlayAsync()`.
- Polling diagnostics via `DispatcherTimer` at 2 Hz.
- Manual subscription to multiple observables with translation to
  UI thread.
- Bespoke teardown handling.

The ideal example shrinks each of those to a single line or hides it
entirely.

The Layer 2 builder doesn't replace `IPlaybackController` — it sits
*on top of* it, the same way ASP.NET Core's `WebApplication.Create`
sits on top of `IHostBuilder`. Power users keep direct access to the
controller; the common case gets a nicer call site.

## Where this fits in the roadmap

The roadmap (Tiers 1–5) covers the *operator vocabulary*. This
document covers the *consumer surface* that sits on top. They're
complementary:

| Tier | Focus | This doc's term |
|---|---|---|
| 1 | Pixel operators (✓ landed) | Layer 1 substrate |
| 2 | Memory-domain operators (Phase A ✓ landed) | Layer 1 substrate |
| 3 | User-domain operators (Whisper, YOLO) | Layer 1 substrate |
| 4 | Audio sink symmetry | Layer 1 substrate |
| 5 | Capture / encode | Layer 1 substrate |
| (new) | Fluent player builder | Layer 2 builder — ADR-0041 candidate |

Tiers 3, 4, 5 land next. The Layer 2 builder ADR is the natural
follow-up once the operator vocabulary is complete enough to be
worth wrapping.

---

*This document is the target. The tiers are the route. Treat the
gaps as a backlog, not as criticism — Layer 1 is most of the way
there, and Layer 2 becomes a clean weekend project once Tiers 3 and
4 land.*
