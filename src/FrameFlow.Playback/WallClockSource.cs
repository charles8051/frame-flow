// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Playback;

/// <summary>
/// Wallclock-backed <see cref="IClockSource"/> used when no audio sink is
/// producing the master clock (the ADR-0003 fallback path). The timeline is a
/// pure function of a <see cref="Stopwatch"/> read <b>on demand</b>: there is no
/// publish ticker, so a descheduled thread can never leave a pacer waiting on a
/// stale clock (ADR-0057, superseding the prior 5 ms publish ticker).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Construct, call <see cref="Start"/> to begin the clock,
/// <see cref="Pause"/>/<see cref="Resume"/> to suspend/restart it,
/// <see cref="Seek"/> to discontinuously jump the value, and
/// <see cref="DisposeAsync"/> to tear down. The instance is consumable as an
/// <see cref="IClockSource"/> immediately after construction — pre-start it
/// reports <see cref="TimeSpan.Zero"/>, and consumers awaiting future targets
/// stay suspended until <see cref="Start"/>.
/// </para>
/// <para>
/// <b>Pull model.</b> <see cref="Latest"/> computes the elapsed value when read;
/// <see cref="WaitUntilAsync"/> recomputes the remaining wait from the live clock each
/// slice and sleeps that long. No fixed tick cadence, no thread to starve — wake-up
/// latency is bounded by the <see cref="TimeProvider"/>'s timer granularity, not by a
/// publisher thread getting scheduled. That granularity is why the default is
/// <see cref="HighResolutionTimeProvider.Preferred"/> rather than
/// <see cref="TimeProvider.System"/>: on Windows the system provider rounds every sleep up
/// to the ~15.625 ms platform tick, which puts a ~34 fps ceiling on a 60 fps source.
/// </para>
/// <para>
/// <b>Threading.</b> The read surface (<see cref="Latest"/>,
/// <see cref="WaitUntilAsync"/>) is safe from any thread. The lifecycle mutators
/// (<see cref="Start"/>, <see cref="Pause"/>, <see cref="Resume"/>,
/// <see cref="Seek"/>, <see cref="DisposeAsync"/>) must be called serially from
/// the owning playback session (the single-owner rule in ADR-0028).
/// </para>
/// </remarks>
public sealed class WallClockSource : IClockSource, ISeekableClock, IAsyncDisposable
{
    // Upper bound on a single sleep slice in the WaitUntilAsync pull loop. Active
    // pacing sleeps the (sub-frame) remaining time directly; this cap only bounds
    // how promptly the loop re-checks a frozen clock (pause / pre-start).
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMilliseconds(50);

    // Supplies the sleep in WaitUntilAsync. Injectable so tests can drive the clock
    // deterministically instead of sleeping; defaulted to the high-resolution provider
    // because the choice decides the frame rate. See the constructor.
    private readonly TimeProvider _timeProvider;

    // Elapsed running time, measured through _timeProvider rather than a private Stopwatch.
    //
    // The delay and the clock have to come from the same source. With Task.Delay on the
    // provider and elapsed on a Stopwatch, a provider that advances its own time without
    // advancing the wall — every test double — fires the timer, finds `remaining` unchanged,
    // and loops forever. Reading both from the provider is what makes the seam mean anything.
    //
    // TimeProvider.GetTimestamp is QPC-backed like Stopwatch, so the default path is
    // unchanged. These reproduce Stopwatch's semantics exactly: Start/Stop are idempotent,
    // Reset clears the accumulator without starting.
    private long _runningSinceTs;
    private TimeSpan _accumulated;
    private bool _isRunning;

    private TimeSpan Elapsed =>
        _isRunning ? _accumulated + _timeProvider.GetElapsedTime(_runningSinceTs) : _accumulated;

    private void ElapsedRestart()
    {
        _accumulated = TimeSpan.Zero;
        _runningSinceTs = _timeProvider.GetTimestamp();
        _isRunning = true;
    }

    private void ElapsedStop()
    {
        if (!_isRunning)
            return;
        _accumulated += _timeProvider.GetElapsedTime(_runningSinceTs);
        _isRunning = false;
    }

    private void ElapsedStart()
    {
        if (_isRunning)
            return;
        _runningSinceTs = _timeProvider.GetTimestamp();
        _isRunning = true;
    }

    private void ElapsedReset()
    {
        _accumulated = TimeSpan.Zero;
        _isRunning = false;
    }

