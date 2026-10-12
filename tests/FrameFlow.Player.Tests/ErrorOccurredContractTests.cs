using FrameFlow.Playback;

namespace FrameFlow.Player.Tests;

/// <summary>
/// Pins when <see cref="IMediaTransport.ErrorOccurred"/> fires for a command's failure (#606):
/// when the failure puts the player into <see cref="PlaybackState.Error"/>, and not when the
/// command is refused with the state unchanged.
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

        Assert.False(sought.IsSuccess);
        Assert.Equal(PlaybackState.Idle, rig.Player.State);
        Assert.Empty(errors);
    }
}
