using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FrameFlow.Camera;
using FrameFlow.Examples.Common;
using FrameFlow.Examples.Multicast.Shared;
using FrameFlow.Graph;
using FrameFlow.Inference;
using FrameFlow.Inference.Cuda;
using FrameFlow.Media;
using FrameFlow.Video;
using FrameFlow.Yolo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery;
using Periphery.Camera;

namespace FrameFlow.Examples.Camera.Multicast;

/// <summary>
/// A live camera, every frame shown in three panes: as captured, under YOLOv8's boxes, and through
/// a color filter. No player or pass takes a camera, so the graph is built here.
/// </summary>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _closing = new();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private ILogger _logger = NullLogger.Instance;
    private Task _startup = Task.CompletedTask;
    private Task _switching = Task.CompletedTask;
    private Yolov8Detector? _detector;
    private DeviceSessionHost<CameraSession>? _host;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        CameraPicker.SelectionChanged += (_, _) =>
        {
            if (CameraPicker.SelectedItem is DeviceInfo camera)
                _switching = SwitchToAsync(_switching, camera);
        };
    }

    /// <summary>Runs while the camera is open: camera, BGRA, the detector beside the picture, the panes.</summary>
    private async Task RunGraphAsync(CameraSession session, CancellationToken ct)
    {
        // Keeps only the newest frame, so a graph slower than the camera never shows a stale one.
        await using var camera = session.AsPushVideoFrameSource(ct, _loggerFactory);

        var graph = new Graph.Graph();
        var frames = graph.Pipeline(camera.Source)
            .Then(VideoOperators.ConvertPixelFormat("bgra", PixelFormat.Bgra32));
        if (_detector is { } detector)
            frames = frames.Infer("yolo", detector, Panes.Detections.Post);
        frames.To(Panes.Sink.AsSinkNode("panes"));

        Dispatcher.UIThread.Post(() => StatusText.Text = $"Capturing from {session.DeviceInfo.Name}");
        try
        {
            await graph.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The capture graph faulted.");
            Dispatcher.UIThread.Post(() => StatusText.Text = $"Capture failed: {ex.Message}");
        }
    }

    private Task<CameraSession> OpenSessionAsync(DeviceInfo camera, CancellationToken ct) =>
        CameraSession
            .For(camera)
            .PreferPixelFormat(CameraPixelFormat.Bgra32)
            // The formats FrameFlow.Camera can hand to a graph.
            .AllowOnlyPixelFormats(
                CameraPixelFormat.Bgra32,
                CameraPixelFormat.Rgba32,
                CameraPixelFormat.Nv12,
                CameraPixelFormat.Yuy2,
                CameraPixelFormat.Uyvy
            )
            .MaxResolution(1280, 720)
            // The graph holds three camera frames at once (the source's, its edge's and the
            // converter's) and the push source one more.
            .WithSessionOptions(o => o with { BufferCount = 4 })
            .WithLogger(_loggerFactory.CreateLogger<CameraSession>())
            .OpenAsync(ct);

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _startup = StartAsync();
    }

    private async Task StartAsync()
    {
        _loggerFactory = ExampleLogging.CreateFactory("camera-multicast.log", onFailure: ex => Title += $"  [no log file: {ex.Message}]");
        _logger = _loggerFactory.CreateLogger<MainWindow>();
        Panes.Logger = _loggerFactory.CreateLogger<MulticastPanes>();

        try
        {
            _detector = await LoadDetectorAsync();
            var cameras = await CameraDevice.EnumerateAsync(_closing.Token);
            CameraPicker.ItemsSource = cameras;
            if (cameras.Count > 0)
                CameraPicker.SelectedIndex = 0;
            else
                StatusText.Text = "No camera found.";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera enumeration failed.");
            StatusText.Text = $"Camera enumeration failed: {ex.Message}";
        }
    }

    /// <summary>The detector, or null when it cannot load: the other panes run on without it.</summary>
    private async Task<Yolov8Detector?> LoadDetectorAsync()
    {
        // CUDA first, and the CPU when there is no NVIDIA GPU.
        var inference = InferenceSessionFactoryBuilder.Create(
            ExecutionProvider.Cuda,
            new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
            {
                [ExecutionProvider.Cuda] = path => new CudaInferenceSession(path),
                [ExecutionProvider.Cpu] = path => new CpuInferenceSession(path),
            }
        );

        Panes.SetDetectionStatus("loading YOLOv8");
        try
        {
            // A cached model opens synchronously, so load it off the UI thread.
            var detector = await Task.Run(() =>
                Yolov8Detector.CreateAsync(inference, ct: _closing.Token, loggerFactory: _loggerFactory)
            );
            _logger.LogInformation("YOLOv8 runs on {Provider}.", inference.ActiveProvider);
            Panes.SetDetectionStatus($"YOLOv8 on {inference.ActiveProvider}");
            return detector;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "YOLOv8 did not load; the second pane shows the camera without boxes.");
            Panes.SetDetectionStatus("YOLOv8 unavailable");
            return null;
        }
    }

    /// <summary>Closes the open camera, after any switch still in progress, and opens <paramref name="camera"/>.</summary>
    private async Task SwitchToAsync(Task previous, DeviceInfo camera)
    {
        await previous;
        await CloseCameraAsync();
        if (_closing.IsCancellationRequested)
            return;

        StatusText.Text = $"Opening {camera.Name}";
        try
        {
            // The host opens a session, runs the graph while it is open, and reopens it if the
            // camera is unplugged and comes back.
            _host = await DeviceSessionHost<CameraSession>.ForDeviceAsync(
                camera,
                createSession: OpenSessionAsync,
                whileSessionActive: RunGraphAsync,
                ct: _closing.Token
            );
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open {Camera}.", camera.Name);
            StatusText.Text = $"Could not open {camera.Name}: {ex.Message}";
        }
    }

    private async Task CloseCameraAsync()
    {
        if (_host is not { } host)
            return;

        _host = null;
        try
        {
            // Stops the graph and closes the session.
            await host.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the camera failed.");
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closed)
            return;

        // Keep the window open until the graph has stopped, then close it again.
        e.Cancel = true;
        if (_closing.IsCancellationRequested)
            return;
        _closing.Cancel();

        try
        {
            await _startup;
            await _switching;
            await CloseCameraAsync();
            // The detector ran inside the graph, so it goes after it.
            _detector?.Dispose();
        }
        finally
        {
            _loggerFactory.Dispose();
            _closing.Dispose();
            _closed = true;
            Close();
        }
    }
}
