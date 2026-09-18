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
            "load" => ParseLoad(rest),

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

    /// <summary>
    /// Parses <c>load [--format &lt;name&gt;] [--option &lt;key&gt;=&lt;value&gt;]... &lt;path&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The flags come before the path because the path is the trailing run of the line and
    /// may contain spaces — the reason the verb split above takes everything after the first
    /// word. Anything from the first non-flag token on is the path, so a path that begins
    /// with two dashes needs quoting, which <see cref="Unquote"/> already handles.
    /// </remarks>
    private static ParseResult ParseLoad(string rest)
    {
        string? format = null;
        Dictionary<string, string>? options = null;

        while (rest.StartsWith("--", StringComparison.Ordinal))
        {
            var (flag, afterFlag) = NextToken(rest);

            // The next flag is not this one's value. Taking it as one turns a typo into a
            // different source rather than an error: 'load --format --bogus clip.mp4' read
            // as format '--bogus' opens with a demuxer name nothing has, and a missing
            // '--option' value would swallow the flag after it. A value that genuinely
            // starts with two dashes is written quoted, and a quoted token starts with '"'.
            if (afterFlag.StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"load {flag} needs a value");

            var (value, afterValue) = NextToken(afterFlag);

            switch (flag)
            {
                case "--format":
                    if (value.Length == 0)
                        return ParseResult.Fail("load --format needs a demuxer name");
                    format = value;
                    break;

                case "--option":
                    var split = value.IndexOf('=', StringComparison.Ordinal);
                    if (split <= 0 || split == value.Length - 1)
                    {
                        return ParseResult.Fail(
                            $"load --option needs key=value, got '{value}'"
                        );
                    }

                    options ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    options[value[..split]] = value[(split + 1)..];
                    break;

                default:
                    return ParseResult.Fail($"load does not take '{flag}'");
            }

            rest = afterValue;
        }

        if (rest.Length == 0)
            return ParseResult.Fail("load needs a path");

        return ParseResult.Ok(new BenchCommand.Load(Unquote(rest), format, options));
    }

    /// <summary>
    /// Splits the leading whitespace-delimited token off <paramref name="text"/> and returns
    /// it with the trimmed remainder. An empty token means there was nothing left.
    /// </summary>
    private static (string Token, string Remainder) NextToken(string text)
    {
        // A quoted token runs to its closing quote rather than to the first space, so an
        // option value may contain one. Without this the formatter could not render such a
        // value in a form the parser reads back, and the transcript would replay as a
        // different command.
        if (text.StartsWith('"'))
        {
            var token = new System.Text.StringBuilder();
            for (var i = 1; i < text.Length; i++)
            {
                if (text[i] != '"')
                {
                    token.Append(text[i]);
                    continue;
                }

                // A doubled quote is one literal quote, not the end of the token. Without
                // this a value containing a quote and a space could not be written at all,
                // and '"' is a legal filename character everywhere but Windows.
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    token.Append('"');
                    i++;
                    continue;
                }

                var after = i + 1;
                return (
                    token.ToString(),
                    after >= text.Length ? string.Empty : text[after..].TrimStart()
                );
            }
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? (text, string.Empty)
            : (text[..space], text[(space + 1)..].TrimStart());
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : text;
}
