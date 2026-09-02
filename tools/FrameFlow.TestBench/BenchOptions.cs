namespace FrameFlow.TestBench;

/// <summary>
/// How the bench was invoked. Everything here is fixed before the first command runs.
/// </summary>
/// <remarks>
/// <para>
/// The half of a reproduction that cannot be a command. Which sinks get built is decided
/// at construction, and Avalonia will not swap a presenter underneath a live player, so
/// these are flags rather than verbs. The deleted grammar had <c>require</c> for
/// asserting on them from inside a script; a C# repro constructs the sinks itself and so
/// already knows.
/// </para>
/// <para>
/// <see cref="PresentCost"/> is here for the same reason:
/// <see cref="FrameFlow.Media.HeadlessVideoSink.PresentCost"/> is get-only and the sink
/// is built before any command is read.
/// </para>
/// </remarks>
internal sealed record BenchOptions
{
    /// <summary>Run a command file instead of reading the console.</summary>
    internal string? ScriptPath { get; init; }

    /// <summary>Load this source at startup, before any script or prompt.</summary>
    internal string? InitialSource { get; init; }

    /// <summary>Build no audio sink. <c>volume</c> and <c>mute</c> then fail.</summary>
    internal bool NoAudio { get; init; }

    /// <summary>Which video surface to present to. Default is no window.</summary>
    /// <remarks>
    /// A request, not an outcome — see <see cref="PresenterSelection"/>, which applies
    /// the platform rule and keeps both.
    /// </remarks>
    internal PresenterKind Presenter { get; init; } = PresenterKind.Headless;

    /// <summary>Synthetic per-frame present cost for the headless sink.</summary>
    internal TimeSpan PresentCost { get; init; }

    /// <summary>Frame pool capacity. Bounded on purpose — see the remarks.</summary>
    /// <remarks>
    /// A bounded pool is what makes the decoder block once frames are in flight, so a
    /// synthetic present cost propagates back as real backpressure instead of billing
    /// wall-clock time while the decoder runs unimpeded.
    /// </remarks>
    internal int PoolCapacity { get; init; } = 3;

    /// <summary>Also write the session to this file.</summary>
    internal string? LogFile { get; init; }

    internal static ParseOutcome Parse(string[] args)
    {
        var options = new BenchOptions();
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            string? Next(string flag) =>
                i + 1 < args.Length ? args[++i] : throw new BadUsage($"{flag} needs a value");

            try
            {
                switch (argument)
                {
                    case "--script":
                        options = options with { ScriptPath = Next(argument) };
                        break;
                    case "--no-audio":
                        options = options with { NoAudio = true };
                        break;
                    case "--presenter":
                        var presenterText = Next(argument)!;
                        var kind =
                            PresenterSelection.ParseKind(presenterText)
                            ?? throw new BadUsage(
                                "--presenter takes 'headless', 'cpu' or 'gpu', got "
                                    + $"'{presenterText}'"
                            );
                        options = options with { Presenter = kind };
                        break;
                    case "--log-file":
                        options = options with { LogFile = Next(argument) };
                        break;
                    case "--present-cost":
                        var raw = Next(argument)!;
                        var cost =
                            CommandParser.ParseDuration(raw)
                            ?? throw new BadUsage(
                                $"--present-cost needs a duration, got '{raw}'. "
                                    + CommandParser.DurationHelp
                            );
                        options = options with { PresentCost = cost };
                        break;
                    case "--pool-capacity":
                        var capacityText = Next(argument)!;
                        if (!int.TryParse(capacityText, out var capacity) || capacity < 1)
                            throw new BadUsage(
                                $"--pool-capacity needs a positive integer, got '{capacityText}'"
                            );
                        options = options with { PoolCapacity = capacity };
                        break;
                    case "--help" or "-h":
                        return new ParseOutcome(null, HelpText, IsHelp: true);
                    default:
                        if (argument.StartsWith('-'))
                            throw new BadUsage($"unknown option '{argument}'");
                        positional.Add(argument);
                        break;
                }
            }
            catch (BadUsage bad)
            {
                return new ParseOutcome(null, bad.Message, IsHelp: false);
            }
        }

        if (positional.Count > 1)
            return new ParseOutcome(
                null,
                $"expected at most one media path, got {positional.Count}",
                IsHelp: false
            );

        if (positional.Count == 1)
            options = options with { InitialSource = positional[0] };

        return new ParseOutcome(options, null, IsHelp: false);
    }

    internal readonly record struct ParseOutcome(
        BenchOptions? Options,
        string? Message,
        bool IsHelp
    );

    private sealed class BadUsage(string message) : Exception(message);

    internal const string HelpText = """
        FrameFlow test bench — a console host that keeps a pipeline warm.

          FrameFlow.TestBench [<media>] [options]

        Options:
          --script <file>        run commands from a file instead of the console
          --presenter <kind>     headless (default), cpu, or gpu
          --no-audio             build no audio sink
          --present-cost <dur>   synthetic per-frame cost for the headless sink
          --pool-capacity <n>    frame pool slots (default 3)
          --log-file <file>      also write the session to this file
          -h, --help             this

        Commands:
          load <path>            build a session on this source
          unload                 tear the session down
          play, pause
          seek <duration>
          volume <level>         0 is silent, 1 is unity
          mute on|off
          repeat off|one|all
          status                 one line: state, position, duration
          diag [--all]           counters, and what moved since the last diag
          wait <duration>        a sleep
          quit

        Durations are a number and a unit: 250ms, 1.5s, 2m, 1h.

        Exit codes: 0 every command succeeded, 1 a command failed,
        2 a script line did not parse and nothing ran.

        --presenter gpu falls back to cpu off Windows. The bench reports the
        presenter it resolved rather than the one requested, so a transcript
        never claims a pipeline the run did not measure.

        The bench drives, observes, and reports. It does not assert — a
        reproduction that asserts is a C# file-based app under scripts/repro/.
        """;
}
