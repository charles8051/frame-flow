// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// Coarse sub-phases of an inference-session load, reported through the
/// optional <see cref="System.IProgress{T}"/> on
/// <see cref="IInferenceSessionFactory.Open(string, System.IProgress{InferenceSessionProgress}?)"/>
/// and <c>Yolov8Detector.CreateAsync</c>.
/// </summary>
/// <remarks>
/// Purely observability — a consumer (e.g. a kiosk splash / status panel)
/// can surface "which provider are we probing", "opening the session",
/// "warming up" instead of a single opaque "loading…" state. The phases
/// follow the order the work actually happens in:
/// <see cref="ProbingProvider"/> (once per EP the factory attempts) →
/// <see cref="OpeningSession"/> (the EP that succeeded, around the
/// session construct) → <see cref="Warmup"/> (the detector's warmup
/// inference, reported by the model wrapper, not the factory).
/// </remarks>
public enum InferenceSessionPhase
{
    /// <summary>
    /// The factory is about to attempt constructing a session with a
    /// candidate execution provider. Reported once per EP in the probe /
    /// fallback chain, before each construction attempt — so a chain that
    /// falls back emits one <see cref="ProbingProvider"/> per EP tried.
    /// The candidate EP is carried on
    /// <see cref="InferenceSessionProgress.Provider"/>.
    /// </summary>
    ProbingProvider,

    /// <summary>
    /// A candidate execution provider's session was constructed
    /// successfully and is being opened (ORT session init: graph load,
    /// CUDA JIT / DML PSO compile, etc.). Reported once, for the EP that
    /// won selection, carried on
    /// <see cref="InferenceSessionProgress.Provider"/>.
    /// </summary>
    OpeningSession,

    /// <summary>
    /// The model wrapper is running its warmup inference to absorb the
    /// EP's cold-start cost before the first real frame (see
    /// <c>Yolov8Detector.Warmup</c>). Reported by the wrapper, not the
    /// factory; <see cref="InferenceSessionProgress.Provider"/> is the
    /// factory's <see cref="IInferenceSessionFactory.ActiveProvider"/>
    /// when known, otherwise <c>null</c>.
    /// </summary>
    Warmup,
}

/// <summary>
/// A single progress report emitted while an inference session loads.
/// Carries the current <see cref="Phase"/>, the
/// <see cref="ExecutionProvider"/> the phase relates to (when known), and
/// an optional human-readable <see cref="Message"/>.
/// </summary>
/// <remarks>
/// Reported through an <see cref="System.IProgress{T}"/> the consumer
/// supplies; the load path is unchanged when no reporter is passed. The
/// struct is intentionally minimal and immutable so it is cheap to create
/// on the load path and safe to hand across threads (the standard
/// <see cref="System.Progress{T}"/> marshals the callback to the captured
/// synchronization context).
/// </remarks>
/// <param name="Phase">The load sub-phase this report describes.</param>
/// <param name="Provider">
/// The execution provider the phase relates to, or <c>null</c> when no
/// single provider applies (e.g. a <see cref="InferenceSessionPhase.Warmup"/>
/// report where the active provider is not known to the caller).
/// </param>
/// <param name="Message">
/// Optional human-readable detail (e.g. for display on a status panel).
/// <c>null</c> when there is nothing to add beyond the phase + provider.
/// </param>
public readonly record struct InferenceSessionProgress(
    InferenceSessionPhase Phase,
    ExecutionProvider? Provider = null,
    string? Message = null);
