# ADR-0010: Logging and Diagnostics Strategy

## Status

Accepted

## Context

FrameFlow is a .NET library, not a standalone application. It must integrate cleanly with whatever logging infrastructure the host application already uses rather than imposing its own provider or sink choices.

Several existing decisions make logging strategy relevant early:

- ADR-0006 lists "diagnostics listeners" as a recognized extension seam
- the ARCHITECTURE.md error handling section calls for rich diagnostics covering missing libraries, unsupported codecs, and failed operations
- the project uses modern .NET patterns including dependency injection and `IOptions<T>`
- Phase 08 covers polish and diagnostics, but logging conventions affect code written from Phase 01 onward

Media playback also introduces performance constraints that general application logging guidance does not address. Decode loops, audio sync timing, and packet demuxing are hot paths where even the overhead of formatting an unused log message can matter. Native interop failures need structured diagnostic information that plain text messages handle poorly.

If logging decisions are deferred until Phase 08, the project will face a painful retrofit across seven phases of implementation, producing inconsistent patterns and likely missing important diagnostic context in the layers that need it most.

## Decision

FrameFlow will adopt a logging and diagnostics strategy early and apply it consistently from the first implementation phase.

### Primary logging abstraction

All FrameFlow libraries will use `Microsoft.Extensions.Logging.ILogger<T>` as the sole logging abstraction. This is the .NET standard, integrates with dependency injection, and allows consumers to plug in any provider they choose — Serilog, NLog, console, or nothing at all.

FrameFlow will not depend on any specific logging provider. It ships `ILogger` usage only.

### Structured logging

All log calls will use semantic message templates with structured parameters rather than string concatenation or interpolation. This ensures log entries are machine-parseable regardless of which provider the consumer configures.

```csharp
// Good — structured template
logger.LogInformation("Opened media source {Uri} with {StreamCount} streams", uri, streamCount);

// Bad — string interpolation
logger.LogInformation($"Opened media source {uri} with {streamCount} streams");
```

### Log level conventions

FrameFlow will follow consistent log level assignments across all subsystems:

| Level | Usage |
|-------|-------|
| **Trace** | Per-frame and per-packet hot-path diagnostics, normally disabled in production |
| **Debug** | Lifecycle transitions, queue state changes, internal decision points |
| **Information** | Session-level events: open, play, stop, seek, error recovery |
| **Warning** | Degraded states: frame drops, audio underruns, fallback activation, recoverable native failures |
| **Error** | Operation failures that the caller should know about |
| **Critical** | Unrecoverable failures: native library load errors, fatal interop crashes |

### Hot-path logging with source generation

Logging in performance-sensitive code paths — decode loops, audio callback timing, synchronization decisions — must use the `[LoggerMessage]` source generator to avoid allocating message strings, boxing value-type arguments, or evaluating expressions when the target level is disabled.

```csharp
[LoggerMessage(Level = LogLevel.Trace, Message = "Decoded frame {FrameIndex} pts={Pts} in {ElapsedMs}ms")]
static partial void LogFrameDecoded(ILogger logger, long frameIndex, long pts, double elapsedMs);
```

For non-hot-path code, standard `ILogger` extension methods with structured templates are acceptable.

### Quantitative telemetry with System.Diagnostics.Metrics

FrameFlow will complement `ILogger` with `System.Diagnostics.Metrics` for quantitative performance telemetry. Counters and histograms will track values such as:

- frames decoded, frames dropped, frames presented
- decode latency per frame
- queue depths (video, audio)
- audio underrun count
- seek latency

`System.Diagnostics.Metrics` is low-overhead by design and compatible with OpenTelemetry exporters, giving consumers opt-in observability without FrameFlow taking a dependency on any telemetry SDK.

### Diagnostics listener extension seam

The "diagnostics listeners" seam identified in ADR-0006 will be implemented as a focused callback interface for playback-level events — state changes, errors, and periodic performance snapshots.

This interface is separate from the `ILogger` pipeline. It keeps the extension seam clean and purpose-built, without requiring consumers to intercept or filter log streams to observe playback behavior programmatically.

## Consequences

### Positive

- Logging patterns are consistent from the first implementation phase instead of being retrofitted
- Consumers control provider choice and filtering without FrameFlow imposing opinions
- Hot-path guards prevent logging from introducing allocation pressure or latency in decode and sync loops
- Metrics provide quantitative observability that complements human-readable log output
- The diagnostics listener seam gives programmatic consumers a stable contract separate from log internals
- Structured logging makes it practical to diagnose native interop and codec failures in production

### Negative

- Every subsystem must follow the log level conventions and hot-path guard discipline from the start
- Source-generated logging via `[LoggerMessage]` adds some ceremony to hot-path code
- Metrics instrumentation requires thought about which measurements are meaningful before the playback core is fully built
- The diagnostics listener interface is an additional contract to design and maintain alongside `ILogger` usage

## Alternatives considered

### EventSource and ETW only

Rejected because EventSource is Windows-centric in practice and significantly harder for consumers to integrate compared to `ILogger`. It also lacks the ecosystem of structured logging providers that `ILogger` enables.

### Custom logging abstraction

Rejected because `ILogger` is the established .NET standard. Introducing a FrameFlow-specific logging interface would force consumers to write adapters and would duplicate work the ecosystem has already solved.

### Defer logging decisions until Phase 08

Rejected because retrofitting structured logging across seven phases of implementation is painful, produces inconsistent conventions, and risks missing diagnostic context in the subsystems that need it most.

### ActivitySource and distributed tracing in v1

Rejected as premature. Distributed tracing is designed for service-to-service request correlation, which is not a meaningful scenario for a media playback library. If consumer demand appears later, `ActivitySource` support can be added without disrupting the `ILogger` and `Metrics` foundations.
