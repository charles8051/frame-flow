// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media.Core;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins ADR-0085 decision 6 on the claim gate a sink composes: one claimant at a time, a lease that
/// releases only its own claim, from any thread, and a gate that refuses claims once its sink is
/// disposed. <see cref="SinkClaimState"/> holds every rule and is tested as a value; the gate tests
/// pin the shell around it, the leases and the release hook.
/// </summary>
public sealed class SinkClaimGateTests
{
    private static SinkClaimGate NewGate() => new(typeof(HeadlessVideoSink));

    // ─── The core ───────────────────────────────────────────────────

    [Fact]
    public void State_AFreeSinkIsTaken_WithTheNextClaimNumber()
    {
        var (next, outcome) = SinkClaimState.Open.TryClaim("a");

        Assert.Equal(SinkClaimOutcome.Taken, outcome);
        Assert.Equal(new SinkClaimState(1, "a", Closed: false), next);
        Assert.True(next.Holds(1));
    }

    [Fact]
    public void State_AHeldSinkRefuses_AndKeepsItsHolder()
    {
        var held = SinkClaimState.Open.TryClaim("a").Next;

        var (next, outcome) = held.TryClaim("b");

        Assert.Equal(SinkClaimOutcome.Held, outcome);
        Assert.Equal(held, next);
    }

    [Fact]
    public void State_ReleasingTheHoldingClaim_FreesTheSink_AndTheNextClaimGetsANewNumber()
    {
        var released = SinkClaimState.Open.TryClaim("a").Next.Release(1);

        Assert.False(released.IsHeld);
        var (next, outcome) = released.TryClaim("b");
        Assert.Equal(SinkClaimOutcome.Taken, outcome);
        Assert.Equal(2, next.Claim);
        Assert.False(next.Holds(1));
    }

    [Fact]
    public void State_ReleasingAnEndedClaim_LeavesALaterOneStanding()
    {
        var later = SinkClaimState.Open.TryClaim("a").Next.Release(1).TryClaim("b").Next;

        Assert.Equal(later, later.Release(1));
        Assert.Equal("b", later.Release(1).Claimant);
    }

    [Fact]
    public void State_AClosedSinkRefusesClaims_AndAClaimItHeldStandsUntilReleased()
    {
        var closed = SinkClaimState.Open.TryClaim("a").Next.Close();

        Assert.Equal("a", closed.Claimant);
        Assert.Equal(SinkClaimOutcome.Disposed, closed.TryClaim("b").Outcome);

        var released = closed.Release(1);
        Assert.False(released.IsHeld);
        Assert.True(released.Closed);
        Assert.Equal(SinkClaimOutcome.Disposed, released.TryClaim("b").Outcome);
    }

    // ─── The gate ───────────────────────────────────────────────────

    [Fact]
    public void TryClaim_SucceedsOnce_AndASecondFails_WhileClaimantNamesTheHolder()
    {
        var gate = NewGate();

        using var first = gate.TryClaim("the first player");

        Assert.NotNull(first);
        Assert.Null(gate.TryClaim("the second player"));
        Assert.Equal("the first player", gate.Claimant);
    }

    [Fact]
    public void Release_FreesTheSink_ForTheNextClaim()
    {
        var gate = NewGate();
        var first = gate.TryClaim("the first player")!;

        first.Dispose();

        Assert.Null(gate.Claimant);
        using var second = gate.TryClaim("the second player");
        Assert.NotNull(second);
        Assert.Equal("the second player", gate.Claimant);
    }

    [Fact]
    public void AStaleLease_DisposedAgainAfterALaterClaim_DoesNotReleaseIt()
    {
        var gate = NewGate();
        var first = gate.TryClaim("the first player")!;
        first.Dispose();
        using var second = gate.TryClaim("the second player");

        first.Dispose();

        Assert.Equal("the second player", gate.Claimant);
        Assert.Null(gate.TryClaim("a third player"));
    }

    [Fact]
    public async Task ALease_ReleasedFromAnotherThread_ReleasesTheClaim()
    {
        var gate = NewGate();
        var lease = gate.TryClaim("the first player")!;

        await Task.Run(lease.Dispose);

        Assert.Null(gate.Claimant);
        using var next = gate.TryClaim("the second player");
        Assert.NotNull(next);
    }

