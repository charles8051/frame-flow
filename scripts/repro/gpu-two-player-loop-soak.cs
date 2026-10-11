#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:package FrameFlow.Player@0.15.*-*
#:package FrameFlow.Avalonia@0.15.*-*
#:package FrameFlow.Avalonia.Windows@0.15.*-*
#:package Avalonia.Desktop@11.*
#:package Avalonia.Themes.Fluent@11.*

// Two players looping at once, each on its own zero-copy presenter. WINDOWS ONLY.
//
//   dotnet run scripts/repro/gpu-two-player-loop-soak.cs
//   dotnet run scripts/repro/gpu-two-player-loop-soak.cs -- --duration 60m --csv soak.csv --label before
//
// signage-gpu-noaudio-soak.cs runs one player. This runs two side by side in one window, each with
// hardware decode and its own CompositionInteropVideoView: two presenters on one GPU, and with
// --hw-device two decoders on one device. Every --sample it reads both players, and a window in
// which a player presented nothing fails. At the end, a stalled loop restart or a playback error
// on either player fails.
//
// --csv appends one row per player per sample, and writes the header only for a new file, so a run
// before a change and a run after it can share one file and be told apart by --label.
//
//   --clip <path>                  the left player's clip (tests/corpus/files/test-1080p-h264-aac.mp4)
//   --second <path>                the right player's clip (the left one's)
//   --duration <d>                 how long to run, a whole number of --sample windows (10m)
//   --sample <d>                   time between samples (60s)
//   --csv <path>                   append the samples here
//   --label <name>                 the run column in the CSV (soak)
//   --hw-device d3d12va|d3d11va    decode both players on one device of that backend
//
// Observed against FrameFlow 0.15.2-alpha.0.14 on Windows 11, RTX 3080 Ti.
//
// Exit codes: 0 every check passed, 1 a check failed, 2 could not run at all. Closing the window
// stops the run early and fails it.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using FrameFlow.Avalonia;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Player;
using Microsoft.Extensions.Logging.Abstractions;

const string DefaultClip = "tests/corpus/files/test-1080p-h264-aac.mp4";

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This reproduction runs the zero-copy compositor presenter, which is Windows-only.");
    return 2;
}

// An absent flag takes its default. A malformed one exits 2 rather than running on the default.
if (
    !Soak.TryDuration(args, "--duration", TimeSpan.FromMinutes(10), out var duration)
    || !Soak.TryDuration(args, "--sample", TimeSpan.FromSeconds(60), out var sample)
    || !Soak.TryValue(args, "--clip", out var clipArg)
    || !Soak.TryValue(args, "--second", out var secondArg)
    || !Soak.TryValue(args, "--csv", out var csvPath)
    || !Soak.TryValue(args, "--label", out var labelArg)
    || !Soak.TryBackend(args, out var backend)
)
    return 2;

// A whole number of windows, so the run lasts --duration and every window is as long as the others.
if (sample <= TimeSpan.Zero || duration < sample || duration.Ticks % sample.Ticks != 0)
{
    Console.Error.WriteLine($"--duration ({duration}) must be a whole number of --sample windows ({sample}).");
    return 2;
}

var leftClip = clipArg ?? Path.Combine(Soak.RepoRoot(), DefaultClip);
var rightClip = secondArg ?? leftClip;
foreach (var clip in new[] { leftClip, rightClip }.Distinct())
{
    if (!File.Exists(clip))
    {
        Console.Error.WriteLine(
            $"Clip not found: {clip}\n" + "Generate the corpus with: dotnet run scripts/generate-test-corpus.cs"
        );
        return 2;
    }
}

var label = labelArg ?? "soak";
using var csv = csvPath is null ? null : Csv.TryOpen(csvPath);
if (csvPath is not null && csv is null)
    return 2;

var windows = (int)(duration / sample);
Console.WriteLine(
    $"Soak '{label}': two players, {windows} window(s) of {sample.TotalSeconds:0}s"
        + (backend is null ? "." : $", both decoding on one {backend} device.")
);

var report = new Report();
var exitCode = 0;
// Closing the window mid-run cancels the run, waits for it to dispose the players, and fails it:
// an aborted soak is not a pass.
using var cancel = new CancellationTokenSource();
var running = false;
var finished = false;

