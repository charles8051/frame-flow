using System.Runtime.InteropServices;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Examples.SdlPlayer;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Playback; // IPlaybackController — this example drives the state machine directly
using FrameFlow.SDL;
using FrameFlow.SDL.Bootstrap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.SDL;

// ──────────────────────────────────────────────────────────────────────
// FrameFlow SDL Player (substrate edition; Crossbar ADR-0014 Phase 3)
//
// A lightweight media player built on PlaybackController.Create + SDL2.
//
// Controls:
//   [O]           Open file dialog
//   [Q] / [Esc]   Quit
//   [Space]       Pause / resume
//   [S]           Stop
//   [L]           Toggle loop
//
// Drag-and-drop a media file onto the window to play it.
// Pass a file path on the command line to play it immediately.
// Pass --loop to enable looping from the start.
// ──────────────────────────────────────────────────────────────────────

// SDL requires that SDL_Init, CreateWindow, PollEvent, and RenderPresent all
// happen on the same OS thread. On macOS/AppKit this must be the actual OS
// main thread — System.Threading.Thread is NOT the OS main thread and causes
// NSInternalInconsistencyException. Run SdlMain() directly here to preserve
// the OS main thread identity.
Environment.ExitCode = SdlMain(args);

// ── Entry point (runs on the OS main thread) ─────────────────────────

static int SdlMain(string[] args)
{
    // --log-file <path> enables a file sink alongside the console
    // provider. Argument-position-agnostic so it can sit before or
    // after the media path. Matches the Avalonia / Live Captioning
    // convention.
    string? logFilePath = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--log-file" && i + 1 < args.Length)
        {
            logFilePath = args[i + 1];
            break;
        }
    }

    using var loggerFactory = CreateLoggerFactory(logFilePath);

    // ── Bootstrap native libraries ───────────────────────────────────
    if (!TryBootstrapFfmpeg(loggerFactory, out var ffmpegMessage))
    {
        Console.Error.WriteLine($"FFmpeg bootstrap failed: {ffmpegMessage}");
        return 1;
    }

    if (!TryBootstrapSdl(loggerFactory, out var sdl))
        return 1;

    if (sdl.Init(Sdl.InitVideo | Sdl.InitEvents) < 0)
    {
        Console.Error.WriteLine($"SDL_Init failed: {sdl.GetErrorS()}");
        return 1;
    }

    // ── Construct sinks directly (no DI) ─────────────────────────────
    // The substrate controller doesn't need a DI container: sinks
    // construct directly and PlaybackController.Create takes them as
    // parameters. The frame pool for the video sink is
    // disposed when the sink itself disposes.
#pragma warning disable CA2000 // framePool ownership transfers to videoSink; videoSink lifetime owned by main + finally
    var framePool = new FrameFlow.Media.CpuFramePool(
        NullLogger<FrameFlow.Media.CpuFramePool>.Instance
    );
    var videoSink = new SdlVideoSink(
        sdl,
        framePool,
        WindowTitle.Default,
        WindowTitle.InitialWidth,
        WindowTitle.InitialHeight,
        loggerFactory.CreateLogger<SdlVideoSink>()
    );
    var audioSink = new OpenAlAudioSink(loggerFactory.CreateLogger<OpenAlAudioSink>());
