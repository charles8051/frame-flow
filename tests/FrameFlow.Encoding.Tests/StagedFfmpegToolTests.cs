using System.Diagnostics;

namespace FrameFlow.Encoding.Tests;

/// <summary>
/// Tests that the <c>ffmpeg</c> and <c>ffprobe</c> staged by <c>scripts/fetch-ffmpeg.cs</c>
/// can run standing on their own, with nothing pointing them at their libraries.
/// </summary>
/// <remarks>
/// <para>
/// The fetch script flattens the tools and the shared libraries into one directory,
/// <c>runtimes/{rid}/native</c>. Two platforms make that work by themselves: Windows
/// resolves DLLs beside the executable, and the macOS dylibs are rewritten to
/// <c>@loader_path</c>. Linux needs the tools to carry an <c>$ORIGIN</c> rpath, and the
/// upstream BtbN build does not supply one — its <c>DT_RPATH</c> reads
/// <c>-Wl:../lib</c>, a quoting slip in their build script.
/// </para>
/// <para>
/// The round-trip tests cannot catch a regression here, because they set
/// <c>LD_LIBRARY_PATH</c> for the staged binary themselves. These facts deliberately
/// strip that, and run from a directory that is not the staging directory, so nothing
/// but the binary's own metadata can resolve the libraries.
/// </para>
/// </remarks>
public sealed class StagedFfmpegToolTests
{
    [RequiresStagedToolFact("ffprobe")]
    public Task StagedFfprobeResolvesItsLibrariesUnaided() => RunUnaidedAsync("ffprobe");

    [RequiresStagedToolFact("ffmpeg")]
    public Task StagedFfmpegResolvesItsLibrariesUnaided() => RunUnaidedAsync("ffmpeg");

    private static async Task RunUnaidedAsync(string tool)
    {
        // Non-null: the fact carrying this tool's name is skipped when it is absent.
        var exe = TestEnvironment.StagedToolPath(tool)!;

        var psi = new ProcessStartInfo(exe)
        {
            // -version exits 0 only after the dynamic loader has resolved every
            // NEEDED library, which is the whole point of the check.
            Arguments = "-version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,

            // Not the staging directory. The upstream rpath is "-Wl:../lib", which the
            // loader reads relative to the working directory; running from anywhere
            // else keeps a broken binary from passing by accident.
            WorkingDirectory = Path.GetTempPath(),
        };

        foreach (var variable in new[] { "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH" })
            psi.Environment.Remove(variable);

        using var proc = Process.Start(psi)!;

        // Both pipes drained before waiting; leaving one buffered deadlocks as soon
        // as it fills, and `-version` prints a full build configuration.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        await proc.WaitForExitAsync();

        Assert.True(
            proc.ExitCode == 0,
            $"'{exe}' exited {proc.ExitCode} with no library path set. On Linux this is "
                + "the staged rpath: scripts/fetch-ffmpeg.cs should have rewritten "
                + "DT_RPATH to $ORIGIN.\n"
                + $"  stdout: '{stdout.Trim()}'\n"
                + $"  stderr: '{stderr.Trim()}'"
        );

        Assert.Contains($"{tool} version", stdout, StringComparison.Ordinal);
    }
}
