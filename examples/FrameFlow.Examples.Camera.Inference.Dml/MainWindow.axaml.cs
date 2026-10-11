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

        // CreateAsync opens the session, and compiles the model for the GPU, on the calling thread,
        // so it runs on the pool. PresentedResults hands each result on as the view presents a
        // frame at or after the one it came from, so the boxes change with the picture.
        if (FaceModelPath is null)
        {
            var yolo = await Task.Run(() => Yolov8Detector.CreateAsync(
                factory, ModelPath, _closingCts.Token, _loggerFactory));
            _model = yolo;
            var onScreen = new PresentedResults<IReadOnlyList<Detection>>(_videoSink);
            _onScreen = onScreen;
            onScreen.Presented += (_, result) => Overlay.Show(result);
            _runModel = chain => chain.Infer("yolo", yolo, onScreen.Post);
            _modelLabel = $"YOLOv8 on {factory.ActiveProvider}";
        }
        else
        {
            var faces = await Task.Run(() => BlazeFaceDetector.CreateAsync(factory, FaceModelPath, _loggerFactory));
            _model = faces;
            var onScreen = new PresentedResults<IReadOnlyList<FaceDetection>>(_videoSink);
            _onScreen = onScreen;
            onScreen.Presented += (_, result) => Overlay.Show(result);
            _runModel = chain => chain.Infer("blazeface", faces, onScreen.Post);
            _modelLabel = $"BlazeFace on {factory.ActiveProvider}";
        }

        _logger.LogInformation("{Model} ready.", _modelLabel);
    }

    // Camera, converted to BGRA, to the view, with the model on a branch that takes the newest frame.
    private FrameFlow.Graph.Graph BuildGraph(SourceNode<IVideoFrame> camera)
    {
        var graph = new FrameFlow.Graph.Graph();
        var bgra = graph.Pipeline(camera).Then(VideoOperators.ConvertPixelFormat("to-bgra", PixelFormat.Bgra32));
        _runModel!(bgra).To(_videoSink.AsSinkNode());
        return graph;
    }

    private async Task CaptureAsync(DeviceInfo camera, CancellationToken ct)
    {
        try
        {
            await using var session = await CameraSession.For(camera)
                .PreferPixelFormat(CameraPixelFormat.Bgra32)
                // The formats FrameFlow.Camera turns into frames.
                .AllowOnlyPixelFormats(
                    CameraPixelFormat.Bgra32, CameraPixelFormat.Rgba32, CameraPixelFormat.Nv12,
                    CameraPixelFormat.Yuy2, CameraPixelFormat.Uyvy)
                .MaxResolution(1280, 720)
                .WithSessionOptions(o => o with { BufferCount = CameraBufferCount() ?? o.BufferCount })
                .WithLogger(_loggerFactory.CreateLogger<CameraSession>())
                .OpenAsync(ct);
            await using var source = session.AsPushVideoFrameSource(ct, _loggerFactory);

            Dispatcher.UIThread.Post(() => StatusText.Text = $"{camera.Name}: {_modelLabel}");
            await BuildGraph(source.Source).RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture from {Camera} failed.", camera.Name);
            Dispatcher.UIThread.Post(() => StatusText.Text = $"Capture from {camera.Name} failed: {ex.Message}");
        }
    }

    // As many buffers as the graph holds of the camera's frames, and one for the frame the push
    // source keeps ahead of it, lets the session hand every frame over without a copy.
    private int? CameraBufferCount()
    {
        var standIn = new SourceNode<IVideoFrame>("camera-source", static _ => default);
        return BuildGraph(standIn).FrameBudgetFor(standIn.Output).Frames + 1;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        try
        {
            await LoadModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The model failed to load.");
            StatusText.Text = $"The model failed to load: {ex.Message}";
            return;
        }

        await RefreshCamerasAsync();
    }

    private async Task RefreshCamerasAsync()
    {
        try
        {
            var cameras = await CameraDevice.EnumerateAsync(_closingCts.Token);
            DevicePicker.ItemsSource = cameras;
            if (cameras.Count == 0)
                StatusText.Text = "No cameras found.";
            else
                DevicePicker.SelectedIndex = Math.Clamp(CameraIndex, 0, cameras.Count - 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera enumeration failed.");
            StatusText.Text = $"Camera enumeration failed: {ex.Message}";
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshCamerasAsync();

    private void OnCameraSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not DeviceInfo camera || _runModel is null)
            return;

        // Each capture starts once the one before it has stopped, so two never run at once.
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
        await _capture;
        _onScreen?.Dispose();
        _model?.Dispose();

        _closed = true;
        Close();
    }

    // After the view has released its sink, which logs as it goes.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _loggerFactory.Dispose();
    }
}
