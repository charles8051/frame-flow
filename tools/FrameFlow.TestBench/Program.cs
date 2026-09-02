using Avalonia;
using FrameFlow.Media;
// FrameFlow.Avalonia shadows the Avalonia root namespace inside FrameFlow.*, so a
// fully-qualified Avalonia.Controls.* below would bind to FrameFlow.Avalonia.Controls
// and fail to resolve. The alias pins it to the real one.
using DesktopLifetime = global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.TestBench;

/// <summary>
/// A console host that builds a real pipeline from the public API, keeps it warm, and
/// takes typed commands.
/// </summary>
/// <remarks>
/// <para>
/// The loop the examples cannot close. Their workflow is launch, wait, read the log:
/// every question costs a full cycle, and any question the existing flags do not answer
/// costs a code change first. Here the pipeline stays up between questions, so state
/// that took thirty seconds to reach is not thrown away to ask the next one.
/// </para>
/// <para>
/// A console subsystem rather than <c>WinExe</c>, which is what makes one interleaved
/// stream possible: the command, the reply, and every log line the pipeline emitted in
/// between, in the order they happened. That ordering is the artifact worth pasting into
/// an issue, and two processes writing two logs cannot produce it without clock
/// correlation. <c>FrameFlow.MotionClip</c> is the precedent — an <c>Exe</c> that also
/// opens an Avalonia window, and pays a console window beside it.
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    internal static int Main(string[] args)
    {
        var (options, message, isHelp) = BenchOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(isHelp ? BenchOptions.HelpText : message);
            if (!isHelp)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(BenchOptions.HelpText);
            }
            return isHelp ? BenchSession.ExitOk : BenchSession.ExitDidNotParse;
        }

        // Parse the whole script before building anything. A typo on line 40 is not
        // worth discovering after a thirty-second run, and it is certainly not worth
        // opening a window and an audio device to find out about.
        if (!TryReadScript(options, out var scripted, out var scriptExit))
            return scriptExit;

        using var log = options.LogFile is { } logFile
            ? new StreamWriter(logFile, append: false) { AutoFlush = true }
            : null;
        using TextWriter output = log is null ? Console.Out : new TeeTextWriter(Console.Out, log);

        var bootstrap = new FrameFlowBootstrapper(new FrameFlowNativeOptions());
        var loaded = bootstrap.Initialize();
        if (!loaded.IsSuccess)
        {
            output.WriteLine($"FAIL  FFmpeg did not load: {loaded.Message}");
            return BenchSession.ExitCommandFailed;
        }

        output.WriteLine(loaded.Message);

        var presenter = PresenterSelection.Resolve(options.Presenter);
        output.WriteLine(DiagnosticsRenderer.Presenter(presenter));

        // These two shape the headless sink and reach nothing else. Silently ignoring
        // them would let a windowed run be read as having had a synthetic cost applied.
        if (presenter.NeedsWindow && options.PresentCost > TimeSpan.Zero)
            output.WriteLine(
                "note      --present-cost applies to the headless sink only, and is "
                    + "ignored by a windowed presenter."
            );

        var session = new BenchSession(options, presenter, loaded.Capabilities, output);

        return presenter.NeedsWindow
            ? RunWindowed(options, presenter, session, scripted, output)
            : RunHeadless(options, session, scripted);
    }

    /// <summary>
    /// No window: the command loop owns the main thread, as it did before presenters
    /// existed.
    /// </summary>
    private static int RunHeadless(
        BenchOptions options,
        BenchSession session,
        List<BenchCommand>? scripted
    )
    {
        // A bounded pool on purpose: it is what makes the decoder block once frames are
        // in flight, so a --present-cost propagates back as real backpressure.
        using var pool = new CpuFramePool(NullLogger<CpuFramePool>.Instance, options.PoolCapacity);
        var sink = new HeadlessVideoSink(pool, options.PresentCost);

        using var ctrlC = CancelOnCtrlC();
        try
        {
            return session
                .RunAsync(sink, sink, scripted, ctrlC.Token)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// A window: Avalonia owns the main thread, so the command loop runs on a worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>StartWithClassicDesktopLifetime</c> blocks until the last window closes, and
    /// the video surfaces are UI-thread affine — both presenters marshal their own work
    /// onto the dispatcher. So the loop cannot have the main thread, and the exit code
    /// has to outlive the call rather than be returned by it.
    /// </para>
    /// <para>
    /// Closing the window ends the run, and so does <c>quit</c>. Both paths converge on
    /// the same teardown: whichever happens first cancels the other.
    /// </para>
    /// </remarks>
    private static int RunWindowed(
        BenchOptions options,
        PresenterSelection presenter,
        BenchSession session,
        List<BenchCommand>? scripted,
        TextWriter output
    )
    {
        var exitCode = BenchSession.ExitOk;
        var closing = new CancellationTokenSource();

        var app = AppBuilder.Configure<BenchApp>().UsePlatformDetect().LogToTrace();

        app.AfterSetup(_ =>
        {
            var lifetime = (DesktopLifetime)app.Instance!.ApplicationLifetime!;

            var window = new BenchWindow(presenter);
            lifetime.MainWindow = window;

            window.Opened += async (_, _) =>
            {
                // AttachSink has to run here: the surface is UI-thread affine and the
                // compositor one needs to be in a window before it can be initialised.
                var sink = window.Surface.AttachSink(BenchSession.Loggers);

                try
                {
                    exitCode = await Task.Run(
                        () => session.RunAsync(sink, null, scripted, closing.Token),
                        closing.Token
                    );
                }
                catch (OperationCanceledException)
                {
                    // The window was closed under a running command. Not a failure.
                }
                catch (Exception ex)
                {
                    output.WriteLine($"FAIL  {ex.Message}");
                    exitCode = BenchSession.ExitCommandFailed;
                }
                finally
                {
                    if (sink is IAsyncDisposable disposable)
                        await disposable.DisposeAsync();
                    window.Close();
                }
            };

            window.Closing += (_, _) => closing.Cancel();
        });

        app.StartWithClassicDesktopLifetime([]);
        return exitCode;
    }

    private static bool TryReadScript(
        BenchOptions options,
        out List<BenchCommand>? scripted,
        out int exitCode
    )
    {
        scripted = null;
        exitCode = BenchSession.ExitOk;

        if (options.ScriptPath is not { } scriptPath)
            return true;

        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"script not found: {scriptPath}");
            exitCode = BenchSession.ExitDidNotParse;
            return false;
        }

        var lines = File.ReadAllLines(scriptPath);
        if (CommandParser.TryParseScript(lines, out scripted, out var errors))
            return true;

        Console.Error.WriteLine($"{scriptPath}: {errors.Count} line(s) did not parse.");
        foreach (var error in errors)
            Console.Error.WriteLine($"  {error}");
        exitCode = BenchSession.ExitDidNotParse;
        return false;
    }

    private static CancellationTokenSource CancelOnCtrlC()
    {
        var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            source.Cancel();
        };
        return source;
    }
}

/// <summary>Writes to the console and to a log file at once.</summary>
/// <remarks>
/// <c>--log-file</c> is a copy on disk rather than the only way to see anything, which
/// is the difference between this and the eleven examples that parse the same flag.
/// </remarks>
internal sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override System.Text.Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }
}