#pragma warning restore CA2000 // audioSink lifetime owned by main + finally below

    var transitionLogger = loggerFactory.CreateLogger("FrameFlow.Examples.SdlPlayer.Transitions");

    // ── Run the event loop ───────────────────────────────────────────
    try
    {
        // Canonical-surface note: this example deliberately drops below
        // the MediaPlayer.CreateAsync facade to the raw
        // PlaybackController.Create state machine. The interactive SDL
        // shell IS a state-machine consumer — it subscribes to
        // PlaybackStateChanged / SeekStateChanged / RepeatModeChanged /
        // ErrorOccurred, drives Load/Play/Pause/Seek/SetRepeatMode by
        // hand from key + drag-drop events, and inspects controller.State
        // each tick to detect terminal states. The facade intentionally
        // hides exactly those transitions, so app/host code should prefer
        // MediaPlayer.CreateAsync; reach for PlaybackController.Create
        // only when the state machine itself is the thing you're building
        // around, as here.
        //
        // Closure-style controller factory replaces the old
        // IPlaybackControllerFactory. Each call returns a fresh
        // PlaybackController bound to the long-lived sinks.
        IPlaybackController Factory() =>
            PlaybackController.Create(
                videoSink: videoSink,
                audioSink: audioSink,
                hardwareDecodeMode: global::FrameFlow.HardwareDecodeMode.Auto,
                loggerFactory: loggerFactory
            );

        return RunEventLoop(sdl, videoSink, Factory, transitionLogger, args);
    }
    finally
    {
        // Best-effort cleanup of the audio sink (deactivated already
        // by the last controller's dispose). DisposeAsync releases the
        // OpenAL device handle.
        try
        {
            audioSink.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { /* swallow during shutdown */ }
        videoSink.DestroyResources();
        sdl.Quit();
    }
}

// ── Logging ──────────────────────────────────────────────────────────

static ILoggerFactory CreateLoggerFactory(string? logFilePath) =>
    LoggerFactory.Create(builder =>
    {
        builder
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("FrameFlow", LogLevel.Information)
            .AddSimpleConsole(options =>
            {
                options.TimestampFormat = "HH:mm:ss.fff ";
                options.SingleLine = true;
                options.IncludeScopes = false;
            });

        // Optional file sink — Debug-level so the post-mortem log is
        // richer than the Info-filtered console. Console stays where
        // it was; the file is purely additive.
        if (!string.IsNullOrEmpty(logFilePath))
            builder.AddProvider(new FileLoggerProvider(ExampleLogPaths.Resolve(logFilePath), LogLevel.Debug));
    });

// ── Native bootstrap ─────────────────────────────────────────────────

static bool TryBootstrapFfmpeg(ILoggerFactory loggerFactory, out string message)
{
    var bootstrapper = new FrameFlowBootstrapper(new FrameFlowNativeOptions(), loggerFactory);
    var result = bootstrapper.Initialize();
    message = result.Message ?? "Unknown error";
    return result.IsSuccess;
}

static bool TryBootstrapSdl(ILoggerFactory loggerFactory, out Sdl sdl)
{
    var bootstrapper = new SdlBootstrapper(new SdlNativeOptions(), loggerFactory);
    var result = bootstrapper.Initialize();
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine($"SDL bootstrap failed: {result.Message}");
        sdl = null!;
        return false;
    }
    sdl = bootstrapper.CreateSdlApi();
    return true;
}

// ── Event loop ───────────────────────────────────────────────────────

static int RunEventLoop(
    Sdl sdl,
    SdlVideoSink videoSink,
    Func<IPlaybackController> controllerFactory,
    ILogger transitionLogger,
    string[] args
)
{
    PrintBanner();

    var cliLoop = args.Contains("--loop");
    var pendingFile = args.FirstOrDefault(a =>
        !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)
    );
    IPlaybackController? controller = null;
    IDisposable? controllerSubscriptions = null;

    SdlEventLoop.Run(
        sdl,
        videoSink,
        onTick: () =>
        {
            if (pendingFile is not null)
            {
                controller = ReplaceController(
                    controllerFactory,
                    controller,
                    ref controllerSubscriptions,
                    transitionLogger,
                    pendingFile,
                    cliLoop
                );
                pendingFile = null;
            }

            if (controller is not null && IsTerminalState(controller.State))
            {
                PrintPlaybackSummary(controller, videoSink);
                DisposeController(controller, ref controllerSubscriptions);
                controller = null;
                videoSink.SetWindowTitle(WindowTitle.Idle);
            }
        },
        onEvent: (ref Event evt) =>
        {
            switch ((EventType)evt.Type)
            {
                case EventType.Dropfile:
                    pendingFile = ReadDroppedFilePath(sdl, ref evt);
                    return true;
                case EventType.Keydown:
                    var action = HandleKeyDown(evt.Key.Keysym.Sym, controller);
                    if (action.OpenFile is not null)
                        pendingFile = action.OpenFile;
                    return action.Continue;
            }
            return true;
        }
    );

    if (controller is not null)
        DisposeController(controller, ref controllerSubscriptions);

    return 0;
}

