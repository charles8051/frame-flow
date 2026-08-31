namespace FrameFlow.Examples.Common;

/// <summary>
/// Locates the FrameFlow repository root from a running example, so examples
/// can resolve workspace-relative paths (corpus files, log files) without
/// hard-coding an absolute machine path. Mirrors the test projects'
/// <c>TestEnvironment.FindRepoRoot</c>.
/// </summary>
public static class RepoRoot
{
    private static readonly Lazy<string> Cached = new(Locate);

    /// <summary>
    /// The repository root: the nearest ancestor of the app base directory
    /// that contains <c>FrameFlow.slnx</c>. Falls back to the conventional
    /// <c>examples/&lt;proj&gt;/bin/&lt;cfg&gt;/&lt;tfm&gt;</c> depth back to the
    /// root when the marker is not found (e.g. a published, relocated app).
    /// </summary>
    public static string Find() => Cached.Value;

    private static string Locate()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "FrameFlow.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
        );
    }
}
