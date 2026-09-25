using FrameFlow.Tests.Shared;
using Xunit;

namespace FrameFlow.Whisper.Tests;

/// <summary>ADR-0080's counting and disposing rule, for the Whisper assembly's items.</summary>
[Collection(RefCountingCollection.Name)]
public sealed class RefCountingRuleTests
{
    [Fact]
    public void CaptionRef_FollowsTheRule()
    {
        RefCountConformance.AssertFollowsTheRule(
            () =>
            {
                var caption = new CaptionRef(
                    new Caption(TimeSpan.Zero, TimeSpan.FromSeconds(1), "hello")
                );
                return (caption, (Func<int>?)null);
            },
            caption => caption.AddRef()
        );
    }
}
