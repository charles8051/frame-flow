using FrameFlow.Graph.Core;
using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// <see cref="TimelineSteps.Step"/>: whether an item continues the followed timeline, starts a new
/// one, or leaves the consumer on the rule it had before timelines (ADR-0084, decision 5).
/// </summary>
public sealed class TimelineStepTests
{
    private static readonly TimelineId A = TimelineId.Next();
    private static readonly TimelineId B = TimelineId.Next();

    // TimelineStep is internal, so a theory names it.
    [Theory]
    [InlineData("none", "none", nameof(TimelineStep.Untracked))]
    [InlineData("none", "a", nameof(TimelineStep.Untracked))]
    [InlineData("a", "none", nameof(TimelineStep.Untracked))]
    [InlineData("a", "a", nameof(TimelineStep.Continues))]
    [InlineData("a", "b", nameof(TimelineStep.NewTimeline))]
    public void EveryStep(string following, string item, string expected)
    {
        var follower = default(TimelineFollower).Follow(Id(following));

        Assert.Equal(Enum.Parse<TimelineStep>(expected), follower.Step(Id(item)));
    }

    private static TimelineId Id(string name) =>
        name switch
        {
            "a" => A,
            "b" => B,
            _ => TimelineId.None,
        };
}
