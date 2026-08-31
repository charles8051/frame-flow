# FrameFlow — Statechart Diagrams

Mermaid statechart diagrams for the refactored playback architecture.
Each section is a separate diagram covering one region.

**Governing ADR:** [ADR-0023](../adr/ADR-0023-hierarchical-state-machine-with-channel-dispatch.md)

**Companion documents:**
- [playback-states](playback-states.md) — full state & transition catalogue
- [playback-controller](playback-controller.md) — controller architecture

---

## 1. Primary Playback State Machine

```mermaid
stateDiagram-v2
    direction TB

    [*] --> Idle

    state Loading {
        [*] --> Initializing
        Initializing --> Preparing : headers received,\ncontainer open
        Preparing --> InitialBuffering : metadata parsed
    }

    state Ready {
        [*] --> Paused
        Paused --> Playing : Play()
        Playing --> Paused : Pause()
        Playing --> Rebuffering : buffer underrun
        Rebuffering --> Playing : buffer refilled\n[playWhenReady]
        Rebuffering --> Paused : Pause()

        Playing --> Playing : repeat one\n(internal seek to 0)\nLoopRestarted event
        Playing --> Playing : repeat all\n(seek to 0 + wrap)\nLoopRestarted event
    }

    Idle --> Loading : Load(source)
    Loading --> Ready : buffer threshold met

    Playing --> Ended : last frame\n[repeatMode == Off]

    Ended --> Stopped : Play()\n(replay teardown)
    Stopped --> Loading : Load(last source)\n(replay recovery)
    Ended --> InitialBuffering : Seek(pos)
    Ended --> Idle : Stop() + Reset()

    Ready --> Stopped : Stop()
    Loading --> Stopped : Stop()
    Stopped --> Idle : Reset()

    Playing --> Error : fatal error
    Loading --> Error : fatal error
    Paused --> Error : fatal error
    Rebuffering --> Error : fatal error

    Error --> Idle : Reset()

    Ready --> Destroyed : Release()
    Loading --> Destroyed : Release()
    Idle --> Destroyed : Release()
    Stopped --> Destroyed : Release()
    Ended --> Destroyed : Release()
    Error --> Destroyed : Release()

    note right of Destroyed : Terminal. No transitions out.\nDisposeAsync() triggers this.
    note right of Ended : Only reachable when\nrepeatMode == Off.
    note left of Ready : Playing self-transitions\nfor repeat/loop never\nleave this composite state.
```

---

## 2. Seeking (Orthogonal Region)

```mermaid
stateDiagram-v2
    direction LR

    [*] --> NotSeeking

    NotSeeking --> SeekPending : Seek(pos)
    SeekPending --> SeekInProgress : gate closed,\nflush started
    SeekInProgress --> NotSeeking : seek complete,\ngate reopened
    SeekInProgress --> SeekPending : new Seek()\nsupersedes current
```

---

## 3. Repeat / Loop Mode (Orthogonal Region)

```mermaid
stateDiagram-v2
    direction LR

    [*] --> RepeatOff

    RepeatOff --> RepeatOne : SetRepeatMode(One)
    RepeatOff --> RepeatAll : SetRepeatMode(All)
    RepeatOne --> RepeatOff : SetRepeatMode(Off)
    RepeatOne --> RepeatAll : SetRepeatMode(All)
    RepeatAll --> RepeatOff : SetRepeatMode(Off)
    RepeatAll --> RepeatOne : SetRepeatMode(One)

    note right of RepeatOff : Ended state is reachable.
    note right of RepeatOne : Playing self-transition.\nInternal seek to 0.\nEmits LoopRestarted.
    note right of RepeatAll : Playing self-transition.\nSeek to 0 + wrap.\nEmits LoopRestarted.
```

---

## 4. Three-Layer Architecture Overview

