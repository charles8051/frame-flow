// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// 1→0..1 operator contract: a function from an input to an optional
/// single output. Returning <see langword="null"/> drops the input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The substrate has called <c>AddRef</c> on the
/// input before invoking the operator; the substrate will
/// <c>Dispose</c> the ref after the operator returns or throws. The
/// operator must NOT dispose the input directly.
/// </para>
/// <para>
/// <b>Output ownership.</b> When the operator returns a non-null
/// output, the substrate takes ownership of that ref. If the output
/// is reference-equal to the input (a pass-through), the substrate
/// detects this and forwards the input ref directly without a
/// dispose-then-dispose cycle.
/// </para>
/// <para>
/// <b>Retaining the input across invocations.</b> If the operator
/// wants to hold the input alive past this invocation (window
/// aggregators, caches), it should call <c>input.AddRef()</c> inside
/// the body. The substrate's auto-dispose still happens once; the
/// retained ref is the operator's responsibility to eventually
/// dispose.
/// </para>
/// </remarks>
public delegate ValueTask<TOut?> Operator<in TIn, TOut>(
    TIn input,
    CancellationToken ct
)
    where TIn : class, IRefCounted
    where TOut : class, IRefCounted;

/// <summary>
/// 1→N operator contract: a function from an input to zero-or-more
/// outputs. Implemented as an async iterator method so operators can
/// use natural <c>yield return</c> syntax.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the historic Channel-bridge pattern (see e.g.
/// FrameFlow's <c>CaptionPipelineExtensions.SplitOnPunctuation</c>)
/// that hand-rolled 1→N expansion around a bounded channel because
/// the old substrate's <c>Transform</c> was strictly 1→1.
/// </para>
/// <para>
/// <b>Ownership.</b> The substrate calls <c>AddRef</c> on the input
/// before invoking the operator and disposes the ref after the
/// iterator completes (or throws). Each yielded output transfers its
/// ref to the substrate; the substrate forwards each downstream and
/// disposes when it lands at a sink.
/// </para>
/// <para>
/// Yielding zero outputs (the operator's iterator completes without
/// yielding) is equivalent to <see cref="Operator{TIn, TOut}"/>
/// returning null — the input is dropped, no downstream emission.
/// </para>
/// </remarks>
public delegate IAsyncEnumerable<TOut> MultiOperator<in TIn, TOut>(
    TIn input,
    CancellationToken ct
)
    where TIn : class, IRefCounted
    where TOut : class, IRefCounted;

/// <summary>
/// Sink operator: receives an input, produces side effects, no output.
/// </summary>
public delegate ValueTask Consumer<in TIn>(
    TIn input,
    CancellationToken ct
)
    where TIn : class, IRefCounted;

/// <summary>
/// Source operator: produces items on demand. Returns null to signal
/// end-of-stream. The substrate takes ownership of the returned ref.
/// </summary>
public delegate ValueTask<TOut?> Producer<TOut>(
    CancellationToken ct
)
    where TOut : class, IRefCounted;
