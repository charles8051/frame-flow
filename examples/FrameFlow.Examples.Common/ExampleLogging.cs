using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.Common;

/// <summary>
/// The examples' log sink: a file under <c>&lt;repo&gt;/logs/</c>, with no flag.
/// </summary>
/// <remarks>
/// <para>
/// Every example used to hand-roll a <c>--log-file</c> parser — eleven copies of
/// the same loop, and on a <c>WinExe</c> the only way to see anything at all.
/// Driving and watching a run is what <c>tools/FrameFlow.TestBench</c> is for
/// (ADR-0068), so the flag is gone and the path is fixed. An example logs to a
/// predictable place or it does not log.
/// </para>
/// <para>
/// <see cref="FileLoggerProvider"/> opens its file in the constructor with
/// <see cref="FileShare.Read"/>, so a second instance of an example cannot have
/// the canonical name. It takes <c>&lt;name&gt;.1.log</c> instead, and so on: an
/// example started twice keeps its diagnostics rather than losing them to the
/// first instance's handle. The canonical name always belongs to whichever
/// instance opened it, and every file's first line carries the time it was
/// opened, so the runs can be told apart.
/// </para>
/// <para>
/// When no candidate can be opened at all — a read-only install, no writable
/// repository root — setup does not throw. An example must still start:
/// diagnostics being unavailable is not a reason to fail to run. The reason
/// goes to <c>onFailure</c>, and to stderr when the caller passes none.
/// </para>
/// </remarks>
public static class ExampleLogging
{
    /// <summary>How many numbered alternatives to try after the canonical name.</summary>
    private const int MaxAlternatives = 9;

    /// <summary>
    /// Adds a file provider writing to
    /// <c>&lt;repo&gt;/logs/&lt;<paramref name="logFileName"/>&gt;</c>, or the first free
    /// numbered alternative. For examples that own an <see cref="ILoggingBuilder"/>
    /// already, such as the Generic Host's <c>ConfigureLogging</c>. Never throws.
    /// </summary>
    /// <param name="builder">The builder to add the provider to.</param>
    /// <param name="logFileName">A bare filename, e.g. <c>sdl-player.log</c>.</param>
    /// <param name="minLevel">The lowest level written to the file.</param>
    /// <param name="onFailure">
    /// Invoked with the reason when no candidate could be opened, so a caller with
    /// somewhere visible to put it can say so. Defaults to a line on stderr.
    /// </param>
    public static ILoggingBuilder AddExampleFile(
        this ILoggingBuilder builder,
        string logFileName,
        LogLevel minLevel = LogLevel.Debug,
        Action<Exception>? onFailure = null
    )
    {
        Exception? last = null;

        foreach (var candidate in Candidates(logFileName))
        {
            try
            {
                builder.AddProvider(
                    new FileLoggerProvider(ExampleLogPaths.Resolve(candidate), minLevel)
                );
                return builder;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (last is not null)
        {
            if (onFailure is not null)
                onFailure(last);
            else
                Console.Error.WriteLine($"File logging is off: {last.Message}");
        }

        return builder;
    }

    /// <summary>
    /// A factory writing to <c>&lt;repo&gt;/logs/&lt;<paramref name="logFileName"/>&gt;</c>,
    /// or the first free numbered alternative. For examples that own no host. Returns
    /// a provider-less factory when none can be opened; the factory is usable either
    /// way. Never throws.
    /// </summary>
    /// <inheritdoc cref="AddExampleFile" path="/param"/>
    public static ILoggerFactory CreateFactory(
        string logFileName,
        LogLevel minLevel = LogLevel.Debug,
        Action<Exception>? onFailure = null
    ) =>
        LoggerFactory.Create(b =>
            b.SetMinimumLevel(minLevel).AddExampleFile(logFileName, minLevel, onFailure)
        );

    /// <summary>
    /// The canonical name, then <c>&lt;stem&gt;.1&lt;ext&gt;</c> upward. An absolute path
    /// is the caller naming one exact file, so it gets no alternatives.
    /// </summary>
    private static IEnumerable<string> Candidates(string logFileName)
    {
        yield return logFileName;

        if (Path.IsPathRooted(logFileName))
            yield break;

        var stem = Path.GetFileNameWithoutExtension(logFileName);
        var extension = Path.GetExtension(logFileName);
        var directory = Path.GetDirectoryName(logFileName);

        for (var i = 1; i <= MaxAlternatives; i++)
        {
            var name = $"{stem}.{i}{extension}";
            yield return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
        }
    }
}
