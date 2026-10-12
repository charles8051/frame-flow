using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Avalonia;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.AvaloniaPlayer;

/// <summary>
/// A media player. One FrameFlow player is built on the first open and kept: every file or
/// folder opened replaces its queue, and the sidebar draws that queue.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly OpenAlAudioSink _audioSink;

    private IMediaPlayer? _player;
    private IDisposable? _queueSubscription;
    private Task _opening = Task.CompletedTask;
    private bool _closing;
    private bool _closed;

    /// <summary>A media file or folder to open on startup.</summary>
    public string? StartupPath { get; init; }

    public MainWindow()
    {
        InitializeComponent();

        _loggerFactory = ExampleLogging.CreateFactory(
            "avalonia-player.log",
            onFailure: ex => StatusText.Text = $"Logging to file is off ({ex.Message})."
        );
        _logger = _loggerFactory.CreateLogger<MainWindow>();
        _audioSink = new OpenAlAudioSink(_loggerFactory.CreateLogger<OpenAlAudioSink>());

        PlayerView.FileOpenRequested += async (_, e) => await OpenAsync(e.FilePath);
        OpenFolderButton.Click += async (_, _) => await PickFolderAsync();
        PlaylistBox.DoubleTapped += async (_, _) => await JumpToSelectedAsync();
        Closing += OnWindowClosing;
    }

    /// <summary>Builds the player with no media: its sinks are warm and its queue is empty.</summary>
    private async Task<IMediaPlayer> BuildPlayerAsync()
    {
        var player = await FrameFlowPlayer
            .Create()
            .WithVideoSink(PlayerView.AttachSink(_loggerFactory))
            .WithAudioSink(_audioSink)
            .WithLogger(_loggerFactory)
            .BuildPlayerAsync();

        _queueSubscription = player.PlaylistChanged.ObserveOnUiThread().Subscribe(ShowQueue);
        PlayerView.Transport = player;
        return player;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (StartupPath is null)
            return;
        // A leading '-' is a stale option, never a path, even if a file has that name.
        if (!StartupPath.StartsWith('-') && (File.Exists(StartupPath) || Directory.Exists(StartupPath)))
            await OpenAsync(StartupPath);
        else
            StatusText.Text = $"Not a media file or folder: {StartupPath}";
    }

    /// <summary>Opens a media file, or every media file in a folder, after any open before it.</summary>
    private async Task OpenAsync(string path)
    {
        if (_closing)
            return;
        _opening = OpenCoreAsync(_opening, path);
        await _opening;
    }

    private async Task OpenCoreAsync(Task previous, string path)
    {
        await previous;
        if (_closing)
            return;

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        try
        {
            var isFolder = Directory.Exists(path);
            List<string> files = isFolder ? MediaFilesIn(path) : [path];
            if (files.Count == 0)
            {
                StatusText.Text = $"No media files in {name}.";
                return;
            }

            // A player whose first item failed to load stays in Error and refuses new media.
            if (_player?.State == PlaybackState.Error)
                await DisposePlayerAsync();
            _player ??= await BuildPlayerAsync();

            var repeat = isFolder ? RepeatMode.All : RepeatMode.Off;
            var repeated = await _player.SetRepeatModeAsync(repeat);
            if (!repeated.IsSuccess)
            {
                ShowError(name, repeated.Error);
                return;
            }

            var replaced = await _player.ReplaceAsync(files.Select(SourceFor));
            if (!replaced.IsSuccess)
            {
                ShowError(name, replaced.Error);
                return;
            }

            // A playing player moves to the new queue on its own, and refuses Play.
            if (_player.State is not (PlaybackState.Playing or PlaybackState.Rebuffering))
            {
                var played = await _player.PlayAsync();
                if (!played.IsSuccess)
                {
                    ShowError(name, played.Error);
                    return;
                }
            }

            StatusText.Text = isFolder ? $"{files.Count} file(s) · {name} · looping" : name;
        }
        catch (Exception ex)
        {
            ShowError($"Could not open {name}: {ex.Message}", ex);
        }
    }

    /// <summary>Draws the player's queue. It fires again as each item starts.</summary>
    private void ShowQueue(PlaylistSnapshot queue)
    {
        PlaylistBox.ItemsSource = queue.Playlist;
        PlaylistBox.SelectedItem = queue.Current;
        Title = queue.Current is { } item ? $"FrameFlow Player — {item}" : "FrameFlow Player";
    }

    private async Task JumpToSelectedAsync()
    {
        if (_player is null || PlaylistBox.SelectedItem is not PlaylistItem item)
            return;

        var jumped = await _player.JumpToAsync(item);
        if (!jumped.IsSuccess)
            ShowError(item.ToString(), jumped.Error);
    }

    private async Task PickFolderAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Open a media folder" }
            );
            if (folders is [var folder] && folder.TryGetLocalPath() is { } path)
                await OpenAsync(path);
        }
        catch (Exception ex)
        {
            ShowError($"Could not pick a folder: {ex.Message}", ex);
        }
    }

    private void ShowError(string name, PlaybackError error) =>
        ShowError($"{name}: {error.Category}: {error.Message}", error.Inner);

    private void ShowError(string message, Exception? ex)
    {
        _logger.LogError(ex, "{Message}", message);
        StatusText.Text = message;
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

        // Let the opens finish, so a player one is still building is disposed too.
        await _opening;
        await DisposePlayerAsync();

        // The audio sink is ours, so it goes after the player. The view disposes its own sink.
        await DisposeLoggedAsync(_audioSink);
        _loggerFactory.Dispose();
        _closed = true;
        Close();
    }

    private async Task DisposePlayerAsync()
    {
        if (_player is not { } player)
            return;

        _player = null;
        PlayerView.Transport = null;
        _queueSubscription?.Dispose();
        await DisposeLoggedAsync(player);
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

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".m2ts",
        ".flv", ".wmv", ".mpg", ".mpeg", ".3gp", ".ogv",
        ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma",
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    /// <summary>
    /// Opened as a held still. .gif and .webp are left out: they may be animated, and a still
    /// would show only the first frame.
    /// </summary>
    private static readonly HashSet<string> StillExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    private static readonly TimeSpan StillDwell = TimeSpan.FromSeconds(5);

    private static List<string> MediaFilesIn(string folder) =>
        Directory
            .EnumerateFiles(folder)
            .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IMediaSource SourceFor(string path) =>
        StillExtensions.Contains(Path.GetExtension(path))
            ? MediaSource.FromStill(path, StillDwell)
            : MediaSource.FromFile(path);
}
