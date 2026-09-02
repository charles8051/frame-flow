using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.TestBench;

/// <summary>
/// Everything between the sinks being built and the last command running. Shared by the
/// headless path and the windowed one, which differ only in who owns the main thread.
/// </summary>
internal sealed class BenchSession(
    BenchOptions options,
    PresenterSelection presenter,
    HardwareDecodeCapabilities capabilities,
    TextWriter output
)
{
    internal const int ExitOk = 0;
    internal const int ExitCommandFailed = 1;
    internal const int ExitDidNotParse = 2;

    /// <summary>
    /// Builds the pipeline, runs <paramref name="scripted"/> or the console, and tears
    /// down.
    /// </summary>
    /// <param name="videoSink">
    /// The sink to present to. Supplied by the caller because the windowed path has to
    /// create it on the UI thread, from a surface that is already in a window.
    /// </param>
    /// <param name="headlessSink">
    /// The same object when it is a <see cref="HeadlessVideoSink"/>, so <c>diag</c> can
    /// report its abandoned count. Null for the windowed presenters.
    /// </param>
    internal async Task<int> RunAsync(
        IVideoSink videoSink,
        HeadlessVideoSink? headlessSink,
        List<BenchCommand>? scripted,
        CancellationToken ct
    )
    {
        OpenAlAudioSink? audioSink = options.NoAudio ? null : new OpenAlAudioSink();
        try
        {
            // The probed capabilities have to be handed over. PlaybackController.Create
            // defaults them to null, and a null capability set resolves every stream to
            // software decode — so a bench that skipped this would report
            // backend=software on a machine that plays back on D3D11VA, and measure the
            // wrong pipeline while looking like it worked. MediaPlayer.CreateAsync does
            // the same at MediaPlayer.cs:116; the bench composes the controller itself
            // and so has to repeat it.
            await using var controller = PlaybackController.Create(
                videoSink: videoSink,
                audioSink: audioSink,
                hardwareDecodeCapabilities: capabilities,
                // Only the compositor surface wants hardware frames. Yielding them to a
                // presenter that cannot map them costs a download per frame and quietly
                // turns a zero-copy measurement into a copying one.
                yieldHardwareFrames: presenter.Resolved == PresenterKind.Gpu
            );

            var runner = new CommandRunner(
                controller,
                audioSink as IVolumeControl,
                headlessSink,
                presenter,
                output
            );

            var failed = false;

            if (options.InitialSource is { } initial)
                failed |= !await runner.RunAsync(new BenchCommand.Load(initial), ct);

            try
            {
                failed |= scripted is not null
                    ? !await RunScriptAsync(runner, scripted, ct)
                    : !await RunInteractiveAsync(runner, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                output.WriteLine();
                output.WriteLine("cancelled.");
            }

            return failed ? ExitCommandFailed : ExitOk;
        }
        finally
        {
            if (audioSink is not null)
                await audioSink.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs a parsed script. Every command runs; a failure is remembered rather than
    /// thrown, so the run still reaches the state the later commands were about.
    /// </summary>
    private async Task<bool> RunScriptAsync(
        CommandRunner runner,
        List<BenchCommand> commands,
        CancellationToken ct
    )
    {
        var allOk = true;
        foreach (var command in commands)
        {
            output.WriteLine($"> {CommandFormatter.Describe(command)}");
            allOk &= await runner.RunAsync(command, ct);
            if (runner.ShouldExit)
                break;
        }
        return allOk;
    }

    private async Task<bool> RunInteractiveAsync(CommandRunner runner, CancellationToken ct)
    {
        var allOk = true;
        output.WriteLine("Type 'quit' to exit.");

        while (!ct.IsCancellationRequested && !runner.ShouldExit)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null) // stdin closed
                break;

            var parsed = CommandParser.Parse(line);
            if (parsed.IsError)
            {
                // Interactive mode reports and continues. A typo at a prompt is not a
                // reason to tear down a session, and the exit code belongs to scripts.
                output.WriteLine($"FAIL  {parsed.Error}");
                continue;
            }

            if (parsed.Command is { } command)
                allOk &= await runner.RunAsync(command, ct);
        }

        return allOk;
    }

    /// <summary>A logger factory that writes nowhere, for the sinks that require one.</summary>
    internal static ILoggerFactory Loggers { get; } = NullLoggerFactory.Instance;
}
