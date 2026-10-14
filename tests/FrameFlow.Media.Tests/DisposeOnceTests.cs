// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Time.Testing;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins ADR-0086 decision 3 on the disposal helpers: the first dispose runs the teardown, and every
/// other one, made while it runs or after, waits for it and ends as it did, success or the same
/// exception (3.1); and a wait that goes on past its interval says so once (3.3). Each teardown
/// here is held on a signal the test releases, and the interval runs on a
/// <see cref="FakeTimeProvider"/>, whose timers fire inside <see cref="FakeTimeProvider.Advance"/>.
/// </summary>
public sealed class DisposeOnceTests
{
    // ─── DisposeOnce ────────────────────────────────────────────────

    [Fact]
    public async Task TheFirstCallRunsTheTeardown_AndALaterCallDoesNotRunItAgain()
    {
        var once = new DisposeOnce();
        var runs = 0;

        await once.RunAsync(() => Count(ref runs));
        await once.RunAsync(() => Count(ref runs));

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ACallMadeWhileTheTeardownRuns_WaitsForIt_WithoutRunningItsOwn()
    {
        var once = new DisposeOnce();
        var entered = Signal();
        var release = Signal();
        var secondRan = false;

        var first = once.RunAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
        }).AsTask();
        await entered.Task.WaitAsync(Bound);
        var second = once.RunAsync(() =>
        {
            secondRan = true;
            return ValueTask.CompletedTask;
        }).AsTask();

        // The teardown cannot end before it is released, so neither can a call that waits for it.
        Assert.False(second.IsCompleted);

