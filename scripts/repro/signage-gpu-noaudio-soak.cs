#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:package FrameFlow.Player@0.7.0-alpha.*
#:package FrameFlow.Native@0.7.0-alpha.*
#:package FrameFlow.Avalonia@0.7.0-alpha.*
#:package FrameFlow.Avalonia.Windows@0.7.0-alpha.*
#:package Avalonia.Desktop@11.*
#:package Avalonia.Themes.Fluent@11.*

// Signage shape, sustained. WINDOWS ONLY. Takes about six minutes.
//
//   dotnet run scripts/repro/signage-gpu-noaudio-soak.cs
//   dotnet run scripts/repro/signage-gpu-noaudio-soak.cs -- --window 10s --gap 20s
//
// signage-gpu-noaudio.cs plays a 3-second clip once, because that is what the
// launch profile it came from named. The symptom that profile is named after
// builds over a long unattended run, and three seconds of testsrc2 cannot show
// it. Same shape, with a fixture and a duration that can fail.
//
// The fixture is bench-1080p60-h264-aac.mp4: 45s of noise at 15 Mbps, 1080p60.
// Noise rather than testsrc2, which "encodes to almost nothing" and costs the
// decoder nothing to play. It is behind --include-benchmarks because of its size.
//
// The window is 30s because the fixture is 45s: generate-test-corpus.cs picked
// that length for exactly this measurement. A window longer than the clip is
// guaranteed to contain a loop restart; at 30s it usually does not, and the
// 57 fps threshold absorbs one when it does. Nothing here can promise a window
// free of restarts under RepeatMode.One, so the check tolerates one rather than
// claiming there is none.
//
// The two windows are the point. A rate that holds in the first and not the
// second is the thing worth chasing; one that holds in the second and not the
// first is a slow start. A single window cannot tell them apart.
//
// --window and --gap exist to make this iterable while working on it. The
// defaults are the soak; anything shorter is a smoke test wearing its name.
//
// Observed against FrameFlow 0.7.0-alpha.4.11 on Windows 11, RTX 3080 Ti.
//
// Exit codes: 0 every check passed, 1 a check failed, 2 could not run at all.

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

const string Fixture = "tests/corpus/files/bench-1080p60-h264-aac.mp4";

var window = Soak.Argument(args, "--window") ?? TimeSpan.FromSeconds(30);
var gap = Soak.Argument(args, "--gap") ?? TimeSpan.FromSeconds(240);

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine(
        "This reproduction pins the zero-copy compositor presenter, which is Windows-only."
    );
    return 2;
}

var fixturePath = Path.Combine(Soak.RepoRoot(), Fixture);
if (!File.Exists(fixturePath))
{
    Console.Error.WriteLine(
        $"Fixture not found: {fixturePath}\n"
            + "It is behind a flag because of its size:\n"
            + "  dotnet run scripts/generate-test-corpus.cs -- --include-benchmarks"
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

Console.WriteLine(
    $"Soak: two {window.TotalSeconds:0}s windows, {gap.TotalSeconds:0}s apart. "
        + $"About {(2 * window + gap).TotalMinutes:0.0} minutes."
);

var report = new Report();
var exitCode = 0;

AppBuilder
    .Configure<SoakApp>()
    .UsePlatformDetect()
    .AfterSetup(builder =>
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)
            builder.Instance!.ApplicationLifetime!;

        var surface = new CompositionInteropVideoView();
        var host = new Window
        {
            Title = "signage-gpu-noaudio-soak",
            Width = 960,
            Height = 540,
            Content = new Panel { Children = { surface } },
        };
        lifetime.MainWindow = host;

        host.Opened += async (_, _) =>
        {
            // On the UI thread, deliberately. AttachSink initialises the compositor
            // surface, and doing it from the worker throws "Call from invalid thread"
            // out of Avalonia's dispatcher.
            var videoSink = ((IVideoSurface)surface).AttachSink(NullLoggerFactory.Instance);

            try
            {
                exitCode = await Task.Run(
                    () => Soak.RunAsync(videoSink, fixturePath, window, gap, report)
                );
            }
            catch (Exception ex)
            {
                report.Fail("the reproduction threw", ex.ToString());
                exitCode = 1;
            }
            finally
            {
                host.Close();
            }
        };
    })
    .StartWithClassicDesktopLifetime([]);

report.Print("signage-gpu-noaudio-soak");
return exitCode;

