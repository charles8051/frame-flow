using System.Runtime.InteropServices;
using FrameFlow.Examples.SdlPlayer;
using FrameFlow.Media;
using FrameFlow.Player;
using FrameFlow.Sdl;
using FrameFlow.Sdl.Bootstrap;
using Microsoft.Extensions.Logging;
using Silk.NET.SDL;

// FrameFlow SDL Player: plays media files in an SDL window, with OpenAL audio.

if (!TryReadArgs(args, out var startFile, out var repeat))
    return 1;

using var loggerFactory = LoggerFactory.Create(logging =>
    logging.AddSimpleConsole(console => console.SingleLine = true).AddExampleFile("sdl-player.log")
);

// SDL wants its window and event loop on the OS main thread (on macOS, AppKit's), so this thread
// runs SDL and never awaits. The player runs beside it on the thread pool, driven by the commands
// the window posts.
return RunWindow(startFile, repeat, loggerFactory);

// ── The player ───────────────────────────────────────────────────────

static async Task OpenAsync(IMediaPlayer player, string path, CancellationToken ct)
{
    Console.WriteLine($"Opening {Path.GetFileName(path)}");
    var replaced = await player.ReplaceAsync([MediaSource.FromFile(path)], ct);
    if (!replaced.IsSuccess)
    {
        Console.Error.WriteLine($"Open failed: {replaced.Error.Message}");
        return;
    }

    // A playing player moves to the new file by itself; in any other state it waits for Play.
    if (player.State != PlaybackState.Playing)
        Check("Play", await player.PlayAsync(ct));
}

static async Task TogglePauseAsync(IMediaPlayer player, CancellationToken ct)
{
    if (player.State == PlaybackState.Playing)
        Check("Pause", await player.PauseAsync(ct));
    else
        Check("Play", await player.PlayAsync(ct));
}

static async Task StopAsync(IMediaPlayer player, CancellationToken ct)
{
    if (player.State == PlaybackState.Playing && !Check("Pause", await player.PauseAsync(ct)))
        return;
    Check("Seek", await player.SeekAsync(TimeSpan.Zero, ct));
}

static async Task ToggleLoopAsync(IMediaPlayer player, CancellationToken ct)
{
    var looping = player.RepeatMode == RepeatMode.One;
    var mode = looping ? RepeatMode.Off : RepeatMode.One;
    if (Check("Loop", await player.SetRepeatModeAsync(mode, ct)))
        Console.WriteLine(mode == RepeatMode.One ? "Loop on" : "Loop off");
}

static bool Check(string command, Result result)
{
    if (!result.IsSuccess)
        Console.Error.WriteLine($"{command} failed: {result.Error.Message}");
    return result.IsSuccess;
}

// ── The window ───────────────────────────────────────────────────────

static int RunWindow(string? startFile, RepeatMode repeat, ILoggerFactory loggerFactory)
{
    var bootstrapper = new SdlBootstrapper(new SdlNativeOptions(), loggerFactory);
    var bootstrap = bootstrapper.Initialize();
    if (!bootstrap.IsSuccess)
    {
        Console.Error.WriteLine($"SDL bootstrap failed: {bootstrap.Message}");
        return 1;
    }

    var sdl = bootstrapper.CreateSdlApi();
    if (sdl.Init(Sdl.InitVideo | Sdl.InitEvents) < 0)
    {
        Console.Error.WriteLine($"SDL_Init failed: {sdl.GetErrorS()}");
        return 1;
    }

    try
    {
#pragma warning disable CA2000 // released by DestroyResources below, which must run on this thread
        var video = new SdlVideoSink(
            sdl,
            "FrameFlow SDL Player",
            1280,
            720,
            loggerFactory.CreateLogger<SdlVideoSink>()
        );
#pragma warning restore CA2000
        try
        {
            RunEventLoop(sdl, video, startFile, repeat, loggerFactory);
        }
        finally
        {
            video.DestroyResources();
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FrameFlow SDL Player failed: {ex}");
        return 1;
    }
    finally
    {
        sdl.Quit();
    }
}

static void RunEventLoop(
    Sdl sdl,
    SdlVideoSink video,
    string? startFile,
    RepeatMode repeat,
    ILoggerFactory loggerFactory
)
{
    // The queue runs the window's commands against the player one at a time, off this thread.
    var player = new PlayerSlot(video, repeat, loggerFactory);
    var commands = new SdlCommandQueue(loggerFactory.CreateLogger<SdlCommandQueue>());
    if (startFile is not null)
        commands.Post(player.Run((p, ct) => OpenAsync(p, startFile, ct)));

    Console.WriteLine(
        "[O] open (or drop a file)  [Space] pause/resume  [S] stop  [L] loop  [Q]/[Esc] quit"
    );
    try
    {
        // Returns once the last command has finished.
        SdlEventLoop.Run(
            sdl,
            video,
            commands,
            onEvent: (ref Event e) => HandleEvent(sdl, ref e, commands, player)
        );
    }
    finally
    {
        // The player is disposed before its window goes.
        player.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

// Runs on the SDL thread: it never waits on the player, it posts a command and returns.
// [O]'s dialog is modal, so the video stops repainting while it is open.
static bool HandleEvent(Sdl sdl, ref Event e, SdlCommandQueue commands, PlayerSlot player)
{
    switch ((EventType)e.Type)
    {
        case EventType.Dropfile:
            PostOpen(commands, player, ReadDroppedFile(sdl, ref e));
            break;

        case EventType.Keydown when e.Key.Repeat == 0:
            switch ((KeyCode)e.Key.Keysym.Sym)
            {
                case KeyCode.KQ or KeyCode.KEscape:
                    return false;
                case KeyCode.KO:
                    PostOpen(commands, player, FileDialogHelper.ShowOpenFileDialog());
                    break;
                case KeyCode.KSpace:
                    commands.Post(player.Run(TogglePauseAsync));
                    break;
                case KeyCode.KS:
                    commands.Post(player.Run(StopAsync));
                    break;
                case KeyCode.KL:
                    commands.Post(player.Run(ToggleLoopAsync));
                    break;
            }
            break;
    }
    return true;
}

static void PostOpen(SdlCommandQueue commands, PlayerSlot player, string? path)
{
    if (path is null)
        return;
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return;
    }
    commands.Post(player.Run((p, ct) => OpenAsync(p, path, ct)));
}

static unsafe string? ReadDroppedFile(Sdl sdl, ref Event e)
{
    var path = Marshal.PtrToStringUTF8((nint)e.Drop.File);
    sdl.Free(e.Drop.File);
    return path;
}

static bool TryReadArgs(string[] args, out string? startFile, out RepeatMode repeat)
{
    repeat = args.Contains("--loop") ? RepeatMode.One : RepeatMode.Off;
    string[] files = [.. args.Where(a => a != "--loop")];
    startFile = files.FirstOrDefault();

    if (files.Length > 1 || startFile?.StartsWith('-') == true)
    {
        Console.Error.WriteLine("Usage: FrameFlow.Examples.SdlPlayer [--loop] [media-file]");
        return false;
    }
    if (startFile is not null && !File.Exists(startFile))
    {
        Console.Error.WriteLine($"File not found: {startFile}");
        return false;
    }
    return true;
}
