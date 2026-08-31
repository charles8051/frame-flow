# Phase 00b — DI Registration and Host Integration

## Status

**Done.** `IFrameFlowBuilder`, the `services.AddFrameFlow…()` family, and `AddHostedBootstrap()` are implemented and exercised by `examples/FrameFlow.Examples.HostedServicePlayer`.

## Goal

Deliver the `IServiceCollection` integration surface, service lifetime decisions, and hosted-service integration so that FrameFlow works naturally in both standalone builder and DI-hosted application scenarios.

Phase 00 designs the consumer API shape and skeleton contracts. Phase 00b bridges those designs into a working DI registration surface, ensuring the `AddFrameFlow()` experience shown in ARCHITECTURE.md actually exists and is consistent with the builder pattern.

## In scope

- `AddFrameFlow()` extension method on `IServiceCollection` with an options configuration delegate
- `AddFrameFlowOpenAlAudio()` and `AddFrameFlowAvaloniaVideoSink()` DI extension methods in their respective adapter projects
- Service lifetime decisions: which services are singleton, transient, or scoped, and why
- Relationship between `FrameFlowBuilder` (standalone path) and DI registration (they should share configuration logic, not duplicate it)
- `IOptions<T>` and `IOptionsSnapshot<T>` binding for all option types
- Optional `FrameFlowHostedService` for bootstrap and probe at application startup
- Configuration section binding (e.g., `FrameFlow`, `FrameFlow:Playback`, `FrameFlow:Native`)
- Usage samples updated to show both standalone and DI paths side by side

## Out of scope

- Keyed services or multi-instance registration (future consideration)
- Scope-per-session lifetime management (deferred until session lifecycle is implemented)
- Configuration hot-reload support (deferred until options are stable)

## Service lifetime table

As-built names in the second column where they differ from the name this phase used.

| Service | As built | Lifetime | Rationale |
|---------|----------|----------|-----------|
| `INativeBootstrapper` | `IFrameFlowBootstrapper` (`FrameFlow.Media`) | Singleton | One-time library loading, expensive to repeat |
| `ICodecRegistry` | never built — codec lookup stayed inside `FrameFlow.Decoding` | — | — |
| `FrameFlowOptions` | same | Singleton (`IOptions<T>`) | Configuration is read once at startup |
| `PlaybackOptions` | `FrameFlowPlaybackOptions`, nested under `FrameFlowOptions.Playback` | Singleton (`IOptions<T>`) | Configuration is read once at startup |
| — | `FrameFlowNativeOptions` — deliberately not nested under `FrameFlowOptions` (issue S-3) | Singleton (`IOptions<T>`) | Bound by `AddFrameFlowNative()` |
| `IPlaybackSessionFactory` | same (`FrameFlow.Playback`) | Singleton | Stateless factory, safe to share |
| `IPlaybackSession` | same | Transient (via factory) | Each session is independent, owns its own resources |
| `IVideoFramePresenter` | `IVideoSink` (`FrameFlow.Media`) | Scoped or per-session | Bound to a specific UI surface, not shareable |
| `IAudioSink` | same | Scoped or per-session | Bound to a specific audio device/context |

## Key design constraints

### Shared configuration core

The `FrameFlowBuilder` (standalone) and `AddFrameFlow()` (DI) paths must share the same options-binding and validation logic. The builder should be implementable as a thin wrapper that populates the same `IServiceCollection` under the hood.

```csharp
// DI path
services.AddFrameFlow(options =>
{
    options.FFmpegPath = "/usr/lib/ffmpeg";
    options.Playback.MaxVideoQueueDepth = 30;
});

// Standalone path (uses DI internally)
var player = new FrameFlowBuilder()
    .Configure(options => options.FFmpegPath = "/usr/lib/ffmpeg")
    .Build();
```

### Optional hosted service

`FrameFlowHostedService` performs eager bootstrap (native library loading and codec probing) at application startup rather than lazily on first session creation. This is opt-in:

```csharp
services.AddFrameFlow(options => { ... })
        .AddHostedBootstrap(); // optional eager init
```

### Extension method pattern for adapters

Each adapter project provides its own `IServiceCollection` extension:

```csharp
services.AddFrameFlow(options => { ... })
        .AddFrameFlowOpenAlAudio()
        .AddFrameFlowAvaloniaVideoSink();
```

This keeps the core registration independent of any specific presenter or audio backend.

## Dependencies

- Phase 00 (API shape and contracts must be designed first)
- ADR-0002 (native bootstrap and library resolution)
- ADR-0006 (extension seams and adapter pattern)

## Acceptance criteria

- [ ] `AddFrameFlow()` registers all core services with correct lifetimes
- [ ] Builder and DI paths share configuration logic, not duplicate it
- [ ] Options bind from `IConfiguration` sections correctly
- [ ] `FrameFlowHostedService` loads native libraries and probes codecs at startup when enabled
- [ ] Adapter extension methods (`AddFrameFlowOpenAlAudio`, `AddFrameFlowAvaloniaVideoSink`) register their implementations correctly
- [ ] A sample DI integration test can resolve `IPlaybackSessionFactory` from a configured `ServiceProvider`
- [ ] Documentation shows both standalone and DI registration usage

## Risks

- **Lifetime mismatch**: If sessions are transient but hold references to singleton services, disposal semantics must be clear. Sessions must not dispose shared singletons.
- **Options validation timing**: If options are invalid (e.g., bad FFmpeg path), the error should surface at bootstrap or first use, not silently at registration time.
- **Builder-DI divergence**: If the builder and DI paths evolve independently, they will drift. The shared core must be enforced by design, not convention.

## Sub-phase gates

See `SUB_PHASE_GATES.md` for the standard review gates that apply to this phase.

- **Gate 1 — Architectural Scrutiny**: applies
- **Gate 2 — FFmpeg Domain Scrutiny**: does not apply (no FFmpeg interaction)
- **Gate 3 — Testing Review and Implementation**: applies