internal static class Soak
{
    internal static async Task<int> RunAsync(
        IVideoSink videoSink,
        string path,
        TimeSpan window,
        TimeSpan gap,
        Report report
    )
    {
        await using var player = await MediaPlayer.CreateAsync(
            source: MediaSource.FromFile(path),
            videoSink: videoSink,
            audioSink: null,
            // RepeatMode.One is what makes this a soak rather than a 45-second run.
            initialRepeatMode: RepeatMode.One,
            yieldHardwareFrames: true
        );

        // 1920x1080, 60 fps, 45.0s — scripts/generate-test-corpus.cs
        report.Check(
            "duration is 45.0s ± 200ms",
            Math.Abs((player.Duration - TimeSpan.FromSeconds(45)).TotalMilliseconds) <= 200,
            player.Duration.ToString(@"mm\:ss\.fff")
        );

        await player.PlayAsync();

        // Two seconds of frames, past the warm-up. Measuring from the first frame
        // would fold start-up cost into the first window and make it the slow one
        // every time.
        if (
            !await WaitUntilAsync(
                () => player.PollDiagnostics().Pipeline.VideoSink.FramesPresented >= 120,
                TimeSpan.FromSeconds(15)
            )
        )
        {
            report.Fail("two seconds of frames within 15s", Trajectory(player, "never warmed up"));
            return 1;
        }

        var first = await MeasureAsync(player, window, report, "first");

        Console.WriteLine($"  … waiting {gap.TotalSeconds:0}s between windows");
        await Task.Delay(gap);

        var second = await MeasureAsync(player, window, report, "second");

        // The comparison the two windows exist for. A soak that only reported each
        // window against an absolute threshold would pass a run that halved.
        if (first is { } a && second is { } b)
        {
            var drop = a > 0 ? (a - b) / a : 0;
            report.Check(
                "the second window is within 10% of the first",
                drop <= 0.10,
                $"{a:0.0} fps → {b:0.0} fps ({drop * 100:0.0}% lower)"
            );
        }

        // RepeatMode.One restarts have their own stall detector (ADR-0034). A stalled
        // loop presents as a frozen last frame while the clock keeps advancing, which
        // reads as choppiness rather than as a stop.
        report.Check("no stalled loop restart", !player.PollDiagnostics().LoopStalled);

        return report.Failures == 0 ? 0 : 1;
    }

    /// <summary>Measures one window, and returns its presented rate.</summary>
    private static async Task<double?> MeasureAsync(
        IMediaPlayer player,
        TimeSpan window,
        Report report,
        string name
    )
    {
        var before = player.PollDiagnostics();
        var startedAt = Stopwatch.GetTimestamp();
        await Task.Delay(window);
        var after = player.PollDiagnostics();
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        // A loop restart under RepeatMode.One does NOT advance the session generation
        // — the session is reused, which is the whole point of the warm-presenter
        // path. So this catches a genuine session rebuild, not a lap.
        if (before.SessionGeneration != after.SessionGeneration)
        {
            report.Fail(
                $"the {name} window stayed inside one session",
                $"generation {before.SessionGeneration} → {after.SessionGeneration}"
            );
            return null;
        }

        Delta(report, $"{name}: decode errors", before, after,
            s => s.Pipeline.Stream.VideoDecoder.DecodeErrors);
        Delta(report, $"{name}: packets shed", before, after,
            s => s.Pipeline.Stream.VideoDecoder.PacketsDroppedForBackpressure);
        Delta(report, $"{name}: frames dropped for sync", before, after,
            s => s.Pipeline.VideoFramesDroppedForSync);
        Delta(report, $"{name}: frames dropped by the sink", before, after,
            s => s.Pipeline.VideoSink.FramesDropped);

        var presented =
            after.Pipeline.VideoSink.FramesPresented - before.Pipeline.VideoSink.FramesPresented;
        var rate = presented / elapsed.TotalSeconds;

        // 57, not 60. The fixture is 60 fps and a loop restart costs a few frames.
        // A 30s window inside a 45s clip usually misses the restart and sometimes
        // does not, so demanding the nominal rate would fail on a healthy run
        // depending on where the window happened to land.
        report.Check(
            $"{name}: presented at 57 fps or better",
            rate >= 57,
            $"{rate:0.0} fps ({presented} frames over {elapsed.TotalSeconds:0.00}s)"
        );

        return rate;
    }

    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline =
            Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

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
        report.Check($"no {what}", delta == 0, $"{delta} (total {read(after)})");
    }

    /// <summary>Reads a duration argument such as <c>--window 30s</c>.</summary>
    internal static TimeSpan? Argument(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0 || index + 1 >= args.Length)
            return null;

        var text = args[index + 1];
        foreach (var (suffix, perUnitMs) in new[] { ("ms", 1.0), ("s", 1_000.0), ("m", 60_000.0) })
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (double.TryParse(text[..^suffix.Length], out var value) && value >= 0)
                return TimeSpan.FromMilliseconds(value * perUnitMs);
        }
        return null;
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

internal sealed class SoakApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
