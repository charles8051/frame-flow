using Avalonia.Controls;
using Avalonia.Interactivity;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Avalonia;
using FrameFlow.Media;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.DualPlayer;

/// <summary>
/// Two independent players side by side in one process. Each pane has its own video view,
/// OpenAL sink, player and chrome, and the two share nothing.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Pane _left;
    private readonly Pane _right;
    private Task _starting = Task.CompletedTask;
    private bool _closing;
    private bool _closed;

    public string LeftPath { get; init; } = "";
    public string RightPath { get; init; } = "";

    /// <summary>Builds one pane's player, binds its chrome and starts it looping.</summary>
    private async Task StartAsync(Pane pane, string path)
    {
        pane.Title.Text = Path.GetFileName(path);
        try
        {
            pane.Player = await FrameFlowPlayer
                .Create()
                .WithMedia(path)
                .WithAvaloniaVideoView(pane.Video)
                .WithAudioSink(pane.Audio)
                .WithRepeatMode(RepeatMode.One)
                .WithLogger(_loggerFactory)
                .BuildPlayerAsync();
            pane.Chrome.Transport = pane.Player;

            var played = await pane.Player.PlayAsync();
            if (!played.IsSuccess)
                ShowError(pane, path, $"{played.Error.Category}: {played.Error.Message}", played.Error.Inner);
        }
        catch (Exception ex)
        {
            ShowError(pane, path, ex.Message, ex);
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        _loggerFactory = ExampleLogging.CreateFactory(
            "dual-player.log",
            onFailure: ex => Title += $"  [no log file: {ex.Message}]"
        );
        _logger = _loggerFactory.CreateLogger<MainWindow>();

        // Before the views join the visual tree, which is where each creates its sink.
        LeftVideo.LoggerFactory = _loggerFactory;
        RightVideo.LoggerFactory = _loggerFactory;

        _left = new Pane(LeftVideo, LeftChrome, LeftTitle, NewAudioSink());
        _right = new Pane(RightVideo, RightChrome, RightTitle, NewAudioSink());
        Closing += OnWindowClosing;
    }

    private OpenAlAudioSink NewAudioSink() =>
        new(_loggerFactory.CreateLogger<OpenAlAudioSink>());

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _starting = Task.WhenAll(StartAsync(_left, LeftPath), StartAsync(_right, RightPath));
        await _starting;
    }

    private void ShowError(Pane pane, string path, string message, Exception? ex)
    {
        _logger.LogError(ex, "{Path}: {Message}", path, message);
        pane.Title.Text = $"{Path.GetFileName(path)}: {message}";
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Hold every close until teardown has run; only the Close() at the end gets through.
        if (_closed)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;

        // Let both builds finish, so a player still being built is disposed too.
        await _starting;
        foreach (var pane in new[] { _left, _right })
        {
            pane.Chrome.Transport = null;
            if (pane.Player is not null)
                await DisposeLoggedAsync(pane.Player);

            // The audio sink is ours, so it goes after its player. The view disposes its own sink.
            await DisposeLoggedAsync(pane.Audio);
        }

        _loggerFactory.Dispose();
        _closed = true;
        Close();
    }

    /// <summary>Disposes one resource and logs a failure, so the next one is still disposed.</summary>
    private async Task DisposeLoggedAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Disposing {Resource} failed", resource.GetType().Name);
        }
    }

    /// <summary>One side of the window: its controls, its audio sink and, once built, its player.</summary>
    private sealed class Pane(
        FrameFlowVideoView video,
        FrameFlowPlayerChrome chrome,
        TextBlock title,
        OpenAlAudioSink audio
    )
    {
        public FrameFlowVideoView Video { get; } = video;
        public FrameFlowPlayerChrome Chrome { get; } = chrome;
        public TextBlock Title { get; } = title;
        public OpenAlAudioSink Audio { get; } = audio;
        public IMediaPlayer? Player { get; set; }
    }
}
