using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Encoding.Tests;

/// <summary>ADR-0080's counting and disposing rule, for the Encoding assembly's items.</summary>
[Collection(RefCountingCollection.Name)]
public sealed class RefCountingRuleTests
{
    [Fact]
    public void EncodedPacket_FollowsTheRule()
    {
        RefCountConformance.AssertFollowsTheRule(
            () =>
            {
                var packet = new EncodedPacket(
                    new byte[4],
                    pts: 0,
                    dts: 0,
                    duration: 1,
                    timeBaseNumerator: 1,
                    timeBaseDenominator: 30,
                    isKeyFrame: true
                );
                return (packet, (Func<int>?)null);
            },
            packet => packet.AddRef()
        );
    }
}
