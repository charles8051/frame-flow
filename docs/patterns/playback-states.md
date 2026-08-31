# FrameFlow — State & Transition Reference

Complete catalogue of states, transitions, guards, and events for the
refactored playback architecture. Covers the primary HSM and the two
v1 orthogonal regions (seeking, repeat).

**Governing ADR:** [ADR-0023](../adr/ADR-0023-hierarchical-state-machine-with-channel-dispatch.md)

**Companion documents:**
- [playback-statechart](playback-statechart.md) — Mermaid diagrams
- [playback-controller](playback-controller.md) — controller architecture

---

## 1. Primary Playback State Machine (Hierarchical)

States marked with `>` are composite (contain substates).

```
                    +---------------+
                    |   Destroyed   |  (terminal)
                    +---------------+
                          ^
                          | Release()
+--------+  Load()  +----+------> Loading ----------------------+
|  Idle  |--------->|  > Loading                                |
|        |<---------|    +- Initializing (open source, headers) |
|        |  Reset() |    +- Preparing   (parse metadata)        |
|        |<--Error--|    +- InitialBuffering                    |
+--------+          +------------------------------------------+
                               | buffer threshold met
                               v
                    +------------------------------------------+
                    |  > Ready                                 |
                    |    +- Paused                             |
                    |    +- Playing                            |
                    |    +- Rebuffering (buffer underrun)      |
                    +------------------------------------------+
                        |              |
             ended -----+   Stop() ----+
                        v              v
                    +--------+    +---------+
                    | Ended  |    | Stopped  |
                    +--------+    +---------+

                    +--------+
                    | Error  |  (from any non-terminal state)
                    +--------+
```

### 1.1 State Descriptions

| State | Description |
|-------|-------------|
| **Idle** | Controller exists but has no media source. Awaiting `LoadAsync()`. |
| **Initializing** | Source assigned. Opening container, reading headers. |
| **Preparing** | Container opened. Parsing metadata, negotiating codecs and pipeline. |
| **InitialBuffering** | Metadata known. Filling buffer to a playable threshold. |
| **Paused** | Sufficient buffer, playback suspended. Clock paused, workers gated. |
| **Playing** | Actively decoding and rendering. Clock advancing. |
| **Rebuffering** | Was playing, but buffer underran. Auto-resumes when refilled if `playWhenReady`. |
| **Ended** | Final frame rendered. Clock stopped at duration. Only reachable when `RepeatMode == Off`. Public replay tears down the ended runtime and reloads the last source before playback is re-primed from the beginning. |
| **Stopped** | Explicitly stopped. Session disposed. Controller reusable via new `LoadAsync()`. |
| **Error** | Unrecoverable failure. Carries a `PlaybackError` with category and message. |
| **Destroyed** | `DisposeAsync()` called. Terminal — no transitions out. |

### 1.2 Trigger Enum

```csharp
internal enum PlaybackTrigger
{
    Load,               // parameterized: IMediaSource
    HeadersReceived,    // internal: fired by session
    MetadataParsed,     // internal: fired by session
    BufferReady,        // internal: buffer threshold met or rebuffer complete
    Play,
    Pause,
    Seek,               // parameterized: TimeSpan
    Stop,
    Reset,
    Release,
    BufferUnderrun,     // internal: fired by workers
    LastFrameRendered,  // internal: fired by workers
    FatalError,         // parameterized: PlaybackError
}
```

### 1.3 Primary Transition Table

