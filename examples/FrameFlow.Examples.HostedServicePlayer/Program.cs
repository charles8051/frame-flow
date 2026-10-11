// Plays the audio of a media file from a BackgroundService in a Generic Host.
//
// The container owns the environment: the FFmpeg bootstrap and the audio sink. The playback is
// built per file, from the sink the container resolves, and is not a service itself.

using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Player;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string Usage = "FrameFlow.Examples.HostedServicePlayer <media-file>";
if (!ExampleArgs.TryReadInputPath(args, Usage, out var inputPath))
    return 2;

var builder = Host.CreateApplicationBuilder();
builder.Logging.AddExampleFile("hosted-service-player.log");

builder.Services
    .AddFrameFlow()
    .AddFrameFlowOpenAlAudio()  // IAudioSink singleton; the container disposes it
    .AddHostedBootstrap();      // loads FFmpeg at startup, so a missing binary fails there
builder.Services.AddHostedService(services =>
    ActivatorUtilities.CreateInstance<PlaybackService>(services, inputPath)
);

using var host = builder.Build();
await host.RunAsync();
return Environment.ExitCode;

internal sealed class PlaybackService(
    string inputPath,
    IAudioSink audio,
    ILoggerFactory loggerFactory,
    ILogger<PlaybackService> logger,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var pass = await FrameFlowPass
                .Create(inputPath)
                .WithAudioSink(audio)
                .WithLogger(loggerFactory)
                .BuildAsync(stoppingToken);

            if (pass.Info.AudioStreams.Count == 0)
            {
                logger.LogError("No audio stream in {Path}.", inputPath);
                Environment.ExitCode = 1;
                return;
            }

            logger.LogInformation("Playing {Path} ({Duration})", inputPath, pass.Info.Duration);
            await pass.RunToCompletionAsync(stoppingToken);
            logger.LogInformation("Playback complete.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Playback failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            // A pass starts the audio sink and leaves it running, and this sink outlives the pass.
            await audio.DeactivateAsync(CancellationToken.None);
            lifetime.StopApplication();
        }
    }
}
