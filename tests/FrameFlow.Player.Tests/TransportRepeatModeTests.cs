using FrameFlow.Playback;

namespace FrameFlow.Player.Tests;

/// <summary>
/// <see cref="IMediaTransport.RepeatMode"/> and <see cref="IMediaTransport.RepeatModeChanged"/>
/// report the controller's repeat mode (#631). Without them a control bound to the transport could
/// set the mode and never read it, so a Loop toggle showed whatever was last clicked.
/// </summary>
public sealed class TransportRepeatModeTests
{
    [Fact]
    public async Task RepeatMode_ReadsTheModeTheBuilderSet_ThenTheModeSetSince()
    {
        await using var rig = new ControllerRig(initialRepeatMode: RepeatMode.All);

        Assert.Equal(RepeatMode.All, rig.Player.RepeatMode);

        Assert.True((await rig.Player.SetRepeatModeAsync(RepeatMode.One)).IsSuccess);

        Assert.Equal(RepeatMode.One, rig.Player.RepeatMode);
    }

    [Fact]
    public async Task RepeatModeChanged_ReportsEachChange_BeforeTheCommandCompletes()
    {
        await using var rig = new ControllerRig();
        var seen = new List<StateTransition<RepeatMode>>();
        using var subscription = rig.Player.RepeatModeChanged.Subscribe(seen.Add);

        Assert.True((await rig.Player.SetRepeatModeAsync(RepeatMode.All)).IsSuccess);
        Assert.Equal([new StateTransition<RepeatMode>(RepeatMode.Off, RepeatMode.All)], seen);

        // The mode already in force: nothing changes, and nothing is raised.
        Assert.True((await rig.Player.SetRepeatModeAsync(RepeatMode.All)).IsSuccess);
        Assert.Single(seen);

        Assert.True((await rig.Player.SetRepeatModeAsync(RepeatMode.Off)).IsSuccess);
        Assert.Equal(
            [
                new StateTransition<RepeatMode>(RepeatMode.Off, RepeatMode.All),
                new StateTransition<RepeatMode>(RepeatMode.All, RepeatMode.Off),
            ],
            seen
        );
        Assert.Equal(RepeatMode.Off, rig.Player.RepeatMode);
    }
}