| # | From | To | Trigger | Guard / Notes |
|---|------|----|---------|---------------|
| 1 | Idle | Initializing | `Load(source)` | Parameterized trigger |
| 2 | Initializing | Preparing | `HeadersReceived` | Internal, fired by session |
| 3 | Preparing | InitialBuffering | `MetadataParsed` | Internal, fired by session |
| 4 | InitialBuffering | Paused | `BufferReady` | `playWhenReady == false` |
| 5 | InitialBuffering | Playing | `Play` | `playWhenReady == true` |
| 6 | Paused | Playing | `Play` | |
| 7 | Playing | Paused | `Pause` | |
| 8 | Playing | Rebuffering | `BufferUnderrun` | |
| 9 | Rebuffering | Playing | `BufferReady` | `playWhenReady == true` |
| 10 | Rebuffering | Paused | `BufferReady` | `playWhenReady == false` |
| 11 | Rebuffering | Paused | `Pause` | |
| 12 | Playing | Ended | `LastFrameRendered` | **Guard:** `_repeat.State == RepeatMode.Off` |
| 13 | Playing | Playing | `LastFrameRendered` | **Internal transition, guard:** `_repeat.State == RepeatMode.One`. Seeks to 0, emits `LoopRestarted`. |
| 14 | Playing | Playing | `LastFrameRendered` | **Internal transition, guard:** `_repeat.State == RepeatMode.All`. Seeks to 0 + wraps, emits `LoopRestarted`. |
| 15 | Ended | InitialBuffering | `Seek(pos)` | |
| 16 | Ended | Playing | `Play` | Manual replay |
| 17 | Ended | Stopped | `Stop` | |
| 18 | Ready.* | Stopped | `Stop` | Exit from Ready composite |
| 19 | Loading.* | Stopped | `Stop` | Exit from Loading composite |
| 20 | Stopped | Idle | `Reset` | |
| 21 | Stopped | Initializing | `Load(source)` | Load new content |
| 22 | * (non-terminal) | Error | `FatalError(err)` | Parameterized with PlaybackError |
| 23 | Error | Idle | `Reset` | |
| 24 | * | Destroyed | `Release` | Terminal, no transitions out |

### 1.4 Entry / Exit Actions

| State | OnEntry | OnExit |
|-------|---------|--------|
| Initializing | Create session, call `InitializeAsync`, fire `HeadersReceived` | |
| Preparing | Call `ParseMetadataAsync`, fire `MetadataParsed` | |
| InitialBuffering | Call `BufferToThresholdAsync`, fire `BufferReady` or `Play` | |
| Playing | Start renderers (first time) or Resume (subsequent) | |
| Paused | Call `session.Pause()` | |
| Rebuffering | (no-op — pipeline stalls naturally) | |
| Stopped | Dispose session, set to null | |
| Error | Publish error to `ErrorOccurred` subject | |

---

## 2. Seeking (Orthogonal Region)

### 2.1 States

| State | Description |
|-------|-------------|
| **NotSeeking** | No seek operation active. |
| **SeekPending** | User issued seek, queued for processing. |
| **SeekInProgress** | Pause gate closed, flush/reposition underway. |

### 2.2 Trigger Enum

```csharp
internal enum SeekTrigger
{
    SeekRequested,    // from user SeekAsync() call
    FlushStarted,     // internal: gate closed, flush begins
    SeekCompleted,    // internal: callback from session
}
```

### 2.3 Transitions

| From | To | Trigger | Notes |
|------|----|---------|-------|
| NotSeeking | SeekPending | `SeekRequested` | User calls `SeekAsync()` |
| SeekPending | SeekInProgress | `FlushStarted` | Gate closed, flush begins |
| SeekInProgress | NotSeeking | `SeekCompleted` | New position rendered |
| SeekInProgress | SeekPending | `SeekRequested` | New seek supersedes in-flight |

### 2.4 Cross-Region Impact

- `IsPlaying` is false while `SeekingState != NotSeeking`.
- Seek-during-seek cancels the first seek and starts a new one.

---

## 3. Repeat / Loop Mode (Orthogonal Region)

### 3.1 States

| State | Description |
|-------|-------------|
| **RepeatOff** | Default. `LastFrameRendered` transitions to `Ended`. |
| **RepeatOne** | Loop current item. Internal seek to 0 on last frame. `Ended` is never entered. |
| **RepeatAll** | Loop all items. Seek to 0 + wrap on last frame. `Ended` unreachable unless playlist empty. |

