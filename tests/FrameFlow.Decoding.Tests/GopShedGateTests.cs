using FrameFlow.Decoding.Internal;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Pins <see cref="GopShedGate"/>, which holds the packet stream shut from the first shed
/// packet to the next keyframe (issue #134).
/// </summary>
/// <remarks>
/// The property that matters is that no packet is ever admitted between a shed and the
/// keyframe that follows it. Admitting one hands the decoder a frame predicted from data it
/// never received; FFmpeg reconstructs it without erroring, so the failure is visible only as
/// a wrong picture and never as a diagnostic.
/// </remarks>
public sealed class GopShedGateTests
{
    /// <summary>Feeds a stream of packets and returns which were admitted.</summary>
    /// <param name="stream">
    /// One character per packet: 'K' keyframe, 'p' inter-coded, 'x' inter-coded that the
    /// queue then rejects (the send sheds it).
    /// </param>
    private static string Run(string stream)
    {
        var state = GopShedState.Intact;
        var admitted = new System.Text.StringBuilder();

        foreach (var c in stream)
        {
            var (next, admission) = GopShedGate.Offer(state, isKeyframe: c == 'K');
            if (admission == PacketAdmission.Shed)
                continue;

            state = next;
            if (c == 'x')
            {
                // The queue was full: the packet the gate admitted is freed anyway.
                state = GopShedGate.AfterShed(state);
                continue;
            }

            admitted.Append(c);
        }

        return admitted.ToString();
    }

    [Fact]
    public void AnIntactStreamAdmitsEveryPacket()
    {
        Assert.Equal("KppppKpppp", Run("KppppKpppp"));
    }

    [Fact]
    public void EverythingAfterAShedIsHeldUntilTheNextKeyframe()
    {
        // The 'x' is the shed. The three p's after it predict from it, so none may pass.
        Assert.Equal("KppKpp", Run("Kppxppp" + "Kpp"));
    }

    [Fact]
    public void TheKeyframeThatReopensTheGateIsItselfAdmitted()
    {
        var (next, admission) = GopShedGate.Offer(
            GopShedGate.AfterShed(GopShedState.Intact),
            isKeyframe: true
        );

        Assert.Equal(PacketAdmission.Admit, admission);
        Assert.False(next.AwaitingKeyframe);
    }

    [Fact]
    public void AKeyframeDroppedByAFullQueueReArmsTheGate()
    {
        var state = GopShedGate.AfterShed(GopShedState.Intact);

        var (afterKeyframe, admission) = GopShedGate.Offer(state, isKeyframe: true);
        Assert.Equal(PacketAdmission.Admit, admission);

        // ...but the queue was full, so the send freed it.
        var reArmed = GopShedGate.AfterShed(afterKeyframe);
        Assert.True(reArmed.AwaitingKeyframe);

        Assert.Equal(PacketAdmission.Shed, GopShedGate.Offer(reArmed, isKeyframe: false).Admission);
    }

    [Fact]
    public void ShedIsNeverReportedWhileTheChainIsIntact()
    {
        var (next, admission) = GopShedGate.Offer(GopShedState.Intact, isKeyframe: false);

        Assert.Equal(PacketAdmission.Admit, admission);
        Assert.False(next.AwaitingKeyframe);
    }

    [Fact]
    public void AResetThatDiscardedPacketsLeavesTheStreamAwaitingAKeyframe()
    {
        // The case the gate would otherwise miss: a keyframe is admitted and opens the
        // gate, then the reset frees it before the decoder ever sees it.
        var (afterKeyframe, admission) = GopShedGate.Offer(
            GopShedGate.AfterShed(GopShedState.Intact),
            isKeyframe: true
        );
        Assert.Equal(PacketAdmission.Admit, admission);
        Assert.False(afterKeyframe.AwaitingKeyframe);

        var afterReset = GopShedGate.AfterReset(afterKeyframe, discardedPackets: true);

        Assert.True(afterReset.AwaitingKeyframe);
        Assert.Equal(
            PacketAdmission.Shed,
            GopShedGate.Offer(afterReset, isKeyframe: false).Admission
        );
    }

    [Fact]
    public void AResetThatDiscardedNothingLeavesAnOpenGateOpen()
    {
        // Arming on every reset would cost the first GOP after every seek for no reason.
        var afterReset = GopShedGate.AfterReset(GopShedState.Intact, discardedPackets: false);

        Assert.False(afterReset.AwaitingKeyframe);
        Assert.Equal(
            PacketAdmission.Admit,
            GopShedGate.Offer(afterReset, isKeyframe: false).Admission
        );
    }

    [Fact]
    public void AResetThatDiscardedNothingDoesNotDisarmAnArmedGate()
    {
        var armed = GopShedGate.AfterShed(GopShedState.Intact);

        Assert.True(GopShedGate.AfterReset(armed, discardedPackets: false).AwaitingKeyframe);
        Assert.True(GopShedGate.AfterReset(armed, discardedPackets: true).AwaitingKeyframe);
    }

    [Fact]
    public void ConsecutiveShedsDoNotStackOrDecay()
    {
        var state = GopShedGate.AfterShed(GopShedGate.AfterShed(GopShedState.Intact));

        Assert.True(state.AwaitingKeyframe);
        Assert.Equal(PacketAdmission.Shed, GopShedGate.Offer(state, isKeyframe: false).Admission);
        Assert.Equal(PacketAdmission.Admit, GopShedGate.Offer(state, isKeyframe: true).Admission);
    }
}
