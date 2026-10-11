// Plays the audio of a media file through OpenAL, with no video, and exits at the end.
//
// FrameFlowPass runs a file through once. It holds no clock, but an audio device takes samples
// only as fast as it plays them, so a pass into an audio sink plays in real time. For pause,
// seek, a queue or video, use FrameFlowPlayer.

using System.Runtime.InteropServices;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

const string Usage = "FrameFlow.Examples.AudioOnlyPlayer <audio-or-media-file>";
if (!ExampleArgs.TryReadInputPath(args, Usage, out var inputPath))
    return 2;

using var loggerFactory = ExampleLogging.CreateFactory("audio-only-player.log");

await using var audio = new OpenAlAudioSink(loggerFactory.CreateLogger<OpenAlAudioSink>());
await using var pass = await FrameFlowPass.Create(inputPath)
    .WithAudioSink(audio)
    .WithLogger(loggerFactory)
    .BuildAsync();

if (pass.Info.AudioStreams.Count == 0)
{
    Console.Error.WriteLine($"No audio stream in {inputPath}.");
    return 1;
}

var stream = pass.Info.AudioStreams[0];
Console.WriteLine(
    $"{inputPath}: {stream.CodecName}, {stream.SampleRate} Hz, {stream.Channels} ch, "
        + $"{pass.Info.Duration:hh\\:mm\\:ss\\.fff}"
);
Console.WriteLine("Playing. Press Ctrl+C to stop.");

using var stop = new CancellationTokenSource();
using var ctrlC = PosixSignalRegistration.Create(
    PosixSignal.SIGINT,
    signal =>
    {
        signal.Cancel = true;
        stop.Cancel();
    }
);

try
{
    await pass.RunToCompletionAsync(stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
}
finally
{
    // A pass starts the audio sink and leaves it running.
    await audio.DeactivateAsync();
}

return 0;
