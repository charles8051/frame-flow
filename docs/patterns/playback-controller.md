# PlaybackController Architecture

Design reference for FrameFlow's async-friendly, Stateless-based playback
controller with channel-serialized command dispatch and a multi-worker pipeline.

**Governing ADRs:**
- [ADR-0023](../adr/ADR-0023-hierarchical-state-machine-with-channel-dispatch.md) — HSM + channel dispatch
- [ADR-0024](../adr/ADR-0024-playback-controller-as-public-api-surface.md) — controller as public API
- [ADR-0022](../adr/ADR-0022-long-lived-workers-with-pause-gate.md) — long-lived workers + pause gate
- [ADR-0020](../adr/ADR-0020-lifecycle-decoupled-from-processing-logic.md) — lifecycle/processing separation

**Companion documents:**
- [playback-states](playback-states.md) — full state & transition catalogue
- [playback-statechart](playback-statechart.md) — Mermaid diagrams
- [video-sink-and-frame-pool](video-sink-and-frame-pool.md) — sink & frame pool design
- [implementation-plan](implementation-plan.md) — phased refactor plan

---

## 1. Public API Surface

### 1.1 IPlaybackController

```csharp
public interface IPlaybackController : IAsyncDisposable
{
    // ── Lifecycle ──────────────────────────────────────────
    Task<Result> LoadAsync(IMediaSource source, CancellationToken ct = default);
    Task<Result> StopAsync(CancellationToken ct = default);

    // ── Transport controls ─────────────────────────────────
    Task<Result> PlayAsync(CancellationToken ct = default);
    Task<Result> PauseAsync(CancellationToken ct = default);
    Task<Result<TimeSpan>> SeekAsync(TimeSpan position, CancellationToken ct = default);

    // ── Repeat ─────────────────────────────────────────────
    Task<Result> SetRepeatModeAsync(RepeatMode mode);

    // ── Observable state (read-only) ───────────────────────
    PlaybackState         State         { get; }
    SeekState             SeekingState  { get; }
    RepeatMode            RepeatMode    { get; }
    bool                  IsPlaying     { get; }   // composite condition
    TimeSpan              Position      { get; }
    TimeSpan              Duration      { get; }
    MediaInfo?            MediaInfo     { get; }

    // ── Events ─────────────────────────────────────────────
    IObservable<StateTransition<PlaybackState>> PlaybackStateChanged  { get; }
    IObservable<StateTransition<SeekState>>     SeekStateChanged      { get; }
    IObservable<StateTransition<RepeatMode>>    RepeatModeChanged     { get; }
    IObservable<LoopRestarted>                  LoopRestarted         { get; }
    IObservable<PlaybackError>                  ErrorOccurred         { get; }
    IObservable<TimeSpan>                       PositionTick          { get; }  // ~250ms
}
```

### 1.2 Supporting Types

```csharp
// ── State enums ─────────────────────────────────────────────

public enum PlaybackState
{
    Idle, Initializing, Preparing, InitialBuffering,
    Paused, Playing, Rebuffering,
    Ended, Stopped, Error, Destroyed
}

public enum SeekState    { NotSeeking, SeekPending, SeekInProgress }
public enum RepeatMode   { Off, One, All }

// ── Transition event ────────────────────────────────────────

public readonly record struct StateTransition<T>(
    T Previous,
    T Current,
    string? TriggerName = null
) where T : struct, Enum;

// ── Domain events ───────────────────────────────────────────

public sealed record LoopRestarted(int LoopCount, TimeSpan ItemDuration);
public sealed record PlaybackError(ErrorCategory Category, string Message, Exception? Inner = null);

public enum ErrorCategory { InvalidOperation, Source, Network, Decode, Io, System }
```

These types replace the current `PlaybackState` enum in `FrameFlow.Media` (which
has 9 values: Idle, Opening, Ready, Playing, Paused, Seeking, Stopped, Ended,
Faulted). The new enum has 11 values that model the hierarchical structure as a
flat enum — Stateless handles the hierarchy via `SubstateOf()` configuration.

---

## 2. Channel-Serialized Command Dispatch