```mermaid
stateDiagram-v2
    direction TB

    state "PlaybackController" as ctrl {
        state "State Machines" as sm
        state "Command Channel" as ch
        state "Dispatch Loop" as dl
        sm --> dl : FireAsync
        ch --> dl : ReadAllAsync
    }

    state "PlaybackSession" as sess {
        state "IPlaybackClock" as clk
        state "PipelineController" as pc
        pc --> clk : reads Position
    }

    state "Workers" as w {
        state "DemuxPump" as dp
        state "VideoDecode" as vd
        state "VideoPresent" as vp
        state "AudioDecodeWrite" as adw
    }

    ctrl --> sess : OnEntry creates/disposes
    sess --> w : StartAsync / PauseAsync / FlushAndRepositionAsync
```

---

## 5. Worker Pipeline Flow

```mermaid
flowchart TB
    source[IMediaSource] --> demux[DemuxPump Worker]
    demux --> |AudioPackets\nChannel| adecode[Audio Decode+Write Worker]
    demux --> |VideoPackets\nChannel| vdecode[Video Decode Worker]
    vdecode --> |VideoFrames\nChannel| vpresent[Video Present Worker]
    adecode --> |IAudioSink| dac[Sound Card / DAC]
    vpresent --> |IVideoSink| display[Display Surface]

    dac --> |GetPlaybackTime| clock[PlaybackClock]
    clock --> |Position| vpresent

    style demux fill:#4A6FA5,color:#fff
    style vdecode fill:#4A6FA5,color:#fff
    style vpresent fill:#4A6FA5,color:#fff
    style adecode fill:#4A6FA5,color:#fff
```

---

## 6. Seek Sequence

```mermaid
sequenceDiagram
    participant User
    participant Controller
    participant Session
    participant Pipeline as PipelineController
    participant Workers

    User->>Controller: SeekAsync(pos)
    Controller->>Controller: Post SeekCommand to channel
    Controller->>Controller: Dispatch loop reads command
    Controller->>Controller: Fire Seek trigger on state machine

    Controller->>Session: SeekAsync(pos)
    Session->>Pipeline: PauseWorkersAsync()
    Pipeline->>Workers: Close pause gate
    Workers-->>Pipeline: Barrier: all workers paused

    Session->>Pipeline: FlushAndRepositionAsync()
    Pipeline->>Pipeline: Drain packet queues
    Pipeline->>Pipeline: Drain frame channel (dispose frames)
    Pipeline->>Pipeline: Flush decoder buffers

    Session->>Session: Demuxer.SeekAsync(pos)
    Session->>Session: Clock.Seek(pos)

    Session->>Pipeline: ResumeWorkers()
    Pipeline->>Workers: Open pause gate
    Workers-->>Workers: Resume from new position

    Session-->>Controller: OnSeekComplete callback
    Controller->>Controller: PostInternalAsync(SeekCompleted)
    Controller->>Controller: SeekState → NotSeeking
    Controller-->>User: Result.Ok()
```

---

## 7. Pause / Resume Sequence

```mermaid
sequenceDiagram
    participant User
    participant Controller
    participant Session
    participant Pipeline as PipelineController
    participant Clock

    User->>Controller: PauseAsync()
    Controller->>Controller: State: Playing → Paused
    Controller->>Session: Pause()
    Session->>Pipeline: PauseWorkersAsync()
    Pipeline->>Pipeline: Close pause gate + await barrier
    Session->>Clock: Pause()
    Session->>Session: AudioSink.PauseAsync()

    Note over Pipeline: Workers are alive but blocked at gate

    User->>Controller: PlayAsync()
    Controller->>Controller: State: Paused → Playing
    Controller->>Session: Resume()
    Session->>Clock: Resume()
    Session->>Session: AudioSink.ResumeAsync()
    Session->>Pipeline: ResumeWorkers()
    Pipeline->>Pipeline: Open pause gate

    Note over Pipeline: Workers unblock and continue
```
