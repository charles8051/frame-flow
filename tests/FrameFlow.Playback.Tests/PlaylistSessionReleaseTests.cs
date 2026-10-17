// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Tests;

/// <summary>
/// The playlist session goes on to the next item once the item it leaves has let go of what the
/// two share, and lets the rest of that item's dispose run on (#780). A runtime's dispose frees
/// its decoders after it has let go of the pacer, the audio sink and the clock, and on a shared GPU
/// that free grows with the number of players switching at once.
/// </summary>
public sealed class PlaylistSessionReleaseTests
{
    [Fact]
    public async Task AJump_OpensAndStartsTheNextItem_WhileTheRestOfTheOldItemsDisposeRuns()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");
        rig.ReleasesSharedFirst = true;
        var disposal = rig.Hold("a", ItemOp.Dispose);

        rig.Coordinator.RequestJump(rig.PlaylistItem("b"));
        await disposal.EnteredAsync();
        await rig.SettleAsync();

        Assert.Equal(
            ["a#1.Dispose", "clock.Stop", "b#1.Open", "b#1.WarmUp", "b#1.Play", "ctl.ItemChanged(b)", "transition(b)"],
            rig.TakeLog()
        );
        disposal.Release();
    }

    [Fact]
    public async Task APromotion_AttachesThePreparedItem_WhileTheRestOfTheOldItemsDisposeRuns()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");
        var b = rig.PlaylistItem("b");
        Assert.True((await rig.Session.PrepareAsync(b, CancellationToken.None)).IsSuccess);
        await rig.SettleAsync();
        rig.TakeLog();
        rig.ReleasesSharedFirst = true;
        var disposal = rig.Hold("a", ItemOp.Dispose);

        rig.Coordinator.RequestJump(b);
        await disposal.EnteredAsync();
        await rig.SettleAsync();

        Assert.Equal(
            ["a#1.Dispose", "clock.Stop", "b#1.Attach", "b#1.Play", "ctl.ItemChanged(b)", "transition(b)"],
            rig.TakeLog()
        );
        disposal.Release();
    }

    [Fact]
    public async Task ARuntimeThatHasNotLetGoOfWhatItShares_HoldsTheNextItem_UntilItsDisposeEnds()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");
        var disposal = rig.Hold("a", ItemOp.Dispose);

        rig.Coordinator.RequestJump(rig.PlaylistItem("b"));
        await disposal.EnteredAsync();
        // The reader waits on the held dispose: a probe sent behind it is handled once it ends.
        var idle = rig.SettleAsync();
        Assert.Equal(["a#1.Dispose"], rig.TakeLog());

        disposal.Release();
        await idle;
        Assert.Equal(
            ["clock.Stop", "b#1.Open", "b#1.WarmUp", "b#1.Play", "ctl.ItemChanged(b)", "transition(b)"],
            rig.TakeLog()
        );
    }
}

/// <summary>
/// The session times each awaited step of a switch on <c>frameflow.playlist.step_duration_ms</c>,
/// tagged by step, so a switch's time splits into stages (#780). Runs alone: the instrument is
/// process-wide, and every playlist session in the test run records on it.
/// </summary>
[Collection(PlaylistStepMetricsCollection.Name)]
public sealed class PlaylistStepMetricsTests
{
    [Fact]
    public async Task ASwitch_RecordsItsSteps_TaggedByName()
    {
        var steps = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "FrameFlow.Playback" && instrument.Name == "frameflow.playlist.step_duration_ms")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>(
            (_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "step" && value >= 0)
                        steps.Add((string)tag.Value!);
                }
            }
        );
        listener.Start();

        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");
        rig.Coordinator.RequestJump(rig.PlaylistItem("b"));
        await rig.SettleAsync();

        Assert.Contains("dispose", steps);
        Assert.Contains("open", steps);
        Assert.Contains("warm_up", steps);
        Assert.Contains("play", steps);
    }
}

/// <summary>Runs <see cref="PlaylistStepMetricsTests"/> with no other test alongside.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlaylistStepMetricsCollection
{
    /// <summary>The collection name, referenced by <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "Playlist step metrics";
}