### 2.1 Why

The current `PlaybackStateMachine` uses `Interlocked.CompareExchange` for atomic
transitions, but compound operations (check state + perform action + transition)
are not atomic. The Stateless library's `FireAsync` is also not thread-safe.

Rather than wrapping every call in `SemaphoreSlim`, all public API methods post
command objects to a bounded `Channel<IPlayerCommand>`. A single background loop
processes them sequentially.

This gives:
- **Serialized state access** — the dispatch loop is the only code calling `FireAsync`
- **Non-blocking callers** — `Task` completes when the command is processed
- **Backpressure** — bounded channel prevents runaway event floods
- **Clean shutdown** — complete the writer, await the loop

### 2.2 Internal Structure

```csharp
public sealed class PlaybackController : IPlaybackController
{
    // ── State machines (one per orthogonal region) ──────────
    private readonly StateMachine<PlaybackState, PlaybackTrigger> _playback;
    private readonly StateMachine<SeekState, SeekTrigger>         _seeking;
    private readonly StateMachine<RepeatMode, RepeatTrigger>      _repeat;

    // ── Parameterized triggers ──────────────────────────────
    private readonly StateMachine<PlaybackState, PlaybackTrigger>
        .TriggerWithParameters<IMediaSource> _loadTrigger;
    private readonly StateMachine<PlaybackState, PlaybackTrigger>
        .TriggerWithParameters<TimeSpan>     _seekTrigger;
    private readonly StateMachine<PlaybackState, PlaybackTrigger>
        .TriggerWithParameters<PlaybackError> _errorTrigger;

    // ── Command channel ─────────────────────────────────────
    private readonly Channel<IPlayerCommand> _commandChannel =
        Channel.CreateBounded<IPlayerCommand>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly Task _dispatchLoop;

    // ── Per-media-item session (nullable, created on Load) ──
    private PlaybackSession? _session;
```

### 2.3 Command Types

```csharp
private interface IPlayerCommand
{
    TaskCompletionSource<Result> Completion { get; }
    CancellationToken CancellationToken { get; }
}

private sealed record FireTriggerCommand(
    PlaybackTrigger Trigger,
    CancellationToken CancellationToken = default
) : IPlayerCommand
{
    public TaskCompletionSource<Result> Completion { get; } = new();
}

private sealed record SeekCommand(
    TimeSpan Position,
    CancellationToken CancellationToken = default
) : IPlayerCommand
{
    public TaskCompletionSource<Result> Completion { get; } = new();
}

private sealed record LoadCommand(
    IMediaSource Source,
    CancellationToken CancellationToken = default
) : IPlayerCommand
{
    public TaskCompletionSource<Result> Completion { get; } = new();
}

private sealed record SetRepeatCommand(
    RepeatMode Mode,
    CancellationToken CancellationToken = default
) : IPlayerCommand
{
    public TaskCompletionSource<Result> Completion { get; } = new();
}
```

### 2.4 Public Methods — Thin Wrappers

```csharp
public Task<Result> PlayAsync(CancellationToken ct = default)
    => PostAndWaitAsync(new FireTriggerCommand(PlaybackTrigger.Play), ct);

public Task<Result> PauseAsync(CancellationToken ct = default)
    => PostAndWaitAsync(new FireTriggerCommand(PlaybackTrigger.Pause), ct);

public Task<Result<TimeSpan>> SeekAsync(TimeSpan position, CancellationToken ct = default)
    => PostAndWaitAsync(new SeekCommand(position), ct);

public Task<Result> LoadAsync(IMediaSource source, CancellationToken ct = default)
    => PostAndWaitAsync(new LoadCommand(source), ct);

public Task<Result> SetRepeatModeAsync(RepeatMode mode)
    => PostAndWaitAsync(new SetRepeatCommand(mode));

private async Task<Result> PostAndWaitAsync(
    IPlayerCommand command,
    CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();

    await using var reg = ct.Register(
        () => command.Completion.TrySetCanceled(ct));

    await _commandChannel.Writer.WriteAsync(command, ct);
    return await command.Completion.Task;
}
```

