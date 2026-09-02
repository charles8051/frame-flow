using FrameFlow.Audio.OpenAL;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.TestBench;

/// <summary>
/// A console host that builds a real pipeline from the public API, keeps it warm, and
/// takes typed commands.
/// </summary>
/// <remarks>
/// <para>
/// The loop the examples cannot close. Their workflow is launch, wait, read the log:
/// every question costs a full cycle, and any question the existing flags do not answer
/// costs a code change first. Here the pipeline stays up between questions, so state
/// that took thirty seconds to reach is not thrown away to ask the next one.
/// </para>
/// <para>
/// A console subsystem rather than <c>WinExe</c>, which is what makes one interleaved
/// stream possible: the command, the reply, and every log line the pipeline emitted in
/// between, in the order they happened. That ordering is the artifact worth pasting into
/// an issue, and two processes writing two logs cannot produce it without clock
/// correlation.
/// </para>
/// </remarks>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitCommandFailed = 1;
    private const int ExitDidNotParse = 2;

    internal static async Task<int> Main(string[] args)
    {
        var (options, message, isHelp) = BenchOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(message);
            if (!isHelp)
                Console.Error.WriteLine();
            Console.Error.WriteLine(isHelp ? "" : BenchOptions.HelpText);
            return isHelp ? ExitOk : ExitDidNotParse;
        }

        // Parse the whole script before building anything. A typo on line 40 is not
        // worth discovering after a thirty-second run, and it is certainly not worth
        // opening an audio device to find out about.
        List<BenchCommand>? scripted = null;
        if (options.ScriptPath is { } scriptPath)
        {
            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"script not found: {scriptPath}");
                return ExitDidNotParse;
            }

            var lines = await File.ReadAllLinesAsync(scriptPath);
            if (!CommandParser.TryParseScript(lines, out scripted, out var errors))
            {
                Console.Error.WriteLine($"{scriptPath}: {errors.Count} line(s) did not parse.");
                foreach (var error in errors)
                    Console.Error.WriteLine($"  {error}");
                return ExitDidNotParse;
            }
        }

        using var log = options.LogFile is { } logFile
            ? new StreamWriter(logFile, append: false) { AutoFlush = true }
            : null;
        using TextWriter output =
            log is null ? Console.Out : new TeeTextWriter(Console.Out, log);

        var bootstrap = new FrameFlowBootstrapper(new FrameFlowNativeOptions());
        var loaded = bootstrap.Initialize();
        if (!loaded.IsSuccess)
        {
            output.WriteLine($"FAIL  FFmpeg did not load: {loaded.Message}");
            return ExitCommandFailed;
        }

        output.WriteLine(loaded.Message);

        // A bounded pool on purpose: it is what makes the decoder block once frames are
        // in flight, so a --present-cost propagates back as real backpressure.
        using var pool = new CpuFramePool(
            NullLogger<CpuFramePool>.Instance,
            options.PoolCapacity
        );
        await using var videoSink = new HeadlessVideoSink(pool, options.PresentCost);

        OpenAlAudioSink? audioSink = options.NoAudio ? null : new OpenAlAudioSink();
        try
        {
            // The probed capabilities have to be handed over. PlaybackController.Create
            // defaults them to null, and a null capability set resolves every stream to
            // software decode -- so a bench that skipped this would report
            // backend=software on a machine that plays back on D3D11VA, and measure the
            // wrong pipeline while looking like it worked. MediaPlayer.CreateAsync does
            // the same at MediaPlayer.cs:116; the bench composes the controller itself
            // and so has to repeat it.
            await using var controller = PlaybackController.Create(
                videoSink: videoSink,
                audioSink: audioSink,
                hardwareDecodeCapabilities: loaded.Capabilities
            );

            var runner = new CommandRunner(
                controller,
                audioSink as IVolumeControl,
                videoSink,
                output
            );

            using var ctrlC = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                ctrlC.Cancel();
            };

            var failed = false;

            if (options.InitialSource is { } initial)
                failed |= !await runner.RunAsync(new BenchCommand.Load(initial), ctrlC.Token);

            try
            {
                failed |= scripted is not null
                    ? !await RunScriptAsync(runner, scripted, output, ctrlC.Token)
                    : !await RunInteractiveAsync(runner, output, ctrlC.Token);
            }
            catch (OperationCanceledException) when (ctrlC.IsCancellationRequested)
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
    private static async Task<bool> RunScriptAsync(
        CommandRunner runner,
        List<BenchCommand> commands,
        TextWriter output,
        CancellationToken ct
    )
    {
        var allOk = true;
        foreach (var command in commands)
        {
            output.WriteLine($"> {Describe(command)}");
            allOk &= await runner.RunAsync(command, ct);
            if (runner.ShouldExit)
                break;
        }
        return allOk;
    }

    private static async Task<bool> RunInteractiveAsync(
        CommandRunner runner,
        TextWriter output,
        CancellationToken ct
    )
    {
        var allOk = true;
        output.WriteLine("Type 'quit' to exit, '--help' was printed at startup.");

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

    /// <summary>Echoes a command back the way it was typed, for the script transcript.</summary>
    private static string Describe(BenchCommand command) =>
        command switch
        {
            BenchCommand.Load load => $"load {load.Path}",
            BenchCommand.Unload => "unload",
            BenchCommand.Play => "play",
            BenchCommand.Pause => "pause",
            BenchCommand.Seek seek => $"seek {Duration(seek.Position)}",
            BenchCommand.Volume volume => $"volume {volume.Level:0.##}",
            BenchCommand.Mute mute => $"mute {(mute.On ? "on" : "off")}",
            BenchCommand.Repeat repeat => $"repeat {repeat.Mode}",
            BenchCommand.Status => "status",
            BenchCommand.Diag diag => diag.All ? "diag --all" : "diag",
            BenchCommand.Wait wait => $"wait {Duration(wait.Duration)}",
            BenchCommand.Quit => "quit",
            _ => command.GetType().Name,
        };

    /// <summary>Renders a duration back in the form the parser accepts.</summary>
    /// <remarks>
    /// The transcript is the artifact worth pasting into an issue, and
    /// <c>wait 00:00:02</c> is not a line this bench would accept back. Round-tripping
    /// keeps a pasted transcript runnable.
    /// </remarks>
    private static string Duration(TimeSpan value) =>
        value.TotalMilliseconds < 1_000 ? $"{value.TotalMilliseconds:0.##}ms"
        : value.TotalSeconds < 60 ? $"{value.TotalSeconds:0.###}s"
        : value.TotalMinutes < 60 ? $"{value.TotalMinutes:0.###}m"
        : $"{value.TotalHours:0.###}h";
}

/// <summary>Writes to the console and to a log file at once.</summary>
/// <remarks>
/// <c>--log-file</c> is a copy on disk rather than the only way to see anything, which
/// is the difference between this and the eleven examples that parse the same flag.
/// </remarks>
internal sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override System.Text.Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }
}
