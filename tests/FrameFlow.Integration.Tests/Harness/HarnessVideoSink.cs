using System.Collections.Concurrent;
using System.Diagnostics;
using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Integration.Tests.Harness;

/// <summary>
/// Instrumented <see cref="IVideoSink"/> for integration tests.
/// Faithfully exercises backpressure via <see cref="CpuFramePool"/> and tracks
/// frame counts, PTS monotonicity, dropped frames, and wall-clock timestamps.
/// </summary>
/// <remarks>
/// <para>
/// Uses the same split-thread pattern as <c>SdlVideoSink</c>:
/// <see cref="PresentAsync"/> stores a frame via <see cref="Interlocked.Exchange{T}(ref T, T)"/>
/// on the pipeline thread, and a background pump task consumes it at ~16 ms intervals.
/// Without the pump, <see cref="CpuFramePool"/>'s semaphore (capacity 3) blocks after
/// three frames and the pipeline deadlocks.
/// </para>
/// </remarks>
internal sealed class HarnessVideoSink : IVideoSink
{
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;

    private IVideoFrame? _pendingFrame;

    // ── Counters (all thread-safe via Interlocked) ──────────────────
    private int _frameCount;
    private int _droppedFrameCount;
    private int _formatChangedCount;
    private int _submittedFrameCount;

    // ── PTS tracking ────────────────────────────────────────────────
    private long _firstPtsTicks = -1;
    private long _lastPtsTicks;
    private int _isPtsMonotonic = 1; // 1 = true, 0 = false

    /// <summary>
    /// The frame pool providing backpressure for this sink.
    /// Uses a real <see cref="CpuFramePool"/> with capacity 3 so integration
    /// tests exercise the same semaphore-based flow control as production.
    /// </summary>
    public IFramePool FramePool { get; } =
        new CpuFramePool(NullLogger<CpuFramePool>.Instance, capacity: 3);

    /// <inheritdoc />

    // ── Observable counters ─────────────────────────────────────────

    /// <summary>Total frames handed to <see cref="PresentAsync"/>.</summary>
    public int SubmittedFrameCount => Volatile.Read(ref _submittedFrameCount);

    /// <summary>Total frames consumed by the pump (not dropped).</summary>
    public int FrameCount => Volatile.Read(ref _frameCount);

    /// <summary>Frames displaced from <c>_pendingFrame</c> before the pump could consume them.</summary>
    public int DroppedFrameCount => Volatile.Read(ref _droppedFrameCount);

    /// <summary>Number of <see cref="OnFormatChangedAsync"/> calls received.</summary>
    public int FormatChangedCount => Volatile.Read(ref _formatChangedCount);

    /// <summary>
    /// Presentation timestamp of the first frame consumed by the pump.
    /// Returns <see cref="TimeSpan.Zero"/> if no frames have been consumed yet.
    /// </summary>
    public TimeSpan FirstPts =>
        Volatile.Read(ref _firstPtsTicks) < 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Volatile.Read(ref _firstPtsTicks));

    /// <summary>Presentation timestamp of the most recent frame consumed by the pump.</summary>
    public TimeSpan LastPts => TimeSpan.FromTicks(Volatile.Read(ref _lastPtsTicks));

    /// <summary>
    /// Total frames consumed or dropped by the pump.
    /// </summary>
    public int ProcessedFrameCount => FrameCount + DroppedFrameCount;

    /// <summary>
    /// Whether a frame is still waiting to be consumed by the pump.
    /// </summary>
    public bool HasPendingFrame => Volatile.Read(ref _pendingFrame) is not null;

    /// <summary>
    /// Whether all submitted frames have been consumed or dropped and no frame remains pending.
    /// </summary>
    public bool IsDrained => !HasPendingFrame && ProcessedFrameCount >= SubmittedFrameCount;

    /// <summary>
    /// Whether all consumed frames had strictly increasing PTS values.
    /// Starts <c>true</c>; set to <c>false</c> if any frame PTS is ≤ the previous.
    /// </summary>
    public bool IsPtsMonotonic => Volatile.Read(ref _isPtsMonotonic) == 1;

    /// <summary>
    /// Wall-clock <see cref="Stopwatch.GetTimestamp()"/> ticks recorded at each
    /// pump consumption. Useful for asserting that frames arrive at a reasonable pace.
    /// </summary>
    public ConcurrentBag<long> WallClockTicks { get; } = new();

    public async Task WaitForDrainAsync(int timeoutMilliseconds = 5000)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));

        while (true)
        {
            var submitted = SubmittedFrameCount;
            var processed = ProcessedFrameCount;
            var hasPendingFrame = HasPendingFrame;

            if (IsDrained)
            {
                return;
            }

            try
            {
                await Task.Delay(10, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out waiting for HarnessVideoSink drain. submitted={submitted}, processed={processed}, pending={hasPendingFrame}, consumed={FrameCount}, dropped={DroppedFrameCount}."
                );
            }
        }
    }

    /// <summary>
    /// Creates a new <see cref="HarnessVideoSink"/> and starts the background pump.
    /// </summary>
    public HarnessVideoSink()
    {
        _pumpTask = Task.Run(() => PumpLoopAsync(_pumpCts.Token));
    }

    /// <inheritdoc />
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        Interlocked.Increment(ref _submittedFrameCount);
        var stale = Interlocked.Exchange(ref _pendingFrame, frame);
        if (stale is not null)
        {
            Interlocked.Increment(ref _droppedFrameCount);
            stale.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
    {
        Interlocked.Increment(ref _formatChangedCount);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Signal the pump to stop.
        _pumpCts.Cancel();

        // Wait for the pump task to exit (bounded to avoid test hangs).
        try
        {
            await _pumpTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Pump did not exit in time — proceed with cleanup anyway.
        }
        catch (OperationCanceledException)
        {
            // Expected when CTS fires.
        }

        // Dispose any residual pending frame.
        var residual = Interlocked.Exchange(ref _pendingFrame, null);
        residual?.Dispose();

        // Dispose the frame pool.
        FramePool.Dispose();

        _pumpCts.Dispose();
    }

    // ── Background pump ─────────────────────────────────────────────

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(16, ct).ConfigureAwait(false);

                var frame = Interlocked.Exchange(ref _pendingFrame, null);
                if (frame is null)
                    continue;

                try
                {
                    // Record metrics before disposing.
                    var ptsTicks = frame.Pts.Ticks;

                    // First-frame PTS (set once via CompareExchange).
                    Interlocked.CompareExchange(ref _firstPtsTicks, ptsTicks, -1);

                    // Monotonicity check: current PTS must be > previous PTS.
                    var previousTicks = Volatile.Read(ref _lastPtsTicks);
                    if (Volatile.Read(ref _frameCount) > 0 && ptsTicks <= previousTicks)
                    {
                        Volatile.Write(ref _isPtsMonotonic, 0);
                    }

                    Volatile.Write(ref _lastPtsTicks, ptsTicks);
                    WallClockTicks.Add(Stopwatch.GetTimestamp());
                    Interlocked.Increment(ref _frameCount);
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — the CTS was cancelled.
        }
    }
}
