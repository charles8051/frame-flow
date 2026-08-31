namespace FrameFlow.Media.Tests;

public sealed class PlaybackStateTests
{
    /// <summary>
    /// Member-count check guards against accidental additions / removals
    /// of the documented 8-state public surface (ADR-0027):
    /// Idle, Loading, Playing, Paused, Rebuffering, Unloaded, Ended, Error.
    /// Per-member existence + IsDefined sweeps are tautological — the
    /// individual states are exercised at every state-machine transition
    /// site.
    /// </summary>
    [Fact]
    public void PlaybackState_HasExpectedMemberCount()
    {
        var values = Enum.GetValues<PlaybackState>();
        Assert.Equal(8, values.Length);
    }

    /// <summary>
    /// Documented Idle ordinal pins the zero-init default. Other states'
    /// ordinals are not part of the public contract — only Idle matters
    /// because uninitialized fields land here.
    /// </summary>
    [Fact]
    public void PlaybackState_Idle_IsZero()
    {
        Assert.Equal(0, (int)PlaybackState.Idle);
    }
}
