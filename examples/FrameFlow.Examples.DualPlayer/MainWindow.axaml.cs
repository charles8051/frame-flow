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

    private sealed record Pane(
        FrameFlowVideoView Video,
        FrameFlowPlayerChrome Chrome,
        TextBlock Title,
        OpenAlAudioSink Audio
    )
    {
        public IMediaPlayer? Player { get; set; }
    }

    public string LeftPath { get; init; } = "";
    public string RightPath { get; init; } = "";

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

    private void ShowError(Pane pane, string path, string message, Exception? ex)
    {
        _logger.LogError(ex, "{Path}: {Message}", path, message);
        pane.Title.Text = $"{Path.GetFileName(path)}: {message}";
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        e.Cancel = true;
        Closing -= OnWindowClosing;

        // Let both builds finish, so a player still being built is disposed too.
        await _starting;
        foreach (var pane in new[] { _left, _right })
        {
            pane.Chrome.Transport = null;
            if (pane.Player is not null)
                await pane.Player.DisposeAsync();

            // The audio sink is ours, so it goes after its player. The view disposes its own sink.
            await pane.Audio.DisposeAsync();
        }

        _loggerFactory.Dispose();
        Close();
    }
}