    [Fact]
    public void TryClaim_AfterClose_ThrowsObjectDisposed_NamingTheSink()
    {
        var gate = NewGate();
        Assert.True(gate.Close());

        var error = Assert.Throws<ObjectDisposedException>(() => gate.TryClaim("a player"));

        Assert.Equal(typeof(HeadlessVideoSink).FullName, error.ObjectName);
    }

    [Fact]
    public void AClaimHeldWhenTheSinkCloses_StandsUntilItsLeaseIsReleased()
    {
        var gate = NewGate();
        var lease = gate.TryClaim("a player")!;

        gate.Close();

        Assert.Equal("a player", gate.Claimant);
        lease.Dispose();
        Assert.Null(gate.Claimant);
        Assert.Throws<ObjectDisposedException>(() => gate.TryClaim("another player"));
    }

    [Fact]
    public void ASecondClose_DoesNothing()
    {
        var gate = NewGate();

        Assert.True(gate.Close());
        Assert.False(gate.Close());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryClaim_RefusesAClaimantWithNoDescription(string? claimant)
    {
        var gate = NewGate();

        Assert.ThrowsAny<ArgumentException>(() => gate.TryClaim(claimant!));
        Assert.Null(gate.Claimant);
    }

    [Fact]
    public void Order_IsUnique_AndLargerForAGateMadeLater()
    {
        var first = NewGate();
        var second = NewGate();

        Assert.True(second.Order > first.Order);
    }

    // ─── The release hook (ADR-0085, decision 2, for the views) ─────

    [Fact]
    public void TheReleaseHook_RunsBeforeTheGateTakesAnotherClaim()
    {
        // A view's sink drops the frame it kept for a player that is gone in this hook. A player
        // built next must not be able to claim the sink, and present a frame the hook would then
        // drop, until it has returned.
        SinkClaimGate? gate = null;
        IDisposable? claimedDuringHook = null;
        string? claimantDuringHook = null;
        gate = new SinkClaimGate(
            typeof(HeadlessVideoSink),
            released: () =>
            {
                claimantDuringHook = gate!.Claimant;
                claimedDuringHook = gate.TryClaim("the next player");
            }
        );
        var lease = gate.TryClaim("the first player")!;

        lease.Dispose();

        Assert.Equal("the first player", claimantDuringHook);
        Assert.Null(claimedDuringHook);
        using var next = gate.TryClaim("the next player");
        Assert.NotNull(next);
    }

    [Fact]
    public void TheReleaseHook_RunsOncePerClaim_AndNotForALeaseDisposedAgain()
    {
        var released = 0;
        var gate = new SinkClaimGate(typeof(HeadlessVideoSink), released: () => released++);
        var first = gate.TryClaim("the first player")!;
        first.Dispose();
        first.Dispose();
        var second = gate.TryClaim("the second player")!;
        first.Dispose();

        Assert.Equal(1, released);
        second.Dispose();
        Assert.Equal(2, released);
    }

    [Fact]
    public void ALeaseDisposedAgainWhileItsReleaseRuns_DoesNotRunTheHookTwice()
    {
        // The claim stands until the hook returns, so a second dispose that lands inside it, as
        // one from another thread can, would find the claim still holding and release it again.
        IDisposable? lease = null;
        var released = 0;
        var gate = new SinkClaimGate(
            typeof(HeadlessVideoSink),
            released: () =>
            {
                if (++released == 1)
                    lease!.Dispose();
            }
        );
        lease = gate.TryClaim("a player")!;

        lease.Dispose();

        Assert.Equal(1, released);
        Assert.Null(gate.Claimant);
    }

    [Fact]
    public void AThrowingReleaseHook_StillReleasesTheClaim()
    {
        var gate = new SinkClaimGate(
            typeof(HeadlessVideoSink),
            released: () => throw new InvalidOperationException("hook")
        );
        var lease = gate.TryClaim("a player")!;

        Assert.Throws<InvalidOperationException>(lease.Dispose);

        Assert.Null(gate.Claimant);
        Assert.NotNull(gate.TryClaim("another player"));
    }
}