AppBuilder
    .Configure<SoakApp>()
    .UsePlatformDetect()
    .AfterSetup(builder =>
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)builder.Instance!.ApplicationLifetime!;

        var leftView = new CompositionInteropVideoView();
        var rightView = new CompositionInteropVideoView();
        Grid.SetColumn(rightView, 1);
        var host = new Window
        {
            Title = "gpu-two-player-loop-soak",
            Width = 1280,
            Height = 360,
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                Children = { leftView, rightView },
            },
        };
        lifetime.MainWindow = host;

        host.Closing += (_, e) =>
        {
            if (!running)
                return;
            e.Cancel = true;
            cancel.Cancel();
        };

        host.Opened += async (_, _) =>
        {
            // On the UI thread: AttachSink initialises the compositor surface, and doing it from
            // the worker throws "Call from invalid thread". A surface that will not initialise is
            // "could not run at all", so it is caught apart from the run.
            Pane[] panes;
            try
            {
                panes =
                [
                    new Pane("left", leftClip, ((IVideoSurface)leftView).AttachSink(NullLoggerFactory.Instance)),
                    new Pane("right", rightClip, ((IVideoSurface)rightView).AttachSink(NullLoggerFactory.Instance)),
                ];
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not initialise the compositor surfaces: {ex}");
                exitCode = 2;
                host.Close();
                return;
            }

            running = true;
            try
            {
                exitCode = await Task.Run(() =>
                    Soak.RunAsync(panes, backend, windows, sample, label, csv, report, cancel.Token)
                );
                finished = true;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                report.Fail("the run finished", "the window was closed first");
                exitCode = 1;
            }
            catch (Exception ex)
            {
                report.Fail("the reproduction threw", ex.ToString());
                exitCode = 1;
            }
            finally
            {
                // After the players are disposed, so each view outlives the player feeding it.
                running = false;
                host.Close();
            }
        };
    })
    .StartWithClassicDesktopLifetime([]);

if (exitCode != 2 && !finished && report.Failures == 0)
{
    report.Fail("the run finished", "the window closed before it did");
    exitCode = 1;
}
if (exitCode != 2)
    report.Print("gpu-two-player-loop-soak");
return exitCode;

internal static class Soak
{
    internal static async Task<int> RunAsync(
        Pane[] panes,
        HardwareDecodeBackendKind? backend,
        int windows,
        TimeSpan sample,
        string label,
        Csv? csv,
        Report report,
        CancellationToken cancellation
    )
    {
        var players = new List<IMediaPlayer>();
        HardwareDevice? device = null;
        try
        {
            if (backend is { } kind)
            {
                try
                {
                    device = HardwareDevice.Create(kind);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not create a {kind} device: {ex.Message}");
                    return 2;
                }
            }

            foreach (var pane in panes)
            {
                IMediaPlayer player;
                try
                {
                    // Required decode and WithHardwareFrames: a soak of the software path, or of
                    // frames downloaded on the way, would report on a path this one is not about.
                    var builder = FrameFlowPlayer
                        .Create()
                        .WithMedia(pane.Clip)
                        .WithVideoSink(pane.Sink)
                        .WithRepeatMode(RepeatMode.One)
                        .WithHardwareDecode(HardwareDecodeMode.Required)
                        .WithHardwareFrames();
                    if (device is not null)
                        builder = builder.WithHardwareDevice(device);
                    player = await builder.BuildPlayerAsync(cancellation);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"The {pane.Name} player could not be built: {ex.Message}");
                    return 2;
                }

                players.Add(player);
                // Subscribed before play, so no restart or stall happens before anyone is listening.
                pane.Watch(player);

                var played = await player.PlayAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!played.IsSuccess)
                {
                    Console.Error.WriteLine(
                        $"The {pane.Name} player refused to play: {played.Error.Category}: {played.Error.Message}"
                    );
                    return 2;
                }
            }

