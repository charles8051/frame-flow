using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using FrameFlow.Sdl;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.SdlPlayer;

/// <summary>
/// The player the window's commands share. The SDL command queue runs them one at a time, so only
/// one thread at a time is in here and it needs no lock.
/// </summary>
internal sealed class PlayerSlot(
    SdlVideoSink video,
    RepeatMode repeat,
    ILoggerFactory loggerFactory
) : IAsyncDisposable
{
    private OpenAlAudioSink? _audio;
    private IMediaPlayer? _player;
    private RepeatMode _repeat = repeat;

    /// <summary>A command for the queue that runs <paramref name="command"/> against the player.</summary>
    public Func<CancellationToken, Task> Run(Func<IMediaPlayer, CancellationToken, Task> command) =>
        async ct => await command(await GetAsync(ct), ct);

    /// <summary>
    /// Disposes the player, then its audio sink, even if the player's disposal fails. Call once no
    /// command is running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_player is not null)
                await _player.DisposeAsync();
        }
        finally
        {
            if (_audio is not null)
                await _audio.DisposeAsync();
        }
    }

    private async Task<IMediaPlayer> GetAsync(CancellationToken ct)
    {
        // Error is final, and a file that fails to open while nothing plays leaves the player
        // there, so the next command gets a fresh one.
        if (_player?.State == PlaybackState.Error)
        {
            _repeat = _player.GetDiagnostics().RepeatMode;
            await _player.DisposeAsync();
            _player = null;
        }

        if (_player is null)
        {
            _audio ??= new OpenAlAudioSink(loggerFactory.CreateLogger<OpenAlAudioSink>());
            _player = await FrameFlowPlayer
                .Create()
                .WithVideoSink(video)
                .WithAudioSink(_audio)
                .WithRepeatMode(_repeat)
                .WithLogger(loggerFactory)
                .BuildPlayerAsync(ct);

            _player.StateChanged.Subscribe(state => Console.WriteLine(state));
            _player.ErrorOccurred.Subscribe(error =>
                Console.Error.WriteLine($"Playback error: {error.Message}")
            );
        }
        return _player;
    }
}
