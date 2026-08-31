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
public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainWindow> _logger;
    private IMediaPlayer? _player;
    private IVideoSurface? _surface;
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

        // Present via the compositor-interop zero-copy view: the hardware-decoded NV12
        // frame stays on the GPU, is color-converted to BGRA, and is imported straight
        // into Avalonia's compositor with no CPU round-trip.
        _surface = new CompositionInteropVideoView();
        VideoHost.Children.Add(_surface.Control);
        var videoSink = _surface.AttachSink(_loggerFactory);
        _logger.LogInformation("Presentation surface: compositor interop (zero-copy).");

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

        StatusText.Text = $"Presenting {Path.GetFileName(StartupFilePath)} …";

        var hwMode = StartupHwMode?.Trim().ToLowerInvariant() switch
        {
            "disabled" or "software" => HardwareDecodeMode.Disabled,
            "required" => HardwareDecodeMode.Required,
            _ => HardwareDecodeMode.Auto,
        };
        _logger.LogInformation(
            "Hardware decode mode: {Mode} (from --hw-mode '{Raw}').", hwMode, StartupHwMode ?? "(unset)");

        try
        {
            _player = await MediaPlayer.CreateAsync(
                source: MediaSource.FromFile(StartupFilePath),
                videoSink: videoSink,
                audioSink: null,
                hardwareDecodeMode: hwMode,
                yieldHardwareFrames: _surface.PrefersHardwareFrames,
                initialRepeatMode: RepeatMode.One,
                loggerFactory: _loggerFactory
            );

            await _player.PlayAsync();
            _logger.LogInformation("Playback started on the zero-copy composition-interop sink.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zero-copy playback failed to start (HW D3D11VA decode required).");
            StatusText.Text = "Failed — see log. (HW D3D11VA decode required for this spike.)";
        }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosing)
            return;
        e.Cancel = true;
        _isClosing = true;
        Closing -= OnWindowClosing;
        _exitTimer?.Stop();

        if (_player is not null)
        {
            try { await _player.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Player teardown threw"); }
            _player = null;
        }

        if (_surface is IAsyncDisposable surfaceDisposable)
            await surfaceDisposable.DisposeAsync();
        _logger.LogInformation("Shutdown complete; flushing log.");
        _loggerFactory.Dispose();
        Close();
    }
}
