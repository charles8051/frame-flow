using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FrameFlow.Avalonia;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.ZeroCopyInterop;

/// <summary>
/// Plays one file, looping, on a <see cref="FrameFlow.Avalonia.Windows.CompositionInteropVideoView"/>:
/// hardware-decoded frames go from the decoder to Avalonia's compositor without leaving the GPU.
/// <c>S</c> toggles the driver's video super resolution.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string? _path;
    private readonly HardwareDecodeBackendKind? _backend;
    private HardwareDevice? _device;
    private IMediaPlayer? _player;
    private Task _startup = Task.CompletedTask;
    private bool _closing;
    private bool _closed;

    public MainWindow(
        ILoggerFactory loggerFactory,
        string? path,
        HardwareDecodeBackendKind? backend,
        bool superResolution
    )
    {
        _loggerFactory = loggerFactory;
        _path = path;
        _backend = backend;
        InitializeComponent();
        VideoView.DriverSuperResolution = superResolution;
        Closing += OnClosing;
    }

    private async Task StartAsync()
    {
        if (_path is null)
        {
            StatusText.Text = "Pass an H.264 or HEVC file to play.";
            return;
        }

        try
        {
            // Optional: one device the decoder borrows instead of creating its own. A D3D12VA
            // frame is converted on that device and copied across to the compositor's D3D11 side.
            _device = _backend is { } backend ? HardwareDevice.Create(backend) : null;

            // The view's sink takes D3D11 and D3D12 frames, so the player keeps hardware-decoded
            // frames on the GPU without being asked. Software-decoded frames are uploaded instead.
            var builder = FrameFlowPlayer
                .Create()
                .WithMedia(_path)
                .WithVideoSink(((IVideoSurface)VideoView).AttachSink(_loggerFactory))
                .WithRepeatMode(RepeatMode.One)
                .WithLogger(_loggerFactory);
            if (_device is not null)
                builder = builder.WithHardwareDevice(_device);

            _player = await builder.BuildPlayerAsync();
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<MainWindow>().LogError(ex, "Could not open {Path}.", _path);
            StatusText.Text = $"Could not open {Path.GetFileName(_path)}: {ex.Message}";
            return;
        }

        var played = await _player.PlayAsync();
        if (!played.IsSuccess)
        {
            StatusText.Text = $"{played.Error.Category}: {played.Error.Message}";
            return;
        }

        // The status changes as frames are presented, so it is polled.
        DispatcherTimer.Run(
            () =>
            {
                StatusText.Text =
                    $"{Path.GetFileName(_path)} · super resolution: {VideoView.DriverSuperResolutionStatus}";
                return !_closing;
            },
            TimeSpan.FromMilliseconds(500)
        );
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _startup = StartAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.S)
            VideoView.DriverSuperResolution = !VideoView.DriverSuperResolution;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;

        // A player still being built finishes first, so it is disposed too. Then the view, which
        // owns the sink, and last the device the decoder borrowed.
        await _startup;
        if (_player is not null)
            await _player.DisposeAsync();
        await VideoView.DisposeAsync();
        _device?.Dispose();

        _closed = true;
        Close();
    }
}