            // Each window counts from here, not from when its player started.
            var started = Stopwatch.GetTimestamp();
            foreach (var pane in panes)
                pane.Baseline();
            for (var window = 1; window <= windows; window++)
            {
                await Task.Delay(sample, cancellation);
                var elapsed = Stopwatch.GetElapsedTime(started);

                foreach (var pane in panes)
                {
                    var row = pane.Sample(elapsed);
                    Console.WriteLine(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"  {elapsed.TotalSeconds,7:0.0}s  {pane.Name,-5}  {row.State,-8}  {row.Fps,5:0.0} fps  "
                                + $"{row.Loops} loops  {row.LoopStalls} stalls  {row.Errors} errors"
                        )
                    );

                    if (row.Presented == 0)
                        pane.FrozenWindows.Add($"{elapsed.TotalSeconds:0}s");

                    if (csv is not null && csv.Write(label, elapsed, pane, row) is { } failure)
                    {
                        report.Fail("every sample was written to the CSV", failure);
                        return 1;
                    }
                }
            }

            foreach (var pane in panes)
            {
                report.Check(
                    $"{pane.Name}: presented frames in every window",
                    pane.FrozenWindows.Count == 0,
                    pane.FrozenWindows.Count == 0 ? null : $"none in the window ending at {string.Join(", ", pane.FrozenWindows)}"
                );
                // Counted from the edge-triggered observable. The snapshot's LoopStalled is
                // level-triggered, so a stall that recovered before the last sample reads false.
                report.Check($"{pane.Name}: no stalled loop restart", pane.LoopStalls == 0, $"{pane.LoopStalls}");
                report.Check($"{pane.Name}: no playback error", pane.Errors == 0, $"{pane.Errors}");
            }

            return report.Failures == 0 ? 0 : 1;
        }
        finally
        {
            foreach (var pane in panes)
                pane.Dispose();
            foreach (var player in players)
                await player.DisposeAsync();
            // Last: the players' decoders borrowed it.
            device?.Dispose();
        }
    }

    /// <summary>The longest duration an argument may name.</summary>
    private const double MaxDurationMs = 24 * 60 * 60 * 1_000;

    /// <summary>
    /// Reads a duration such as <c>--sample 30s</c>. False only when the flag was given and its value
    /// is not a duration; an absent flag takes <paramref name="fallback"/>.
    /// </summary>
    internal static bool TryDuration(string[] args, string flag, TimeSpan fallback, out TimeSpan value)
    {
        value = fallback;
        if (!TryValue(args, flag, out var text))
            return false;
        if (text is null)
            return true;

        // "ms" before "m", or every millisecond value is wrong by a factor of 60,000.
        foreach (var (suffix, perUnitMs) in new[] { ("ms", 1.0), ("s", 1_000.0), ("m", 60_000.0) })
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (
                !double.TryParse(text[..^suffix.Length], CultureInfo.InvariantCulture, out var number)
                || number < 0
            )
                break;

            // Range-checked before converting: TimeSpan.FromMilliseconds throws past its range.
            var milliseconds = number * perUnitMs;
            if (double.IsNaN(milliseconds) || milliseconds > MaxDurationMs)
            {
                Console.Error.WriteLine($"{flag}: '{text}' is out of range. The longest accepted value is 24h.");
                return false;
            }

            value = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        Console.Error.WriteLine($"{flag}: '{text}' is not a duration. A number and a unit: 250ms, 30s, 2m.");
        return false;
    }

    /// <summary>
    /// Reads the value after <paramref name="flag"/>: null when the flag is absent, false when it is
    /// given with nothing after it.
    /// </summary>
    internal static bool TryValue(string[] args, string flag, out string? value)
    {
        value = null;
        var index = Array.IndexOf(args, flag);
        if (index < 0)
            return true;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{flag} needs a value.");
            return false;
        }
        value = args[index + 1];
        return true;
    }

    internal static bool TryBackend(string[] args, out HardwareDecodeBackendKind? backend)
    {
        backend = null;
        if (!TryValue(args, "--hw-device", out var text))
            return false;
        switch (text?.ToLowerInvariant())
        {
            case null:
                return true;
            case "d3d12va":
                backend = HardwareDecodeBackendKind.D3D12Va;
                return true;
            case "d3d11va":
                backend = HardwareDecodeBackendKind.D3D11Va;
                return true;
            default:
                Console.Error.WriteLine($"--hw-device: '{text}' is not d3d12va or d3d11va.");
                return false;
        }
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

/// <summary>One sample of one player.</summary>
internal sealed record Row(
    string State,
    string Repeat,
    double PositionSeconds,
    long Presented,
    long PresentedTotal,
    long DroppedTotal,
    long DroppedForSyncTotal,
    int Loops,
    int LoopStalls,
    int Errors,
    double Fps
);

/// <summary>
/// One player and its presenter. Counts loops, stalls and errors from the player's observables, and
/// turns the sink's running frame count into a count per window.
/// </summary>
internal sealed class Pane(string name, string clip, IVideoSink sink) : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private IMediaTransport? _player;
    private long _presentedAtLastSample;
    private TimeSpan _elapsedAtLastSample;
    private int _loops;
    private int _loopStalls;
    private int _errors;

    public string Name { get; } = name;

    public string Clip { get; } = clip;

    public IVideoSink Sink { get; } = sink;

    /// <summary>The end of each window in which this player presented nothing.</summary>
    public List<string> FrozenWindows { get; } = [];

    public int LoopStalls => Volatile.Read(ref _loopStalls);

    public int Errors => Volatile.Read(ref _errors);

    public void Watch(IMediaTransport player)
    {
        _player = player;
        _subscriptions.Add(
            player.LoopRestarted.Subscribe(new ActionObserver<LoopRestarted>(_ => Interlocked.Increment(ref _loops)))
        );
        _subscriptions.Add(
            player.LoopStalled.Subscribe(new ActionObserver<LoopStalled>(_ => Interlocked.Increment(ref _loopStalls)))
        );
        _subscriptions.Add(
            player.ErrorOccurred.Subscribe(new ActionObserver<PlaybackError>(_ => Interlocked.Increment(ref _errors)))
        );
    }

    /// <summary>Starts the first window at the frames presented so far.</summary>
    public void Baseline() =>
        _presentedAtLastSample = _player!.GetDiagnostics().Pipeline.VideoSink.FramesPresented;

    public Row Sample(TimeSpan elapsed)
    {
        var diagnostics = _player!.GetDiagnostics();
        var presentedTotal = diagnostics.Pipeline.VideoSink.FramesPresented;
        var presented = presentedTotal - _presentedAtLastSample;
        var window = (elapsed - _elapsedAtLastSample).TotalSeconds;
        _presentedAtLastSample = presentedTotal;
        _elapsedAtLastSample = elapsed;

        return new Row(
            diagnostics.State.ToString(),
            diagnostics.RepeatMode.ToString(),
            diagnostics.Position.TotalSeconds,
            presented,
            presentedTotal,
            diagnostics.Pipeline.VideoSink.FramesDropped,
            diagnostics.Pipeline.VideoFramesDroppedForSync,
            Volatile.Read(ref _loops),
            LoopStalls,
            Errors,
            window > 0 ? presented / window : 0
        );
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
    }
}

