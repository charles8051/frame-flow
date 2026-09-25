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
        var clock = new ArmingClock();
        var logger = new RecordingLogger();
        var pool = new DecodePoolGeneration(1, size: 20, budget: 1, logger, "D3D11Va");
        pool.AttachFrame();

        var wait = pool.WaitForRoomAsync(clock, Watchdog, null, CancellationToken.None).AsTask();
        await clock.Armed(1).WaitAsync(FailureBound);

        clock.Advance(Watchdog);
        var report = await logger.NextWarning.WaitAsync(FailureBound);
        Assert.Contains("D3D11Va", report);
        Assert.Contains("budget of 1", report);

        // Another whole interval with no release reports nothing more, and the decoder still
        // waits. The third arming comes after the guard has handled the second interval's end.
        var next = logger.NextWarning;
        await clock.Armed(2).WaitAsync(FailureBound);
        clock.Advance(Watchdog);
        await clock.Armed(3).WaitAsync(FailureBound);

        Assert.False(next.IsCompleted);
        Assert.False(wait.IsCompleted);

        pool.DetachFrame();
        await wait.WaitAsync(FailureBound);
        Assert.Equal(1, logger.Warnings);
        pool.Release();
    }

    [Fact]
    public async Task AWaitWhileTheWatchdogIsSuspended_IsNotReported_UntilAWholeIntervalAfterItResumes()
    {
        // Paused playback: holders keep their frames on purpose, so a long wait is expected.
        var clock = new ArmingClock();
        var logger = new RecordingLogger();
        var pool = new DecodePoolGeneration(1, size: 20, budget: 1, logger, "D3D11Va");
        pool.AttachFrame();
        var waiter = new TestWaiter { PoolWatchdogSuspended = true };
        var warning = logger.NextWarning;

        var wait = pool.WaitForRoomAsync(clock, Watchdog, waiter, CancellationToken.None).AsTask();
        await clock.Armed(1).WaitAsync(FailureBound);

        clock.Advance(Watchdog); // an interval spent suspended
        await clock.Armed(2).WaitAsync(FailureBound);

        waiter.PoolWatchdogSuspended = false;
        clock.Advance(Watchdog); // an interval that saw the resume
        await clock.Armed(3).WaitAsync(FailureBound);
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
        var clock = new ArmingClock();
        var logger = new RecordingLogger();
        var pool = new DecodePoolGeneration(1, size: 20, budget: 1, logger, "D3D11Va");
        pool.AttachFrame();
        var waiter = new TestWaiter();
        var warning = logger.NextWarning;

        var wait = pool.WaitForRoomAsync(clock, Watchdog, waiter, CancellationToken.None).AsTask();
        await clock.Armed(1).WaitAsync(FailureBound);

        clock.Advance(Watchdog - TimeSpan.FromSeconds(1));
        waiter.PoolWatchdogSuspended = true;
        waiter.PoolWatchdogSuspended = false;
        clock.Advance(TimeSpan.FromSeconds(1)); // the interval ends having seen a pause
        await clock.Armed(2).WaitAsync(FailureBound);
        Assert.False(warning.IsCompleted);

        clock.Advance(Watchdog);
        Assert.Contains("budget of 1", await warning.WaitAsync(FailureBound));

        pool.DetachFrame();
        await wait.WaitAsync(FailureBound);
        pool.Release();
    }

    [Fact]
    public async Task APauseWhileTheIntervalIsArmed_DiscardsIt()
    {
        // A pause and resume that both land as the guard arms its first interval.
        var waiter = new TestWaiter();
        var clock = new ArmingClock
        {
            OnArmed = armed =>
            {
                if (armed == 1)
                {
                    waiter.PoolWatchdogSuspended = true;
                    waiter.PoolWatchdogSuspended = false;
                }
            },
        };
        var logger = new RecordingLogger();
        var pool = new DecodePoolGeneration(1, size: 20, budget: 1, logger, "D3D11Va");
        pool.AttachFrame();
        var warning = logger.NextWarning;

        var wait = pool.WaitForRoomAsync(clock, Watchdog, waiter, CancellationToken.None).AsTask();
        await clock.Armed(1).WaitAsync(FailureBound);

        clock.Advance(Watchdog);
        await clock.Armed(2).WaitAsync(FailureBound);
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

    /// <summary>A decoder stand-in: counts waits, and says whether the watchdog is suspended.</summary>
    private sealed class TestWaiter : IPoolWaiter
    {
        private int _suspended;
        private int _epoch;

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

        public int PoolWatchdogEpoch => Volatile.Read(ref _epoch);

        public void OnPoolWait() => OnWait?.Invoke();
    }

    /// <summary>
    /// A fake clock that tells a test when the guard has armed a watchdog interval. Each interval
    /// is one <c>WaitAsync</c>, which creates one timer, and the timer is armed when
    /// <see cref="CreateTimer"/> returns.
    /// </summary>
    private sealed class ArmingClock : TimeProvider
    {
        private readonly FakeTimeProvider _fake = new();
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _waits = [];
        private int _armed;

        /// <summary>Runs as each interval is armed, with the count so far.</summary>
        public Action<int>? OnArmed { get; init; }

        public void Advance(TimeSpan delta) => _fake.Advance(delta);

        public override DateTimeOffset GetUtcNow() => _fake.GetUtcNow();

        public override long GetTimestamp() => _fake.GetTimestamp();

        public override long TimestampFrequency => _fake.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => _fake.LocalTimeZone;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = _fake.CreateTimer(callback, state, dueTime, period);
            int armed = Interlocked.Increment(ref _armed);
            OnArmed?.Invoke(armed);
            lock (_gate)
            {
                foreach (var (count, done) in _waits)
                {
                    if (armed >= count)
                        done.TrySetResult();
                }
            }
            return timer;
        }

        /// <summary>Completes once <paramref name="count"/> intervals have been armed.</summary>
        public Task Armed(int count)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (Volatile.Read(ref _armed) >= count)
                    done.TrySetResult();
                else
                    _waits.Add((count, done));
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
