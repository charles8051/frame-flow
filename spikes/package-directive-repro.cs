#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:package FrameFlow.Player@0.7.0-alpha.4.11
#:package FrameFlow.Native@0.7.0-alpha.4.11
#:package Microsoft.Extensions.Logging.Abstractions@10.0.0

// Spike: can a .NET file-based app consume FrameFlow through #:package?
//
// The open question at the head of Decision 6 in
// docs/adr/command-driven-testbench-host.md is whether test-bench repro files
// should be C# rather than a bespoke assertion grammar. That argument turns on
// file-based apps being self-contained — "the script IS a program, and there is
// no host runtime to inherit" — which only holds if one .cs file can take a
// package reference to FrameFlow and get both the managed surface and the
// FFmpeg natives underneath it. The ADR flagged the directive as untested:
// no script in scripts/ uses it.
//
// Deliberately shaped like a repro would be — drive, observe, assert, exit
// non-zero — so that what it proves is what a repro would need. The bench's
// grammar would express these same four checks as `expect` statements.
//
//   dotnet run spikes/package-directive-repro.cs -- [path-to-media]
//
// With no argument it synthesises a clip with the staged ffmpeg.

using System.Diagnostics;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Player;
using Microsoft.Extensions.Logging.Abstractions;

var failures = 0;

void Check(string what, bool ok, string? detail = null)
{
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(ok ? "  PASS  " : "  FAIL  ");
    Console.ResetColor();
    Console.WriteLine(what + (detail is null ? "" : $" — {detail}"));
    if (!ok)
        failures++;
}

Console.WriteLine("FrameFlow via #:package — file-based app spike");
Console.WriteLine(new string('─', 64));

// ── 1. The managed surface resolved ─────────────────────────────────────────
// Compiling at all means the directives restored from the local feed and the
// compiler saw the packaged types. Nothing here references a project.
Console.WriteLine($"  FrameFlow.Player: {typeof(FrameFlowPlayer).Assembly.GetName().Version}");
Check("managed assemblies resolved from the package", true);

// ── 2. The FFmpeg natives came with them ────────────────────────────────────
// The half that was actually in doubt. FrameFlow.Native carries a
// runtimes/{rid}/native payload; a file-based app has to resolve that the same
// way a project does.
var bootstrap = new FrameFlowBootstrapper(new FrameFlowNativeOptions());
var loaded = bootstrap.Initialize();
Check("FFmpeg natives loaded", loaded.IsSuccess, loaded.Message);

if (!loaded.IsSuccess)
{
    Console.Error.WriteLine("Cannot continue without natives.");
    return 1;
}

var input = args.FirstOrDefault() ?? SynthesiseClip();
if (input is null)
{
    Console.Error.WriteLine("No input given, and no staged ffmpeg to synthesise one.");
    return 1;
}

Console.WriteLine();
Console.WriteLine($"  Input: {input}");
Console.WriteLine();

// ── 3. A headless run, counting frames ──────────────────────────────────────
// HeadlessVideoSink is the counting sink from ADR Decision 4. A repro that
// needs a frame count and no window is exactly what it is for.
using var pool = new CpuFramePool(NullLogger<CpuFramePool>.Instance);
await using var sink = new HeadlessVideoSink(pool);

await using var player = await MediaPlayer.CreateAsync(
    source: MediaSource.FromFile(input),
    videoSink: sink
);

Check(
    "video stream present",
    player.MediaInfo.VideoStreams.Count > 0,
    player.MediaInfo.VideoStreams.Count > 0
        ? $"{player.MediaInfo.VideoStreams[0].CodecName} "
            + $"{player.MediaInfo.VideoStreams[0].Width}x{player.MediaInfo.VideoStreams[0].Height}, "
            + $"{player.Duration:mm\\:ss\\.fff}"
        : "none"
);

// ── 4. Seek, then ask ───────────────────────────────────────────────────────
// The shape the bench exists for and a run config cannot do: put the pipeline
// in a state, then interrogate it. In the grammar this is
// `seek 1s` / `expect position >= 900ms`.
await player.SeekAsync(TimeSpan.FromSeconds(1));
var immediately = player.Position;

// Position is clock-driven, so it does not step to the seek target the instant
// SeekAsync returns — it lands once the pipeline presents at the new point.
// Polling for it is what the grammar's `wait` command exists to express, and a
// repro needs the same idea whatever language it is written in.
var settled = immediately;
for (var i = 0; i < 50 && settled < TimeSpan.FromMilliseconds(900); i++)
{
    await Task.Delay(20);
    settled = player.Position;
}

Check(
    "seek moved the position",
    settled >= TimeSpan.FromMilliseconds(900),
    $"immediately {immediately:mm\\:ss\\.fff}, settled {settled:mm\\:ss\\.fff}"
);

await player.PlayAsync();
await Task.Delay(TimeSpan.FromSeconds(2));
await player.PauseAsync();

// `diag` in the grammar. Here it is a property with completion on it.
var diag = player.PollDiagnostics();
Check(
    "frames reached the sink",
    sink.PresentedCount > 0,
    $"presented {sink.PresentedCount}, abandoned {sink.AbandonedCount}"
);
Console.WriteLine($"          diagnostics: {diag}");

Console.WriteLine();
Console.WriteLine(new string('─', 64));
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

// ── Helpers ─────────────────────────────────────────────────────────────────

// A repro needs an input. Synthesising one with the staged ffmpeg keeps the
// spike self-contained rather than depending on a corpus that is gitignored.
static string? SynthesiseClip()
{
    var rid = OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsMacOS() ? (RuntimeArch() == "arm64" ? "osx-arm64" : "osx-x64")
        : $"linux-{RuntimeArch()}";
    var exe = Path.Combine(
        FindRepoRoot(),
        "runtimes",
        rid,
        "native",
        OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"
    );
    if (!File.Exists(exe))
        return null;

    var output = Path.Combine(Path.GetTempPath(), "frameflow-spike-clip.mp4");
    var psi = new ProcessStartInfo(exe)
    {
        Arguments =
            "-y -f lavfi -i testsrc=size=320x240:rate=30:duration=5 "
            + $"-c:v libopenh264 -pix_fmt yuv420p \"{output}\"",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };

    using var proc = Process.Start(psi)!;
    var stderr = proc.StandardError.ReadToEndAsync();
    proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0)
    {
        Console.Error.WriteLine(stderr.Result);
        return null;
    }
    return output;
}

static string RuntimeArch() =>
    System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
    == System.Runtime.InteropServices.Architecture.Arm64
        ? "arm64"
        : "x64";

static string FindRepoRoot()
{
    var dir = Environment.CurrentDirectory;
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir, "FrameFlow.slnx")))
            return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return Environment.CurrentDirectory;
}
