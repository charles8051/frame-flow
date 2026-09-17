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
/// FrameFlow Avalonia player.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FrameFlow.Avalonia.FrameFlowPlayerView"/> supplies the video
/// surface, transport bar, seek bar, volume control, status badge, stream
/// summary, position label, file picker, drag-drop and keyboard shortcuts.
/// The window is left with two jobs: build a player when a file or folder is
/// opened, and keep the playlist sidebar in step with playback.
/// </para>
/// <para>
/// A single file and a folder are both built with
/// <c>FrameFlowPlayer.Create().WithMedia(...).BuildPlayerAsync()</c>; the folder passes every
/// source at once and plays them over one warm presenter.
/// </para>
/// <para>
/// Presenter selection, hardware-decode A/B, running without audio and
/// self-terminating runs are diagnostics, and live in
/// <c>tools/FrameFlow.TestBench</c> (ADR-0068).
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private ILoggerFactory? _loggerFactory;
    private ILogger<MainWindow>? _logger;
    private IMediaPlayer? _player;

    // Folder mode: the same instance as _player, kept typed for the
    // playlist-specific surface (transition stream, jump). Null for a single file.
    private IMediaPlaylistPlayer? _playlistPlayer;
    private IDisposable? _transitionSub;
    private IReadOnlyList<PlaylistEntry> _playlistEntries = [];

    private OpenAlAudioSink? _audioSink;
    private bool _isClosing;

    /// <summary>One file in the open folder: its display name + the source to play.</summary>
    private sealed record PlaylistEntry(string Name, IMediaSource Source);

    /// <summary>Extensions enumerated as playable when a folder is opened.</summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".m2ts",
        ".flv", ".wmv", ".mpg", ".mpeg", ".3gp", ".ogv",
        ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma",
    };

    /// <summary>A media file or a folder to open on startup, from the command line.</summary>
    public string? StartupPath { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        PlayerView.FileOpenRequested += async (_, e) => await OpenFileAsync(e.FilePath);
        OpenFolderButton.Click += async (_, _) => await OpenFolderAsync();
        OpenFileButton.Click += async (_, _) => await OpenFilePickerAsync();
        PlaylistBox.DoubleTapped += async (_, _) => await JumpToSelectedAsync();
        Closing += OnWindowClosing;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _loggerFactory = ExampleLogging.CreateFactory(
            "avalonia-player.log",
            onFailure: ex =>
                StatusText.Text = $"Logging to file is off ({ex.Message}). Playback still works."
        );
        _logger = _loggerFactory.CreateLogger<MainWindow>();

        // Materialize the hosted surface's sink now that it is in the tree.
        PlayerView.AttachSink(_loggerFactory);

        if (Directory.Exists(StartupPath))
            await PlayFolderAsync(StartupPath);
        else if (File.Exists(StartupPath))
            await OpenFileAsync(StartupPath);
    }

    /// <summary>Builds a player for one file and hands it to the view.</summary>
    private async Task OpenFileAsync(string path)
    {
        if (_loggerFactory is null)
            return;

        await TeardownPlayerAsync();

        PlaylistBox.ItemsSource = null;
        _playlistEntries = [];
        StatusText.Text = Path.GetFileName(path);

        try
        {
            // The heart of the example: construct the sinks, wire them through
            // the builder, hand the player to the view.
            var videoSink = PlayerView.AttachSink(_loggerFactory);
            _audioSink = new OpenAlAudioSink(_loggerFactory.CreateLogger<OpenAlAudioSink>());

            _player = await FrameFlowPlayer
                .Create()
                .WithMedia(path)
                .WithVideoSink(videoSink)
                .WithAudioSink(_audioSink)
                .WithHardwareFrames(PlayerView.VideoSurface.PrefersHardwareFrames)
                .WithLogger(_loggerFactory)
                .BuildPlayerAsync();

            PlayerView.MediaPlayer = _player;
            Title = $"FrameFlow Player — {Path.GetFileName(path)}";

            // ADR-0069: a refused command comes back as a Result. The catch
            // below still covers what genuinely throws.
            var played = await _player.PlayAsync();
            if (!played.IsSuccess)
                ShowError(
                    played.Error.Inner,
                    $"{Path.GetFileName(path)}: {played.Error.Category}: {played.Error.Message}"
                );
        }
        catch (Exception ex)
        {
            ShowError(ex, $"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a playlist player over every media file in a folder. One video sink and
    /// one audio sink serve the whole folder, so the presenter stays warm across each
    /// boundary and only the decode source is swapped.
    /// </summary>
    private async Task PlayFolderAsync(string folderPath)
    {
        if (_loggerFactory is null || _logger is null)
            return;

        await TeardownPlayerAsync();

        var files = Directory
            .EnumerateFiles(folderPath)
            .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            _logger.LogWarning("No media files found in {Folder}.", folderPath);
            StatusText.Text = $"No media files in {Path.GetFileName(folderPath)}.";
            PlaylistBox.ItemsSource = null;
            _playlistEntries = [];
            return;
        }

        _playlistEntries = files
            .Select(f => new PlaylistEntry(Path.GetFileName(f), MediaSource.FromFile(f)))
            .ToList();
        PlaylistBox.ItemsSource = _playlistEntries;
        StatusText.Text =
            $"{_playlistEntries.Count} file(s) · {Path.GetFileName(folderPath)} · looping";

        try
        {
            var videoSink = PlayerView.AttachSink(_loggerFactory);
            _audioSink = new OpenAlAudioSink(_loggerFactory.CreateLogger<OpenAlAudioSink>());

            var playlist = await FrameFlowPlayer
                .Create()
                .WithMedia(_playlistEntries.Select(e => e.Source))
                .WithVideoSink(videoSink)
                .WithAudioSink(_audioSink)
                .WithHardwareFrames(PlayerView.VideoSurface.PrefersHardwareFrames)
                .WithRepeatMode(RepeatMode.All)
                .WithLogger(_loggerFactory)
                .BuildPlayerAsync();

            _playlistPlayer = playlist;
            _player = playlist;

            // Follow the now-playing file in the list as the presenter advances.
            _transitionSub = playlist
                .SourceTransitioned.ObserveOnUiThread()
                .Subscribe(OnSourceTransitioned);

            PlayerView.MediaPlayer = playlist;
            UpdateSelection(playlist.CurrentSource);
            Title =
                $"FrameFlow Player — {Path.GetFileName(folderPath)} ({_playlistEntries.Count} files)";

            var played = await playlist.PlayAsync();
            if (!played.IsSuccess)
                ShowError(
                    played.Error.Inner,
                    $"{Path.GetFileName(folderPath)}: {played.Error.Category}: {played.Error.Message}"
                );
        }
        catch (Exception ex)
        {
            ShowError(ex, $"Could not play {Path.GetFileName(folderPath)}: {ex.Message}");
        }
    }

    private async Task OpenFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Open a media folder", AllowMultiple = false }
        );
        if (folders.Count == 0)
            return;

        var folderPath = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        {
            _logger?.LogWarning("Selected folder has no usable local path.");
            return;
        }

        await PlayFolderAsync(folderPath);
    }

    private async Task OpenFilePickerAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Open a media file", AllowMultiple = false }
        );
        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            await OpenFileAsync(path);
    }

    /// <summary>Highlights the currently playing file in the playlist (UI thread).</summary>
    private void OnSourceTransitioned(PlaylistTransition transition)
    {
        UpdateSelection(transition.Source);
        Title = $"FrameFlow Player — {transition.Source.DisplayName}";
    }

    private void UpdateSelection(IMediaSource? source)
    {
        if (source is null)
            return;
        var entry = _playlistEntries.FirstOrDefault(e => ReferenceEquals(e.Source, source));
        if (entry is not null)
            PlaylistBox.SelectedItem = entry;
    }

    private async Task JumpToSelectedAsync()
    {
        if (_playlistPlayer is null || PlaylistBox.SelectedItem is not PlaylistEntry entry)
            return;

        // Each file's source object is the one the playlist was created with, so it names
        // the file's item in the player's playlist.
        var item = _playlistPlayer
            .GetPlaylist()
            .Playlist.FirstOrDefault(i => ReferenceEquals(i.Source, entry.Source));
        if (item is null)
            return;

        try
        {
            // Move the playlist to the picked file. The presenter stays warm across the
            // jump, and the loop carries on from there. A jump to the file already playing
            // does nothing.
            var jumped = await _playlistPlayer.JumpToAsync(item);
            if (!jumped.IsSuccess)
                _logger?.LogWarning(
                    "Jump to {Name} refused: {Message}",
                    entry.Name,
                    jumped.Error.Message
                );
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Jump to {Name} failed", entry.Name);
        }
    }

    /// <summary>Puts a failure where the user can see it, and in the log.</summary>
    private void ShowError(Exception? ex, string message)
    {
        _logger?.LogError(ex, "{Message}", message);
        StatusText.Text = message;
        Title = "FrameFlow Player — error";
    }

    private async Task TeardownPlayerAsync()
    {
        // Unbind from the view so the sub-controls dispose their observable
        // subscriptions before the player itself dies.
        PlayerView.MediaPlayer = null;

        _transitionSub?.Dispose();
        _transitionSub = null;
        _playlistPlayer = null;

        if (_player is not null)
        {
            try
            {
                await _player.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Player teardown threw");
            }
            _player = null;
        }

        // The audio sink is caller-owned. The controller already deactivated it
        // during its dispose; this releases the native OpenAL device handle.
        if (_audioSink is not null)
        {
            try
            {
                await _audioSink.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Audio sink teardown threw");
            }
            _audioSink = null;
        }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosing)
            return;
        e.Cancel = true;
        _isClosing = true;
        Closing -= OnWindowClosing;

        await TeardownPlayerAsync();
        _loggerFactory?.Dispose();
        Close();
    }
}
