#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:package FrameFlow.Player@0.7.0-alpha.*
#:package FrameFlow.Native@0.7.0-alpha.*
#:package FrameFlow.Avalonia@0.7.0-alpha.*
#:package FrameFlow.Avalonia.Windows@0.7.0-alpha.*
#:package Avalonia.Desktop@11.*
#:package Avalonia.Themes.Fluent@11.*

// Signage shape: GPU zero-copy presenter, no audio sink. WINDOWS ONLY.
//
//   dotnet run scripts/repro/signage-gpu-noaudio.cs
//
// Converted from the Repro-Signage-NoAudio-GPU profile in
// examples/FrameFlow.Examples.AvaloniaPlayer/Properties/launchSettings.json, by
// way of a .bench script against a grammar that was dropped — see the resolution
// at the head of Decision 6 in docs/adr/command-driven-testbench-host.md.
//
// With no audio sink the player paces video off WallClockSource rather than the
// audio sample counter (ADR-0003), while the composition-interop presenter keeps
// D3D11VA frames on the GPU through present (ADR-0061). That pair is the signage
// deployment's shape, and it is the pair this reproduction pins.
//
// Observed against FrameFlow 0.7.0-alpha.4.11 on Windows 11, RTX 3080 Ti,
// D3D11VA. The directives float within 0.7.0-alpha so the file keeps running as
// the local feed advances; the build it was seen on is recorded here.
//
// Exit codes: 0 every check passed, 1 a check failed, 2 the reproduction could
// not run at all (wrong platform, missing fixture).

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using FrameFlow.Avalonia;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback.Diagnostics;
using FrameFlow.Player;
using Microsoft.Extensions.Logging.Abstractions;

const string Fixture = "tests/corpus/files/test-1080p-h264-aac.mp4";

// ── require presenter gpu ───────────────────────────────────────────────────
// The old grammar spelled this `require presenter gpu`, and the rule was that it
// checked the RESOLVED configuration rather than the flag string — because
// asking for the GPU surface off Windows silently gets you the CPU one while the
// request still reads "gpu". In C# the check is the construction: this file only
// ever builds CompositionInteropVideoView, and refuses to run where that cannot
// mean what it says.
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine(
        "This reproduction pins the zero-copy compositor presenter, which is "
            + "Windows-only. There is no software-path equivalent of it to fall back "
            + "to — a CPU run would be a different reproduction, not this one."
    );
    return 2;
}

var fixturePath = Path.Combine(Repro.RepoRoot(), Fixture);
if (!File.Exists(fixturePath))
{
    Console.Error.WriteLine(
        $"Fixture not found: {fixturePath}\n"
            + "Generate the corpus first: dotnet run scripts/generate-test-corpus.cs"
    );
    return 2;
}

var bootstrap = new FrameFlowBootstrapper(new FrameFlowNativeOptions());
var loaded = bootstrap.Initialize();
if (!loaded.IsSuccess)
{
    Console.Error.WriteLine($"FFmpeg did not load: {loaded.Message}");
    return 2;
}

var report = new Report();
var exitCode = 0;

// Avalonia owns the main thread, so the reproduction runs on a worker and the
// exit code has to outlive StartWithClassicDesktopLifetime.
AppBuilder
    .Configure<ReproApp>()
    .UsePlatformDetect()
    .AfterSetup(builder =>
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)
            builder.Instance!.ApplicationLifetime!;

        var surface = new CompositionInteropVideoView();
        var window = new Window
        {
            Title = "signage-gpu-noaudio",
            Width = 960,
            Height = 540,
            Content = new Panel { Children = { surface } },
        };
        lifetime.MainWindow = window;

        window.Opened += async (_, _) =>
        {
            // On the UI thread, deliberately. AttachSink initialises the compositor
            // surface, and doing it from the worker throws "Call from invalid thread"
            // out of Avalonia's dispatcher — the reproduction has to be built on the
            // thread that owns the window, then driven from another.
            //
            // Guarded separately from the run below, because the exit codes mean
            // different things. A compositor that will not initialise is "could not
            // run at all" (2), not "ran and something was wrong" (1). Left outside a
            // try it escaped the async handler entirely and the process exited on
            // whatever the host decided.
            IVideoSink videoSink;
            try
            {
                videoSink = ((IVideoSurface)surface).AttachSink(NullLoggerFactory.Instance);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not initialise the compositor surface: {ex}");
                exitCode = 2;
                window.Close();
                return;
            }

            try
            {
                exitCode = await Task.Run(() => RunAsync(videoSink, fixturePath, report));
            }
            catch (Exception ex)
            {
                report.Fail("the reproduction threw", ex.ToString());
                exitCode = 1;
            }
            finally
            {
                window.Close();
            }
        };
    })
    .StartWithClassicDesktopLifetime([]);