    /// <summary>
    /// Creates a wallclock paced by <see cref="HighResolutionTimeProvider.Preferred"/>.
    /// </summary>
    public WallClockSource()
        : this(null) { }

    /// <summary>Creates a wallclock paced by <paramref name="timeProvider"/>.</summary>
    /// <param name="timeProvider">
    /// Supplies the sleep in <see cref="WaitUntilAsync"/>. Null uses
    /// <see cref="HighResolutionTimeProvider.Preferred"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why the default is not <see cref="TimeProvider.System"/>.</b> The system provider
    /// routes to the platform timer queue, which on Windows rounds every sleep up to the
    /// ~15.625 ms tick. A 60 fps frame period is 16.67 ms — just over one quantum — so a
    /// sleep for one frame costs two: 28.9 ms measured against 16.4 ms through a
    /// high-resolution timer, which is 34.6 fps versus 61.0 fps for the same source. The
    /// pacing loop is unchanged; which provider supplies its sleep is the whole difference
    /// (#128, #152).
    /// </para>
    /// <para>
    /// Passing <see cref="TimeProvider.System"/> explicitly opts back out. Off Windows, and
    /// on Windows before 10 1803, the default already <i>is</i> the system provider.
    /// </para>
    /// </remarks>
    public WallClockSource(TimeProvider? timeProvider) =>
        _timeProvider = timeProvider ?? HighResolutionTimeProvider.Preferred;

    // Added to Elapsed to produce the value; reset on Seek so the
    // consumer sees a jump to the seeked position.
    private TimeSpan _baseOffset = TimeSpan.Zero;

    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    // The timeline as a pure function of the monotonic clock, read on demand.
    // When paused the accumulator stops advancing, so this freezes; pre-start it is
    // _baseOffset (zero).
    private TimeSpan CurrentTime => _baseOffset + Elapsed;

    /// <inheritdoc/>
    public TimeSpan Latest => CurrentTime;

    /// <inheritdoc/>
    public ValueTask WaitUntilAsync(TimeSpan target, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Fast path: already due — no await, no allocation.
        if (CurrentTime >= target)
            return ValueTask.CompletedTask;
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        return new ValueTask(WaitUntilCoreAsync(target, cancellationToken));
    }

    private async Task WaitUntilCoreAsync(TimeSpan target, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCts.Token
        );
        var token = linked.Token;
        while (true)
        {
            var remaining = target - CurrentTime;
            if (remaining <= TimeSpan.Zero)
                return;
            var slice = remaining < MaxSleep ? remaining : MaxSleep;
            await Task.Delay(slice, _timeProvider, token).ConfigureAwait(false);
        }
    }

    /// <summary>Starts (or restarts) the wallclock at zero. Idempotent.</summary>
    public void Start()
    {
        ThrowIfDisposed();
        _baseOffset = TimeSpan.Zero;
        ElapsedRestart();
    }

    /// <summary>
    /// Suspends the clock at the current value: elapsed stops advancing, so
    /// <see cref="Latest"/> freezes and pending waits for a future target stay
    /// suspended until <see cref="Resume"/>.
    /// </summary>
    public void Pause()
    {
        ThrowIfDisposed();
        ElapsedStop();
    }

    /// <summary>Resumes the clock from the current (paused) value.</summary>
    public void Resume()
    {
        ThrowIfDisposed();
        ElapsedStart();
    }

    /// <summary>
    /// Discontinuously jumps the value to <paramref name="position"/>. Pending
    /// waits whose target is at or before the new value resolve on their next
    /// slice; waits ahead continue against the new origin.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        bool wasRunning = _isRunning;
        ElapsedReset();
        _baseOffset = position;
        if (wasRunning)
            ElapsedStart();
    }

    /// <inheritdoc cref="ISeekableClock.SeekBaseline"/>
    /// <remarks>
    /// The wallclock has always reseated its origin atomically via <see cref="Seek"/>;
    /// this is the <see cref="ISeekableClock"/> facet so the seek orchestrator can
    /// reseat whichever clock masters (wallclock or audio sink) through one call.
    /// </remarks>
    public void SeekBaseline(TimeSpan position) => Seek(position);

    /// <summary>Whether the wallclock is currently advancing.</summary>
    public bool IsRunning => _isRunning;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return default;

        // Release any in-flight WaitUntilAsync sleeps.
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        return default;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WallClockSource));
    }
}
