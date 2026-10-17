// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Media.Diagnostics;

/// <summary>
/// Diagnostic payload for a detected presenter stall — handed to
/// <see cref="PresenterStallWatchdog.Stalled"/> subscribers (e.g. a host health node) and logged.
/// </summary>
/// <param name="StalledForMs">How long the relevant progress signal had been absent when the stall was raised.</param>
/// <param name="FramesPresented">The enqueue frame count the presenter froze at.</param>
/// <param name="Reason">Which stall signature fired (ADR-0064 §Observability).</param>
public readonly record struct PresenterStallInfo(
    double StalledForMs,
    int FramesPresented,
    PresenterStalledReason Reason);

/// <summary>
/// Diagnostic payload for a <b>confirmed</b> recovery from a previously raised stall — handed to
/// <see cref="PresenterStallWatchdog.Recovered"/> subscribers so a host can clear whatever it
/// latched on <see cref="PresenterStallWatchdog.Stalled"/>.
/// </summary>
/// <param name="FramesPresented">The enqueue frame count once presenting had resumed.</param>
/// <param name="Reason">Which stall signature the presenter recovered from.</param>
/// <param name="ConfirmedOverSamples">How many consecutive forward-progress samples confirmed it.</param>
public readonly record struct PresenterRecoveryInfo(
    int FramesPresented,
    PresenterStalledReason Reason,
    int ConfirmedOverSamples);

