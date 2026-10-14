// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Pins <see cref="WorkerBinding{TWorker}"/>, the position ticker's binding, under ADR-0086
/// decision 3: a stop cancels the worker and waits for its loop with no bound, says once when the
/// worker has not ended, and disposes the worker only after its loop has ended; a stop made while
/// another runs waits for it and ends as it did; and a start is refused until the stop has ended.
/// </summary>
/// <remarks>
/// A worker that ignores its token holds its loop until the test releases it, so a stop cannot end
/// before that. The warning's interval runs on a <see cref="FakeTimeProvider"/>, whose timers fire
/// inside <see cref="FakeTimeProvider.Advance"/>. Every wait is on a signal the worker raises; the
/// bound only limits a failure.
/// </remarks>
public sealed class WorkerBindingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AStart_CompletesOnlyOnceTheWorkerHasStartedItsLoop()
    {
        // The worker's start runs on the thread pool. What it sets up before its first wait, the
        // ticker's timer for one, has to be in place when the caller goes on: a test that advances
        // a fake clock right after a play otherwise advances it before the timer exists, and the
        // tick never comes. The worker's start blocks here until the test lets it return, so the
        // binding's start cannot complete before that.
        var workers = new Workers { BlockStart = true };
        var binding = new WorkerBinding<HeldWorker>(workers.Create);

        var start = binding.StartAsync();
        var worker = workers.Single();
        await worker.Started.WaitAsync(Bound);

        Assert.False(start.IsCompleted, "The start completed before the worker's own start had returned.");
        worker.UnblockStart();
        await start.WaitAsync(Bound);

        worker.Release();
        await binding.StopAsync().WaitAsync(Bound);
    }

    [Fact]
    public async Task AStop_CancelsTheWorker_WaitsForItsLoop_ThenDisposesIt()
    {
        var workers = new Workers();
        var binding = new WorkerBinding<HeldWorker>(workers.Create);

        await binding.StartAsync();
        var worker = workers.Single();
        await worker.Started.WaitAsync(Bound);

        await binding.StopAsync().WaitAsync(Bound);

        Assert.True(worker.LoopEnded);
        Assert.Equal(1, worker.Stops);
        Assert.Equal(1, worker.Disposals);
        Assert.False(worker.DisposedWhileLooping);
    }

    [Fact]
    public async Task AStop_WaitsForAWorkerThatIgnoresItsToken_WithNoBound_AndSaysSoOnce()
    {
        // The binding used to wait five seconds, then dispose the worker under its running loop and
        // forget the loop.
        var time = new FakeTimeProvider();
        var logger = new WarningLog();
        var workers = new Workers { IgnoreToken = true };
        var binding = new WorkerBinding<HeldWorker>(workers.Create, warningInterval: Interval, timeProvider: time, logger: logger);
        await binding.StartAsync();
        var worker = workers.Single();
        await worker.Started.WaitAsync(Bound);

        var stop = binding.StopAsync();

        time.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.Empty(logger.Warnings);
        time.Advance(TimeSpan.FromTicks(1));
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(nameof(HeldWorker), warning, StringComparison.Ordinal);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Single(logger.Warnings);

        // However long it has waited, the loop is still running, so nothing has been disposed.
        Assert.False(stop.IsCompleted);
        Assert.Equal(0, worker.Disposals);

        worker.Release();
        await stop.WaitAsync(Bound);
        Assert.Equal(1, worker.Disposals);
        Assert.False(worker.DisposedWhileLooping);
    }

    [Fact]
    public async Task ASecondStop_MadeWhileTheFirstRuns_WaitsForIt_AndEndsAsItDid()
    {
        var failure = new InvalidOperationException("the worker's dispose failed");
        var workers = new Workers { IgnoreToken = true, DisposeThrows = failure };
        var binding = new WorkerBinding<HeldWorker>(workers.Create);
        await binding.StartAsync();
        var worker = workers.Single();
        await worker.Started.WaitAsync(Bound);

        var first = binding.StopAsync();
        var second = binding.StopAsync();

        // The loop cannot end before it is released, so neither stop can.
        Assert.False(second.IsCompleted);

        worker.Release();
        var thrownByFirst = await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Bound));
        var thrownBySecond = await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Bound));
        Assert.Same(failure, thrownByFirst);
        Assert.Same(failure, thrownBySecond);
        Assert.Equal(1, worker.Stops);
        Assert.Equal(1, worker.Disposals);
    }

    [Fact]
    public async Task AStartWhileAStopRuns_IsRefused_AndAStartAfterIt_RunsAFreshWorker()
    {
        var workers = new Workers { IgnoreToken = true };
        var binding = new WorkerBinding<HeldWorker>(workers.Create);
        await binding.StartAsync();
        var first = workers.Single();
        await first.Started.WaitAsync(Bound);

        var stop = binding.StopAsync();
        await binding.StartAsync();
        Assert.Equal(1, workers.Count);

        first.Release();
        await stop.WaitAsync(Bound);
        await binding.StartAsync();

        Assert.Equal(2, workers.Count);
        var second = workers.Last();
        await second.Started.WaitAsync(Bound);
        second.Release();
        await binding.StopAsync().WaitAsync(Bound);
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public async Task AStopThatThrows_LeavesTheBindingIdle_SoAStartRunsAFreshWorker()
    {
        var workers = new Workers { DisposeThrows = new InvalidOperationException("dispose") };
        var binding = new WorkerBinding<HeldWorker>(workers.Create);
        await binding.StartAsync();
        await workers.Single().Started.WaitAsync(Bound);

        await Assert.ThrowsAsync<InvalidOperationException>(() => binding.StopAsync().WaitAsync(Bound));
        await binding.StartAsync();

        Assert.Equal(2, workers.Count);
    }

    [Fact]
    public async Task AWorkersFault_GoesToOnError_AndOneOnErrorThrows_IsThrownByTheStop()
    {
        // A fault the loop ends with is reported, not dropped (ADR-0086, decision 5): to onError,
        // and what onError throws, by the stop.
        var fault = new InvalidOperationException("the loop faulted");
        var reportFailure = new InvalidOperationException("reporting it failed");
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Workers { LoopThrows = fault };
        var binding = new WorkerBinding<HeldWorker>(
            workers.Create,
            onError: (_, ex) =>
            {
                reported.TrySetResult(ex);
                throw reportFailure;
            }
        );

        await binding.StartAsync();
        Assert.Same(fault, await reported.Task.WaitAsync(Bound));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => binding.StopAsync().WaitAsync(Bound));
        Assert.Same(reportFailure, thrown);
        Assert.Equal(1, workers.Single().Disposals);
    }

    /// <summary>Creates the workers the binding asks for, configured alike, and keeps them.</summary>
    private sealed class Workers
    {
        private readonly List<HeldWorker> _created = [];

        public bool IgnoreToken { get; init; }

        public Exception? DisposeThrows { get; init; }

        public Exception? LoopThrows { get; init; }

        public bool BlockStart { get; init; }

        public int Count
        {
            get
            {
                lock (_created)
                    return _created.Count;
            }
        }

        public HeldWorker Create()
        {
            var worker = new HeldWorker(IgnoreToken, DisposeThrows, LoopThrows, BlockStart);
            lock (_created)
                _created.Add(worker);
            return worker;
        }

        public HeldWorker Single()
        {
            lock (_created)
                return Assert.Single(_created);
        }

        public HeldWorker Last()
        {
            lock (_created)
                return _created[^1];
        }
    }

    /// <summary>
    /// A worker whose loop runs until its token is cancelled or, when it ignores its token, until
    /// <see cref="Release"/>. Its start, when it blocks, holds the calling thread until
    /// <see cref="UnblockStart"/>, as a start that sets something up before its first wait does.
    /// Records its stops and disposals, and whether it was disposed while its loop still ran.
    /// </summary>
    private sealed class HeldWorker(bool ignoreToken, Exception? disposeThrows, Exception? loopThrows, bool blockStart)
        : IStateBoundWorker
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _unblocked = new(initialState: !blockStart);
        private int _loopEnded;
        private int _stops;
        private int _disposals;
        private int _disposedWhileLooping;

        public Task Started => _started.Task;

        public bool LoopEnded => Volatile.Read(ref _loopEnded) != 0;

        public int Stops => Volatile.Read(ref _stops);

        public int Disposals => Volatile.Read(ref _disposals);

        public bool DisposedWhileLooping => Volatile.Read(ref _disposedWhileLooping) != 0;

        public void Release() => _released.TrySetResult();

        public void UnblockStart() => _unblocked.Set();

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _started.TrySetResult();
                _unblocked.Wait();
                if (loopThrows is not null)
                    throw loopThrows;
                if (ignoreToken)
                    await _released.Task.ConfigureAwait(false);
                else
                    await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _loopEnded, 1);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stops);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            if (!LoopEnded)
                Volatile.Write(ref _disposedWhileLooping, 1);
            return disposeThrows is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeThrows);
        }
    }

    /// <summary>Keeps what is logged at Warning.</summary>
    private sealed class WarningLog : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                    return [.. _warnings];
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
            lock (_warnings)
                _warnings.Add(formatter(state, exception));
        }
    }
}
