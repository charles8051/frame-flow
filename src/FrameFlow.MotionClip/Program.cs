// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Reflection;
using Avalonia;
using FrameFlow.MotionClip;
using Microsoft.Extensions.Logging;
using Velopack;

namespace FrameFlow.MotionClip;

/// <summary>
/// Entry point for the motion-triggered clip recorder (ADR-0052).
/// <c>scan</c> lists cameras; <c>run</c> (the default verb) records. Default run
/// tracks the first available camera with a windowed preview; <c>--IdStartsWith</c>
/// selects a specific camera, <c>--headless</c> drops the UI, <c>--synthetic</c>
/// uses a generated scene. See <see cref="ClipRecorderArgs"/> for the full set.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack hook entry point -- MUST be the first thing in Main when
        // shipping via a Velopack installer. When MotionClip is launched by
        // Velopack install/update/uninstall flows (with special hook args),
        // this handles the event and exits; on a normal launch it's a no-op
        // and falls through. Safe even when the app wasn't installed via
        // Velopack (dotnet tool, raw exe, etc.). Not auto-update -- this is
        // just the bootstrap hook; auto-update would need a separate
        // UpdateManager.CheckForUpdatesAsync call which isn't wired here.
        VelopackApp.Build().Run();

        ClipRecorderArgs parsed = ClipRecorderArgs.Parse(args);

        switch (parsed.Verb)
        {
            case MotionClipVerb.Help:
                PrintHelp();
                return 0;
            case MotionClipVerb.Version:
                PrintVersion();
                return 0;
            case MotionClipVerb.Scan:
                return CameraTracking.ScanAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        // Verb is Run from here.
        if (parsed.Headless)
            return RunHeadlessAsync(parsed).GetAwaiter().GetResult();

        // Windowed run hands off to Avalonia. App re-parses argv with the same parser.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void PrintVersion()
    {
        string version =
            typeof(Program)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0]
            ?? typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        Console.WriteLine($"motionclip {version}");
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            motionclip — motion-triggered pre-roll clip recorder (ADR-0052)

            USAGE:
              motionclip <command> [options]

            COMMANDS:
              run     Record. Tracks a camera (or --synthetic) and writes a clip
                      that begins before detected motion.
              scan    List cameras (Id + name), then exit.

            RUN OPTIONS:
              --IdStartsWith <prefix>   Track the camera whose Id starts with <prefix>.
              --camera <index>          Track the camera at this enumeration index.
              --synthetic               Use the generated scene instead of a camera.
              --headless                Run with no preview window.
              --sensitivity <s>         Motion sensitivity 0.0-1.0. Higher = more
                                        sensitive: 0.1 ignores all but big movements,
                                        0.8 (default) catches normal activity,
                                        1.0 triggers on tiny twitches.
              --motion-sectors <list>   Restrict motion detection to a subset of a
                                        3x3 grid numbered like a numpad (7-8-9 top,
                                        4-5-6 middle, 1-2-3 bottom). Examples: "5"
                                        (center only), "4,5,6" (middle row),
                                        "1,2,3,4,5,6" (ignore the ceiling). Preview
                                        and recording are unaffected. Default: "all"
                                        (every sector armed).
              --camera-buffers <n>      Pre-allocated camera buffer pool (default 3).
                                        Bump to 6-8 if you see "Frame dropped (#N)"
                                        warnings during encoder bursts.
              --output-dir <dir>        Where clips are written (default ./clips).
              --fps <n>                 Source/clip frame rate (default 30).
              --exit-after <seconds>    Self-stop after N seconds (0 = until Ctrl+C/close).
              --log-file <path>         Opt-in file log sink (explicit path).
              --log-dir <dir>           Write a timestamped log file into <dir>.
              --log-level <level>       trace|debug|info|warning|error|critical|none
                                        (default info).

            GLOBAL:
              -h, --help                Show this help and exit.
              --version                 Print version and exit.

            EXAMPLES:
              motionclip scan
              motionclip run
              motionclip run --IdStartsWith "USB\VID_046D" --sensitivity 0.9
              motionclip run --headless --synthetic --exit-after 12
            """
        );

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static async Task<int> RunHeadlessAsync(ClipRecorderArgs a)
    {
        using ILoggerFactory loggerFactory = RecorderLogging.Create(
            a.LogFile,
            a.LogDirectory,
            a.LogLevel
        );

        await using MotionClipRecorder? recorder = MotionClipRecorder.Create(
            a,
            preview: null,
            loggerFactory
        );
        if (recorder is null)
            return 1; // FFmpeg native bootstrap failed; the factory logged it.

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // drain + save instead of a hard kill
            cts.Cancel();
        };
        if (a.ExitAfterSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(a.ExitAfterSeconds));

        string output = Path.GetFullPath(a.OutputDirectory);

        if (a.Synthetic)
        {
            FrameFlow.Graph.Graph graph = recorder.BuildGraph(
                SyntheticSceneSource.Create(
                    RecorderPipeline.CaptureWidth,
                    RecorderPipeline.CaptureHeight,
                    a.FrameRate
                )
            );
            recorder.Logger.LogInformation(
                "MotionClip running headless on the synthetic scene "
                    + "(fps={Fps}, sensitivity={Sensitivity:0.##} → ratio={Threshold:0.###}, "
                    + "output={Out}). Ctrl+C to stop.",
                a.FrameRate,
                a.Sensitivity,
                a.MotionThreshold,
                output
            );
            try
            {
                await graph.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on Ctrl+C / --exit-after.
            }
        }
        else
        {
            Periphery.DeviceProfile profile = await CameraTracking
                .BuildProfileAsync(a.IdStartsWith, a.CameraIndex, recorder.Logger, cts.Token)
                .ConfigureAwait(false);
            recorder.Logger.LogInformation(
                "MotionClip running headless (fps={Fps}, sensitivity={Sensitivity:0.##} → "
                    + "ratio={Threshold:0.###}, output={Out}). Tracking camera — "
                    + "runs whether or not it's plugged in; connects when it appears. Ctrl+C to stop.",
                a.FrameRate,
                a.Sensitivity,
                a.MotionThreshold,
                output
            );

            // Host disposes at the end of this block, tearing down any active
            // session before the recorder's own DisposeAsync runs its final
            // flush + encoder drain.
            await using (
                await recorder
                    .StartCameraAsync(profile, onDisconnected: null, cts.Token)
                    .ConfigureAwait(false)
            )
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C / --exit-after.
                }
            }
        }

        // recorder.DisposeAsync runs at the end of the await-using scope:
        // flushes any half-built clip, drains the encoder worker, logs the
        // final clip count.
        return 0;
    }
}
