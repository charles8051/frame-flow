using FrameFlow.Playback;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The rule <see cref="PlaylistSession"/> uses to decide when to stop skipping failed items
/// (#180), as <see cref="PlaylistQueue"/> applies it. The end-to-end cases, over real playback,
/// are in the integration suite's <c>PlaylistFaultTests</c>.
/// </summary>
public sealed class PlaylistFailureGuardTests
{
    private static readonly TimeSpan Early = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan LongItem = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Unknown = TimeSpan.Zero;

    private PlaylistQueue _queue = PlaylistQueue.Create([], RepeatMode.Off);

    private int ConsecutiveFailures => _queue.ConsecutiveFailures;

    private bool ItemFailed(TimeSpan playedFor, TimeSpan itemLength)
    {
        (_queue, var giveUp) = _queue.ItemFailed(playedFor, itemLength);
        return giveUp;
    }

    private void ItemEnded() => _queue = _queue.ItemEnded();

    [Fact]
    public void GivesUp_OnTheFailureAfterTheLimit()
    {

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(ItemFailed(Early, LongItem), $"Gave up on failure {i + 1}.");

        Assert.True(ItemFailed(Early, LongItem));
    }

    [Fact]
    public void ItemsThatNeverStarted_Count()
    {

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(ItemFailed(TimeSpan.Zero, Unknown));

        Assert.True(ItemFailed(TimeSpan.Zero, Unknown));
    }

    [Fact]
    public void AnItemThatEnds_ResetsTheCount()
    {
        // A skip and a natural end are the same call. A rotation advanced by skips, with a
        // bad item in it, must not accumulate the bad item's failures across passes.

        for (var pass = 0; pass < 3 * PlaylistFailureGuard.MaxConsecutiveFailures; pass++)
        {
            Assert.False(ItemFailed(Early, LongItem), $"Gave up on pass {pass + 1}.");
            ItemEnded();
        }

        Assert.Equal(0, ConsecutiveFailures);
    }

    [Fact]
    public void AFaultAfterTheThreshold_DoesNotCount()
    {
        // A source with no end, faulting once in a long while, must not be given up on.

        for (var i = 0; i < 3 * PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(ItemFailed(PlaylistFailureGuard.ProgressThreshold, Unknown));

        Assert.Equal(0, ConsecutiveFailures);
    }

    [Fact]
    public void AFaultAfterProgress_ClearsTheFailuresBeforeIt()
    {
        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            ItemFailed(Early, LongItem);

        Assert.False(ItemFailed(PlaylistFailureGuard.ProgressThreshold, LongItem));

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(ItemFailed(Early, LongItem));
        Assert.True(ItemFailed(Early, LongItem));
    }

    [Fact]
    public void AFaultJustShortOfTheThreshold_Counts()
    {
        var justShort = PlaylistFailureGuard.ProgressThreshold - TimeSpan.FromTicks(1);

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            ItemFailed(justShort, LongItem);

        Assert.True(ItemFailed(justShort, LongItem));
    }

    [Fact]
    public void AShortClipThatFaultsNearItsEnd_IsNotGivenUpOn()
    {
        // A three-second clip that plays most of itself and faults near the end on every
        // pass never reaches five seconds. Half its length is progress.
        var clip = TimeSpan.FromSeconds(3);

        for (var pass = 0; pass < 3 * PlaylistFailureGuard.MaxConsecutiveFailures; pass++)
        {
            Assert.False(
                ItemFailed(TimeSpan.FromSeconds(2.8), clip),
                $"Gave up on pass {pass + 1}."
            );
        }
    }

    [Fact]
    public void AShortClipThatFaultsInItsFirstHalf_Counts()
    {
        var clip = TimeSpan.FromSeconds(3);
        var firstHalf = TimeSpan.FromSeconds(1.4);

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(ItemFailed(firstHalf, clip));

        Assert.True(ItemFailed(firstHalf, clip));
    }

    [Theory]
    [InlineData(0, 5)] // unknown length: the full threshold
    [InlineData(3, 1.5)] // shorter than twice the threshold: half its length
    [InlineData(10, 5)] // exactly twice the threshold
    [InlineData(60, 5)]
    public void ProgressNeeded_IsTheThreshold_OrHalfTheLengthWhenShorter(
        double lengthSeconds,
        double neededSeconds
    )
    {
        Assert.Equal(
            TimeSpan.FromSeconds(neededSeconds),
            PlaylistFailureGuard.ProgressNeeded(TimeSpan.FromSeconds(lengthSeconds))
        );
    }
}
