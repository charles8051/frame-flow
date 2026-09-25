using FrameFlow.Decoding.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The guard's shell: the decoder's wait at its budget, its cancellation, and the watchdog
/// (ADR-0081 decision 5). No FFmpeg: a pool here is an identity and a size.
/// </summary>
/// <remarks>
/// A pool changes <c>DecodePoolMetrics.Capacity</c> while it lives, so this class runs in the
/// decode-pool collection with the tests that assert on it, and every test releases each holder
/// it adds: its frames and the pool's own.
/// </remarks>
[Collection(DecodePoolCollection.Name)]
public sealed class DecodePoolGenerationTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task BelowTheBudget_TheDecoderDoesNotWait()
    {
        var pool = new DecodePoolGeneration(1, size: 20, budget: 2);
        pool.AttachFrame();
        int waits = 0;

        await pool.WaitForRoomAsync(new FakeTimeProvider(), Watchdog, Waiter(() => waits++), CancellationToken.None);

        Assert.Equal(0, waits);
        pool.DetachFrame();
        pool.Release();
    }

    [Fact]
    public async Task AtTheBudget_TheDecoderWaits_UntilAFrameIsReleased()
    {
        var pool = new DecodePoolGeneration(1, size: 20, budget: 2);
        pool.AttachFrame();
        pool.AttachFrame();
        int waits = 0;

        var wait = pool.WaitForRoomAsync(new FakeTimeProvider(), Watchdog, Waiter(() => waits++), CancellationToken.None).AsTask();

        // The call found the pool at its budget and parked; nothing has released since.
        Assert.False(wait.IsCompleted);
        Assert.Equal(1, waits);

        pool.DetachFrame();
        await wait.WaitAsync(FailureBound);

        Assert.Equal(1, pool.Outstanding);
        pool.DetachFrame();
        pool.Release();
    }

    [Fact]
    public async Task AnUnguardedPool_NeverWaits()
    {
        var pool = new DecodePoolGeneration(1, size: 20, budget: 0);
        for (int i = 0; i < 25; i++)
            pool.AttachFrame();
        int waits = 0;

        await pool.WaitForRoomAsync(new FakeTimeProvider(), Watchdog, Waiter(() => waits++), CancellationToken.None);

        Assert.Equal(0, waits);
        for (int i = 0; i < 25; i++)
            pool.DetachFrame();
        pool.Release();
    }

    [Fact]
    public async Task AParkedDecoder_ExitsWhenItsSourceIsCancelled()
    {
        var pool = new DecodePoolGeneration(1, size: 20, budget: 1);
        pool.AttachFrame();
        using var cts = new CancellationTokenSource();

        var wait = pool.WaitForRoomAsync(new FakeTimeProvider(), Watchdog, null, cts.Token).AsTask();
        Assert.False(wait.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(FailureBound));
        pool.DetachFrame();
        pool.Release();
    }

    [Fact]
    public async Task AWaitWithNoRelease_IsReportedOnceAsAProbableDeadlock_AndKeepsWaiting()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var pool = new DecodePoolGeneration(1, size: 20, budget: 1, logger, "D3D11Va");
        pool.AttachFrame();

        var wait = pool.WaitForRoomAsync(clock, Watchdog, null, CancellationToken.None).AsTask();

        clock.Advance(Watchdog);
        var report = await logger.NextWarning.WaitAsync(FailureBound);
        Assert.Contains("D3D11Va", report);
        Assert.Contains("budget of 1", report);

        // Another interval with no release reports nothing more, and the decoder still waits.
        var next = logger.NextWarning;
        clock.Advance(Watchdog);
        pool.DetachFrame();
        await wait.WaitAsync(FailureBound);

        Assert.False(next.IsCompleted);
        Assert.Equal(1, logger.Warnings);
        pool.Release();
    }

    [Fact]
    public async Task AWaitWhileTheWatchdogIsSuspended_IsNotReported_UntilAWholeIntervalAfterItResumes()
    {
        // Paused playback: holders keep their frames on purpose, so a long wait is expected.
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var pool = new DecodePoolGeneration(1, size: 20, budget: 1, logger, "D3D11Va");
        pool.AttachFrame();
        var waiter = new TestWaiter { PoolWatchdogSuspended = true };
        var warning = logger.NextWarning;

        // The guard reads the epoch as it arms each interval, and again as an unsuspended one
        // ends, so the counts below say when the next interval is armed.
        var wait = pool.WaitForRoomAsync(clock, Watchdog, waiter, CancellationToken.None).AsTask();
        await waiter.EpochReads(1).WaitAsync(FailureBound); // armed

        clock.Advance(Watchdog); // an interval spent suspended: ends without reading the epoch
        await waiter.EpochReads(2).WaitAsync(FailureBound); // armed again

        waiter.PoolWatchdogSuspended = false;
        clock.Advance(Watchdog); // an interval that saw the resume: ends, then re-arms
        await waiter.EpochReads(4).WaitAsync(FailureBound);
        Assert.False(warning.IsCompleted);

        clock.Advance(Watchdog); // a whole interval of unpaused waiting
        Assert.Contains("budget of 1", await warning.WaitAsync(FailureBound));

        pool.DetachFrame();
        await wait.WaitAsync(FailureBound);
        pool.Release();
    }

    [Fact]
    public async Task APauseInsideAnInterval_StartsAFreshOne()
    {
        // Waited 9 s, paused, resumed: the interval that held the pause is discarded, so the
        // report needs another whole interval after the resume.
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var pool = new DecodePoolGeneration(1, size: 20, budget: 1, logger, "D3D11Va");
        pool.AttachFrame();
        var waiter = new TestWaiter();
        var warning = logger.NextWarning;

        var wait = pool.WaitForRoomAsync(clock, Watchdog, waiter, CancellationToken.None).AsTask();
        await waiter.EpochReads(1).WaitAsync(FailureBound); // armed

        clock.Advance(Watchdog - TimeSpan.FromSeconds(1));
        waiter.PoolWatchdogSuspended = true;
        waiter.PoolWatchdogSuspended = false;
        clock.Advance(TimeSpan.FromSeconds(1)); // the interval ends having seen a pause, re-arms
        await waiter.EpochReads(3).WaitAsync(FailureBound);
        Assert.False(warning.IsCompleted);

        clock.Advance(Watchdog);
        Assert.Contains("budget of 1", await warning.WaitAsync(FailureBound));

        pool.DetachFrame();
        await wait.WaitAsync(FailureBound);
        pool.Release();
    }

    [Fact]
    public void AFrameOverTheBudget_IsReportedOncePerBreach()
    {
        var logger = new RecordingLogger();
        var pool = new DecodePoolGeneration(1, size: 20, budget: 1, logger, "D3D11Va");

        pool.AttachFrame();
        pool.AttachFrame();
        pool.AttachFrame();
        Assert.Equal(1, logger.Warnings);

        pool.DetachFrame();
        pool.DetachFrame();
        pool.AttachFrame();
        Assert.Equal(2, logger.Warnings);

        pool.DetachFrame();
        pool.DetachFrame();
        pool.Release();
    }

    private static TestWaiter Waiter(Action onWait) => new() { OnWait = onWait };

    /// <summary>
    /// A decoder stand-in: counts waits, says whether the watchdog is suspended, and lets a test
    /// wait for the guard's reads of the epoch, which mark when it arms an interval.
    /// </summary>
    private sealed class TestWaiter : IPoolWaiter
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _armWaits = [];
        private int _suspended;
        private int _epoch;
        private int _armed;

        public Action? OnWait { get; init; }

        public bool PoolWatchdogSuspended
        {
            get => Volatile.Read(ref _suspended) != 0;
            set
            {
                if (Interlocked.Exchange(ref _suspended, value ? 1 : 0) != (value ? 1 : 0))
                    Interlocked.Increment(ref _epoch);
            }
        }

        public int PoolWatchdogEpoch
        {
            get
            {
                int armed = Interlocked.Increment(ref _armed);
                lock (_gate)
                {
                    foreach (var (count, done) in _armWaits)
                    {
                        if (armed >= count)
                            done.TrySetResult();
                    }
                }
                return Volatile.Read(ref _epoch);
            }
        }

        public void OnPoolWait() => OnWait?.Invoke();

        /// <summary>
        /// Completes once the guard has read the epoch <paramref name="count"/> times: once as it
        /// arms each interval, and once more as an interval ends while unsuspended.
        /// </summary>
        public Task EpochReads(int count)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (Volatile.Read(ref _armed) >= count)
                    done.TrySetResult();
                else
                    _armWaits.Add((count, done));
            }
            return done.Task;
        }
    }

    /// <summary>Records warnings, and raises each one to a waiting test.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly object _gate = new();
        private TaskCompletionSource<string> _next = New();
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        /// <summary>Completes with the next warning logged after this is read.</summary>
        public Task<string> NextWarning
        {
            get
            {
                lock (_gate)
                    return _next.Task;
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel != LogLevel.Warning)
                return;

            Interlocked.Increment(ref _warnings);
            TaskCompletionSource<string> next;
            lock (_gate)
            {
                next = _next;
                _next = New();
            }
            next.TrySetResult(formatter(state, exception));
        }

        private static TaskCompletionSource<string> New() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
