using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Player;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.HostedServicePlayer;

/// <summary>
/// Demonstrates Generic Host integration with the player surface
/// (<see cref="FrameFlowPlayer"/>). An
/// <see cref="IHostedService"/> manages playback lifecycle alongside
/// the host: <see cref="IHostedService.StartAsync"/> opens the file
/// and begins play-to-EOS; the host stays up until playback
/// completes (or Ctrl+C cancels).
/// </summary>
/// <remarks>
/// <para>
/// This example is the reference consumer of the
/// <c>services.AddFrameFlow()</c> DI builder seam. It registers the
/// OpenAL audio backend through
/// <see cref="FrameFlowOpenAlServiceCollectionExtensions.AddFrameFlowOpenAlAudio"/>
/// and the FFmpeg bootstrap through
/// <see cref="FrameFlow.Native.FrameFlowNativeServiceCollectionExtensions.AddHostedBootstrap"/>,
/// then resolves the <see cref="IAudioSink"/> from the container and
/// hands it to an explicitly constructed <see cref="FrameFlowPlayer"/>
/// session. This matches the architecture's "application environment
/// lifecycle vs per-playback-session lifecycle" split
/// (<c>docs/ARCHITECTURE.md</c>, "Hosted lifecycle integration"): the
/// DI container owns the environment pieces (bootstrap, the audio sink
/// singleton it disposes on teardown), while the playback session
/// stays an explicitly created runtime object rather than a singleton.
/// </para>
/// <para>
/// Built on <see cref="FrameFlowPlayer"/>.
/// The old version used <c>IPlaybackSessionFactory</c> directly,
/// which is now internal.
/// </para>
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // First non-flag arg is the input file. --log-file <path>
        // attaches a FileLoggerProvider via ConfigureLogging below.
        // Argument-position-agnostic so launchSettings.json can put
        // the flag in any order.
        string? inputPath = null;
        string? logFilePath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--log-file" && i + 1 < args.Length)
            {
                logFilePath = args[i + 1];
                i++;
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                inputPath ??= args[i];
        }

        if (string.IsNullOrEmpty(inputPath))
        {
            Console.Error.WriteLine(
                "Usage: FrameFlow.Examples.HostedServicePlayer <media-file> [--log-file <path>]"
            );
            return 2;
        }
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 2;
        }

        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(
                (_, builder) =>
                {
                    // Surface the media path as a config key the hosted
                    // service can read — exercises the standard Generic
                    // Host configuration pipeline.
                    builder.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["Playback:Input"] = inputPath }
                    );
                }
            )
            .ConfigureLogging(b =>
            {
                // Plug the FileLoggerProvider into the host's logging
                // pipeline so it lives + dies with the host. The
                // generic host's default console provider stays;
                // file is additive.
                if (!string.IsNullOrEmpty(logFilePath))
                    b.AddProvider(new FileLoggerProvider(ExampleLogPaths.Resolve(logFilePath), LogLevel.Debug));
            })
            .ConfigureServices(services =>
            {
                // The DI builder seam in action. AddFrameFlow() returns an
                // IFrameFlowBuilder; the adapter packages extend it so a host
                // app composes the engine's environment pieces fluently:
                //   - AddFrameFlowOpenAlAudio() registers the OpenAL sink as the
                //     IAudioSink singleton (the container owns + disposes it).
                //   - AddHostedBootstrap() runs the FFmpeg native bootstrap as an
                //     IHostedService at startup, so a missing/broken FFmpeg fails
                //     fast on host start rather than on first decode.
                // PlaybackHostedService then resolves IAudioSink from the
                // container and builds the playback session explicitly — the
                // session itself is deliberately NOT a DI singleton (see the
                // class remarks and docs/ARCHITECTURE.md).
                services
                    .AddFrameFlow()
                    .AddFrameFlowOpenAlAudio()
                    .AddHostedBootstrap();

                services.AddSingleton<PlaybackHostedService>();
                services.AddHostedService(sp => sp.GetRequiredService<PlaybackHostedService>());
            })
            .Build();

        // Capture a reference to the singleton service BEFORE RunAsync
        // so we can read its ExitCode after the host's service provider
        // is disposed during shutdown.
        var svc = host.Services.GetRequiredService<PlaybackHostedService>();

        await host.RunAsync();

        // Surface the playback service's exit code so the host runner
        // can distinguish "played to EOS" from "fatal config error."
        return svc.ExitCode;
    }
}

internal sealed class PlaybackHostedService(
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<PlaybackHostedService> logger,
    ILoggerFactory loggerFactory,
    IAudioSink audioSink
) : IHostedService
{
    private Task? _playbackTask;

    public int ExitCode { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var inputPath = configuration["Playback:Input"];
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            logger.LogError("No Playback:Input configured. Pass a media file path on the command line.");
            ExitCode = 2;
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        // Fire-and-forget playback; we'll stop the host when it
        // finishes (or surface a failure code).
        _playbackTask = Task.Run(
            () => RunPlaybackAsync(inputPath, lifetime.ApplicationStopping),
            CancellationToken.None
        );
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_playbackTask is null)
            return;

        try
        {
            await _playbackTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Forced shutdown — the playback task has its own stopping
            // token from lifetime.ApplicationStopping and will unwind on
            // its own; don't block the host shutdown waiting.
        }
    }

    private async Task RunPlaybackAsync(string inputPath, CancellationToken ct)
    {
        try
        {
            // The sink is the IAudioSink singleton resolved from the DI
            // container (registered by AddFrameFlowOpenAlAudio()). Per
            // ADR-0044 the container owns its disposal — we do NOT dispose
            // it here.
            //
            // Activation is PlayerSession's, not ours: it activates the sink
            // it was given. We still own the matching DeactivateAsync below,
            // because this sink is a container singleton that outlives the
            // session and must be quiesced between runs.
            try
            {
                await using var player = await FrameFlowPlayer
                    .Open(inputPath)
                    .WithAudioSink(audioSink)
                    .WithLogger(loggerFactory)
                    .BuildAsync(ct)
                    .ConfigureAwait(false);

                if (player.Info.AudioStreams.Count == 0)
                {
                    logger.LogError("No audio streams in {Path}.", inputPath);
                    ExitCode = 1;
                    return;
                }

                logger.LogInformation(
                    "Playing {Path} ({Duration:hh\\:mm\\:ss\\.fff})",
                    inputPath,
                    player.Info.Duration
                );

                await player.PlayToCompletionAsync(ct).ConfigureAwait(false);
                logger.LogInformation("Playback complete.");
            }
            finally
            {
                // Deactivate (not dispose) the device-side sink — the DI
                // container disposes the singleton on host teardown. Use
                // CancellationToken.None so the deactivate completes even
                // when the application has been asked to stop.
                await audioSink.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Playback cancelled by host shutdown.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Playback failed.");
            ExitCode = 1;
        }
        finally
        {
            // Either we finished cleanly or we faulted — either way,
            // the host can stop now.
            lifetime.StopApplication();
        }
    }
}
