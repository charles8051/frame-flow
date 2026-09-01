// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

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
/// </remarks>
public sealed class PresenterStallWatchdog : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private readonly Func<PresenterSample> _sample;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private readonly int _recoverySamples;
    private PresenterStallEvaluator _evaluator;
    private bool _wasStalled;
    private int _disposed;

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
    {
        _sample = sample;
        _logger = logger;
        _recoverySamples = recoverySamples;
        _evaluator = PresenterStallEvaluator.Create(timeout ?? DefaultTimeout, recoverySamples);
        var period = interval ?? DefaultInterval;
        _timer = new Timer(Tick, null, period, period);
    }

    private void Tick(object? _)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

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
            Stalled?.Invoke(new PresenterStallInfo(ms, sample.FramesPresented, outcome.Reason));
        }
        else if (outcome.Recovered)
        {
            _logger.LogWarning(
                "Presenter RECOVERED from {Reason}: presenting again at {FramesPresented} frames, confirmed "
                    + "over {ConfirmedOverSamples} consecutive samples of forward progress. A host that "
                    + "latched on the stall can clear it.",
                outcome.Reason, sample.FramesPresented, _recoverySamples);
            Recovered?.Invoke(
                new PresenterRecoveryInfo(sample.FramesPresented, outcome.Reason, _recoverySamples));
        }
        _wasStalled = outcome.Stalled;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _timer.Dispose();
    }
}
