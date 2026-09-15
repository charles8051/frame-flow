using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FrameFlow.Avalonia;
using FrameFlow.Avalonia.Windows;
using FrameFlow.Media;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.ZeroCopyInterop;

/// <summary>
/// Drives the zero-copy composition-interop spike: opens a file with
/// <see cref="HardwareDecodeMode.Required"/> + <c>yieldHardwareFrames: true</c>
/// so the decoder produces D3D11 <c>GpuVideoFrame</c>s, and hands them to the
/// <see cref="CompositionInteropVideoView"/> sink which presents them with no
/// CPU round-trip.
/// </summary>
/// <remarks>
/// With <see cref="Soak"/> set it runs two such players side by side, each on its own presenter,
/// and samples both into a CSV. Two presenters on one GPU is the condition ADR-0063's hang needed,
/// and the samples are what a before/after comparison of a playback change reads.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainWindow> _logger;
    private readonly List<IVideoSurface> _surfaces = [];
    private readonly List<IMediaPlayer> _players = [];
    private SoakSampler? _sampler;
    private DispatcherTimer? _sampleTimer;
    private DispatcherTimer? _exitTimer;
    private bool _isClosing;

    public string? StartupFilePath { get; set; }

    /// <summary>Run full-screen (covers the output).</summary>
    public bool StartupFullscreen { get; set; }

    /// <summary>Hardware-decode policy: <c>auto</c> (default), <c>disabled</c>/<c>software</c>,
    /// or <c>required</c>. <c>auto</c> uses the zero-copy GPU path when D3D11VA binds and the
    /// CPU upload fallback otherwise.</summary>
    public string? StartupHwMode { get; set; }

    /// <summary>When &gt; 0, the window closes itself after this many seconds —
    /// a graceful shutdown that flushes the log, for autonomous/headless runs.</summary>
    public int ExitAfterSeconds { get; set; }

    /// <summary>Set by <c>--soak</c>: two players, sampled into a CSV.</summary>
    public SoakOptions? Soak { get; set; }

    public MainWindow(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainWindow>();
        InitializeComponent();
        Closing += OnWindowClosing;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _logger.LogInformation("Window loaded; bringing up GPU interop and playback.");

        if (StartupFullscreen)
            WindowState = WindowState.FullScreen;

        if (ExitAfterSeconds > 0)
        {
            _exitTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(ExitAfterSeconds),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    _logger.LogInformation("--exit-after {N}s elapsed; closing.", ExitAfterSeconds);
                    Close();
                }
            );
            _exitTimer.Start();
        }

        if (string.IsNullOrEmpty(StartupFilePath) || !File.Exists(StartupFilePath))
        {
            const string msg =
                "No video file. Pass an H.264/HEVC .mp4 path as an argument "
                + "(the box must support D3D11VA decode of that codec).";
            StatusText.Text = msg;
            _logger.LogWarning("{Message}", msg);
            return;
        }

        // A soak measures the hardware path, so it requires hardware decode unless the caller
        // named a mode. Auto would fall back to software and report a soak that proved nothing.
        var hwMode = StartupHwMode?.Trim().ToLowerInvariant() switch
        {
            "disabled" or "software" => HardwareDecodeMode.Disabled,
            "required" => HardwareDecodeMode.Required,
            null or "" when Soak is not null => HardwareDecodeMode.Required,
            _ => HardwareDecodeMode.Auto,
        };
        _logger.LogInformation(
            "Hardware decode mode: {Mode} (from --hw-mode '{Raw}').", hwMode, StartupHwMode ?? "(unset)");

        if (Soak is { } soak)
        {
            await StartSoakAsync(soak, hwMode);
            return;
        }

        StatusText.Text = $"Presenting {Path.GetFileName(StartupFilePath)} …";
        await StartPlayerAsync(StartupFilePath, hwMode, Host(VideoHost, column: null));
    }

    /// <summary>Starts both players, then samples them on the interval the options set.</summary>
    private async Task StartSoakAsync(SoakOptions soak, HardwareDecodeMode hwMode)
    {
        var left = StartupFilePath!;
        var right = soak.SecondFilePath is { } second && File.Exists(second) ? second : left;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
        };
        VideoHost.Children.Add(grid);

        _sampler = SoakSampler.TryCreate(soak.CsvPath, soak.Label, _logger);
        if (_sampler is null)
        {
            StatusText.Text = $"Soak aborted — cannot write {soak.CsvPath}.";
            return;
        }

        StatusText.Text = $"Soak '{soak.Label}' → {soak.CsvPath}";
        _logger.LogInformation(
            "Soak '{Label}': left={Left}, right={Right}, every {N}s → {Csv}.",
            soak.Label,
            Path.GetFileName(left),
            Path.GetFileName(right),
            soak.SampleSeconds,
            soak.CsvPath
        );

        var leftPlayer = await StartPlayerAsync(left, hwMode, Host(grid, column: 0));
        var rightPlayer = leftPlayer is null
            ? null
            : await StartPlayerAsync(right, hwMode, Host(grid, column: 1));

        // A soak is two panes or nothing: one pane measures a condition the soak is not about, and
        // a player left running unsampled says nothing. Stop what started.
        if (leftPlayer is null || rightPlayer is null || _isClosing)
        {
            _logger.LogWarning("Soak '{Label}' did not start both panes; stopping.", soak.Label);
            StatusText.Text = "Soak aborted — a pane did not start. See the log.";
            await StopPlaybackAsync();
            return;
        }

        _sampler.Watch("left", Path.GetFileName(left), leftPlayer);
        _sampler.Watch("right", Path.GetFileName(right), rightPlayer);

        _sampleTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(soak.SampleSeconds),
            DispatcherPriority.Background,
            (_, _) =>
            {
                if (_sampler is null)
                    return;
                StatusText.Text = _sampler.Sample();
                if (_sampler.Faulted)
                    _sampleTimer?.Stop();
            }
        );
        _sampleTimer.Start();
    }

    /// <summary>A panel inside <paramref name="parent"/>, in <paramref name="column"/> if it is a grid.</summary>
    private static Panel Host(Panel parent, int? column)
    {
        if (column is null)
            return parent;

        var host = new Panel();
        Grid.SetColumn(host, column.Value);
        parent.Children.Add(host);
        return host;
    }

    /// <summary>
    /// Builds a zero-copy presenter inside <paramref name="host"/> and plays <paramref name="path"/>
    /// through it, looping. Returns the player, or <see langword="null"/> when it could not start.
    /// </summary>
    private async Task<IMediaPlayer?> StartPlayerAsync(
        string path,
        HardwareDecodeMode hwMode,
        Panel host
    )
    {
        // Present via the compositor-interop zero-copy view: the hardware-decoded NV12
        // frame stays on the GPU, is color-converted to BGRA, and is imported straight
        // into Avalonia's compositor with no CPU round-trip.
        //
        // The surface joins the teardown list only once its player is built, so a teardown that
        // runs while it is being built cannot dispose a surface that build is still using. Until
        // then this method owns it.
        IVideoSurface surface = new CompositionInteropVideoView();
        host.Children.Add(surface.Control);
        var videoSink = surface.AttachSink(_loggerFactory);
        _logger.LogInformation("Presentation surface: compositor interop (zero-copy).");

        try
        {
            var player = await FrameFlowPlayer
                .Open(path)
                .WithVideoSink(videoSink)
                .WithHardwareDecode(hwMode)
                .WithHardwareFrames(surface.PrefersHardwareFrames)
                .WithRepeatMode(RepeatMode.One)
                .WithLogger(_loggerFactory)
                .BuildPlayerAsync();

            // The window can start closing while a player is being built. Its teardown has already
            // run, so this player and its surface dispose themselves instead of joining the lists.
            if (_isClosing)
            {
                await player.DisposeAsync();
                await DisposeSurfaceAsync(surface);
                return null;
            }

            // Both are handed to the teardown together, on the UI thread, with no await between
            // the check above and here.
            _players.Add(player);
            _surfaces.Add(surface);

            var played = await player.PlayAsync();
            if (!played.IsSuccess)
            {
                _logger.LogError(
                    played.Error.Inner,
                    "Zero-copy playback refused: {Category}: {Message}",
                    played.Error.Category,
                    played.Error.Message
                );
                StatusText.Text = $"Refused — {played.Error.Message}";
                return null;
            }

            _logger.LogInformation("Playback started on the zero-copy composition-interop sink.");
            return player;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zero-copy playback failed to start (HW D3D11VA decode required).");
            StatusText.Text = "Failed — see log. (HW D3D11VA decode required for this spike.)";
            // A build that threw left the surface with this method, so it disposes it. One that
            // reached the lists is the teardown's.
            if (!_surfaces.Contains(surface))
                await DisposeSurfaceAsync(surface);
            return null;
        }
    }

    private static async Task DisposeSurfaceAsync(IVideoSurface surface)
    {
        if (surface is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }

    /// <summary>Disposes the sampler, every player and every surface, in that order.</summary>
    private async Task StopPlaybackAsync()
    {
        _sampleTimer?.Stop();
        _sampler?.Dispose();
        _sampler = null;

        foreach (var player in _players)
        {
            try { await player.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Player teardown threw"); }
        }
        _players.Clear();

        foreach (var surface in _surfaces)
        {
            await DisposeSurfaceAsync(surface);
        }
        _surfaces.Clear();
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosing)
            return;
        e.Cancel = true;
        _isClosing = true;
        Closing -= OnWindowClosing;
        _exitTimer?.Stop();
        _sampleTimer?.Stop();

        // A last sample, so a run that ends mid-window still records what that window did.
        _sampler?.Sample();
        await StopPlaybackAsync();

        _logger.LogInformation("Shutdown complete; flushing log.");
        _loggerFactory.Dispose();
        Close();
    }
}
