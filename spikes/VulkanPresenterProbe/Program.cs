// Can a Vulkan-decoded frame reach Avalonia's compositor on Linux without leaving the GPU?
//
//   vulkan  FFmpeg decodes on Vulkan, on a HardwareDevice the player borrows. The sink converts
//           each NV12 frame to RGBA8 with a compute shader on FFmpeg's own VkDevice, reading the
//           decoded image through per-plane views, into a ring of images exported as opaque FDs.
//           Avalonia imports each ring image and a pair of binary semaphores once, and shows a
//           frame with UpdateWithSemaphoresAsync. FFmpeg's timeline semaphore is waited on at
//           sem_value and signalled at sem_value + 1, under lock_frame, as hwcontext_vulkan.h asks.
//   cpu     Today's Linux path: FrameFlowVideoView, whose sink takes CPU frames, so the player
//           decodes on its default hardware backend and reads every frame back.
//   sw      Software decode into the same view.
//
// Run on an X server the GPU drives. Under NVIDIA PRIME render offload onto another display, the
// render loop stalled in glXSwapBuffers on every path (docs/investigations/2026-09-30-vulkan-presenter.md).
//
//   DISPLAY=:2 dotnet run --project spikes/VulkanPresenterProbe -c Release -- vulkan|cpu|sw <clip> [seconds] [warmup]
//
// PROBE_AVALONIA_RENDERER=vulkan selects Avalonia's X11 Vulkan renderer; -p:AvaloniaVersion=12.1.3
// builds against Avalonia 12.
//
// Writes its findings to stdout and to probe-result-<mode>.txt beside its build output, then
// closes.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using FrameFlow.Avalonia;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace VulkanPresenterProbe;