### 3.2 Trigger Enum

```csharp
internal enum RepeatTrigger
{
    SelectOff,
    SelectOne,
    SelectAll,
}
```

### 3.3 Transitions

Any state can transition to any other state — the user can freely switch modes.

| From | To | Trigger |
|------|----|---------|
| RepeatOff | RepeatOne | `SelectOne` |
| RepeatOff | RepeatAll | `SelectAll` |
| RepeatOne | RepeatOff | `SelectOff` |
| RepeatOne | RepeatAll | `SelectAll` |
| RepeatAll | RepeatOff | `SelectOff` |
| RepeatAll | RepeatOne | `SelectOne` |

### 3.4 Guard on Playing → Ended

The repeat region's state gates the primary machine's `LastFrameRendered` handling:

```
Playing → Ended            : [repeatMode == Off]
Playing → Playing (internal): [repeatMode == One]  → seek to 0, emit LoopRestarted
Playing → Playing (internal): [repeatMode == All]  → seek to 0, emit LoopRestarted
```

### 3.5 Why Ended is Unreachable During Looping

If looping were modeled as `Playing → Ended → Playing`, every observer of the
primary state (UI, analytics, position reporting) would see a momentary `Ended`
flash. For gapless looping this is incorrect — the player should never appear to
have ended. The internal transition keeps `state == Playing` throughout the loop.

### 3.6 Events Emitted on Loop Boundaries

| Event | When |
|-------|------|
| `LoopRestarted(loopCount, itemDuration)` | RepeatOne or RepeatAll: item restarts from position 0 |

These events exist so that UI loop indicators and analytics can observe the
boundary crossing even though the primary state never changes.

---

## 4. Composite IsPlaying

```csharp
public bool IsPlaying =>
    _playback.State == PlaybackState.Playing
    && _playWhenReady
    && _seeking.State == SeekState.NotSeeking;
```

This is a computed property, not a state. It is recalculated whenever any
constituent state changes.

---

## 5. Error Taxonomy

| Category | Examples | Recovery |
|----------|----------|----------|
| **InvalidOperation** | Play while Idle, Seek while Stopped | `Result.Fail` — no state change |
| **Source** | Invalid URL, unsupported format, 404 | → Error → Idle (Reset) |
| **Network** | Timeout, DNS, TLS error | → Rebuffering (retry) → Error |
| **Decode** | Corrupt packet, unsupported codec | Skip segment or → Error |
| **Io** | Disk read failure (local file) | → Error |
| **System** | Audio device loss, presenter failure | → Error |

---

## 6. Edge Cases

| Scenario | Behavior |
|----------|----------|
| Seek during seek | Cancel first seek, start new seek |
| Seek past duration | Clamp to duration → may trigger Ended |
| Seek before 0 | Clamp to 0 |
| `PlayAsync()` with no source | Returns `Result.Fail(InvalidOperation)` |
| `PauseAsync()` while Ended | Returns `Result.Fail(InvalidOperation)` |
| `StopAsync()` while Loading | Cancel loading → Stopped |
| Repeat One + last frame | Internal seek to 0, never leaves Playing |
| Repeat All + last frame | Internal seek to 0, wraps item index, never leaves Playing |

---

## 7. State Machine Interaction Rules

1. Each orthogonal region is a **separate** `StateMachine<TState, TTrigger>` instance.
2. All machines fire on the **same dispatch loop thread** — `.State` reads are race-free.
3. Machines **read** each other's `.State` via guards (safe: same thread).
4. Machines **never** call `FireAsync` on each other — cross-region triggers go through the command channel.
5. `OnTransitioned` callbacks publish to `Subject<StateTransition<T>>` — observers are notified on the dispatch loop thread and must marshal to UI if needed.
6. Worker tasks **never** touch any state machine — they post via `PostInternalAsync`.
ine — they post via `PostInternalAsync`.
