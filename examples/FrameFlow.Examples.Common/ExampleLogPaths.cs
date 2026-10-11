using System.Diagnostics.CodeAnalysis;

namespace FrameFlow.Examples.Common;

/// <summary>
/// Places the examples' files under <c>&lt;repo&gt;/logs/</c>, whatever directory an example
/// was started from.
/// </summary>
public static class ExampleLogPaths
{
    /// <summary>The directory under the repository root that example logs land in.</summary>
    public const string LogsDirectoryName = "logs";

    /// <summary>
    /// Resolves a file name: <see langword="null"/> or empty returns <see langword="null"/>; an
    /// absolute path is returned unchanged; a relative path or bare file name is placed under
    /// <c>&lt;repo&gt;/logs/</c>.
    /// </summary>
    [return: NotNullIfNotNull(nameof(fileName))]
    public static string? Resolve(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        if (Path.IsPathRooted(fileName))
            return fileName;

        return Path.GetFullPath(
            Path.Combine(RepoRoot.Find(), LogsDirectoryName, fileName)
        );
    }
}
