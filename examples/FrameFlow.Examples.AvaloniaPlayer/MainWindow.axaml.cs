using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Avalonia;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.AvaloniaPlayer;

/// <summary>
/// FrameFlow Avalonia player — the canonical minimum-effort
/// example post-FrameFlowPlayerView refactor.
/// </summary>
/// <remarks>
/// <para>
/// The window now hosts a single
/// <see cref="FrameFlow.Avalonia.FrameFlowPlayerView"/> control which
/// supplies video surface, transport bar, seek bar, volume control,
/// status badge, stream summary, position label, file picker,
/// drag-drop, and keyboard shortcuts. The example's responsibilities
/// shrink to:
/// </para>
/// <list type="bullet">
///   <item>Logger factory wiring (TextBox + optional file sink).</item>
///   <item>Handling <c>FileOpenRequested</c> by building an
///         <see cref="IMediaPlayer"/> and assigning it to the view.</item>
///   <item>CLI arg propagation (startup file, loop flag, log file).</item>
/// </list>
/// <para>
/// migrated to the substrate via
/// <see cref="MediaPlayer.CreateAsync"/>. The <see cref="IMediaPlayer"/>
/// returned is the same internal wrapper the old
/// <c>FrameFlowPlayer.Open(...).BuildAsync()</c> path returned, just
/// pointed at <see cref="FrameFlow.Playback.PlaybackController"/>
/// instead of the legacy <see cref="FrameFlow.Playback.PlaybackController"/>.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private ILoggerFactory? _loggerFactory;
    private ILogger<MainWindow>? _logger;
    private IMediaPlayer? _player;

    // Folder-playlist mode: the same instance as _player, kept typed for the
    // playlist-specific surface (transition stream, enqueue, skip). Null in
    // single-file mode.
    private IMediaPlaylistPlayer? _playlistPlayer;
    private IDisposable? _transitionSub;
    private IReadOnlyList<PlaylistEntry> _playlistEntries = [];

    private OpenAlAudioSink? _audioSink;
    private bool _useGpu;
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

    public string? StartupFilePath { get; set; }

    /// <summary>Folder to auto-open on startup and play through (gapless playlist). Takes precedence over <see cref="StartupFilePath"/>.</summary>
    public string? StartupFolderPath { get; set; }
    public bool StartupLoop { get; set; }
    public string? StartupLogFilePath { get; set; }
    public string? StartupHwMode { get; set; }

    /// <summary>Video presenter: <c>cpu</c> (default — full player chrome) or <c>gpu</c>
    /// (the Windows zero-copy composition-interop presenter; video-only for now).</summary>
    public string? StartupPresenter { get; set; }

    /// <summary>When set (<c>--no-audio</c>), no audio sink is attached and the
    /// player paces video off the <see cref="WallClockSource"/> fallback — the
    /// headless-signage repro path.</summary>
    public bool StartupNoAudio { get; set; }

    /// <summary>When &gt; 0 (<c>--exit-after N</c>), auto-closes the window after
    /// N seconds so the example can run unattended.</summary>
    public int StartupExitAfterSeconds { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        PlayerView.FileOpenRequested += async (_, e) => await OpenFileAsync(e.FilePath);
        OpenFolderButton.Click += async (_, _) => await OpenFolderAsync();
        OpenFileButton.Click += async (_, _) => await OpenFilePickerAsync();
        // Double-click a file in the list to jump to it (no presenter rebuild).
        PlaylistBox.DoubleTapped += async (_, _) => await JumpToSelectedAsync();
        Closing += OnWindowClosing;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new TextBoxLoggerProvider(LogOutput, LogLevel.Information));
            if (!string.IsNullOrEmpty(StartupLogFilePath))
                b.AddProvider(new FileLoggerProvider(ExampleLogPaths.Resolve(StartupLogFilePath), LogLevel.Debug));
        });
        _logger = _loggerFactory.CreateLogger<MainWindow>();
        _logger.LogInformation("FrameFlow Player ready.");

        // --exit-after N: schedule an unattended clean shutdown so an agent can
        // launch, let the failing path surface, and read the flushed log.
        if (StartupExitAfterSeconds > 0)
        {
            _logger.LogInformation(
                "Auto-exit scheduled in {Seconds}s (--exit-after).",
                StartupExitAfterSeconds
            );
            _ = Task.Delay(TimeSpan.FromSeconds(StartupExitAfterSeconds))
                .ContinueWith(
                    _ => global::Avalonia.Threading.Dispatcher.UIThread.Post(Close),
                    TaskScheduler.Default
                );
        }

        // Presenter selection. --presenter gpu injects the Windows zero-copy surface INTO
        // the player via the IVideoSurface seam — so it keeps the full transport chrome.
        var wantGpu = string.Equals(StartupPresenter?.Trim(), "gpu", StringComparison.OrdinalIgnoreCase);
        _useGpu = wantGpu && OperatingSystem.IsWindows();
        if (wantGpu && !_useGpu)
            _logger.LogWarning("--presenter gpu requested but not on Windows; using the CPU surface.");

        if (_useGpu)
        {
            PlayerView.VideoSurface = new CompositionInteropVideoView();
            _logger.LogInformation("Presenter: GPU zero-copy surface (with full player chrome).");
        }
        else
        {
            _logger.LogInformation("Presenter: CPU (FrameFlowVideoView).");
        }

        // Wire the logger + materialize the hosted surface's sink (works for either surface;
        // for the GPU surface this also brings up the compositor interop now that it's attached).
        PlayerView.AttachSink(_loggerFactory);
        PlayerView.LoopByDefault = StartupLoop;

        // A folder takes precedence (gapless playlist over one warm presenter);
        // otherwise fall back to the single startup file.
        if (!string.IsNullOrEmpty(StartupFolderPath) && Directory.Exists(StartupFolderPath))
            await PlayFolderAsync(StartupFolderPath);
        else if (!string.IsNullOrEmpty(StartupFilePath) && File.Exists(StartupFilePath))
            await OpenFileAsync(StartupFilePath);
    }

    private async Task OpenFileAsync(string path)
    {
        if (_loggerFactory is null || _logger is null)
            return;

        await TeardownPlayerAsync();

        // Single-file mode: clear any folder playlist shown in the sidebar.
        PlaylistBox.ItemsSource = null;
        _playlistEntries = [];
        PlaylistStatus.Text = $"Single file: {Path.GetFileName(path)}";

        try
        {
            // ── The heart of the example: construct sinks directly (no
            //     DI), wire them through MediaPlayer.CreateAsync, and hand
            //     the resulting IMediaPlayer to the view.

            // --no-audio: attach NO audio sink, so the player falls back to the
            // WallClockSource pacer (ADR-0003) — the exact shape a signage
            // deployment uses (audioSink: null + GPU presenter). With an audio sink,
            // the audio device backpressures the pipeline to realtime; without
            // one, this reproduces whatever the wallclock-paced path does.
            if (StartupNoAudio)
            {
                _audioSink = null;
                _logger.LogInformation(
                    "Audio DISABLED (--no-audio): audioSink=null -> WallClockSource pacing (signage repro)."
                );
            }
            else
            {
                _audioSink = new OpenAlAudioSink(
                    _loggerFactory.CreateLogger<OpenAlAudioSink>()
                );
            }

            // The hosted surface's sink — CPU AvaloniaVideoSink or the GPU presenter's sink.
            var videoSink = PlayerView.AttachSink(_loggerFactory);

            _player = await MediaPlayer.CreateAsync(
                source: MediaSource.FromFile(path),
                videoSink: videoSink,
                audioSink: _audioSink,
                hardwareDecodeMode: ResolveHwMode(),
                yieldHardwareFrames: PlayerView.VideoSurface.PrefersHardwareFrames,
                initialRepeatMode: StartupLoop ? RepeatMode.One : RepeatMode.Off,
                loggerFactory: _loggerFactory
            );

            // Chrome binds to the player for both surfaces now (the seam keeps the UI).
            PlayerView.MediaPlayer = _player;

            Title = $"FrameFlow Player — {Path.GetFileName(path)}";

            await _player.PlayAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open {File}", Path.GetFileName(path));
            Title = "FrameFlow Player — error";
        }
    }

    // ── Folder playlist: open a folder and play through all its media files ──
    //     over ONE warm presenter (the same video + audio sink for every file),
    //     swapping only the decode source at each boundary. This is the
    //     gapless-playlist feature in action — no per-item present-pipeline
    //     rebuild, unlike opening each file as its own player.

    private async Task OpenFolderAsync()
    {
        if (_loggerFactory is null || _logger is null)
            return;

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
            _logger.LogWarning("Selected folder has no usable local path.");
            return;
        }

        await PlayFolderAsync(folderPath);
    }

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
            PlaylistStatus.Text = $"No media files in {Path.GetFileName(folderPath)}.";
            PlaylistBox.ItemsSource = null;
            _playlistEntries = [];
            return;
        }

        _playlistEntries = files
            .Select(f => new PlaylistEntry(Path.GetFileName(f), MediaSource.FromFile(f)))
            .ToList();
        PlaylistBox.ItemsSource = _playlistEntries;
        PlaylistStatus.Text =
            $"{_playlistEntries.Count} file(s) · {Path.GetFileName(folderPath)} · looping";

        try
        {
            _audioSink = new OpenAlAudioSink(_loggerFactory.CreateLogger<OpenAlAudioSink>());

            // ONE sink for the whole folder — the warm presenter the playlist
            // feeds sequential decoders into.
            var videoSink = PlayerView.AttachSink(_loggerFactory);

            var playlist = await MediaPlaylistPlayer.CreateAsync(
                sources: _playlistEntries.Select(e => e.Source),
                videoSink: videoSink,
                audioSink: _audioSink,
                hardwareDecodeMode: ResolveHwMode(),
                yieldHardwareFrames: PlayerView.VideoSurface.PrefersHardwareFrames,
                initialRepeatMode: RepeatMode.All,
                loggerFactory: _loggerFactory
            );

            _playlistPlayer = playlist;
            _player = playlist;

            // Follow the now-playing file in the list as the presenter advances.
            // Fully qualified because FrameFlow.Avalonia and FrameFlow.Playback
            // both expose a Subscribe(IObservable, Action) extension.
            _transitionSub = AvaloniaObservableExtensions.Subscribe(
                playlist.SourceTransitioned.ObserveOnUiThread(),
                OnSourceTransitioned
            );

            PlayerView.MediaPlayer = playlist;
            UpdateSelection(playlist.CurrentSource);
            Title =
                $"FrameFlow Player — {Path.GetFileName(folderPath)} ({_playlistEntries.Count} files)";

            _logger.LogInformation(
                "Playing folder {Folder}: {Count} files over one warm presenter (looping).",
                folderPath,
                _playlistEntries.Count
            );

            await playlist.PlayAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start folder playback for {Folder}", folderPath);
            Title = "FrameFlow Player — error";
        }
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
        _logger?.LogInformation(
            "Now playing [{Index}] {Name}{Wrapped}",
            transition.Index,
            transition.Source.DisplayName,
            transition.Wrapped ? " (looped to start)" : string.Empty
        );
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
        if (ReferenceEquals(_playlistPlayer.CurrentSource, entry.Source))
            return;

        try
        {
            // Steer the preroll target to the picked file, then hand off — the
            // presenter stays warm across the jump.
            await _playlistPlayer.SetNextAsync(entry.Source);
            await _playlistPlayer.SkipToNextAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Jump to {Name} failed", entry.Name);
        }
    }

    /// <summary>Maps the <c>--hw-mode</c> CLI string to the decoder policy (unset/unknown = Auto).</summary>
    private HardwareDecodeMode ResolveHwMode()
    {
        var hwMode = StartupHwMode?.Trim().ToLowerInvariant() switch
        {
            "disabled" => HardwareDecodeMode.Disabled,
            "software" => HardwareDecodeMode.Disabled,
            "required" => HardwareDecodeMode.Required,
            _ => HardwareDecodeMode.Auto,
        };
        _logger?.LogInformation(
            "Hardware decode mode (from --hw-mode '{Raw}'): {Mode}",
            StartupHwMode ?? "(unset)",
            hwMode
        );
        return hwMode;
    }

    private async Task TeardownPlayerAsync()
    {
        // Unbind from the view so the sub-controls dispose their
        // observable subscriptions before the player itself dies.
        PlayerView.MediaPlayer = null;

        // Stop following playlist transitions before the player goes away.
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

        // the audio sink is now caller-owned (the
        // old DI-based path had the container handle disposal). The
        // controller already deactivated the sink during its dispose;
        // here we release the native OpenAL device handle.
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
        // The hosted GPU surface (if any) cleans up on detach when the window closes.
        _loggerFactory?.Dispose();
        Close();
    }
}
