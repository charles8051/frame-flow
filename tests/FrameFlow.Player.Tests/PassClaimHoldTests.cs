// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Player.Core;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins when a pass lets go of the claims on its sinks (ADR-0085, decision 6): once it is disposed
/// and no run is in flight, whichever comes last, and once only. A dispose that races a run is one
/// of these orders, so each is driven as a value.
/// </summary>
public sealed class PassClaimHoldTests
{
    [Fact]
    public void ADisposeWithNoRunStarted_Releases()
    {
        var (_, release) = PassClaimHold.Built.Dispose();

        Assert.True(release);
    }

    [Fact]
    public void ARunThatEnds_ReleasesNothing_AndTheDisposeAfterItReleases()
    {
        var (running, started) = PassClaimHold.Built.StartRun();
        var (ended, releasedAtEnd) = running.EndRun();
        var (_, releasedAtDispose) = ended.Dispose();

        Assert.True(started);
        Assert.False(releasedAtEnd);
        Assert.True(releasedAtDispose);
    }

    [Fact]
    public void ADisposeDuringARun_LeavesTheReleaseToTheRunsEnd()
    {
        // The pass's dispose does not wait for a run in flight, and what that run still sends would
        // otherwise reach a sink the next pass may have claimed.
        var (running, _) = PassClaimHold.Built.StartRun();
        var (disposed, releasedAtDispose) = running.Dispose();
        var (_, releasedAtEnd) = disposed.EndRun();

        Assert.False(releasedAtDispose);
        Assert.True(releasedAtEnd);
    }

    [Fact]
    public void ARun_CannotStartOnceThePassIsDisposed()
    {
        var (disposed, _) = PassClaimHold.Built.Dispose();

        var (next, started) = disposed.StartRun();

        Assert.False(started);
        Assert.Equal(disposed, next);
    }

    [Fact]
    public void TheClaimsAreReleasedOnce()
    {
        var (disposed, _) = PassClaimHold.Built.Dispose();

        Assert.False(disposed.Dispose().Release);
        Assert.False(disposed.EndRun().Release);
    }
}