### 2.5 Dispatch Loop

```csharp
private async Task DispatchLoopAsync()
{
    await foreach (var cmd in _commandChannel.Reader.ReadAllAsync())
    {
        try
        {
            cmd.CancellationToken.ThrowIfCancellationRequested();

            switch (cmd)
            {
                case FireTriggerCommand ftc:
                    if (!_playback.CanFire(ftc.Trigger))
                    {
                        cmd.Completion.TrySetResult(
                            Result.Fail(ErrorCategory.InvalidOperation,
                                $"Cannot {ftc.Trigger} from {_playback.State}"));
                        continue;
                    }
                    await _playback.FireAsync(ftc.Trigger);
                    break;

                case SeekCommand sc:
                    await _playback.FireAsync(_seekTrigger, sc.Position);
                    await _seeking.FireAsync(SeekTrigger.SeekRequested);
                    break;

                case LoadCommand lc:
                    await _playback.FireAsync(_loadTrigger, lc.Source);
                    break;

                case SetRepeatCommand src:
                    var trigger = src.Mode switch
                    {
                        RepeatMode.Off => RepeatTrigger.SelectOff,
                        RepeatMode.One => RepeatTrigger.SelectOne,
                        RepeatMode.All => RepeatTrigger.SelectAll,
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    await _repeat.FireAsync(trigger);
                    break;
            }

            cmd.Completion.TrySetResult(Result.Ok());
        }
        catch (OperationCanceledException)
        {
            cmd.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            cmd.Completion.TrySetException(ex);
        }
    }
}
```

### 2.6 Primary State Machine Configuration