internal static class Program
{
    public static void Main(string[] args)
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        // PROBE_AVALONIA_RENDERER=vulkan asks for Avalonia's X11 Vulkan renderer instead of GLX.
        if (Environment.GetEnvironmentVariable("PROBE_AVALONIA_RENDERER") == "vulkan")
            builder = builder.With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Vulkan] });
        builder.StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var probe = new Probe(desktop, desktop.Args ?? []);
            desktop.MainWindow = new Window
            {
                Title = "Vulkan presenter probe",
                Width = 960,
                Height = 540,
                Background = Avalonia.Media.Brushes.Blue,
                Content = probe.Content,
            };
            desktop.MainWindow.Opened += (_, _) => _ = probe.RunAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class Probe
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly string _mode;
    private readonly string _clip;
    private readonly TimeSpan _run;
    private readonly TimeSpan _warmup;
    private readonly List<string> _log = [];
    private readonly ILoggerFactory _loggerFactory;
    private readonly VulkanVideoView? _vulkanView;
    private readonly FrameFlowVideoView? _cpuView;
    private long _renderTicks;

    public Control Content { get; }

    public Probe(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        _desktop = desktop;
        _mode = args is [var m, ..] ? m : "vulkan";
        _clip = args is [_, var c, ..] ? c : "tests/corpus/files/test-1080p60-h264-aac.mp4";
        _run = TimeSpan.FromSeconds(args is [_, _, var s, ..] ? double.Parse(s) : 20);
        _warmup = TimeSpan.FromSeconds(args is [_, _, _, var w, ..] ? double.Parse(w) : 4);
        _loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

        if (_mode == "vulkan")
            Content = _vulkanView = new VulkanVideoView(Log);
        else
            Content = _cpuView = new FrameFlowVideoView();
    }

    private void Log(string line)
    {
        line = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        Console.WriteLine(line);
        lock (_log)
            _log.Add(line);
    }

    public async Task RunAsync()
    {
        try
        {
            await ProbeAsync();
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex}");
        }
        finally
        {
            lock (_log)
                File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, $"probe-result-{_mode}.txt"), _log);
            _desktop.Shutdown();
        }
    }

    private async Task ProbeAsync()
    {
        Log($"mode {_mode}, clip {_clip}, run {_run.TotalSeconds}s after {_warmup.TotalSeconds}s warmup, skip compositor wait {VulkanConverter.SkipCompositorWait}");
        Log($"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")} renderer={Environment.GetEnvironmentVariable("PROBE_AVALONIA_RENDERER") ?? "default"} avalonia={typeof(Application).Assembly.GetName().Version}");
        FfmpegVulkan.CheckLayouts();

        var boot = new FrameFlowBootstrapper(new FrameFlowNativeOptions(), _loggerFactory).Initialize();
        if (!boot.IsSuccess)
            throw new InvalidOperationException($"FFmpeg bootstrap failed: {boot.Message}");
        Log($"hardware backends: {string.Join(", ", boot.Capabilities.Available.Where(b => b.Initialized).Select(b => b.Kind))}");

        IVideoSink sink;
        HardwareDevice? device = null;
        var builder = FrameFlowPlayer.Create()
            .WithMedia(_clip)
            .WithRepeatMode(RepeatMode.All)
            .WithLogger(_loggerFactory);

        if (_vulkanView is not null)
        {
            if (await _vulkanView.Ready.Task is null)
                return;
            device = HardwareDevice.Create(HardwareDecodeBackendKind.Vulkan);
            sink = new VulkanPresenterSink(_vulkanView, Log);
            builder = builder.WithHardwareDevice(device).WithHardwareDecode(HardwareDecodeMode.Required);
        }
        else
        {
            sink = ((IVideoSurface)_cpuView!).AttachSink(_loggerFactory);
            if (_mode == "sw")
                builder = builder.WithHardwareDecode(HardwareDecodeMode.Disabled);
        }

        await using var player = await builder.WithVideoSink(sink).BuildPlayerAsync();
        var play = await player.PlayAsync();
        Log($"play: {(play.IsSuccess ? "ok" : play.Error.Message)}");

        using var dump = sink is VulkanPresenterSink stallSink
            ? new Timer(_ =>
            {
                Log($"submitted {stallSink.Submitted} dropped {stallSink.Dropped} shown {_vulkanView!.Shown} completed {_vulkanView.Completed}");
                foreach (var line in stallSink.DescribeSlots())
                    Log("  " + line);
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            : null;

        // Render-loop liveness: animation frames are serviced from the compositor's render loop,
        // so they stop when the render thread blocks.
        var top = TopLevel.GetTopLevel(Content)!;
        void Tick(TimeSpan _)
        {
            Interlocked.Increment(ref _renderTicks);
            top.RequestAnimationFrame(Tick);
        }
        await Dispatcher.UIThread.InvokeAsync(() => top.RequestAnimationFrame(Tick));

        await Task.Delay(_warmup);
        var process = Process.GetCurrentProcess();
        var ticksAtStart = Interlocked.Read(ref _renderTicks);
        var start = Sample(process, sink);
        var clock = Stopwatch.StartNew();
        await Task.Delay(_run);
        var end = Sample(process, sink);
        var wall = clock.Elapsed.TotalSeconds;
        var ticks = Interlocked.Read(ref _renderTicks) - ticksAtStart;
        Log($"render loop: {ticks} animation frames ({ticks / wall:F1}/s)");

        var decoder = player.GetDiagnostics().Pipeline.Stream.VideoDecoder;
        Log($"decoder backend: {decoder.HardwareBackend?.ToString() ?? "software"}, decode errors {decoder.DecodeErrors}");

        long frames = end.Presented - start.Presented;
        double cpu = (end.Cpu - start.Cpu).TotalMilliseconds;
        Log($"window {wall:F1}s: presented {frames} ({frames / wall:F1}/s), dropped {end.Dropped - start.Dropped}, committed {end.Committed - start.Committed}");
        Log($"process CPU: {cpu:F0} ms ({cpu / wall / 10:F1}% of one core), {(frames > 0 ? cpu / frames : 0):F2} ms per presented frame");
        if (sink is VulkanPresenterSink v)
            Log($"vulkan sink: submitted {v.Submitted}, dropped {v.Dropped}, not vulkan {v.NotVulkan}, wrong format {v.WrongFormat}; "
                + $"view: shown {_vulkanView!.Shown}, completed {_vulkanView.Completed}, faulted {_vulkanView.Faulted}");

        await player.PauseAsync();
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        await sink.DisposeAsync();
        device?.Dispose();
    }

    private static (TimeSpan Cpu, long Presented, long Dropped, long Committed) Sample(Process process, IVideoSink sink)
    {
        process.Refresh();
        var d = sink.GetDiagnostics();
        return (process.TotalProcessorTime, d.FramesPresented, d.FramesDropped, d.FramesCommitted);
    }
}