report.Print("signage-gpu-noaudio");
return exitCode;

// ── The reproduction ────────────────────────────────────────────────────────

static Task<int> RunAsync(IVideoSink videoSink, string path, Report report) =>
    Repro.RunAsync(videoSink, path, report);

internal static class Repro
{
    internal static async Task<int> RunAsync(IVideoSink videoSink, string path, Report report)
    {
    await using var player = await MediaPlayer.CreateAsync(
        source: MediaSource.FromFile(path),
        videoSink: videoSink,
        // require audio off. No sink at all, rather than a muted one: the point is
        // that video paces off the wall clock, and a silent audio sink would still
        // be the master.
        audioSink: null,
        yieldHardwareFrames: true
    );

    // 1920x1080, 30 fps, 3.0s — scripts/generate-test-corpus.cs
    report.Check(
        "duration is 3.0s ± 100ms",
        Math.Abs((player.Duration - TimeSpan.FromSeconds(3)).TotalMilliseconds) <= 100,
        player.Duration.ToString(@"mm\:ss\.fff")
    );

    await player.PlayAsync();

    // Everything here is asynchronous: PlayAsync returns before a frame arrives.
    // The old grammar had `wait` for this and it was the one part of it that was
    // load-bearing rather than incidental — asserting straight after an action
    // asserts against a pipeline that has not caught up.
    if (
        !await WaitUntilAsync(
            () => player.PollDiagnostics().Pipeline.VideoSink.FramesPresented >= 15,
            TimeSpan.FromSeconds(10)
        )
    )
    {
        report.Fail(
            "half a second of frames within 10s",
            Trajectory(player, "the pipeline never reached 15 presented frames")
        );
        return 1;
    }

    var settled = player.PollDiagnostics();

    // No audio sink, so there is nothing to drift against. Asserted rather than
    // assumed: a null drift read as zero would make an A/V assertion pass on a
    // pipeline that never produced timed audio at all.
    report.Check("no A/V drift to measure", settled.AvSyncDrift is null, $"{settled.AvSyncDrift}");

    // FramesCommitted stays zero on the WriteableBitmap path, so this catches a
    // silent fall back to the CPU surface — the failure where the reproduction runs
    // green because it stopped reproducing anything.
    report.Check(
        "frames reached the compositor",
        settled.Pipeline.VideoSink.FramesCommitted >= 1,
        $"committed {settled.Pipeline.VideoSink.FramesCommitted}"
    );

    // Hardware decode, without naming a backend: the same check holds under VAAPI
    // if this reproduction ever gains a Linux sibling.
    var backend = settled.Pipeline.Stream.VideoDecoder.HardwareBackend;
    report.Check("hardware decode engaged", backend is not null, backend?.ToString() ?? "software");

    // ── The window that matters ─────────────────────────────────────────────
    var warm = player.PollDiagnostics();
    var startedAt = Stopwatch.GetTimestamp();

    if (
        !await WaitUntilAsync(
            () => player.PollDiagnostics().State == PlaybackState.Ended,
            TimeSpan.FromSeconds(8)
        )
    )
    {
        report.Fail("playback reaches Ended within 8s", Trajectory(player, "still not Ended"));
        return 1;
    }

    var ended = player.PollDiagnostics();
    var elapsed = Stopwatch.GetElapsedTime(startedAt);

    // Generations differ only across a load, and there is none here — so a
    // difference would mean the session was rebuilt underneath the measurement and
    // the counters are not comparable. Checked rather than assumed, because
    // subtracting across it reports a restart as a drop burst.
    if (warm.SessionGeneration != ended.SessionGeneration)
    {
        report.Fail(
            "the measurement window stayed inside one session",
            $"generation {warm.SessionGeneration} → {ended.SessionGeneration}"
        );
        return 1;
    }

    Delta(report, "decode errors", warm, ended, s => s.Pipeline.Stream.VideoDecoder.DecodeErrors);
    Delta(
        report,
        "packets shed for backpressure",
        warm,
        ended,
        s => s.Pipeline.Stream.VideoDecoder.PacketsDroppedForBackpressure
    );
    Delta(report, "frames dropped for sync", warm, ended, s => s.Pipeline.VideoFramesDroppedForSync);
    Delta(report, "frames dropped by the sink", warm, ended, s => s.Pipeline.VideoSink.FramesDropped);

    var presented =
        ended.Pipeline.VideoSink.FramesPresented - warm.Pipeline.VideoSink.FramesPresented;
    var rate = presented / elapsed.TotalSeconds;
    report.Check(
        "presented at 25 fps or better",
        rate >= 25,
        $"{rate:0.0} fps ({presented} frames over {elapsed.TotalSeconds:0.00}s)"
    );

    return report.Failures == 0 ? 0 : 1;
}

// ── Harness ─────────────────────────────────────────────────────────────────
//
// Inlined rather than shared. A .NET file-based app is a single file: a helper
// would have to be a `#:project` reference, which makes the reproduction need a
// checkout and gives up the property that made `#:package` worth having — that
// one file plus the SDK is the whole thing. Duplicating sixty lines is the
// cheaper side of that trade.

internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
    while (Stopwatch.GetTimestamp() < deadline)
    {
        if (condition())
            return true;
        await Task.Delay(50);
    }
    return condition();
}

