# ADR-0013: CancellationToken Propagation Policy

## Status

Accepted

## Context

All async public APIs in FrameFlow accept `CancellationToken`: `IPlaybackSession`, `IDemuxSession`, `IVideoDecoder`, `IAudioDecoder`, `IAudioSink`, and `IVideoFramePresenter`.

The playback session manages multiple concurrent worker loops (ADR-0009), each with its own linked `CancellationTokenSource`. The state machine has nine states: Idle, Opening, Ready, Playing, Paused, Seeking, Stopped, Ended, and Faulted.

Cancellation can originate from several sources:

- Consumer calling `StopAsync`
- Consumer disposing the session via `DisposeAsync`
- Consumer-supplied `CancellationToken` being cancelled
- Internal error causing a transition to the Faulted state

Without a clear policy, cancellation semantics become ambiguous. Does cancelling a token passed to `PlayAsync` mean "pause", "stop", or "dispose"? What happens if a worker loop does not respond to cancellation during disposal? These questions must have consistent answers before implementation begins.

ADR-0009 establishes that each worker loop uses a linked `CancellationTokenSource`. This ADR defines the rules for how those tokens relate to consumer-supplied tokens and session lifecycle.

## Decision

### Consumer tokens mean "abort this operation"

A `CancellationToken` passed to a public API method (e.g., `OpenAsync`, `PlayAsync`, `SeekAsync`) means "I want to cancel this specific operation." It does not mean "destroy the session" or "stop playback."

```csharp
// Cancel the open attempt after 5 seconds, but keep the session alive
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
try
{
    await session.OpenAsync(uri, cts.Token);
}
catch (OperationCanceledException)
{
    // Session is still in Idle state — can try again
    await session.OpenAsync(differentUri);
}
```

If the consumer wants to destroy the session, they call `DisposeAsync`. Cancellation and disposal are separate concerns.

### DisposeAsync is the authoritative teardown

`DisposeAsync` is the single, authoritative way to tear down a session and release all resources. It:

1. Signals all internal `CancellationTokenSource` instances
2. Awaits worker loop completion with a bounded timeout
3. Disposes native resources (format context, codec contexts, frames)
4. Transitions to a terminal state

No amount of token cancellation from the outside can substitute for `DisposeAsync`. The session is not "done" until disposed.

### Linked tokens per worker loop

Each internal worker loop (demux, video decode, audio decode, presentation, audio output) creates a linked `CancellationTokenSource` that combines:

- The session-level cancellation source (signalled by `StopAsync` or `DisposeAsync`)
- The consumer-supplied token for the current operation (if any)

```csharp
using var linked = CancellationTokenSource.CreateLinkedTokenSource(
    _sessionCts.Token,
    consumerToken);
```

This means a worker stops if either the session is shutting down or the consumer cancels their specific operation.

### Bounded disposal timeout

`DisposeAsync` will signal cancellation and then await worker completion with a bounded timeout (default: 5 seconds). If workers do not exit within the timeout, disposal completes anyway and logs a warning.

This prevents a hung native call or unresponsive worker from blocking disposal indefinitely. The timeout is configurable via options.

```csharp
public async ValueTask DisposeAsync()
{
    _sessionCts.Cancel();
    var allWorkers = Task.WhenAll(_demuxTask, _videoDecodeTask, _audioDecodeTask);
    if (await Task.WhenAny(allWorkers, Task.Delay(_disposeTimeout)) != allWorkers)
    {
        _logger.LogWarning("Workers did not exit within {Timeout}; completing disposal", _disposeTimeout);
    }
    // Dispose native resources regardless
    DisposeNativeResources();
}
```

### Seek cancellation returns to pre-seek state

If a `SeekAsync` operation is cancelled (via its consumer token), the session returns to whatever state it was in before the seek began (Playing or Paused). It does not transition to Stopped or Faulted.

The seek operation should drain the pipeline and refill from the new position. If cancelled mid-drain, the pipeline may contain stale frames. The implementation must handle this by either completing the drain or flushing the pipeline before resuming the prior state.

### OperationCanceledException is normal flow

`OperationCanceledException` thrown due to consumer token cancellation is expected, non-exceptional flow. It should not:

- Transition the session to Faulted
- Be logged at Error or Warning level
- Leave the session in an inconsistent state

It may be logged at Debug level for diagnostic purposes.

`OperationCanceledException` thrown due to session disposal is also expected and should not be re-thrown from `DisposeAsync`.

### No CancellationToken on fire-and-forget paths

Internal fire-and-forget operations (e.g., updating a UI progress indicator) should use `CancellationToken.None` or the session-level token, never a consumer-supplied operation token. This prevents accidental cancellation of background housekeeping when a consumer cancels an operation.

## Consequences

### Positive

- Clear separation between "cancel this operation" and "destroy the session"
- Linked tokens provide clean composition without manual token forwarding
- Bounded disposal timeout prevents hung workers from blocking teardown
- Seek cancellation has well-defined recovery semantics
- `OperationCanceledException` handling is consistent across all subsystems
- Consumers can safely retry operations after cancellation without session corruption

### Negative

- The linked token pattern requires allocating a `CancellationTokenSource` per worker per operation
- Bounded disposal timeout means some worker cleanup may be incomplete if the timeout is too short
- Seek cancellation recovery is complex — flushing stale frames from the pipeline adds implementation effort
- Every async code path must distinguish between "my operation was cancelled" and "the session is shutting down," which requires checking which token triggered the exception

## Alternatives considered

### CancellationToken means "stop playback"

Rejected because it conflates operation-level and session-level semantics. A consumer who cancels an `OpenAsync` timeout should not find their session in the Stopped state.

### No consumer CancellationToken — only session-level stop

Rejected because it removes fine-grained control from the consumer. Operations like seek and open benefit from per-operation timeout and cancellation support.

### Unbounded disposal wait

Rejected because a hung native FFmpeg call could block `DisposeAsync` indefinitely, preventing application shutdown. The bounded timeout with a warning log is a safer default.

### CancellationToken on DisposeAsync itself

Rejected because `DisposeAsync` is a cleanup obligation. The caller should not be able to cancel cleanup — that leads to resource leaks. The internal timeout provides the bounded behavior without exposing it as an API choice.
