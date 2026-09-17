using FrameFlow.Audio.OpenAL;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.AudioOnlyPlayer;

/// <summary>
/// Console audio player — plays audio from a media file through
/// OpenAL with no video output. Demonstrates the player surface
/// (<see cref="FrameFlowPlayer"/>) used in the minimal
/// "open + play to EOS" shape.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // One argument: the file to play.
        var inputPath = args.FirstOrDefault(a => !a.StartsWith('-'));

        if (string.IsNullOrEmpty(inputPath))
        {
            Console.Error.WriteLine(
                "Usage: FrameFlow.Examples.AudioOnlyPlayer <audio-or-media-file>"
            );
            return 2;
        }
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 2;
        }

        using var loggerFactory = ExampleLogging.CreateFactory(
            "audio-only-player.log",
            onFailure: ex => Console.Error.WriteLine($"File logging is off: {ex.Message}")
        );

        Console.WriteLine($"Audio playback: {inputPath}");

        // OpenAlAudioSink is the production audio sink — implements
        // IAudioSink + IClockSource. PlayerSession only consumes
        // the IAudioSink data plane; clock-source wiring would be
        // needed when video joins the pipeline for sync. Audio-only
        // playback doesn't need the clock fed back anywhere.
        await using var sink = new OpenAlAudioSink();

        // No ActivateAsync here: PlayerSession activates the sink it was
        // given, the same way SubstrateSession and BuildPlayerAsync do.
        // Pre-activating would rebase the sink's sample counter twice —
        // see the contract on IAudioSink.ActivateAsync.

        try
        {
            await using var player = await FrameFlowPlayer
                .Open(inputPath)
                .WithAudioSink(sink)
                .WithLogger(loggerFactory)
                .BuildAsync();

            if (player.Info.AudioStreams.Count == 0)
            {
                Console.Error.WriteLine($"No audio streams found in {inputPath}.");
                return 1;
            }

            var a = player.Info.AudioStreams[0];
            Console.WriteLine(
                $"Decoding audio stream [{a.StreamIndex}]: {a.CodecName} "
                    + $"{a.SampleRate} Hz, {a.Channels} ch"
            );
            Console.WriteLine($"Duration: {player.Info.Duration:hh\\:mm\\:ss\\.fff}");
            Console.WriteLine();
            Console.WriteLine("Playing… press Ctrl+C to stop.");

            using var ctrlC = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                ctrlC.Cancel();
            };

            try
            {
                await player.PlayToCompletionAsync(ctrlC.Token);
            }
            catch (OperationCanceledException) when (ctrlC.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("Cancelled.");
            }
        }
        finally
        {
            await sink.DeactivateAsync();
        }

        Console.WriteLine("Done.");
        return 0;
    }
}
