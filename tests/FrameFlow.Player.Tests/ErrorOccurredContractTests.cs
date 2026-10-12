using FrameFlow.Playback;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins when <see cref="IMediaTransport.ErrorOccurred"/> fires for a command's failure (#606):
/// when the failure puts the player into <see cref="PlaybackState.Error"/>, and not when the
/// command is refused with the state unchanged. Also pins that a command on a disposed player
/// throws rather than returning a <see cref="Result"/>.
/// </summary>
/// <remarks>
/// The summary used to say a command's own failure is never repeated on
/// <see cref="IMediaTransport.ErrorOccurred"/>, while its remarks, and the controller's, said an
/// error that stops playback is raised as the player enters Error. The controller does the
/// latter, and these tests hold the documented contract to it.
/// </remarks>
public sealed class ErrorOccurredContractTests
{
    [Fact]
    public async Task APlayWhoseItemCannotOpen_ReportsOnItsResult_AndOnErrorOccurred_WithOneError()
    {
        await using var rig = new ControllerRig();
        rig.Factory.Reservable = new ControllerRig.FakeSource();
        rig.Session.InitializeThrows = new InvalidOperationException("not media");
        var errors = new List<PlaybackError>();
        using var subscription = rig.Player.ErrorOccurred.Subscribe(errors.Add);

        var played = await rig.Player.PlayAsync();

        Assert.False(played.IsSuccess);
        Assert.Equal(PlaybackState.Error, rig.Player.State);
        // Raised before the command completed, once, and as the same instance the Result carries,
        // so a caller that handles both can tell the second sighting by reference.
        Assert.Same(played.Error, Assert.Single(errors));
        Assert.IsType<InvalidOperationException>(played.Error.Inner);
    }

    [Fact]
    public async Task APlayWithNothingToPlay_ReportsOnItsResultOnly()
    {
        await using var rig = new ControllerRig();
        var errors = new List<PlaybackError>();
        using var subscription = rig.Player.ErrorOccurred.Subscribe(errors.Add);

        var played = await rig.Player.PlayAsync();
        await AfterTheDispatchLoopCatchesUp(rig);

        Assert.False(played.IsSuccess);
        Assert.Equal(ErrorCategory.InvalidOperation, played.Error.Category);
        Assert.Equal(PlaybackState.Idle, rig.Player.State);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ASeekWithNothingLoaded_ReportsOnItsResultOnly()
    {
        await using var rig = new ControllerRig();
        var errors = new List<PlaybackError>();
        using var subscription = rig.Player.ErrorOccurred.Subscribe(errors.Add);

        var sought = await rig.Player.SeekAsync(TimeSpan.FromSeconds(1));
        await AfterTheDispatchLoopCatchesUp(rig);

        Assert.False(sought.IsSuccess);
        Assert.Equal(PlaybackState.Idle, rig.Player.State);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("Play")]
    [InlineData("Pause")]
    [InlineData("Seek")]
    [InlineData("SetRepeatMode")]
    public async Task ACommandOnADisposedPlayer_Throws(string command)
    {
        await using var rig = new ControllerRig();
        await rig.Player.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => Issue(rig.Player, command));
    }

    private static Task<Result> Issue(IMediaTransport player, string command) =>
        command switch
        {
            "Play" => player.PlayAsync(),
            "Pause" => player.PauseAsync(),
            "Seek" => player.SeekAsync(TimeSpan.FromSeconds(1)),
            "SetRepeatMode" => player.SetRepeatModeAsync(RepeatMode.One),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };

    // The dispatch loop runs one command at a time, in the order posted, so a further command has
    // completed only after anything the refused one queued has run. That gives "nothing was
    // raised" a happens-before even if a raise were deferred to a later step of the loop.
    private static async Task AfterTheDispatchLoopCatchesUp(ControllerRig rig) =>
        Assert.True((await rig.Player.SetRepeatModeAsync(RepeatMode.All)).IsSuccess);
}