```csharp
private void ConfigurePlaybackMachine()
{
    _playback = new StateMachine<PlaybackState, PlaybackTrigger>(PlaybackState.Idle);

    _loadTrigger  = _playback.SetTriggerParameters<IMediaSource>(PlaybackTrigger.Load);
    _seekTrigger  = _playback.SetTriggerParameters<TimeSpan>(PlaybackTrigger.Seek);
    _errorTrigger = _playback.SetTriggerParameters<PlaybackError>(PlaybackTrigger.FatalError);

    _playback.OnTransitioned(t =>
        _playbackStateSubject.OnNext(
            new StateTransition<PlaybackState>(t.Source, t.Destination, t.Trigger.ToString())));

    // ── Idle ──
    _playback.Configure(PlaybackState.Idle)
        .Permit(PlaybackTrigger.Load, PlaybackState.Initializing)
        .Permit(PlaybackTrigger.Release, PlaybackState.Destroyed);

    // ── Loading composite ──
    _playback.Configure(PlaybackState.Initializing)
        .SubstateOf(PlaybackState.Initializing)  // placeholder for composite
        .OnEntryFromAsync(_loadTrigger, async source =>
        {
            if (_session is not null)
                await _session.DisposeAsync();

            _session = CreateSession(source);
            await _session.InitializeAsync(_sessionCts.Token);
            await _playback.FireAsync(PlaybackTrigger.HeadersReceived);
        })
        .Permit(PlaybackTrigger.HeadersReceived, PlaybackState.Preparing)
        .Permit(PlaybackTrigger.Stop, PlaybackState.Stopped)
        .Permit(PlaybackTrigger.FatalError, PlaybackState.Error);

    _playback.Configure(PlaybackState.Preparing)
        .OnEntryAsync(async () =>
        {
            _mediaInfo = await _session!.ParseMetadataAsync();
            await _playback.FireAsync(PlaybackTrigger.MetadataParsed);
        })
        .Permit(PlaybackTrigger.MetadataParsed, PlaybackState.InitialBuffering)
        .Permit(PlaybackTrigger.Stop, PlaybackState.Stopped)
        .Permit(PlaybackTrigger.FatalError, PlaybackState.Error);

    _playback.Configure(PlaybackState.InitialBuffering)
        .OnEntryAsync(async () =>
        {
            await _session!.BufferToThresholdAsync(_sessionCts.Token);
            var next = _playWhenReady
                ? PlaybackTrigger.Play
                : PlaybackTrigger.BufferReady;
            await _playback.FireAsync(next);
        })
        .Permit(PlaybackTrigger.BufferReady, PlaybackState.Paused)
        .Permit(PlaybackTrigger.Play, PlaybackState.Playing)
        .Permit(PlaybackTrigger.Stop, PlaybackState.Stopped)
        .Permit(PlaybackTrigger.FatalError, PlaybackState.Error);

    // ── Ready.Paused ──
    _playback.Configure(PlaybackState.Paused)
        .OnEntry(() => _session!.Pause())
        .Permit(PlaybackTrigger.Play, PlaybackState.Playing)
        .Permit(PlaybackTrigger.Stop, PlaybackState.Stopped)
        .Permit(PlaybackTrigger.FatalError, PlaybackState.Error);

    // ── Ready.Playing ──
    _playback.Configure(PlaybackState.Playing)
        .OnEntry(() =>
        {
            if (!_session!.RenderersStarted)
                _session.StartRenderers();
            else
                _session.Resume();
        })

        // End — only when repeat is off
        .PermitIf(PlaybackTrigger.LastFrameRendered, PlaybackState.Ended,
            () => _repeat.State == RepeatMode.Off,
            "End only when repeat is off")

        // Repeat One — internal transition, never leaves Playing
        .InternalTransitionIf(PlaybackTrigger.LastFrameRendered,
            () => _repeat.State == RepeatMode.One,
            _ =>
            {
                _session!.SeekInternal(TimeSpan.Zero);
                _loopCount++;
                _loopRestartedSubject.OnNext(new LoopRestarted(_loopCount, Duration));
            })

        // Repeat All — internal transition, advance + wrap
        .InternalTransitionIf(PlaybackTrigger.LastFrameRendered,
            () => _repeat.State == RepeatMode.All,
            _ =>
            {
                _session!.SeekInternal(TimeSpan.Zero);
                _loopCount++;
                _loopRestartedSubject.OnNext(new LoopRestarted(_loopCount, Duration));
            })

        .Permit(PlaybackTrigger.Pause, PlaybackState.Paused)
        .Permit(PlaybackTrigger.BufferUnderrun, PlaybackState.Rebuffering)
        .Permit(PlaybackTrigger.Stop, PlaybackState.Stopped)
        .Permit(PlaybackTrigger.FatalError, PlaybackState.Error);

    // ── Ready.Rebuffering ──
    _playback.Configure(PlaybackState.Rebuffering)
        .PermitIf(PlaybackTrigger.BufferReady, PlaybackState.Playing,
            () => _playWhenReady)
        .PermitIf(PlaybackTrigger.BufferReady, PlaybackState.Paused,
            () => !_playWhenReady)
        .Permit(PlaybackTrigger.Pause, PlaybackState.Paused)
        .Permit(PlaybackTrigger.Stop, PlaybackState.Stopped)
        .Permit(PlaybackTrigger.FatalError, PlaybackState.Error);

    // ── Ended ──
    _playback.Configure(PlaybackState.Ended)
        .Permit(PlaybackTrigger.Seek, PlaybackState.InitialBuffering)
        .Permit(PlaybackTrigger.Stop, PlaybackState.Stopped)
        .Permit(PlaybackTrigger.Release, PlaybackState.Destroyed);

    // ── Stopped ──
    _playback.Configure(PlaybackState.Stopped)
        .OnEntryAsync(async () =>
        {
            if (_session is not null)
                await _session.DisposeAsync();
            _session = null;
        })
        .Permit(PlaybackTrigger.Load, PlaybackState.Initializing)
        .Permit(PlaybackTrigger.Reset, PlaybackState.Idle)
        .Permit(PlaybackTrigger.Release, PlaybackState.Destroyed);

    // ── Error ──
    _playback.Configure(PlaybackState.Error)
        .OnEntryFrom(_errorTrigger, err => _errorSubject.OnNext(err))
        .Permit(PlaybackTrigger.Reset, PlaybackState.Idle)
        .Permit(PlaybackTrigger.Release, PlaybackState.Destroyed);
}
```

### 2.7 Composite IsPlaying

