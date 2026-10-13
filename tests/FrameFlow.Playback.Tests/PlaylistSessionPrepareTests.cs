namespace FrameFlow.Playback.Tests;

/// <summary>
/// The playlist session's shell preparing an item ahead (media-clips decision 8), over fake item
/// runtimes. The protocol's rules are in <c>PlaylistSessionProtocolTests</c>; these pin what the
/// shell does with its actions: a runtime prepared in the background, made current without opening
/// another, and disposed when it is not used.
/// </summary>
public sealed class PlaylistSessionPrepareTests
{
    [Fact]
    public async Task APreparedItem_OpensAndWarmsAhead_AndIsAttachedWhenItBecomesCurrent()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");
        var b = rig.PlaylistItem("b");

        var prepared = await rig.Session.PrepareAsync(b, CancellationToken.None);
        await rig.SettleAsync();

        Assert.True(prepared.IsSuccess, prepared.Error?.Message);
        Assert.Equal(["b#1.Open", "b#1.WarmUp"], rig.TakeLog());

        rig.Coordinator.RequestJump(b);
        await rig.SettleAsync();

        Assert.Equal(
            ["a#1.Dispose", "clock.Stop", "b#1.Attach", "b#1.Play", "ctl.ItemChanged(b)", "transition(b)"],
            rig.TakeLog()
        );
    }

    [Fact]
    public async Task APreparationThatFails_IsReported_AndTheItemIsOpenedWhenItIsTaken()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");
        var b = rig.PlaylistItem("b");
        rig.Fail("b", ItemOp.Open, new InvalidOperationException("boom"));

        var prepared = await rig.Session.PrepareAsync(b, CancellationToken.None);
        await rig.SettleAsync();
        Assert.False(prepared.IsSuccess);
        rig.TakeLog();

        rig.Coordinator.RequestJump(b);
        await rig.SettleAsync();

        Assert.Equal(
            ["a#1.Dispose", "clock.Stop", "b#1.Dispose", "b#2.Open", "b#2.WarmUp", "b#2.Play", "ctl.ItemChanged(b)", "transition(b)"],
            rig.TakeLog()
        );
    }

    [Fact]
    public async Task ALaterRequest_ReplacesThePreparedItem_AndTheEarlierRequestFails()
    {
        await using var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b", "c");
        var warmUp = rig.Hold("b", ItemOp.WarmUp);
        var disposal = rig.Hold("b", ItemOp.Dispose);

        var first = rig.Session.PrepareAsync(rig.PlaylistItem("b"), CancellationToken.None);
        await warmUp.EnteredAsync();
        var second = await rig.Session.PrepareAsync(rig.PlaylistItem("c"), CancellationToken.None);
        var firstResult = await first;

        // The replaced runtime is disposed in the background, while the session plays on: its
        // warm-up was cancelled, and its disposal is reached with the session still live.
        await disposal.EnteredAsync();
        disposal.Release();

        Assert.False(firstResult.IsSuccess);
        Assert.True(second.IsSuccess, second.Error?.Message);
    }

    [Fact]
    public async Task APreparedItemThatIsNotUsed_IsDisposedWithTheSession()
    {
        var rig = await PlaylistSessionRig.PlayingAsync(RepeatMode.Off, "a", "b");
        Assert.True((await rig.Session.PrepareAsync(rig.PlaylistItem("b"), CancellationToken.None)).IsSuccess);
        await rig.SettleAsync();
        rig.TakeLog();

        await rig.DisposeAsync();

        var log = rig.TakeLog();
        Assert.Contains("b#1.Dispose", log);
        Assert.DoesNotContain("b#1.Attach", log);
    }

    [Fact]
    public async Task ASessionThatPreparesAhead_PreparesTheNextItem_AndPromotesItAtTheEnd()
    {
        await using var rig = await PlaylistSessionRig.PlayingPreparingNextAsync(RepeatMode.Off, "a", "b");

        // The preparation runs in the background. A request for the same item joins it.
        Assert.True((await rig.Session.PrepareAsync(rig.PlaylistItem("b"), CancellationToken.None)).IsSuccess);
        await rig.SettleAsync();
        rig.TakeLog();

        rig.Runtime("a#1").RaiseEndOfStream();
        await rig.SettleAsync();

        var log = rig.TakeLog();
        Assert.Contains("b#1.Attach", log);
        Assert.DoesNotContain("b#2.Open", log);
    }
}
