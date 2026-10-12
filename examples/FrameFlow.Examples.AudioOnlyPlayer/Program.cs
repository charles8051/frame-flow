// Plays the audio of a media file through OpenAL, with no video, and exits at the end.
//
// FrameFlowPlayer paces playback and runs the audio sink for it. WaitForEndAsync completes once
// the device has played the last sample, so the process exits after the end is heard.

using System.Runtime.InteropServices;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

const string Usage = "FrameFlow.Examples.AudioOnlyPlayer <audio-or-media-file>";
if (!ExampleArgs.TryReadInputPath(args, Usage, out var inputPath))
    return 2;

using var loggerFactory = ExampleLogging.CreateFactory("audio-only-player.log");

await using var audio = new OpenAlAudioSink(loggerFactory.CreateLogger<OpenAlAudioSink>());
await using var player = await FrameFlowPlayer.Create()
    .WithMedia(inputPath)
    .WithAudioSink(audio)
    .WithLogger(loggerFactory)
    .BuildPlayerAsync();

if (player.MediaInfo is not { AudioStreams.Count: > 0 } info)
{
    Console.Error.WriteLine($"No audio stream in {inputPath}.");
    return 1;
}

var stream = info.AudioStreams[0];
Console.WriteLine(
    $"{inputPath}: {stream.CodecName}, {stream.SampleRate} Hz, {stream.Channels} ch, "
        + $"{info.Duration:hh\\:mm\\:ss\\.fff}"
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

var play = await player.PlayAsync();
if (!play.IsSuccess)
{
    Console.Error.WriteLine($"Could not play {inputPath}: {play.Error.Message}");
    return 1;
}

try
{
    var ended = await player.WaitForEndAsync(stop.Token);
    if (!ended.IsSuccess)
    {
        Console.Error.WriteLine($"Playback failed: {ended.Error.Message}");
        return 1;
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
}

return 0;
