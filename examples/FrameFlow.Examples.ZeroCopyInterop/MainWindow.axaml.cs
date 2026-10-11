using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FrameFlow.Avalonia;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Playback;
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
    private readonly bool _d3d12Device;
    private HardwareDevice? _device;
    private IMediaPlayer? _player;
    private IDisposable? _errors;
    private string? _error;
    private Task _startup = Task.CompletedTask;
    private bool _closing;
    private bool _closed;

    public MainWindow(ILoggerFactory loggerFactory, string? path, bool d3d12Device)
    {
        _loggerFactory = loggerFactory;
        _path = path;
        _d3d12Device = d3d12Device;
        InitializeComponent();
    }

    private async Task StartAsync()
    {
        if (_path is null)
        {
            StatusText.Text = "Usage: <video file> [--hw-device d3d12va]";
            return;
        }
        if (!File.Exists(_path))
        {
            StatusText.Text = $"File not found: {_path}";
            return;
        }

        try
        {
            // Optional: one D3D12VA device the decoder borrows instead of creating its own. Each
            // frame is converted on that device and copied across to the compositor's D3D11 side.
            _device = _d3d12Device ? HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va) : null;

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
            if (_closing)
                return;

            // Commands answer with a Result; a failure during playback arrives here instead.
            _errors = _player.ErrorOccurred.Subscribe(error =>
                Dispatcher.UIThread.Post(() => _error = $"{error.Category}: {error.Message}")
            );

            var played = await _player.PlayAsync();
            if (!played.IsSuccess)
            {
                StatusText.Text = $"{played.Error.Category}: {played.Error.Message}";
                return;
            }
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<MainWindow>().LogError(ex, "Could not play {Path}.", _path);
            StatusText.Text = $"Could not play {Path.GetFileName(_path)}: {ex.Message}";
            return;
        }

        // The super-resolution status is updated as frames are presented, so it is polled.
        DispatcherTimer.Run(
            () =>
            {
                StatusText.Text =
                    _error
                    ?? $"{Path.GetFileName(_path)} · super resolution: {VideoView.DriverSuperResolutionStatus}";
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closed)
            return;

        // Hold the window open until everything is disposed, then close it for real.
        e.Cancel = true;
        if (!_closing)
        {
            _closing = true;
            _ = TearDownAsync();
        }
    }

    private async Task TearDownAsync()
    {
        try
        {
            // A player still being built finishes first, so it is disposed too. Then the view,
            // which owns the sink, and last the device the decoder borrowed.
            await _startup;
            _errors?.Dispose();
            if (_player is not null)
                await _player.DisposeAsync();
            await VideoView.DisposeAsync();
            _device?.Dispose();
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<MainWindow>().LogError(ex, "Teardown failed.");
        }
        finally
        {
            _closed = true;
            Close();
        }
    }
}
