namespace FrameFlow.Playback.Tests;

/// <summary>
/// A runtime names the timeline of its current run before the run starts, and the run's decoders
/// stamp exactly that one (ADR-0084, decision 7; #756). Over <see cref="ItemRuntimeRig"/>: no FFmpeg
/// and no real time.
/// </summary>
public sealed class SubstrateSessionTimelineTests
{
    private static readonly TimeSpan Bound = ItemRuntimeRig.Bound;

    [Fact]
    public async Task TheFirstRun_StartsOnTheTimelineTheOpenNamed()
    {
        // Audio only, so nothing warms up and the run starts at the Play.
        await using var rig = await ItemRuntimeRig.Create(MediaShape.AudioOnly).OpenAsync();
        var named = rig.Session.Timeline;
        Assert.NotEqual(TimelineId.None, named);
        Assert.Empty(rig.Media.RunTimelines);

        await rig.Session.PlayAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(named, Assert.Single(rig.Media.RunTimelines));
        Assert.Equal(named, rig.Session.Timeline);
    }

    [Fact]
    public async Task APausedSeek_NamesANewTimeline_AndThePlayStartsTheRunOnIt()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        var before = rig.Session.Timeline;
        Assert.Equal(before, Assert.Single(rig.Media.RunTimelines));

        await rig.Session.PauseAsync().AsTask().WaitAsync(Bound);
        await rig.Session.SeekAsync(TimeSpan.FromSeconds(2)).AsTask().WaitAsync(Bound);
        var named = rig.Session.Timeline;
        Assert.NotEqual(before, named);
        Assert.Single(rig.Media.RunTimelines);

        await rig.Session.PlayAsync().AsTask().WaitAsync(Bound);

        Assert.Equal([before, named], rig.Media.RunTimelines);
    }

    [Fact]
    public async Task ARewindThatPlaysOn_StartsItsRunOnTheTimelineNamedWhenItCompletes()
    {
        await using var rig = await ItemRuntimeRig.Create().PlayingAsync();
        var before = rig.Session.Timeline;

        await rig.Session.RewindToStartAsync().AsTask().WaitAsync(Bound);

        Assert.NotEqual(before, rig.Session.Timeline);
        Assert.Equal([before, rig.Session.Timeline], rig.Media.RunTimelines);
        Assert.Empty(rig.Log.Violations);
    }
}