```csharp
public bool IsPlaying =>
    _playback.State == PlaybackState.Playing
    && _playWhenReady
    && _seeking.State == SeekState.NotSeeking;
```

### 2.8 Shutdown

```csharp
public async ValueTask DisposeAsync()
{
    _commandChannel.Writer.TryComplete();
    await _dispatchLoop;

    _playbackStateSubject.Dispose();
    _loopRestartedSubject.Dispose();
    _errorSubject.Dispose();

    if (_session is not null)
        await _session.DisposeAsync();
}
```

---

## 3. Clock Synchronization

### 3.1 The Problem: Clocks Disagree

When the player is running, at least two clocks tick simultaneously:

| Clock | Source | Drift |
|-------|--------|-------|
| **Wall clock** | `Stopwatch` / system high-perf counter | Reference (0) |
| **Audio hardware clock** | Crystal oscillator on the DAC | +/-30-200 ppm |

A drift of 50 ppm means **180 ms of desync per hour**. The human ear detects
audio/video desync at ~45 ms. After 15 minutes of wall-clock-driven playback,
users notice lip sync drift.

### 3.2 Audio Clock as Master

Per ADR-0003, FrameFlow uses the audio device's sample consumption as the master
clock when an audio track exists. The existing `PlaybackClock` and
`AudioMasterSyncStrategy` already implement this pattern.

In the refactored architecture, the clock lives in `PlaybackSession` (per-item
lifetime). The controller does not own the clock — it reads
`_session.Clock.Position` to service the public `Position` property.

```
┌──────────────────────────────────────────────┐
│               Audio Subsystem                 │
│                                               │
│  Audio decoder → channel → audio sink (DAC)   │
│                                    │          │
│                    IAudioSink.GetPlaybackTime()│
│                                    │          │
│                                    ▼          │
│                         PlaybackClock.Position │
└──────────────────────────────────────────────┘
                          │
                 audio clock (master)
                          │
             ┌────────────┼────────────┐
             ▼            ▼            ▼
       Video renderer  ISyncStrategy  Controller.Position
       "show frame     "compute       "report to
        whose PTS <=    delay"         consumers"
        audio_pos"
```

### 3.3 Video Renderer Sync Logic

The existing `AudioMasterSyncStrategy.GetVideoDelay()` computes the delay. The
video present worker uses this to decide whether to present, wait, or drop:

```csharp
// Inside the video present worker loop
var referenceTime = _clock.Position;
var delay = _syncStrategy.GetVideoDelay(frame.PresentationTime, referenceTime);

if (delay < -framePeriod)
{
    // Too late — drop
    _stats.DroppedFrames++;
    frame.Dispose();
    continue;
}

if (delay > halfFramePeriod)
{
    // Too early — wait
    await Task.Delay(delay - renderMargin, ct);
}

// Within tolerance — present
await _sink.PresentAsync(frame, ct);
frame.Dispose();
```

---

## 4. Three-Layer Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  PlaybackController                                          │
│  (state machines + command channel + public API)             │
│  Owns the "what" — decides which state we're in              │
│  Lifetime: application / DI scope                            │
└──────────────────────┬───────────────────────────────────────┘
                       │ creates / disposes per Load
┌──────────────────────▼───────────────────────────────────────┐
│  PlaybackSession                                             │
│  (per-media-item: demuxer, decoders, clock, queues)          │
│  Owns the "how" — runs the pipeline for one media item       │
│  Lifetime: Load → Stop / Load-new / Dispose                  │
└──────────────────────┬───────────────────────────────────────┘
                       │ owns
┌──────────────────────▼───────────────────────────────────────┐
│  PipelineController (pause gate, worker barrier, flush)      │
│  ├─ DemuxPumpWorker                                          │
│  ├─ VideoDecodeWorker                                        │
│  ├─ VideoPresentWorker                                       │
│  └─ AudioDecodeWriteWorker                                   │
│  Lifetime: first Play → Stop / Dispose                       │
└──────────────────────────────────────────────────────────────┘
```

---

## 5. PlaybackSession — Per-Item Lifecycle

Sessions are created when the controller enters `Initializing` and disposed
when it enters `Stopped` or on a new `Load`. Sessions never overlap.

```csharp
internal sealed class PlaybackSession : IAsyncDisposable
{
    // ── Identity ──────────────────────────────────────────
    public IMediaSource       Source   { get; }
    public MediaInfo?         Info     { get; private set; }

