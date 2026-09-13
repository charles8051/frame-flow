using FrameFlow.Playback;
using Xunit;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The rule <see cref="PlaylistSession"/> uses to decide when to stop skipping failed items
/// (#180). The end-to-end cases, over real playback, are in the integration suite's
/// <c>PlaylistFaultTests</c>.
/// </summary>
public sealed class PlaylistFailureGuardTests
{
    private static readonly TimeSpan Early = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan LongItem = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Unknown = TimeSpan.Zero;

    [Fact]
    public void GivesUp_OnTheFailureAfterTheLimit()
    {
        var guard = new PlaylistFailureGuard();

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(guard.ItemFailed(Early, LongItem), $"Gave up on failure {i + 1}.");

        Assert.True(guard.ItemFailed(Early, LongItem));
    }

    [Fact]
    public void ItemsThatNeverStarted_Count()
    {
        var guard = new PlaylistFailureGuard();

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(guard.ItemFailed(TimeSpan.Zero, Unknown));

        Assert.True(guard.ItemFailed(TimeSpan.Zero, Unknown));
    }

    [Fact]
    public void AnItemThatEnds_ResetsTheCount()
    {
        // A skip and a natural end are the same call. A rotation advanced by skips, with a
        // bad item in it, must not accumulate the bad item's failures across passes.
        var guard = new PlaylistFailureGuard();

        for (var pass = 0; pass < 3 * PlaylistFailureGuard.MaxConsecutiveFailures; pass++)
        {
            Assert.False(guard.ItemFailed(Early, LongItem), $"Gave up on pass {pass + 1}.");
            guard.ItemEnded();
        }

        Assert.Equal(0, guard.ConsecutiveFailures);
    }

    [Fact]
    public void AFaultAfterTheThreshold_DoesNotCount()
    {
        // A source with no end, faulting once in a long while, must not be given up on.
        var guard = new PlaylistFailureGuard();

        for (var i = 0; i < 3 * PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(guard.ItemFailed(PlaylistFailureGuard.ProgressThreshold, Unknown));

        Assert.Equal(0, guard.ConsecutiveFailures);
    }

    [Fact]
    public void AFaultAfterProgress_ClearsTheFailuresBeforeIt()
    {
        var guard = new PlaylistFailureGuard();
        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            guard.ItemFailed(Early, LongItem);

        Assert.False(guard.ItemFailed(PlaylistFailureGuard.ProgressThreshold, LongItem));

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(guard.ItemFailed(Early, LongItem));
        Assert.True(guard.ItemFailed(Early, LongItem));
    }

    [Fact]
    public void AFaultJustShortOfTheThreshold_Counts()
    {
        var guard = new PlaylistFailureGuard();
        var justShort = PlaylistFailureGuard.ProgressThreshold - TimeSpan.FromTicks(1);

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            guard.ItemFailed(justShort, LongItem);

        Assert.True(guard.ItemFailed(justShort, LongItem));
    }

    [Fact]
    public void AShortClipThatFaultsNearItsEnd_IsNotGivenUpOn()
    {
        // A three-second clip that plays most of itself and faults near the end on every
        // pass never reaches five seconds. Half its length is progress.
        var guard = new PlaylistFailureGuard();
        var clip = TimeSpan.FromSeconds(3);

        for (var pass = 0; pass < 3 * PlaylistFailureGuard.MaxConsecutiveFailures; pass++)
        {
            Assert.False(
                guard.ItemFailed(TimeSpan.FromSeconds(2.8), clip),
                $"Gave up on pass {pass + 1}."
            );
        }
    }

    [Fact]
    public void AShortClipThatFaultsInItsFirstHalf_Counts()
    {
        var guard = new PlaylistFailureGuard();
        var clip = TimeSpan.FromSeconds(3);
        var firstHalf = TimeSpan.FromSeconds(1.4);

        for (var i = 0; i < PlaylistFailureGuard.MaxConsecutiveFailures; i++)
            Assert.False(guard.ItemFailed(firstHalf, clip));

        Assert.True(guard.ItemFailed(firstHalf, clip));
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
