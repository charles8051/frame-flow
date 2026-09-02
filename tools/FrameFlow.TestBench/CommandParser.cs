using System.Globalization;
using FrameFlow.Media;

namespace FrameFlow.TestBench;

/// <summary>
/// Turns a line of bench input into a <see cref="BenchCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no pipeline, no I/O, no clock. That is what makes the command surface
/// testable without a media file, and it is most of what this file is for.
/// </para>
/// <para>
/// Blank lines and <c>#</c> comments parse to <see langword="null"/> rather than to an
/// error, so a script file can be annotated the way a person would annotate it.
/// </para>
/// </remarks>
internal static class CommandParser
{
    /// <summary>The outcome of parsing one line.</summary>
    /// <param name="Command">
    /// The command, or <see langword="null"/> for a blank or comment line.
    /// </param>
    /// <param name="Error">Why the line did not parse, or <see langword="null"/>.</param>
    internal readonly record struct ParseResult(BenchCommand? Command, string? Error)
    {
        internal bool IsError => Error is not null;

        internal static ParseResult Ok(BenchCommand command) => new(command, null);

        internal static ParseResult Skip() => new(null, null);

        internal static ParseResult Fail(string error) => new(null, error);
    }

    /// <summary>
    /// Parses <paramref name="line"/>.
    /// </summary>
    internal static ParseResult Parse(string line)
    {
        var text = StripComment(line).Trim();
        if (text.Length == 0)
            return ParseResult.Skip();

        // Split on the first run of whitespace only. `load` takes a path, and a path
        // with spaces in it is ordinary; splitting it into words would make the bench
        // unable to open half the files on a desktop.
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();

        return verb switch
        {
            "load" => rest.Length == 0
                ? ParseResult.Fail("load needs a path")
                : ParseResult.Ok(new BenchCommand.Load(Unquote(rest))),

            "unload" => NoArguments(verb, rest, new BenchCommand.Unload()),
            "play" => NoArguments(verb, rest, new BenchCommand.Play()),
            "pause" => NoArguments(verb, rest, new BenchCommand.Pause()),
            "status" => NoArguments(verb, rest, new BenchCommand.Status()),
            "quit" or "exit" => NoArguments(verb, rest, new BenchCommand.Quit()),

            "seek" => ParseDuration(rest) is { } seek
                ? ParseResult.Ok(new BenchCommand.Seek(seek))
                : ParseResult.Fail($"seek needs a duration, got '{rest}'. {DurationHelp}"),

            "wait" => ParseDuration(rest) is { } wait
                ? ParseResult.Ok(new BenchCommand.Wait(wait))
                : ParseResult.Fail($"wait needs a duration, got '{rest}'. {DurationHelp}"),

            "volume" => ParseVolume(rest),
            "mute" => ParseOnOff(rest) is { } muted
                ? ParseResult.Ok(new BenchCommand.Mute(muted))
                : ParseResult.Fail($"mute needs 'on' or 'off', got '{rest}'"),

            "repeat" => ParseRepeat(rest),
            "diag" => ParseDiag(rest),

            _ => ParseResult.Fail($"unknown command '{verb}'"),
        };
    }

    /// <summary>
    /// Parses every line of <paramref name="lines"/>, reporting every error rather than
    /// the first.
    /// </summary>
    /// <remarks>
    /// Parse first, run second. A typo on line 40 is not worth discovering after a
    /// thirty-second run — the one rule of the deleted grammar that survives as
    /// mechanism rather than as compilation. Reporting all of them at once means one
    /// round trip instead of one per typo.
    /// </remarks>
    internal static bool TryParseScript(
        IReadOnlyList<string> lines,
        out List<BenchCommand> commands,
        out List<string> errors
    )
    {
        commands = [];
        errors = [];

        for (var i = 0; i < lines.Count; i++)
        {
            var result = Parse(lines[i]);
            if (result.IsError)
                errors.Add($"line {i + 1}: {result.Error}");
            else if (result.Command is { } command)
                commands.Add(command);
        }

        return errors.Count == 0;
    }

    internal const string DurationHelp =
        "Durations are a number and a unit: 250ms, 1.5s, 2m, 1h.";

    /// <summary>
    /// Parses <c>250ms</c>, <c>1.5s</c>, <c>2m</c>, <c>1h</c>.
    /// </summary>
    /// <remarks>
    /// The unit is required. A bare number would have to mean seconds or milliseconds
    /// by convention, and a script that meant the other one is off by a thousand
    /// without saying anything.
    /// </remarks>
    internal static TimeSpan? ParseDuration(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        // Longest suffix first: "ms" would otherwise be read as "m" plus a stray 's'.
        (string Suffix, double PerUnitMs)[] units =
        [
            ("ms", 1),
            ("s", 1_000),
            ("m", 60_000),
            ("h", 3_600_000),
        ];

        foreach (var (suffix, perUnitMs) in units)
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var number = text[..^suffix.Length].Trim();
            if (
                !double.TryParse(
                    number,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value
                )
                || double.IsNaN(value)
                || double.IsInfinity(value)
                || value < 0
            )
                return null;

            return TimeSpan.FromMilliseconds(value * perUnitMs);
        }

        return null;
    }

    private static ParseResult ParseVolume(string rest)
    {
        if (
            !float.TryParse(
                rest.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var level
            )
            || float.IsNaN(level)
            || level < 0
        )
            return ParseResult.Fail($"volume needs a number at or above 0, got '{rest}'");

        // Above 1.0 is accepted, matching IVolumeControl: unity is 1.0 and higher gains
        // are legal but may distort. Rejecting them here would make the bench unable to
        // reproduce a report about exactly that.
        return ParseResult.Ok(new BenchCommand.Volume(level));
    }

    private static ParseResult ParseRepeat(string rest) =>
        rest.Trim().ToLowerInvariant() switch
        {
            "off" => ParseResult.Ok(new BenchCommand.Repeat(RepeatMode.Off)),
            "one" => ParseResult.Ok(new BenchCommand.Repeat(RepeatMode.One)),
            "all" => ParseResult.Ok(new BenchCommand.Repeat(RepeatMode.All)),
            _ => ParseResult.Fail($"repeat needs 'off', 'one' or 'all', got '{rest}'"),
        };

    private static ParseResult ParseDiag(string rest)
    {
        var argument = rest.Trim();
        return argument switch
        {
            "" => ParseResult.Ok(new BenchCommand.Diag(All: false)),
            "--all" or "all" => ParseResult.Ok(new BenchCommand.Diag(All: true)),
            _ => ParseResult.Fail($"diag takes '--all' or nothing, got '{argument}'"),
        };
    }

    private static bool? ParseOnOff(string text) =>
        text.Trim().ToLowerInvariant() switch
        {
            "on" or "true" => true,
            "off" or "false" => false,
            _ => null,
        };

    private static ParseResult NoArguments(string verb, string rest, BenchCommand command) =>
        rest.Length == 0
            ? ParseResult.Ok(command)
            : ParseResult.Fail($"{verb} takes no arguments, got '{rest}'");

    /// <summary>
    /// Removes a trailing <c>#</c> comment, leaving one inside quotes alone.
    /// </summary>
    /// <remarks>
    /// A file path can contain <c>#</c>, so a naive cut at the first one would silently
    /// truncate <c>load "C:\clips\take #3.mp4"</c> to a path that does not exist.
    /// </remarks>
    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == '#' && !quoted)
                return line[..i];
        }
        return line;
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
}