    // ── Clock ─────────────────────────────────────────────
    public IPlaybackClock     Clock    { get; }

    // ── Pipeline ──────────────────────────────────────────
    private PipelineController? _pipeline;

    // ── Lifecycle methods called by controller OnEntry ─────
    public Task InitializeAsync(CancellationToken ct);
    public Task<MediaInfo> ParseMetadataAsync();
    public Task BufferToThresholdAsync(CancellationToken ct);
    public void StartRenderers();
    public void Pause();
    public void Resume();
    public Task SeekAsync(TimeSpan position, CancellationToken ct);
    public void SeekInternal(TimeSpan position);  // for loop restart
    public ValueTask DisposeAsync();

    // ── Callbacks to controller (wired at creation) ───────
    public Func<Task>?              OnBufferThresholdMet  { get; init; }
    public Func<Task>?              OnBufferUnderrun      { get; init; }
    public Func<Task>?              OnLastFrameRendered   { get; init; }
    public Func<PlaybackError,Task>? OnFatalError         { get; init; }
    public Func<Task>?              OnSeekComplete        { get; init; }
}
```

### 5.1 Controller → Session Wiring

State machine `OnEntry` / `OnExit` actions call session methods:

```csharp
// In ConfigurePlaybackMachine():

_playback.Configure(PlaybackState.Initializing)
    .OnEntryFromAsync(_loadTrigger, async source =>
    {
        if (_session is not null)
            await _session.DisposeAsync();

        _session = new PlaybackSession(source, _dependencies)
        {
            OnBufferThresholdMet = () => PostInternalAsync(PlaybackTrigger.BufferReady),
            OnBufferUnderrun     = () => PostInternalAsync(PlaybackTrigger.BufferUnderrun),
            OnLastFrameRendered  = () => PostInternalAsync(PlaybackTrigger.LastFrameRendered),
            OnFatalError         = err => PostInternalAsync(_errorTrigger, err),
            OnSeekComplete       = () => PostInternalAsync(SeekTrigger.SeekCompleted),
        };

        await _session.InitializeAsync(_sessionCts.Token);
        await _playback.FireAsync(PlaybackTrigger.HeadersReceived);
    });
```

### 5.2 Worker → State Machine Bridge

Workers never touch state machines. They fire callback delegates that post
commands back through the command channel:

```csharp
private Task PostInternalAsync(PlaybackTrigger trigger)
{
    var cmd = new FireTriggerCommand(trigger);
    _commandChannel.Writer.TryWrite(cmd);   // non-blocking
    return cmd.Completion.Task;
}
```

---

## 6. Worker Topology

```
                         ┌──────────────┐
                         │  IMediaSource │
                         └──────┬───────┘
                                │
                         ┌──────▼───────┐
                         │ DemuxPump    │  reads packets
                         │ Worker       │  routes by stream
                         └──┬───────┬───┘
                            │       │
               ┌────────────▼──┐ ┌──▼────────────┐
               │ AudioPackets  │ │ VideoPackets   │  Channel<DemuxPacket>
               │ (bounded)     │ │ (bounded)      │
               └────────┬──────┘ └──────┬─────────┘
                        │               │
               ┌────────▼──────┐ ┌──────▼─────────┐
               │ Audio Decode  │ │ Video Decode    │
               │ + Write       │ │ Worker          │
               │ Worker        │ │                 │
               └────────┬──────┘ └──────┬──────────┘
                        │               │
                        │        ┌──────▼──────────┐
                        │        │ VideoFrames      │  Channel<IVideoFrame>
                        │        │ (bounded)        │
                        │        └──────┬──────────┘
                        │               │
               ┌────────▼──────┐ ┌──────▼──────────┐
               │ IAudioSink    │ │ Video Present    │
               │ (drives clock)│ │ Worker           │
               │               │ │ → IVideoSink     │
               └───────────────┘ └─────────────────┘
