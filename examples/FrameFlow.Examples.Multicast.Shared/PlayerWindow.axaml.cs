using Avalonia.Controls;
using Avalonia.Interactivity;
using FrameFlow.Audio.OpenAL;
using FrameFlow.Examples.Common;
using FrameFlow.Inference;
using FrameFlow.Media;
using FrameFlow.Player;
using FrameFlow.Yolo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Examples.Multicast.Shared;

/// <summary>
/// One player, one decode, and every frame shown in three panes: as decoded, under YOLOv8's boxes,
/// and through a color filter.
/// </summary>
public partial class PlayerWindow : Window
{
    private readonly CancellationTokenSource _closing = new();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private ILogger _logger = NullLogger.Instance;
    private Task _startup = Task.CompletedTask;
    private Yolov8Detector? _detector;
    private OpenAlAudioSink? _audio;
    private IMediaPlayer? _player;
    private bool _closed;

    public PlayerWindow()
    {
        InitializeComponent();
        Chrome.FileOpenRequested += async (_, e) => await OpenAsync(e.FilePath);
    }

    private async Task StartAsync()
    {
        _loggerFactory = ExampleLogging.CreateFactory(LogFileName, onFailure: ex => Title += $"  [no log file: {ex.Message}]");
        _logger = _loggerFactory.CreateLogger<PlayerWindow>();
        Panes.Logger = _loggerFactory.CreateLogger<MulticastPanes>();

        try
        {
            // YOLOv8 on the GPU, or on the CPU when the GPU provider cannot open the model.
            var inference = InferenceSessionFactoryBuilder.Create(
                GpuProvider,
                new Dictionary<ExecutionProvider, Func<string, IInferenceSession>>
                {
                    [GpuProvider] = OpenOnGpu,
                    [ExecutionProvider.Cpu] = path => new CpuInferenceSession(path),
                },
                loggerFactory: _loggerFactory
            );
            var detector = await Panes.LoadDetectorAsync(inference, _loggerFactory, _closing.Token);
            _detector = detector;
            _audio = new OpenAlAudioSink(_loggerFactory.CreateLogger<OpenAlAudioSink>());

            // One player for the life of the window: Open replaces what it plays.
            _player = await FrameFlowPlayer.Create()
                .WithAudioSink(_audio)
                .WithVideoSink(Panes.Sink)
                .ConfigureVideo(chain => detector is null ? chain : chain.Infer("yolo", detector, Panes.Detections.Post))
                .WithLogger(_loggerFactory)
                .BuildPlayerAsync(_closing.Token);
            Chrome.Transport = _player;

            if (StartupFile is not null)
                await OpenAsync(StartupFile);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The player could not be built.");
            Title = $"FrameFlow multicast: {ex.Message}";
        }
    }

    private async Task OpenAsync(string path)
    {
        if (_player is not { } player)
            return;

        var replaced = await player.ReplaceAsync([MediaSource.FromFile(path)]);
        if (!replaced.IsSuccess)
        {
            _logger.LogError("Could not open {File}: {Message}", path, replaced.Error.Message);
            Title = $"FrameFlow multicast: could not open {Path.GetFileName(path)}";
            return;
        }

        Title = $"FrameFlow multicast: {Path.GetFileName(path)}";
        if (player.State == PlaybackState.Playing)
            return;

        var played = await player.PlayAsync();
        if (!played.IsSuccess)
        {
            _logger.LogError(played.Error.Inner, "Play refused: {Category}: {Message}", played.Error.Category, played.Error.Message);
            Title = $"FrameFlow multicast: could not play {Path.GetFileName(path)}";
        }
    }

    /// <summary>The GPU execution provider YOLOv8 tries before the CPU.</summary>
    public required ExecutionProvider GpuProvider { get; init; }

    /// <summary>Opens a model on <see cref="GpuProvider"/>.</summary>
    public required Func<string, IInferenceSession> OpenOnGpu { get; init; }

    /// <summary>The file to play on start, if any.</summary>
    public string? StartupFile { get; init; }

    /// <summary>The log's file name, under the repository's <c>logs/</c> directory.</summary>
    public required string LogFileName { get; init; }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _startup = StartAsync();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closed)
            return;

        // Keep the window open until the player has stopped, then close it again.
        e.Cancel = true;
        if (_closing.IsCancellationRequested)
            return;
        _closing.Cancel();

        try
        {
            await _startup;
            Chrome.Transport = null;
            if (_player is not null)
                await _player.DisposeAsync();
            // Both sinks are ours, and the detector ran inside the player: all three go after it.
            await Panes.Sink.DisposeAsync();
            if (_audio is not null)
                await _audio.DisposeAsync();
            _detector?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Teardown failed.");
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