// ── Controller lifecycle ─────────────────────────────────────────────

static IPlaybackController? ReplaceController(
    Func<IPlaybackController> factory,
    IPlaybackController? previous,
    ref IDisposable? previousSubscriptions,
    ILogger transitionLogger,
    string filePath,
    bool loop
)
{
    if (previous is not null)
        DisposeController(previous, ref previousSubscriptions);

    var controller = factory();
    var subscriptions = SubscribeToStateMachineTransitions(controller, transitionLogger, filePath);

    var loadResult = controller.LoadAsync(MediaSource.FromFile(filePath)).GetAwaiter().GetResult();
    if (!loadResult.IsSuccess)
    {
        Console.Error.WriteLine(
            $"Load failed [{Path.GetFileName(filePath)}]: {loadResult.Error?.Message}"
        );
        subscriptions.Dispose();
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        previousSubscriptions = null;
        return null;
    }

    var playResult = controller.PlayAsync().GetAwaiter().GetResult();
    if (!playResult.IsSuccess)
    {
        Console.Error.WriteLine(
            $"Play failed [{Path.GetFileName(filePath)}]: {playResult.Error?.Message}"
        );
        subscriptions.Dispose();
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        previousSubscriptions = null;
        return null;
    }

    Console.WriteLine($"Playing: {Path.GetFileName(filePath)}");

    if (loop)
    {
        controller.SetRepeatModeAsync(RepeatMode.One).GetAwaiter().GetResult();
        Console.WriteLine("Loop: ON (via --loop)");
    }

    previousSubscriptions = subscriptions;
    return controller;
}