```

All inter-stage connections are `Channel<T>` with bounded capacity (per ADR-0009).

### 6.1 Pause Gate

Per ADR-0022, each worker checks an `AsyncManualResetEvent` once per loop
iteration. Pause closes the gate; resume opens it. Workers are long-lived —
only `StopAsync` / `DisposeAsync` cancels the shutdown CTS that kills them.

### 6.2 Seek via Gate + Flush

Per ADR-0022, seeking does not destroy workers:

```
1. Close pause gate                 ← workers block at next iteration
2. Await worker barrier             ← confirm all workers paused
3. Drain decoder input queues       ← discard stale packets
4. Drain video frame channel        ← dispose stale frames
5. Flush decoder codec buffers
6. Seek demuxer
7. Seek clock
8. Reset audio sink position
9. Open pause gate                  ← workers resume from new position
```

---

## 7. Cancellation Hierarchy

```
_controllerCts                         (controller lifetime)
   └─ _sessionCts                      (per-media-item, cancelled on Stop/Load-new)
        └─ shutdown CTS in Pipeline    (cancelled only by Stop/Dispose)
             ├─ demux pump token
             ├─ video decode token
             ├─ video present token
             └─ audio decode+write token
```

- `CancellationToken` on public API methods means "abort this specific command."
- `_sessionCts` is cancelled on Stop or new Load.
- The pipeline's shutdown CTS is the only thing that kills workers.
- Pause, resume, and seek never touch any CTS (per ADR-0022).

---

## 8. Backpressure Chain

```
Sound card consumption rate
   → Audio sink blocks on full device buffer
     → AudioSamples channel blocks on full queue
       → Audio decoder blocks on WriteAsync
         → AudioPackets channel blocks on full queue
           → Demuxer blocks on WriteAsync
             → Source read blocks (natural TCP backpressure)

Display timing / vsync rate
   → Video renderer blocks waiting for presentation time
     → VideoFrames channel blocks on full queue
       → Video decoder blocks on WriteAsync
         → FramePool.RentAsync blocks (all surfaces in use)

Both chains converge at the demuxer. If either consumer is slow,
the demuxer stalls, which stalls the other consumer's feed too.
This is correct: A/V must advance together.
```

---

## 9. How Rebuffering Works Naturally

Rebuffering requires **zero explicit orchestration**:

```
1. Network slows → demuxer's ReadPacketAsync blocks
2. Packet queues drain as decoders consume remaining packets
3. Decoder output queues drain as renderers consume remaining data
4. Audio sink runs dry → no GetPlaybackTime advancement → clock freezes
5. Video renderer reads Clock.Position → frozen time → frame is "early" → waits
6. Everything stalls naturally

Recovery:
7. Demuxer gets data → packet queues fill
8. Decoders unblock → produce frames/samples
9. Audio sink unblocks → feeds DAC → clock advances
10. Video renderer sees clock moving → presents frames
```

The state machine reflects what's already happening:

```
Playing ─(OnBufferUnderrun)─► Rebuffering ─(OnBufferThresholdMet)─► Playing
```

---

## 10. Ownership Summary

| Concern | Owner | Mechanism |
|---------|-------|-----------|
| "What state are we in?" | PlaybackController (state machines) | Stateless + command channel |
| "Start/stop the pipeline" | PlaybackSession (per-item lifecycle) | PipelineController |
| "Move bytes through the pipe" | Workers (4 async loops) | Channel backpressure |
| "What time is it?" | IPlaybackClock (audio-driven) | IAudioSink.GetPlaybackTime() |
| "Workers → state machine" | Callback delegates → command channel | PostInternalAsync |
| "State machine → workers" | OnEntry/OnExit → session methods | Pause/Resume/Seek/Dispose |
| "Pause" | AsyncManualResetEvent gate | Workers block cooperatively |
| "Seek" | Close gate → flush → reposition → open gate | Workers pause and resume |
| "Rebuffer" | Emerges from empty queues | Channel backpressure + frozen clock |
