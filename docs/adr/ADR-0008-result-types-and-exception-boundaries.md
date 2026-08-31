# ADR-0008: Result Types and Exception Boundaries

## Status

Accepted

## Context

FrameFlow has several kinds of failure modes:

- expected operational failures such as unsupported media, unavailable FFmpeg binaries, invalid options, or seek limitations
- programmer errors such as invalid API usage or broken invariants
- unexpected runtime failures such as native interop bugs or environmental failures

If the library uses exceptions for every unsuccessful outcome, public APIs can become harder to reason about and normal operational failures may feel overly exceptional.

If the library uses `Result`-style return values everywhere, the internal code can become noisy, hot paths can accumulate unnecessary churn, and genuine exceptional failures can become artificially flattened into routine control flow.

FrameFlow needs a consistent boundary so consumers and implementers know when to expect explicit success or failure values versus exceptions.

## Decision

FrameFlow will use `Result`-style return types selectively at public and orchestration boundaries where failure is expected and recoverable.

Good candidates include:

1. FFmpeg bootstrap and probe operations
2. media inspection and open operations
3. capability and compatibility checks
4. options and configuration validation
5. seek or control requests that may fail for normal media reasons

FrameFlow will continue to use exceptions for:

- programmer errors and invalid internal states
- violated invariants
- unexpected environmental failures that are not part of normal control flow
- low-level failures that should abort the current operation rather than be treated as an alternate expected outcome

FrameFlow will not force `Result`-style returns into hot-path decode, conversion, rendering, or timing internals where they would add routine overhead and reduce clarity.

The library should prefer one coherent error model per API surface instead of mixing ad hoc exceptions and `Result` values within the same kind of operation.

## Consequences

### Positive

- consumer-facing operations can communicate expected failures explicitly
- the public API can distinguish operational outcomes from truly exceptional failures
- internal hot paths remain simpler and avoid unnecessary control-flow noise

### Negative

- some boundary design effort is required to keep error surfaces consistent
- implementers must be disciplined about not expanding `Result` usage into inappropriate internal paths

## Alternatives Considered

### Use exceptions for all failures

Rejected because many normal media and environment outcomes are expected and should be represented more explicitly at the API boundary.

### Use `Result`-style returns throughout the entire library

Rejected because it would add noise to internal control flow and weaken the distinction between expected operational failures and truly exceptional conditions.

### Leave error handling style to each subsystem

Rejected because it would produce an inconsistent consumer experience and make cross-layer behavior harder to reason about.
