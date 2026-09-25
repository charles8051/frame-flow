// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Graph;

/// <summary>
/// 1→0..1 operator contract: a function from an input to an optional
/// single output. Returning <see langword="null"/> drops the input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The substrate holds one ref on the input across the
/// call and releases it after the operator returns or throws. The
/// operator must NOT dispose the input directly.
/// </para>
/// <para>
/// <b>Output ownership.</b> When the operator returns a non-null
/// output, the substrate takes ownership of that ref. To forward the
/// input, return the input itself: the substrate sees the same object
/// and moves its ref downstream instead of releasing it. That is right for
/// every item type. Do not return <c>input.AddRef()</c> instead: for a type
/// whose <c>AddRef</c> returns the same instance (ADR-0080), the substrate
/// still sees a pass-through and moves one ref, and the extra one leaks on
/// every item. Until #42 removes them, <c>VideoFrameRef</c>,
/// <c>PcmAudioBufferRef</c> and the detection composites return a new
/// wrapper instead, which the substrate treats as a fresh output.
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
/// <b>Ownership.</b> The substrate holds one ref on the input across the
/// iteration and releases it after the iterator completes (or throws),
/// whatever was yielded. Each yielded output transfers its ref to the
/// substrate; the substrate forwards each downstream and disposes when it
/// lands at a sink.
/// </para>
/// <para>
/// <b>Forwarding the input.</b> Unlike <see cref="Operator{TIn, TOut}"/>, a
/// multi-operator does not move the input's ref: it is released after the
/// iteration regardless. To emit the input, yield <c>input.AddRef()</c>,
/// once per time it is emitted.
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
