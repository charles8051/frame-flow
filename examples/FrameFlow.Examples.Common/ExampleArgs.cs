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
    /// An option, or a second path, is an error rather than a guess: taking the first
    /// path-shaped argument would play whatever file followed an unknown option.
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
            return false;
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine(
                args.Length == 0
                    ? "No media file given."
                    : $"Expected one media file, got {args.Length}."
            );
            Console.Error.WriteLine($"Usage: {usage}");
            return false;
        }

        if (!File.Exists(args[0]))
        {
            Console.Error.WriteLine($"Input file not found: {args[0]}");
            return false;
        }

        inputPath = args[0];
        return true;
    }
}
