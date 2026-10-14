// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Player.Core;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins the lifecycle of a pass's run and dispose (ADR-0085, decision 6; #696): a pass runs once
/// and never once its dispose has begun, and its dispose waits for a run in flight before it tears
/// down. A run and a dispose that race take the lock in one order or the other, so each
/// order is driven as a value.
/// </summary>
public sealed class PassLifecycleTests
{
    [Fact]
    public void ADisposeWithNoRunStarted_TearsDownAtOnce()
    {
        var (_, teardown) = PassLifecycle.Built.Dispose();

        Assert.Equal(PassTeardown.Now, teardown);
    }

    [Fact]
    public void ADisposeDuringARun_TearsDownAfterTheRun()
    {
        var (running, start) = PassLifecycle.Built.StartRun();

        var (_, teardown) = running.Dispose();

        Assert.Equal(PassRunStart.Started, start);
        Assert.Equal(PassTeardown.AfterTheRun, teardown);
    }

    [Fact]
    public void ADisposeAfterTheRunEnded_TearsDownAtOnce()
    {
        var (running, _) = PassLifecycle.Built.StartRun();

        var (_, teardown) = running.EndRun().Dispose();

        Assert.Equal(PassTeardown.Now, teardown);
    }

    [Fact]
    public void ARunThatRacesADispose_AndLosesTheLock_IsRefused()
    {
        var (disposed, _) = PassLifecycle.Built.Dispose();

        var (next, start) = disposed.StartRun();

        Assert.Equal(PassRunStart.Disposed, start);
        Assert.Equal(disposed, next);
    }

    [Fact]
    public void ARunThatRacesADispose_AndWinsTheLock_IsTheRunTheDisposeWaitsFor()
    {
        var (running, start) = PassLifecycle.Built.StartRun();
        var (disposed, teardown) = running.Dispose();

        Assert.Equal(PassRunStart.Started, start);
        Assert.Equal(PassTeardown.AfterTheRun, teardown);

        // The run ends after the dispose began, and a run asked for after that is refused.
        Assert.Equal(PassRunStart.Disposed, disposed.EndRun().StartRun().Start);
    }

    [Fact]
    public void APass_RunsOnce()
    {
        var (running, _) = PassLifecycle.Built.StartRun();

        Assert.Equal(PassRunStart.AlreadyRan, running.StartRun().Start);
        Assert.Equal(PassRunStart.AlreadyRan, running.EndRun().StartRun().Start);
    }

    [Fact]
    public void ARunAskedForAfterADispose_IsRefusedAsDisposed_EvenWhenThePassHasRun()
    {
        var (running, _) = PassLifecycle.Built.StartRun();
        var (disposed, _) = running.EndRun().Dispose();

        Assert.Equal(PassRunStart.Disposed, disposed.StartRun().Start);
    }

    [Fact]
    public void ASecondDispose_LeavesTheTeardownToTheFirst()
    {
        var (disposed, _) = PassLifecycle.Built.Dispose();

        var (next, teardown) = disposed.Dispose();

        Assert.Equal(PassTeardown.Begun, teardown);
        Assert.Equal(disposed, next);
    }

    [Fact]
    public void ASecondDispose_DuringTheRun_LeavesTheTeardownToTheFirst()
    {
        var (running, _) = PassLifecycle.Built.StartRun();
        var (disposed, _) = running.Dispose();

        Assert.Equal(PassTeardown.Begun, disposed.Dispose().Teardown);
        Assert.Equal(PassTeardown.Begun, disposed.EndRun().Dispose().Teardown);
    }
}
