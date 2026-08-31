using System.Diagnostics.CodeAnalysis;

namespace FrameFlow.Examples.Common;

/// <summary>
/// Resolves the <c>--log-file</c> argument to a concrete path for the
/// FrameFlow examples.
/// </summary>
/// <remarks>
/// <para>
/// Examples pass a <b>bare filename</b> (e.g. <c>dual-player.log</c>) on the
/// command line rather than an absolute path. That keeps
/// <c>launchSettings.json</c> free of any one machine's workspace location —
/// the file always lands in a single <c>logs/</c> directory at the repository
/// root (<see cref="RepoRoot.Find"/>), regardless of the working directory the
/// example was launched from. So every example's log is at a predictable,
/// consistent <c>&lt;repo&gt;/logs/&lt;name&gt;.log</c>.
/// </para>
/// <para>
/// An absolute path is still honoured verbatim, so an operator can redirect a
/// single run elsewhere without changing code.
/// </para>
/// </remarks>
public static class ExampleLogPaths
{
    /// <summary>The repo-relative directory all example logs land in.</summary>
    public const string LogsDirectoryName = "logs";

    /// <summary>
    /// Resolves <paramref name="logFileArg"/>: <see langword="null"/>/empty
    /// returns <see langword="null"/> (no file sink); an absolute path is
    /// returned unchanged; a relative path or bare filename is placed under
    /// <c>&lt;repo&gt;/logs/</c>.
    /// </summary>
    [return: NotNullIfNotNull(nameof(logFileArg))]
    public static string? Resolve(string? logFileArg)
    {
        if (string.IsNullOrWhiteSpace(logFileArg))
            return null;

        if (Path.IsPathRooted(logFileArg))
            return logFileArg;

        return Path.GetFullPath(
            Path.Combine(RepoRoot.Find(), LogsDirectoryName, logFileArg)
        );
    }
}