// A timeout has to separate "never started" from "started and stalled", so it
// prints the counters that tell those apart rather than only the one waited on.
internal static string Trajectory(IMediaPlayer player, string headline)
{
    var d = player.PollDiagnostics();
    return $"{headline}\n"
        + $"      state           {d.State} at {d.Position:mm\\:ss\\.fff}\n"
        + $"      demux.packets   {d.Pipeline.Stream.Demux.PacketsRead}\n"
        + $"      video.decoded   {d.Pipeline.Stream.VideoDecoder.FramesDecoded}\n"
        + $"      sink.presented  {d.Pipeline.VideoSink.FramesPresented}\n"
        + $"      sink.committed  {d.Pipeline.VideoSink.FramesCommitted}";
}

internal static void Delta(
    Report report,
    string what,
    PlaybackDiagnosticsSnapshot before,
    PlaybackDiagnosticsSnapshot after,
    Func<PlaybackDiagnosticsSnapshot, long> read
)
{
    var delta = read(after) - read(before);
    report.Check($"no {what} in the window", delta == 0, $"{delta} (total {read(after)})");
}

internal static string RepoRoot()
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

}

/// <summary>
/// Reports each check as it happens, then recaps.
/// </summary>
/// <remarks>
/// Live rather than batched, because a soak that printed nothing for six minutes
/// is indistinguishable from a hung one. The recap repeats only the failures, so
/// the end of a long run is the list of things to look at rather than a second
/// copy of everything.
/// </remarks>
internal sealed class Report
{
    private readonly List<string> _failures = [];

    internal int Failures => _failures.Count;

    internal void Check(string what, bool ok, string? detail = null)
    {
        var line = $"  {(ok ? "PASS" : "FAIL")}  {what}{(detail is null ? "" : $" — {detail}")}";
        Console.WriteLine(line);
        if (!ok)
            _failures.Add(line);
    }

    internal void Fail(string what, string detail)
    {
        var line = $"  FAIL  {what}" + Environment.NewLine + $"      {detail}";
        Console.WriteLine(line);
        _failures.Add(line);
    }

    internal void Print(string title)
    {
        Console.WriteLine(new string('─', 64));
        if (_failures.Count == 0)
        {
            Console.WriteLine($"{title}: all checks passed.");
            return;
        }

        Console.WriteLine($"{title}: {_failures.Count} check(s) failed.");
        foreach (var line in _failures)
            Console.WriteLine(line);
    }
}

internal sealed class ReproApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
