using FrameFlow.Graph.Core;
using Xunit;

namespace FrameFlow.Graph.Tests;

/// <summary>
/// <see cref="TimelineFollower"/> (ADR-0084, decision 5): on a stream in the player's order, any id
/// other than the current one starts a new timeline, and <see cref="TimelineId.None"/> on either
/// side is current.
/// </summary>
public sealed class TimelineFollowerTests
{
    private static readonly TimelineId A = TimelineId.Next();
    private static readonly TimelineId B = TimelineId.Next();
    private static readonly TimelineId C = TimelineId.Next();
    private static readonly TimelineId None = TimelineId.None;

    [Fact]
    public void ANewFollower_FollowsNothing()
    {
        Assert.Equal(None, default(TimelineFollower).Current);
        Assert.Equal(default, new TimelineFollower());
    }

    public static TheoryData<string, string, bool, string> Steps => new()
    {
        // following, item, starts a new timeline, following after
        { "none", "none", false, "none" },
        { "none", "a", false, "a" },
        { "a", "none", false, "a" },
        { "a", "a", false, "a" },
        { "a", "b", true, "b" },
    };

    [Theory]
    [MemberData(nameof(Steps))]
    public void EveryStep(string following, string item, bool startsNew, string after)
    {
        var follower = default(TimelineFollower).Follow(Id(following));

        Assert.Equal(startsNew, follower.StartsNewTimeline(Id(item)));
        Assert.Equal(Id(after), follower.Follow(Id(item)).Current);
    }

    [Fact]
    public void AnIdMintedEarlier_StillStartsANewTimeline()
    {
        // An item prepared ahead mints its id before the playing item's later loops do (#674).
        // Followed in the order the player presents them, it is still the next timeline.
        var prepared = TimelineId.Next();
        var firstLoop = TimelineId.Next();
        var secondLoop = TimelineId.Next();

        var follower = default(TimelineFollower).Follow(firstLoop);
        Assert.True(follower.StartsNewTimeline(secondLoop));
        follower = follower.Follow(secondLoop);
        Assert.True(follower.StartsNewTimeline(prepared));
        Assert.Equal(prepared, follower.Follow(prepared).Current);
    }

    [Fact]
    public void ARunThatComesBack_IsANewTimelineAgain()
    {
        // No history: on an in-order stream the timeline left cannot come back, so whatever
        // arrives after another id is new, even an id seen before.
        var follower = default(TimelineFollower).Follow(A).Follow(B);

        Assert.True(follower.StartsNewTimeline(A));
    }

    [Fact]
    public void EverySequenceOfFiveItems_StartsANewTimelineExactlyWhereTheIdChanges()
    {
        // Exhaustive over five items from {none, a, b, c}. The model: skip items on none; an item
        // starts a new timeline when an earlier item named one and the last such differs from it.
        TimelineId[] alphabet = [None, A, B, C];
        foreach (var sequence in Sequences(alphabet, 5))
        {
            var follower = default(TimelineFollower);
            TimelineId? lastNamed = null;
            foreach (var item in sequence)
            {
                var expected = item != None && lastNamed is { } last && last != item;
                Assert.Equal(expected, follower.StartsNewTimeline(item));

                follower = follower.Follow(item);
                if (item != None)
                    lastNamed = item;
                Assert.Equal(lastNamed ?? None, follower.Current);
            }
        }
    }

    [Fact]
    public void FollowingTheCurrentTimeline_ReturnsTheSameFollower()
    {
        var follower = default(TimelineFollower).Follow(A);

        Assert.Equal(follower, follower.Follow(A));
        Assert.Equal(follower, follower.Follow(None));
    }

    private static TimelineId Id(string name) =>
        name switch
        {
            "none" => None,
            "a" => A,
            "b" => B,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };

    internal static IEnumerable<TimelineId[]> Sequences(TimelineId[] alphabet, int length)
    {
        if (length == 0)
        {
            yield return [];
            yield break;
        }

        foreach (var head in Sequences(alphabet, length - 1))
        {
            foreach (var next in alphabet)
                yield return [.. head, next];
        }
    }
}
