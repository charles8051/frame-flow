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
    public async Task ATeardownThatThrowsBeforeItsFirstWait_IsTheOutcomeOfEveryCall()
    {
        var once = new DisposeOnce();
        var thrown = new InvalidOperationException("thrown at once");

        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => once.RunAsync(() => throw thrown).AsTask()));
        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => once.RunAsync(() => ValueTask.CompletedTask).AsTask()));
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

    private static ValueTask Count(ref int runs)
    {
        runs++;
        return ValueTask.CompletedTask;
    }
}
