using FrameFlow.Inference.Core;
using Xunit;

namespace FrameFlow.Inference.Abstractions.Tests;

/// <summary>
/// Disposing a session during a run frees nothing until the run ends (#432). Pure transitions.
/// </summary>
public sealed class SessionUseTests
{
    [Fact]
    public void DisposedWithNoRun_ItIsFreedAtOnce()
    {
        var (state, release) = SessionUse.Dispose(default);

        Assert.True(release);
        Assert.True(state.Released);
    }

    [Fact]
    public void DisposedDuringARun_ItIsFreedWhenTheRunEnds()
    {
        var (running, _) = SessionUse.Enter(default);

        var (disposed, releaseOnDispose) = SessionUse.Dispose(running);
        var (ended, releaseOnExit) = SessionUse.Exit(disposed);

        Assert.False(releaseOnDispose);
        Assert.True(releaseOnExit);
        Assert.True(ended.Released);
    }

    [Fact]
    public void WithTwoRuns_TheLastToEndFreesIt()
    {
        var state = SessionUse.Enter(SessionUse.Enter(default).State).State;
        state = SessionUse.Dispose(state).State;

        var (afterFirst, releaseFirst) = SessionUse.Exit(state);
        var (_, releaseSecond) = SessionUse.Exit(afterFirst);

        Assert.False(releaseFirst);
        Assert.True(releaseSecond);
    }

    [Fact]
    public void ARunAfterDispose_IsRefused()
    {
        var (_, entered) = SessionUse.Enter(SessionUse.Dispose(default).State);

        Assert.False(entered);
    }

    [Fact]
    public void ItIsFreedOnce()
    {
        var (freed, _) = SessionUse.Dispose(default);

        Assert.False(SessionUse.Dispose(freed).Release);
        Assert.False(SessionUse.Exit(freed).Release);
    }
}
