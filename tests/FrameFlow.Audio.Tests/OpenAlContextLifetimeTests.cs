// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Audio.OpenAL;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// The shared OpenAL device's lifetime decisions (ADR-0058, #633), with no device: leasing,
/// release, and the two steps of an exit.
/// </summary>
public sealed class OpenAlContextLifetimeTests
{
    [Fact]
    public void TheFirstLeaseOpens_TheNextShares_AndTheLastReleaseCloses()
    {
        var (afterFirst, first) = OpenAlContextLifetime.Initial.Acquire();
        Assert.Equal(ContextAcquire.Open, first);
        var open = afterFirst.Opened();
        Assert.Equal(1, open.Leases);

        var (shared, second) = open.Acquire();
        Assert.Equal(ContextAcquire.Share, second);
        Assert.Equal(2, shared.Leases);

        var (one, closeAtOne) = shared.Release();
        Assert.False(closeAtOne);
        Assert.Equal(1, one.Leases);

        var (none, closeAtNone) = one.Release();
        Assert.True(closeAtNone);
        Assert.False(none.Open);
        Assert.Equal(0, none.Leases);
    }

    [Fact]
    public void AFailedOpen_ChangesNothing()
    {
        // Open is a request to try. Until Opened, nothing is open and nothing is leased.
        var (next, action) = OpenAlContextLifetime.Initial.Acquire();

        Assert.Equal(ContextAcquire.Open, action);
        Assert.Equal(OpenAlContextLifetime.Initial, next);
    }

    [Fact]
    public void AReleaseWithNothingOpen_ClosesNothing()
    {
        var (next, close) = OpenAlContextLifetime.Initial.Release();

        Assert.False(close);
        Assert.Equal(OpenAlContextLifetime.Initial, next);
    }

    [Fact]
    public void Exiting_RefusesEveryLaterLease()
    {
        var open = OpenAlContextLifetime.Initial.Acquire().Next.Opened();

        var (_, whileOpen) = open.BeginExit().Acquire();
        var (_, whenClosed) = OpenAlContextLifetime.Initial.BeginExit().Acquire();

        // Nothing reopens the device behind the shutdown, open or not.
        Assert.Equal(ContextAcquire.Refuse, whileOpen);
        Assert.Equal(ContextAcquire.Refuse, whenClosed);
    }

    [Fact]
    public void Exiting_ReleasesStillCountDown_AndTheLastStillCloses()
    {
        // The holders give their leases back one by one during an exit.
        var twoLeases = OpenAlContextLifetime.Initial.Acquire().Next.Opened().Acquire().Next;
        var exiting = twoLeases.BeginExit();

        var (one, first) = exiting.Release();
        var (none, second) = one.Release();

        Assert.False(first);
        Assert.True(second);
        Assert.False(none.Open);
        Assert.True(none.Exiting);
    }

    [Fact]
    public void Abandon_ClosesWhateverIsStillLeased()
    {
        var threeLeases = OpenAlContextLifetime.Initial
            .Acquire().Next.Opened()
            .Acquire().Next
            .Acquire().Next;

        var (next, close) = threeLeases.BeginExit().Abandon();

        // The lease count is not a reason to keep the device open into the runtime's shutdown.
        Assert.True(close);
        Assert.False(next.Open);
        Assert.Equal(0, next.Leases);
    }

    [Fact]
    public void Abandon_WithNothingOpen_ClosesNothing()
    {
        var (next, close) = OpenAlContextLifetime.Initial.BeginExit().Abandon();

        Assert.False(close);
        Assert.True(next.Exiting);
    }

    [Fact]
    public void AReleaseAfterAbandon_ClosesNothingTwice()
    {
        // A sink disposed after the exit closed the device must not close it again.
        var abandoned = OpenAlContextLifetime.Initial.Acquire().Next.Opened().BeginExit().Abandon().Next;

        var (_, close) = abandoned.Release();

        Assert.False(close);
    }
}
