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
/// Setup is best-effort. <see cref="FileLoggerProvider"/> opens its file in the
/// constructor with <see cref="FileShare.Read"/>, so a second instance of an
/// example, or a copy installed somewhere unwritable, throws there. An example
/// must still start: diagnostics being unavailable is not a reason to fail to
/// run. Both members below swallow that and report it through
/// <c>onFailure</c>.
/// </para>
/// </remarks>
public static class ExampleLogging
{
    /// <summary>
    /// Adds a file provider writing to
    /// <c>&lt;repo&gt;/logs/&lt;<paramref name="logFileName"/>&gt;</c>. For examples that
    /// own an <see cref="ILoggingBuilder"/> already, such as the Generic Host's
    /// <c>ConfigureLogging</c>. Never throws.
    /// </summary>
    /// <param name="builder">The builder to add the provider to.</param>
    /// <param name="logFileName">A bare filename, e.g. <c>sdl-player.log</c>.</param>
    /// <param name="minLevel">The lowest level written to the file.</param>
    /// <param name="onFailure">
    /// Invoked with the reason when the file could not be opened, so a caller with
    /// somewhere visible to put it (a status line, stderr) can say so.
    /// </param>
    public static ILoggingBuilder AddExampleFile(
        this ILoggingBuilder builder,
        string logFileName,
        LogLevel minLevel = LogLevel.Debug,
        Action<Exception>? onFailure = null
    )
    {
        try
        {
            builder.AddProvider(
                new FileLoggerProvider(ExampleLogPaths.Resolve(logFileName), minLevel)
            );
        }
        catch (Exception ex)
        {
            onFailure?.Invoke(ex);
        }

        return builder;
    }

    /// <summary>
    /// A factory writing to <c>&lt;repo&gt;/logs/&lt;<paramref name="logFileName"/>&gt;</c>.
    /// For examples that own no host. Returns a provider-less factory when the file
    /// cannot be opened; the factory is usable either way. Never throws.
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
}
