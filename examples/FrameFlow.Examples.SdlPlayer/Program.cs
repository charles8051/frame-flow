using System.Runtime.InteropServices;
using System.Threading.Channels;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Examples.SdlPlayer;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using FrameFlow.Sdl;
using FrameFlow.Sdl.Bootstrap;
using Microsoft.Extensions.Logging;
using Silk.NET.SDL;
using PlayerCommand = System.Func<FrameFlow.Player.IMediaPlayer, System.Threading.Tasks.Task>;

// FrameFlow SDL Player: plays media files in an SDL window, with OpenAL audio.

if (!TryReadArgs(args, out var startFile, out var repeat))
    return 1;

using var loggerFactory = LoggerFactory.Create(logging =>
    logging.AddSimpleConsole(console => console.SingleLine = true).AddExampleFile("sdl-player.log")
);

// The window posts commands here; RunPlayerAsync runs them against the player one at a time.
var commands = Channel.CreateUnbounded<PlayerCommand>();
if (startFile is not null)
    commands.Writer.TryWrite(player => OpenAsync(player, startFile));

// SDL wants its window and event loop on the OS main thread (on macOS, AppKit's), so this thread
// runs SDL and never awaits. The player runs beside it on the thread pool.
return RunWindow(commands, repeat, loggerFactory);

// ── The player ───────────────────────────────────────────────────────

static async Task RunPlayerAsync(
    SdlVideoSink video,
    ChannelReader<PlayerCommand> commands,
    RepeatMode repeat,
    ILoggerFactory loggerFactory
)
{
    await using var audio = new OpenAlAudioSink(loggerFactory.CreateLogger<OpenAlAudioSink>());

    async Task<IMediaPlayer> BuildAsync()
    {
        var built = await FrameFlowPlayer
            .Create()
            .WithVideoSink(video)
            .WithAudioSink(audio)
            .WithRepeatMode(repeat)
            .WithLogger(loggerFactory)
            .BuildPlayerAsync();

        built.StateChanged.Subscribe(state => Console.WriteLine(state));
        built.ErrorOccurred.Subscribe(error =>
            Console.Error.WriteLine($"Playback error: {error.Message}")
        );
        return built;
    }

    var player = await BuildAsync();
    try
    {
        await foreach (var command in commands.ReadAllAsync())
        {
            // Error is final, and a file that fails to open while nothing plays leaves the player
            // there, so the next command gets a fresh one.
            if (player.State == PlaybackState.Error)
            {
                repeat = player.GetDiagnostics().RepeatMode;
                await player.DisposeAsync();
                player = await BuildAsync();
            }

            await command(player);
        }
    }
    finally
    {
        await player.DisposeAsync();
    }
}

static async Task OpenAsync(IMediaPlayer player, string path)
{
    Console.WriteLine($"Opening {Path.GetFileName(path)}");
    var replaced = await player.ReplaceAsync([MediaSource.FromFile(path)]);
    if (!replaced.IsSuccess)
    {
        Console.Error.WriteLine($"Open failed: {replaced.Error.Message}");
        return;
    }

    // A playing player moves to the new file by itself; in any other state it waits for Play.
    if (player.State != PlaybackState.Playing)
        Check("Play", await player.PlayAsync());
}

static async Task TogglePauseAsync(IMediaPlayer player)
{
    if (player.State == PlaybackState.Playing)
        Check("Pause", await player.PauseAsync());
    else
        Check("Play", await player.PlayAsync());
}

static async Task StopAsync(IMediaPlayer player)
{
    if (player.State == PlaybackState.Playing && !Check("Pause", await player.PauseAsync()))
        return;
    Check("Seek", await player.SeekAsync(TimeSpan.Zero));
}

static async Task ToggleLoopAsync(IMediaPlayer player)
{
    // IMediaTransport sets the repeat mode but does not report it; the diagnostics snapshot does.
    var looping = player.GetDiagnostics().RepeatMode == RepeatMode.One;
    var mode = looping ? RepeatMode.Off : RepeatMode.One;
    if (Check("Loop", await player.SetRepeatModeAsync(mode)))
        Console.WriteLine(mode == RepeatMode.One ? "Loop on" : "Loop off");
}

static bool Check(string command, Result result)
{
    if (!result.IsSuccess)
        Console.Error.WriteLine($"{command} failed: {result.Error.Message}");
    return result.IsSuccess;
}

// ── The window ───────────────────────────────────────────────────────

static int RunWindow(
    Channel<PlayerCommand> commands,
    RepeatMode repeat,
    ILoggerFactory loggerFactory
)
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
            RunEventLoop(sdl, video, commands, repeat, loggerFactory);
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
    Channel<PlayerCommand> commands,
    RepeatMode repeat,
    ILoggerFactory loggerFactory
)
{
    using var closeWindow = new CancellationTokenSource();
    var playerLoop = Task.Run(async () =>
    {
        try
        {
            await RunPlayerAsync(video, commands.Reader, repeat, loggerFactory);
        }
        finally
        {
            closeWindow.Cancel(); // a player that fails closes the window
        }
    });

    Console.WriteLine(
        "[O] open (or drop a file)  [Space] pause/resume  [S] stop  [L] loop  [Q]/[Esc] quit"
    );
    try
    {
        SdlEventLoop.Run(
            sdl,
            video,
            onEvent: (ref Event e) => HandleEvent(sdl, ref e, commands.Writer),
            ct: closeWindow.Token
        );
    }
    finally
    {
        // The player is disposed before its window goes.
        commands.Writer.Complete();
        playerLoop.GetAwaiter().GetResult();
    }
}

// Runs on the SDL thread: it never waits on the player, it posts a command and returns.
// [O]'s dialog is modal, so the video stops repainting while it is open.
static bool HandleEvent(Sdl sdl, ref Event e, ChannelWriter<PlayerCommand> commands)
{
    switch ((EventType)e.Type)
    {
        case EventType.Dropfile:
            PostOpen(commands, ReadDroppedFile(sdl, ref e));
            break;

        case EventType.Keydown when e.Key.Repeat == 0:
            switch ((KeyCode)e.Key.Keysym.Sym)
            {
                case KeyCode.KQ or KeyCode.KEscape:
                    return false;
                case KeyCode.KO:
                    PostOpen(commands, FileDialogHelper.ShowOpenFileDialog());
                    break;
                case KeyCode.KSpace:
                    commands.TryWrite(TogglePauseAsync);
                    break;
                case KeyCode.KS:
                    commands.TryWrite(StopAsync);
                    break;
                case KeyCode.KL:
                    commands.TryWrite(ToggleLoopAsync);
                    break;
            }
            break;
    }
    return true;
}

static void PostOpen(ChannelWriter<PlayerCommand> commands, string? path)
{
    if (path is null)
        return;
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return;
    }
    commands.TryWrite(player => OpenAsync(player, path));
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
