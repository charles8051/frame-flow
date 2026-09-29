// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Inference;

/// <summary>
/// Factory for <see cref="IInferenceSession"/> instances with
/// configurable execution-provider selection and fallback. Lifts the
/// "try preferred EP → fall back → log the choice" pattern out of
/// individual model wrappers (<c>Yolov8Detector</c>, future Whisper /
/// face / gaze / OCR) into a shared abstraction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy probe.</b> The first <see cref="Open(string, System.IProgress{InferenceSessionProgress})"/> call attempts the
/// preferred EP and, on failure, walks the fallback chain until one
/// succeeds. The selected EP is cached as <see cref="ActiveProvider"/>;
/// subsequent <c>Open</c> calls construct with the cached
/// provider directly without re-probing. If that construct fails, as
/// DirectML does for the rest of the process after a GPU reset, the
/// factory walks the chain from the preferred EP, skipping the one that
/// just failed. If every EP in the chain fails, <c>Open</c> throws;
/// <see cref="ActiveProvider"/> keeps the EP it held, which is <c>null</c>
/// until an <c>Open</c> has succeeded, since a model no EP opens says nothing
/// against it.
/// </para>
/// <para>
/// <b>What a failure does.</b> The factory reads why an EP failed from its
/// exception (#497):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Out of memory</b> (a CUDA or ONNX Runtime allocation failure,
/// <c>E_OUTOFMEMORY</c>): <c>Open</c> throws <see cref="ProviderOutOfMemoryException"/>
/// without trying another EP, and <see cref="ActiveProvider"/> does not change.
/// The model did not fit; the next one may.
/// </description></item>
/// <item><description>
/// <b>Device lost</b> (<c>DXGI_ERROR_DEVICE_REMOVED</c>, <c>_HUNG</c>,
/// <c>_RESET</c>) or <b>EP unavailable</b> (its library or entry point missing,
/// no adapter): the walk goes on, and the EP that opens is cached.
/// </description></item>
/// <item><description>
/// <b>Anything else</b>, such as a model an EP cannot run: the walk goes on
/// and opens the model, but the cache does not change, so the next model
/// starts where this one did.
/// </description></item>
/// </list>
/// <para>
/// A bad model fails on every EP, so such an <c>Open</c> tries every EP.
/// </para>
/// <para>
/// <b>Bootstrap policy.</b> Each EP's session constructor is
/// responsible for its own bootstrap (CUDA: <c>CudaDllResolver</c>,
/// DML: <c>AppendExecutionProvider_DML</c>, CPU: nothing). The factory
/// probes success / failure by attempting construction and catching.
/// EP packages don't need a separate "is available?" probe.
/// </para>
/// <para>
/// <b>Layering.</b> This interface lives in
/// <c>FrameFlow.Inference.Abstractions</c>; the concrete EP packages
/// (<c>FrameFlow.Inference.Cuda</c>, <c>FrameFlow.Inference.Dml</c>) do
/// not depend on it. Consumers compose: the caller wires up the per-EP
/// construction delegates (<c>path => new CudaInferenceSession(path)</c>)
/// and hands them to <see cref="InferenceSessionFactoryBuilder.Create"/>,
/// which returns an <see cref="IInferenceSessionFactory"/>. Model
/// wrappers consume the factory without knowing which EPs the caller
/// registered.
/// </para>
/// </remarks>
public interface IInferenceSessionFactory
{
    /// <summary>
    /// EP used by previous and future <c>Open</c> calls.
    /// <c>null</c> until <see cref="Open(string, System.IProgress{InferenceSessionProgress})"/> has succeeded at least once.
    /// </summary>
    ExecutionProvider? ActiveProvider { get; }

    /// <summary>
    /// Opens an <see cref="IInferenceSession"/> for the given model
    /// path. The first call drives EP selection through the preferred +
    /// fallback chain; subsequent calls use <see cref="ActiveProvider"/>
    /// directly. Throws <see cref="InvalidOperationException"/>
    /// (wrapping the per-EP failures in an <see cref="AggregateException"/>)
    /// if every EP in the chain fails to construct a session.
    /// </summary>
    /// <remarks>
    /// Equivalent to <see cref="Open(string, IProgress{InferenceSessionProgress}?)"/>
    /// with no progress reporter. Provided as a default interface method
    /// so existing call sites and implementers are unaffected.
    /// </remarks>
    /// <exception cref="ProviderOutOfMemoryException">
    /// An EP ran out of memory opening the model. No other EP was tried.
    /// </exception>
    IInferenceSession Open(string modelPath) => Open(modelPath, progress: null);

    /// <summary>
    /// Opens an <see cref="IInferenceSession"/> for the given model path,
    /// reporting load sub-phases through the optional
    /// <paramref name="progress"/> reporter. Selection logic is identical
    /// to <see cref="Open(string)"/>: passing <c>null</c> (or using the
    /// parameterless overload) leaves behaviour byte-for-byte unchanged.
    /// </summary>
    /// <param name="modelPath">Path to the ONNX model to load.</param>
    /// <param name="progress">
    /// Optional reporter for <see cref="InferenceSessionProgress"/>. The
    /// factory reports <see cref="InferenceSessionPhase.ProbingProvider"/>
    /// before each EP construction attempt and
    /// <see cref="InferenceSessionPhase.OpeningSession"/> for the EP that
    /// succeeds. <c>null</c> disables reporting entirely (no allocations,
    /// no behavioural change). The <see cref="InferenceSessionPhase.Warmup"/>
    /// phase is reported by the model wrapper (e.g. <c>Yolov8Detector</c>),
    /// not the factory.
    /// </param>
    /// <exception cref="ProviderOutOfMemoryException">
    /// An EP ran out of memory opening the model: it did not fit. No other EP
    /// was tried and <see cref="ActiveProvider"/> did not change. Derives from
    /// <see cref="InvalidOperationException"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Every EP in the chain failed to construct a session. The per-EP failures
    /// are in an <see cref="AggregateException"/> as the inner exception.
    /// </exception>
    IInferenceSession Open(string modelPath, IProgress<InferenceSessionProgress>? progress);
}
