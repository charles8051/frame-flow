using System.Diagnostics.CodeAnalysis;

namespace FrameFlow.Examples.Common;

/// <summary>
/// The console examples' command line: one media path, and nothing else.
/// </summary>
public static class ExampleArgs
{
    /// <summary>
    /// Reads the single positional media path, writing a usage line to stderr and
    /// returning <see langword="false"/> when the command line is not exactly that.
    /// </summary>
    /// <remarks>
    /// Strict on purpose. Scanning for the first path-shaped argument is what let
    /// <c>--log-file &lt;name&gt; &lt;clip&gt;</c> play the log instead of the clip, and the
    /// flag being gone removes today's instance of that rather than the shape. An
    /// option here is a stale invocation, so it is an error and not a guess.
    /// </remarks>
    public static bool TryReadInputPath(
        string[] args,
        string usage,
        [NotNullWhen(true)] out string? inputPath
    )
    {
        inputPath = null;

        var option = args.FirstOrDefault(a => a.StartsWith('-'));
        if (option is not null)
        {
            Console.Error.WriteLine($"Unexpected option: {option}");
            Console.Error.WriteLine($"Usage: {usage}");
            Console.Error.WriteLine(
                "Diagnostic switches live in tools/FrameFlow.TestBench (ADR-0068);"
                    + $" the log is written to {ExampleLogPaths.LogsDirectoryName}/ with no flag."
            );
            return false;
        }

        var positional = args.Where(a => !a.StartsWith('-')).ToList();
        if (positional.Count != 1)
        {
            Console.Error.WriteLine(
                positional.Count == 0
                    ? "No media file given."
                    : $"Expected one media file, got {positional.Count}."
            );
            Console.Error.WriteLine($"Usage: {usage}");
            return false;
        }

        if (!File.Exists(positional[0]))
        {
            Console.Error.WriteLine($"Input file not found: {positional[0]}");
            return false;
        }

        inputPath = positional[0];
        return true;
    }
}