/// <summary>
/// Imperative shell around <see cref="PresenterStallEvaluator"/>: a background timer that samples
/// the presenter's liveness counters and, on the <b>rising edge</b> of a stall, logs a critical
/// event and raises <see cref="Stalled"/>.
/// </summary>
/// <remarks>
/// <para>
/// Runs on its own thread-pool timer, reads only the counters its sampler hands back, and touches
/// no UI dispatcher and no GPU object — so it keeps firing and detects the freeze <i>even when the
/// presenting thread is wedged</i>. It only <b>detects</b>; recovery is the host's job, because the
/// wedged producer usually cannot be rebuilt in-process.
/// </para>
/// <para>
/// <b>Presenter-agnostic.</b> The sampler is injected as a <c>Func&lt;PresenterSample&gt;</c>, so
/// nothing here knows which presenter it is watching. The zero-copy compositor presenter in
/// <c>FrameFlow.Avalonia.Windows</c> is the caller it was written for, and the case that motivates
/// the wording of the critical log — a UI thread hung inside <c>VideoProcessorBlt</c>
/// (investigation 2026-06-12 §9), and the compositor-queue signature from ADR-0064
/// §Observability. Those strings are left verbatim: they are what a field incident is grepped for.
/// A presenter with a different failure mode reports the same
/// <see cref="PresenterStalledReason"/> with a different cause behind it.
/// </para>
/// <para>
/// <b>Stall telemetry belongs to the caller.</b> The watchdog does not own a meter. The compositor
/// presenter records <c>frameflow.presenter.stalls</c> from its own <see cref="Stalled"/> handler,
/// which is the same rising edge.
/// </para>
/// <para>
/// <b>Stopping.</b> <see cref="DisposeAsync"/> stops the timer and completes once a check already
/// running, with any handler it raised, has returned, so none runs after it; a second call waits
/// for the first (ADR-0086, decision 3). <see cref="Dispose"/> stops the timer and returns at once.
/// A handler must not wait for the watchdog's <see cref="DisposeAsync"/>, or for a dispose that
/// awaits it, since that dispose waits for the handler. A stop still waiting after five seconds
/// logs once, at Warning, what it waits for: the sampler or the handler, by name.
/// </para>
/// </remarks>
public sealed class PresenterStallWatchdog : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private readonly Func<PresenterSample> _sample;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private readonly int _recoverySamples;
    private PresenterStallEvaluator _evaluator;
    private bool _wasStalled;

    // Stops the timer once, and has every DisposeAsync wait for a check in flight (ADR-0086,
    // decision 3). A check that starts after the stop has begun returns without sampling.
    private readonly DisposeOnce _dispose = new();

    // How many timer callbacks are running, and completed once none is after the stop has begun.
    // The stop waits for this rather than for the timer, so it waits the same on any clock's timer:
    // a fake clock's timer does not wait for its callback when disposed.
    private int _inFlight;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // What each check in flight is doing, by the check's number: in the sampler, or in a handler it
    // raised. The timer may start a check while another still runs. Read by the warning a stop that
    // waits too long logs (ADR-0086, decision 3.3).
    private readonly ConcurrentDictionary<long, string> _running = new();
    private readonly string _samplerName;
    private long _checks;

    /// <summary>
    /// Times how long a stop waits for a check before it says what it waits for. For tests: a fake
    /// clock stands in for the machine's.
    /// </summary>
    internal TimeProvider WarningTime { get; set; } = TimeProvider.System;

    /// <summary>Raised once on the rising edge of each detected stall.</summary>
    public event Action<PresenterStallInfo>? Stalled;

    /// <summary>
    /// Raised once per stall, on the sample that <b>confirms</b> the presenter is presenting again
    /// (the frozen counter advanced for <see cref="PresenterStallEvaluator.DefaultRecoverySamples"/>
    /// consecutive samples). Pairs with <see cref="Stalled"/> so a host latch has a clear-path that
    /// does not require an operator or a process restart. Never raised without a preceding
    /// <see cref="Stalled"/>, and never on a bare counter reset — see
    /// <see cref="PresenterStallOutcome.Recovered"/> for why that distinction is load-bearing.
    /// </summary>
    public event Action<PresenterRecoveryInfo>? Recovered;

    /// <summary>
    /// Starts sampling immediately. <paramref name="sample"/> reads the live counters (it must be
    /// allocation-light and non-blocking — it runs on the timer thread every <paramref name="interval"/>).
    /// </summary>
    public PresenterStallWatchdog(
        Func<PresenterSample> sample,
        ILogger logger,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        int recoverySamples = PresenterStallEvaluator.DefaultRecoverySamples)
        : this(sample, logger, TimeProvider.System, timeout, interval, recoverySamples) { }

    /// <summary>
    /// Samples on a timer of <paramref name="sampleTime"/>. For tests: each advance of a fake clock
    /// past an interval runs a check.
    /// </summary>
    internal PresenterStallWatchdog(
        Func<PresenterSample> sample,
        ILogger logger,
        TimeProvider sampleTime,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        int recoverySamples = PresenterStallEvaluator.DefaultRecoverySamples)
    {
        _sample = sample;
        _samplerName = Describe("its sampler", sample);
        _logger = logger;
        _recoverySamples = recoverySamples;
        _evaluator = PresenterStallEvaluator.Create(timeout ?? DefaultTimeout, recoverySamples);
        var period = interval ?? DefaultInterval;
        _timer = sampleTime.CreateTimer(Tick, null, period, period);
    }

    private void Tick(object? _)
    {
        // Counted in before reading the stop, as the stop sets its flag before reading the count:
        // either this check sees the stop and does not sample, or the stop sees this check and
        // waits for it.
        Interlocked.Increment(ref _inFlight);
        try
        {
            if (_dispose.HasBegun)
                return;

            var check = Interlocked.Increment(ref _checks);
            try
            {
                Check(check);
            }
            finally
            {
                _running.TryRemove(check, out string? _);
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _inFlight) == 0 && _dispose.HasBegun)
                _drained.TrySetResult();
        }
    }

    private void Check(long check)
    {
        _running[check] = _samplerName;
        PresenterSample sample;
        PresenterStallOutcome outcome;
        try
        {
            sample = _sample();
            outcome = _evaluator.Observe(in sample);
        }
        catch
        {
            // A sampler hiccup (e.g. the sink swapped mid-read) must never kill the watchdog.
            return;
        }
        _evaluator = outcome.Next;

        if (outcome.Stalled && !_wasStalled)
        {
            double ms = outcome.SinceProgressTicks * 1000.0 / Stopwatch.Frequency;
            if (outcome.Reason == PresenterStalledReason.OutputNotComposited)
                _logger.LogCritical(
                    "Presenter STALL ({Reason}): {FramesPresented} frames enqueued to the compositor but none "
                        + "committed for {StalledForMs:F0}ms — frames are reaching the compositor's queue but not "
                        + "the screen (ADR-0064 §Observability; e.g. a converter pinned to a disposed decode device). "
                        + "The presenter cannot self-recover; the host must rebuild the decode pipeline.",
                    outcome.Reason, sample.FramesPresented, ms);
            else
                _logger.LogCritical(
                    "Presenter STALL ({Reason}): no frame enqueued for {StalledForMs:F0}ms while the sink kept "
                        + "accepting frames — the UI-thread convert/present loop is wedged in the GPU driver "
                        + "(ADR-0063: the VideoProcessorBlt hang this replaces). Frozen at {FramesPresented} presented. The presenter "
                        + "cannot self-recover; the host must rebuild the decode pipeline.",
                    outcome.Reason, ms, sample.FramesPresented);
            // The stall counter itself belongs to whichever presenter owns this watchdog, so it
            // is recorded by the Stalled subscriber rather than here. Same rising edge either way.
            if (Stalled is { } stalled)
            {
                _running[check] = Describe("its Stalled handler", stalled);
                stalled(new PresenterStallInfo(ms, sample.FramesPresented, outcome.Reason));
            }
        }
        else if (outcome.Recovered)
        {
            _logger.LogWarning(
                "Presenter RECOVERED from {Reason}: presenting again at {FramesPresented} frames, confirmed "
                    + "over {ConfirmedOverSamples} consecutive samples of forward progress. A host that "
                    + "latched on the stall can clear it.",
                outcome.Reason, sample.FramesPresented, _recoverySamples);
            if (Recovered is { } recovered)
            {
                _running[check] = Describe("its Recovered handler", recovered);
                recovered(new PresenterRecoveryInfo(sample.FramesPresented, outcome.Reason, _recoverySamples));
            }
        }
        _wasStalled = outcome.Stalled;
    }

    /// <summary>
    /// Stops sampling, and returns at once: a check already running may still finish, and raise
    /// <see cref="Stalled"/> or <see cref="Recovered"/>, after this returns.
    /// <see cref="DisposeAsync"/> waits for it.
    /// </summary>
    public void Dispose() => _ = _dispose.RunAsync(StopAsync).AsTask();

    /// <summary>
    /// Stops sampling, and completes once a check already running, with any handler it raised, has
    /// returned. No check runs and no event is raised after it completes. A call made while another
    /// stop runs, or after, waits for that one (ADR-0086, decision 3).
    /// </summary>
    public ValueTask DisposeAsync() => _dispose.RunAsync(StopAsync);

    // Waits for the timer callbacks in flight to return. A check that does not return is named
    // once (ADR-0086, decision 3.3).
    private ValueTask StopAsync()
    {
        _timer.Dispose();
        if (Volatile.Read(ref _inFlight) == 0)
            _drained.TrySetResult();
        return new(
            SlowWorkWarning.AwaitAsync(
                _drained.Task,
                SlowWorkWarning.DefaultInterval,
                WarningTime,
                WarnStillRunning
            )
        );
    }

    private void WarnStillRunning() =>
        _logger.LogWarning(
            "Presenter stall watchdog was stopped {Seconds:F0}s ago and still waits for {Running} to return (ADR-0086).",
            SlowWorkWarning.DefaultInterval.TotalSeconds,
            _running.IsEmpty ? "a check" : string.Join("; ", _running.Values.Distinct())
        );

    // "its sampler CompositionInteropVideoView.SampleStall", say: what a warning names.
    private static string Describe(string what, Delegate handlers) =>
        $"{what} {string.Join(", ", handlers.GetInvocationList().Select(h => $"{h.Method.DeclaringType?.Name}.{h.Method.Name}"))}";
}
