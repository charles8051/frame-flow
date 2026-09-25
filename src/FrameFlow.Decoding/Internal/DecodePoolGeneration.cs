// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// One hardware decode pool, from the frame that revealed it until nothing uses it: the
/// decoder that created it and every <see cref="GpuVideoFrame"/> built from its surfaces. It
/// also guards the pool (ADR-0081 decision 5).
/// </summary>
/// <remarks>
/// <para>
/// The pool is an FFmpeg <c>AVHWFramesContext</c>, reference-counted by the codec context and
/// by every frame cloned from it, so it outlives its decoder while frames are held. Its
/// surfaces stay in <see cref="DecodePoolMetrics.Capacity"/> for exactly that long, so the
/// capacity and the outstanding count describe the same live surfaces.
/// </para>
/// <para>
/// A renegotiation in <c>get_format</c> creates a new pool. The decoder then lets go of the old
/// generation, and the old one leaves the capacity when its last frame is released. Its frames
/// do not count against the new pool's budget, because they pin none of its surfaces.
/// </para>
/// <para>
/// The guard's policy is <see cref="DecodePoolGuard"/>. This class is its shell: it holds the
/// state under a lock, signals releases to a waiting decoder, and runs the watchdog.
/// </para>
/// </remarks>
/// <summary>The decoder, as a pool's guard sees it while it waits.</summary>
internal interface IPoolWaiter
{
    /// <summary>Called once, as a wait starts.</summary>
    void OnPoolWait();

    /// <summary>
    /// Whether holders are not releasing on purpose, as while playback is paused, so a long
    /// wait is expected and is not a probable deadlock.
    /// </summary>
    bool PoolWatchdogSuspended { get; }

    /// <summary>
    /// Moves on every change of <see cref="PoolWatchdogSuspended"/>, so a watchdog interval can
    /// tell that a pause began or ended inside it.
    /// </summary>
    int PoolWatchdogEpoch { get; }
}

internal sealed partial class DecodePoolGeneration
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly string _source;
    private PoolGuardState _guard;
    private TaskCompletionSource _released = NewSignal();
    private int _holders = 1; // the decoder that saw the pool first

    /// <param name="framesContext">The <c>AVHWFramesContext*</c>, used only as the pool's identity.</param>
    /// <param name="size">The pool's <c>initial_pool_size</c>.</param>
    /// <param name="budget">The most frames it may hand out; zero leaves it unguarded.</param>
    /// <param name="logger">Where breaches and probable deadlocks are reported.</param>
    /// <param name="source">Names the pool in those reports, for example the backend.</param>
    public DecodePoolGeneration(
        nint framesContext,
        int size,
        int budget = 0,
        ILogger? logger = null,
        string source = "hardware decoder"
    )
    {
        FramesContext = framesContext;
        Size = size;
        _guard = PoolGuardState.Initial(budget);
        _logger = logger ?? NullLogger.Instance;
        _source = source;
        DecodePoolMetrics.OnPoolCapacityChanged(size);
    }

    /// <summary>The pool's identity. Never dereferenced.</summary>
    public nint FramesContext { get; }

    /// <summary>The number of surfaces in the pool.</summary>
    public int Size { get; }

    /// <summary>The most frames the pool may hand out; zero when it is unguarded.</summary>
    public int Budget
    {
        get
        {
            lock (_gate)
                return _guard.Budget;
        }
    }

    /// <summary>Frames built from the pool and not yet released.</summary>
    public int Outstanding
    {
        get
        {
            lock (_gate)
                return _guard.Outstanding;
        }
    }

    /// <summary>
    /// A frame built from the pool's surfaces was handed downstream. The frame holds the pool
    /// until its final release, and counts against the budget.
    /// </summary>
    public void AttachFrame()
    {
        Interlocked.Increment(ref _holders);
        bool report;
        PoolGuardState state;
        lock (_gate)
        {
            (_guard, report) = DecodePoolGuard.HandedOut(_guard);
            state = _guard;
        }

        if (report)
            LogOverBudget(_logger, _source, state.Budget, state.Outstanding);
    }

    /// <summary>A frame built from the pool had its final release.</summary>
    public void DetachFrame()
    {
        TaskCompletionSource released;
        lock (_gate)
        {
            _guard = DecodePoolGuard.Released(_guard);
            released = _released;
            _released = NewSignal();
        }

        released.TrySetResult();
        Release();
    }

    /// <summary>The decoder lets go of the pool, on dispose or when the pool is replaced.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _holders) == 0)
            DecodePoolMetrics.OnPoolCapacityChanged(-Size);
    }

    /// <summary>
    /// Returns once the decoder may decode into the pool, waiting for a release while the pool
    /// has handed out its budget. Reports a probable deadlock once when a wait sees no release
    /// for <paramref name="watchdog"/>, and keeps waiting.
    /// </summary>
    /// <remarks>
    /// Only a whole interval of unpaused waiting counts: an interval in which the waiter was
    /// suspended, or became suspended or resumed, is discarded and a fresh one starts.
    /// </remarks>
    /// <param name="clock">The watchdog's clock.</param>
    /// <param name="watchdog">How long a wait sees no release before it is reported.</param>
    /// <param name="waiter">
    /// The waiting decoder: told when the wait starts, and asked at each watchdog interval
    /// whether a long wait is expected.
    /// </param>
    /// <param name="cancellationToken">Ends the wait; the decoder's source is cancelled on stop, seek and teardown.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask WaitForRoomAsync(
        TimeProvider clock,
        TimeSpan watchdog,
        IPoolWaiter? waiter,
        CancellationToken cancellationToken
    )
    {
        bool waited = false;
        bool reported = false;
        while (true)
        {
            Task released;
            int budget;
            lock (_gate)
            {
                if (DecodePoolGuard.MayDecode(_guard))
                    return;
                released = _released.Task;
                budget = _guard.Budget;
            }

            if (!waited)
            {
                waited = true;
                waiter?.OnPoolWait();
            }

            int epoch = waiter?.PoolWatchdogEpoch ?? 0;
            try
            {
                await released.WaitAsync(watchdog, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                bool quiet =
                    waiter is null
                    || (!waiter.PoolWatchdogSuspended && waiter.PoolWatchdogEpoch == epoch);
                if (!reported && quiet)
                {
                    reported = true;
                    LogProbableDeadlock(_logger, _source, budget, watchdog.TotalSeconds);
                }
            }
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The {Source} pool has {Outstanding} frames held downstream, over its budget of "
            + "{Budget}. A holder is keeping more frames than the pool allows for."
    )]
    private static partial void LogOverBudget(ILogger logger, string source, int budget, int outstanding);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The {Source} decoder has waited {Seconds} s at its pool budget of {Budget} "
            + "frames with no frame released. A holder is probably never releasing; the decoder "
            + "keeps waiting."
    )]
    private static partial void LogProbableDeadlock(ILogger logger, string source, int budget, double seconds);
}