static void DisposeController(IPlaybackController controller, ref IDisposable? subscriptions)
{
    subscriptions?.Dispose();
    subscriptions = null;
    UnloadIfActive(controller);
    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static IDisposable SubscribeToStateMachineTransitions(
    IPlaybackController controller,
    ILogger logger,
    string filePath
)
{
    var sourceName = Path.GetFileName(filePath);

    var playbackSubscription = controller.PlaybackStateChanged.Subscribe(transition =>
        logger.LogInformation(
            "[{Source}] playback: {Previous} -> {Current}",
            sourceName,
            transition.Previous,
            transition.Current
        )
    );

    var seekSubscription = controller.SeekStateChanged.Subscribe(transition =>
        logger.LogInformation(
            "[{Source}] seek: {Previous} -> {Current}",
            sourceName,
            transition.Previous,
            transition.Current
        )
    );

    var repeatSubscription = controller.RepeatModeChanged.Subscribe(transition =>
        logger.LogInformation(
            "[{Source}] repeat: {Previous} -> {Current}",
            sourceName,
            transition.Previous,
            transition.Current
        )
    );

    var errorSubscription = controller.ErrorOccurred.Subscribe(error =>
        logger.LogError(
            error.Inner,
            "[{Source}] playback error: {Category} - {Message}",
            sourceName,
            error.Category,
            error.Message
        )
    );

    return new CompositeSubscription(
        playbackSubscription,
        seekSubscription,
        repeatSubscription,
        errorSubscription
    );
}

static void UnloadIfActive(IPlaybackController controller)
{
    if (controller.State is PlaybackState.Playing or PlaybackState.Paused)
        controller.UnloadAsync().GetAwaiter().GetResult();
}

static bool IsTerminalState(PlaybackState state) =>
    state is PlaybackState.Ended or PlaybackState.Error;

// ── Key handling ─────────────────────────────────────────────────────

static KeyAction HandleKeyDown(int key, IPlaybackController? controller)
{
    switch (key)
    {
        case (int)KeyCode.KQ or (int)KeyCode.KEscape:
            return new KeyAction(Continue: false, OpenFile: null);

        case (int)KeyCode.KO:
            return new KeyAction(Continue: true, OpenFile: FileDialogHelper.ShowOpenFileDialog());

        case (int)KeyCode.KSpace when controller is not null:
            TogglePause(controller);
            return new KeyAction(true, null);

        case (int)KeyCode.KS when controller is not null:
            if (controller.State is PlaybackState.Playing or PlaybackState.Paused)
            {
                controller.PauseAsync().GetAwaiter().GetResult();
                controller.SeekAsync(TimeSpan.Zero).GetAwaiter().GetResult();
                Console.WriteLine("Stopped (at beginning). Press [Space] to play.");
            }
            return new KeyAction(true, null);

        case (int)KeyCode.KL when controller is not null:
            ToggleLoop(controller);
            return new KeyAction(true, null);
    }
    return new KeyAction(true, null);
}

static void TogglePause(IPlaybackController controller)
{
    if (controller.State == PlaybackState.Playing)
    {
        controller.PauseAsync().GetAwaiter().GetResult();
        Console.WriteLine("Paused.");
    }
    else if (controller.State == PlaybackState.Paused)
    {
        controller.PlayAsync().GetAwaiter().GetResult();
        Console.WriteLine("Resumed.");
    }
}

static void ToggleLoop(IPlaybackController controller)
{
    var newMode = controller.RepeatMode == RepeatMode.Off ? RepeatMode.One : RepeatMode.Off;
    controller.SetRepeatModeAsync(newMode).GetAwaiter().GetResult();
    Console.WriteLine(newMode == RepeatMode.One ? "Loop: ON" : "Loop: OFF");
}

// ── Drag-and-drop ────────────────────────────────────────────────────

static unsafe string? ReadDroppedFilePath(Sdl sdl, ref Event evt)
{
    var path = Marshal.PtrToStringUTF8((nint)evt.Drop.File);
    sdl.Free(evt.Drop.File);

    if (path is null || !File.Exists(path))
    {
        if (path is not null)
            Console.Error.WriteLine($"Dropped file not found: {path}");
        return null;
    }

    return path;
}

// ── Console output ───────────────────────────────────────────────────

static void PrintBanner()
{
    Console.WriteLine("FrameFlow SDL Player ready.");
    Console.WriteLine("Controls: [O] open  [Space] pause/resume  [S] stop  [L] loop  [Q/Esc] quit");
    Console.WriteLine("          Drop a file onto the window to play it.");
}

static void PrintPlaybackSummary(IPlaybackController controller, SdlVideoSink videoSink)
{
    Console.WriteLine(
        $"Playback {controller.State} — "
            + $"{videoSink.RenderedFrameCount} rendered, "
            + $"{videoSink.DroppedFrameCount} dropped"
    );
}

// ── Type declarations (must follow all top-level statements) ────────

/// <summary>
/// Result of a key-down dispatch. <see cref="Continue"/> is false when the
/// key requested quit (Q/Esc); <see cref="OpenFile"/> is non-null when the
/// key triggered a file-picker dialog and the user chose a file.
/// </summary>
record struct KeyAction(bool Continue, string? OpenFile);

// ── Window title constants ───────────────────────────────────────────

static class WindowTitle
{
    public const string Default = "FrameFlow SDL Player";
    public const string Idle = "FrameFlow SDL Player — press [O] to open a file";
    public const int InitialWidth = 1280;
    public const int InitialHeight = 720;
}

sealed class CompositeSubscription(params IDisposable[] subscriptions) : IDisposable
{
    private readonly IDisposable[] _subscriptions = subscriptions;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
    }
}