/// <summary>
/// The samples, appended one row per player per sample. The header is written only for a new file,
/// so two runs can share one and be told apart by the run column.
/// </summary>
internal sealed class Csv : IDisposable
{
    private const string Header =
        "run,elapsed_s,pane,clip,state,repeat,position_s,presented,presented_total,"
        + "dropped_total,dropped_for_sync_total,loops,loop_stalled,errors,fps";

    private readonly StreamWriter _writer;

    private Csv(StreamWriter writer) => _writer = writer;

    /// <summary>Opens <paramref name="path"/> for appending, or reports why it cannot.</summary>
    public static Csv? TryOpen(string path)
    {
        try
        {
            var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
            var endsMidRow = !isNew && !EndsWithNewLine(path);
            var writer = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
            if (isNew)
                writer.WriteLine(Header);
            else if (endsMidRow)
                // A run killed mid-write leaves a partial row. Start on a line of our own.
                writer.WriteLine();
            return new Csv(writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"The samples cannot be written to {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes one row, and returns why it could not, or null.</summary>
    public string? Write(string label, TimeSpan elapsed, Pane pane, Row row)
    {
        try
        {
            _writer.WriteLine(
                string.Join(
                    ',',
                    Field(label),
                    Number(elapsed.TotalSeconds),
                    Field(pane.Name),
                    Field(Path.GetFileName(pane.Clip)),
                    row.State,
                    row.Repeat,
                    Number(row.PositionSeconds),
                    row.Presented,
                    row.PresentedTotal,
                    row.DroppedTotal,
                    row.DroppedForSyncTotal,
                    row.Loops,
                    row.LoopStalls,
                    row.Errors,
                    Number(row.Fps)
                )
            );
            return null;
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
    }

    public void Dispose() => _writer.Dispose();

    private static bool EndsWithNewLine(string path)
    {
        using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        probe.Seek(-1, SeekOrigin.End);
        return probe.ReadByte() == '\n';
    }

    private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>Quotes a field holding a comma, a quote or a line break, so it cannot shift the columns.</summary>
    private static string Field(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}

/// <summary>Reports each check as it happens, then recaps the failures.</summary>
internal sealed class Report
{
    private readonly List<string> _failures = [];

    internal int Failures => _failures.Count;

    internal void Check(string what, bool ok, string? detail = null)
    {
        var line = $"  {(ok ? "PASS" : "FAIL")}  {what}{(detail is null ? "" : $": {detail}")}";
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
        Console.WriteLine(new string('-', 64));
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

/// <summary>Minimal <see cref="IObserver{T}"/> over an action.</summary>
internal sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void OnNext(T value) => onNext(value);
}

internal sealed class SoakApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
