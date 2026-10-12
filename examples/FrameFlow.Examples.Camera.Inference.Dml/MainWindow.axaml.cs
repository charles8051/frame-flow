using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FrameFlow.Avalonia;
using FrameFlow.Camera;
using FrameFlow.Face;
using FrameFlow.Inference;
using FrameFlow.Inference.Dml;
using FrameFlow.Media;
using FrameFlow.Video;
using FrameFlow.Yolo;
using Microsoft.Extensions.Logging;
using Periphery;
using Periphery.Camera;

namespace FrameFlow.Examples.Camera.Inference.Dml;

/// <summary>
/// A live camera on a <c>FrameFlowVideoView</c>, with YOLOv8 running on DirectML beside the
/// picture and its detections drawn over it. <c>--face</c> runs a BlazeFace model instead.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly AvaloniaVideoSink _videoSink;
    private readonly CancellationTokenSource _closingCts = new();
    private IDisposable? _model;
    private IDisposable? _onScreen;
    private Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? _runModel;
    private string _modelLabel = "";
    private Task _starting = Task.CompletedTask;
    private CancellationTokenSource? _captureCts;
    private Task _capture = Task.CompletedTask;
    private bool _closing;
    private bool _closed;

    public string? ModelPath { get; init; }
    public string? FaceModelPath { get; init; }
    public int CameraIndex { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        _loggerFactory = ExampleLogging.CreateFactory(
            "camera-inference-dml.log", onFailure: ex => Title += $"  [no log file: {ex.Message}]");
        _logger = _loggerFactory.CreateLogger<MainWindow>();
        VideoView.LoggerFactory = _loggerFactory;
        _videoSink = VideoView.EnsureSink();
    }

    private async Task CaptureAsync(DeviceInfo camera, CancellationToken ct)
    {
        CameraSession? session = null;
        try
        {
            session = await CameraSession.For(camera)
                .PreferPixelFormat(CameraPixelFormat.Bgra32)
                // The formats FrameFlow.Camera turns into frames.
                .AllowOnlyPixelFormats(
                    CameraPixelFormat.Bgra32, CameraPixelFormat.Rgba32, CameraPixelFormat.Nv12,
                    CameraPixelFormat.Yuy2, CameraPixelFormat.Uyvy)
                .MaxResolution(1280, 720)
                .WithSessionOptions(o => o with { BufferCount = CameraBufferCount() ?? o.BufferCount })
                .WithLogger(_loggerFactory.CreateLogger<CameraSession>())
                .OpenAsync(ct);

            // Cancelled when the graph ends, so the camera's pump stops even if the graph failed.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using var source = session.AsPushVideoFrameSource(stop.Token, _loggerFactory);
            Dispatcher.UIThread.Post(() => StatusText.Text = $"{camera.Name}: {_modelLabel}");
            try
            {
                await BuildGraph(source.Source).RunAsync(stop.Token);
            }
            finally
            {
                stop.Cancel();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture from {Camera} failed.", camera.Name);
            Overlay.Clear();
            Dispatcher.UIThread.Post(() => StatusText.Text = $"Capture from {camera.Name} failed: {ex.Message}");
        }
        finally
        {
            if (session is not null)
                await CloseCameraAsync(session);
        }
    }

    // Camera, converted to BGRA, to the view. _runModel is chain.Infer(id, model, onScreen.Post),
    // which runs the model on a branch that takes the newest frame.
    private FrameFlow.Graph.Graph BuildGraph(SourceNode<IVideoFrame> camera)
    {
        var graph = new FrameFlow.Graph.Graph();
        var bgra = graph.Pipeline(camera).Then(VideoOperators.ConvertPixelFormat("to-bgra", PixelFormat.Bgra32));
        _runModel!(bgra).To(_videoSink.AsSinkNode());
        return graph;
    }

    // As many buffers as the graph holds of the camera's frames, and one for the frame the push
    // source keeps ahead of it, lets the session hand every frame over without a copy.
    private int? CameraBufferCount()
    {
        var standIn = new SourceNode<IVideoFrame>("camera-source", static _ => default);
        return BuildGraph(standIn).FrameBudgetFor(standIn.Output).Frames + 1;
    }

    // Closing a camera can hang after an unclean unplug, so give up after two seconds rather
    // than hold up the next capture or the window closing. Opening that camera again can then
    // fail until the OS lets go of it, and Refresh tries again.
    private async Task CloseCameraAsync(CameraSession session)
    {
        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The camera did not close within two seconds; abandoning it.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the camera failed.");
        }
    }

    private async Task LoadModelAsync()
    {
        // DirectML, and the CPU when DirectML cannot open the model.
        var factory = InferenceSessionFactoryBuilder.Create(
            ExecutionProvider.DirectML,
            new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
            {
                [ExecutionProvider.DirectML] = path => new DmlInferenceSession(path),
                [ExecutionProvider.Cpu] = path => new CpuInferenceSession(path),
            },
            loggerFactory: _loggerFactory);

        if (FaceModelPath is null)
        {
            var yolo = await Yolov8Detector.CreateAsync(factory, ModelPath, _closingCts.Token, _loggerFactory);
            _model = yolo;
            Use(yolo, "yolo", $"YOLOv8 on {factory.ActiveProvider}", Overlay.Show);
        }
        else
        {
            var faces = await BlazeFaceDetector.CreateAsync(factory, FaceModelPath, _loggerFactory);
            _model = faces;
            Use(faces, "blazeface", $"BlazeFace on {factory.ActiveProvider}", Overlay.Show);
        }

        _logger.LogInformation("{Model} ready.", _modelLabel);
    }

    // PresentedResults hands each result on when the view presents a frame at or after the one it
    // came from, and the boxes stay until the next, so on a moving object they trail by about one
    // inference.
    private void Use<T>(IImageModel<T> model, string id, string label, Action<InferenceResult<T>> show)
    {
        var onScreen = new PresentedResults<T>(_videoSink);
        onScreen.Presented += (_, result) => show(result);
        onScreen.Cleared += (_, _) => Overlay.Clear();
        _onScreen = onScreen;
        _runModel = chain => chain.Infer(id, model, onScreen.Post);
        _modelLabel = label;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _starting = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await LoadModelAsync();
        }
        catch (Exception ex)
        {
            if (_closing)
                return;
            _logger.LogError(ex, "The model failed to load.");
            StatusText.Text = $"The model failed to load: {ex.Message}";
            return;
        }

        await RefreshCamerasAsync();
    }

    // Nothing starts once the window is closing: it would outlive the model and the log.
    private async Task RefreshCamerasAsync()
    {
        if (_closing)
            return;
        try
        {
            var cameras = await CameraDevice.EnumerateAsync(_closingCts.Token);
            if (_closing)
                return;
            DevicePicker.ItemsSource = cameras;
            if (cameras.Count == 0)
                StatusText.Text = "No cameras found.";
            else
                DevicePicker.SelectedIndex = Math.Clamp(CameraIndex, 0, cameras.Count - 1);
        }
        catch (Exception ex)
        {
            if (_closing)
                return;
            _logger.LogError(ex, "Camera enumeration failed.");
            StatusText.Text = $"Camera enumeration failed: {ex.Message}";
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshCamerasAsync();

    private void OnCameraSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_closing || DevicePicker.SelectedItem is not DeviceInfo camera || _runModel is null)
            return;

        // Each capture starts once the one before it has stopped, so two never run at once.
        Overlay.Clear();
        _captureCts?.Cancel();
        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;
        var previous = _capture;
        _capture = Task.Run(async () =>
        {
            await previous;
            await CaptureAsync(camera, ct);
        });
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closed)
            return;

        // The graph uses the model and the view's sink, so it stops before they go.
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;

        _closingCts.Cancel();
        _captureCts?.Cancel();
        await _starting;
        await _capture;
        _onScreen?.Dispose();
        _model?.Dispose();

        _closed = true;
        Dispatcher.UIThread.Post(Close);
    }

    // After the view has released its sink, which logs as it goes.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _loggerFactory.Dispose();
    }
}