        release.SetResult();
        await first.WaitAsync(Bound);
        await second.WaitAsync(Bound);
        Assert.False(secondRan);
    }

    [Fact]
    public async Task EveryCall_ThrowsWhatTheFirstsTeardownThrew_TheSameException()
    {
        var once = new DisposeOnce();
        var entered = Signal();
        var release = Signal();
        var thrown = new InvalidOperationException("the teardown failed");

        var first = once.RunAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
            throw thrown;
        }).AsTask();
        await entered.Task.WaitAsync(Bound);
        var during = once.RunAsync(() => ValueTask.CompletedTask).AsTask();
        release.SetResult();

        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Bound)));
        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => during.WaitAsync(Bound)));
        var after = once.RunAsync(() => ValueTask.CompletedTask).AsTask();
        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => after.WaitAsync(Bound)));
    }

    [Fact]
    public async Task ATeardownThatThrowsBeforeItReturnsItsTask_StillReleasesEveryLaterCall()
    {
        // RunAsync is the form that takes a teardown which can throw synchronously.
        var once = new DisposeOnce();
        var thrown = new InvalidOperationException("thrown at once");

        var first = once.RunAsync(() => throw thrown).AsTask();
        var later = once.RunAsync(() => ValueTask.CompletedTask).AsTask();

        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Bound)));
        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => later.WaitAsync(Bound)));
    }

    [Fact]
    public async Task TheDirectForm_RunsTheTeardownOnce_AndEveryOtherCallWaitsAndEndsAsItDid()
    {
        var once = new DisposeOnce();
        var entered = Signal();
        var release = Signal();
        var thrown = new InvalidOperationException("the teardown failed");
        var runs = 0;

        async ValueTask TearDownAsync()
        {
            runs++;
            entered.SetResult();
            await release.Task;
            throw thrown;
        }

        ValueTask DisposeAsync() => once.TryBegin() ? once.EndAsync(TearDownAsync()) : once.WaitAsync();

        var first = DisposeAsync().AsTask();
        await entered.Task.WaitAsync(Bound);
        var second = DisposeAsync().AsTask();
        Assert.False(second.IsCompleted);

        release.SetResult();
        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Bound)));
        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Bound)));
        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => DisposeAsync().AsTask().WaitAsync(Bound)));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task TheDirectForm_AnAsyncTeardownThatThrowsAtOnce_ReachesEveryLaterCall()
    {
        // An async method puts even its first throw in its task, which is why the direct form needs one.
        var once = new DisposeOnce();
        var thrown = new InvalidOperationException("thrown at once");

#pragma warning disable CS1998 // the point is an async method that throws before any wait
        async ValueTask TearDownAsync() => throw thrown;
#pragma warning restore CS1998

        Assert.True(once.TryBegin());
        var first = once.EndAsync(TearDownAsync()).AsTask();
        Assert.False(once.TryBegin());
        var later = once.WaitAsync().AsTask();

        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Bound)));
        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => later.WaitAsync(Bound)));
    }

    [Fact]
    public async Task EndAsync_WithoutABegin_Throws()
    {
        var once = new DisposeOnce();

        await Assert.ThrowsAsync<InvalidOperationException>(() => once.EndAsync(ValueTask.CompletedTask).AsTask());
    }

    [Fact]
    public async Task HasBegun_IsSetAsTheFirstCallBegins()
    {
        var once = new DisposeOnce();
        bool? duringTeardown = null;

        Assert.False(once.HasBegun);
        await once.RunAsync(() =>
        {
            duringTeardown = once.HasBegun;
            return ValueTask.CompletedTask;
        });

        Assert.True(duringTeardown);
        Assert.True(once.HasBegun);
    }

    // ─── SlowWorkWarning ────────────────────────────────────────────

    [Fact]
    public async Task AWaitForWorkThatHasNotEnded_WarnsOnce_AtTheInterval()
    {
        var time = new FakeTimeProvider();
        var work = Signal();
        var warnings = 0;

        var waiting = SlowWorkWarning.AwaitAsync(work.Task, Interval, time, () => warnings++);

        time.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.Equal(0, warnings);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, warnings);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, warnings);

        work.SetResult();
        await waiting.WaitAsync(Bound);
    }

    [Fact]
    public async Task AWaitForWorkThatEndsInTime_SaysNothing()
    {
        var time = new FakeTimeProvider();
        var work = Signal();
        var warnings = 0;

        var waiting = SlowWorkWarning.AwaitAsync(work.Task, Interval, time, () => warnings++);
        work.SetResult();
        await waiting.WaitAsync(Bound);
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(0, warnings);
    }

    [Fact]
    public async Task AWait_EndsAsTheWorkEnded()
    {
        var thrown = new InvalidOperationException("the work failed");

        var waiting = SlowWorkWarning.AwaitAsync(Task.FromException(thrown), Interval, new FakeTimeProvider(), () => { });

        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => waiting));
    }

    [Fact]
    public async Task TheWarningsTimer_IsDisposedByTheTimeTheWaitReturns()
    {
        var time = new CapturingTime();
        var work = Signal();

        var waiting = SlowWorkWarning.AwaitAsync(work.Task, Interval, time, () => { });
        work.SetResult();
        await waiting.WaitAsync(Bound);

        Assert.True(time.TimerDisposed);
    }

    [Fact]
    public async Task AWarningThatFiresAfterTheWorkEnded_SaysNothing()
    {
        // The timer can fire as the work ends, before the wait has disposed it.
        var time = new CapturingTime();
        var work = Signal();
        var warnings = 0;

        var waiting = SlowWorkWarning.AwaitAsync(work.Task, Interval, time, () => warnings++);
        work.SetResult();
        await waiting.WaitAsync(Bound);
        time.Fire();

        Assert.Equal(0, warnings);
    }

    [Fact]
    public async Task AWarningThatThrows_IsDropped_AndTheWaitGoesOn()
    {
        var time = new FakeTimeProvider();
        var work = Signal();

        var waiting = SlowWorkWarning.AwaitAsync(work.Task, Interval, time, () => throw new InvalidOperationException());
        time.Advance(Interval);
        work.SetResult();

        await waiting.WaitAsync(Bound);
    }

    private static readonly TimeSpan Interval = SlowWorkWarning.DefaultInterval;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Keeps the one timer a wait makes, so a test can fire it when it likes and see whether it was
    /// disposed. Nothing fires on its own.
    /// </summary>
    private sealed class CapturingTime : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public bool TimerDisposed { get; private set; }

        public void Fire() => _callback!(_state);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            _callback = callback;
            _state = state;
            return new Timer(this);
        }

        private sealed class Timer(CapturingTime time) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => time.TimerDisposed = true;

            public ValueTask DisposeAsync()
            {
                time.TimerDisposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private static ValueTask Count(ref int runs)
    {
        runs++;
        return ValueTask.CompletedTask;
    }
}
