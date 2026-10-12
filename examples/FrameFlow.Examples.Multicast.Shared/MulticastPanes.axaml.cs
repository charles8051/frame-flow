using Avalonia;
using Avalonia.Controls;
using FrameFlow.Avalonia;
using FrameFlow.Examples.Common;
using FrameFlow.Inference;
using FrameFlow.Media;
using FrameFlow.Yolo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Examples.Multicast.Shared;

/// <summary>
/// Three views of one frame: as it arrives, under YOLOv8's boxes, and through a color filter.
/// </summary>
public partial class MulticastPanes : UserControl
{
    private readonly AvaloniaVideoSink _direct;
    private readonly AvaloniaVideoSink _detection;
    private readonly AvaloniaVideoSink _filtered;
    private int _filter;
    private int _loggedCount = -1;
    private long _nextLogAt;

    public MulticastPanes()
    {
        InitializeComponent();

        _direct = DirectView.EnsureSink();
        _detection = DetectionView.EnsureSink();
        _filtered = FilteredView.EnsureSink();

        // Each pane keeps up to two of the frames it is given.
        Sink = new DelegatingVideoSink(PresentAsync, maxHeldFrames: 6);

        Detections = new PresentedResults<IReadOnlyList<Detection>>(_detection);
        Detections.Presented += (_, shown) =>
        {
            Overlay.Show(shown);
            long now = Environment.TickCount64;
            if (shown.Result.Count != _loggedCount && now >= _nextLogAt)
            {
                _loggedCount = shown.Result.Count;
                _nextLogAt = now + 1000;
                Logger.LogDebug("The second pane shows {Count} object(s).", _loggedCount);
            }
        };
        Detections.Cleared += (_, _) => Overlay.Show(null);

        FilterPicker.ItemsSource = Enum.GetValues<ColorFilter>();
        FilterPicker.SelectedIndex = 0;
        FilterPicker.SelectionChanged += (_, _) =>
        {
            if (FilterPicker.SelectedItem is ColorFilter filter)
                Volatile.Write(ref _filter, (int)filter);
        };
    }

    /// <summary>
    /// Presents each frame it is given in all three panes. Give it to a player, or end a graph at it,
    /// and dispose it after either has stopped. It takes BGRA frames in CPU memory.
    /// </summary>
    public IVideoSink Sink { get; }

    /// <summary>
    /// Holds YOLOv8 results until their frame is on screen in the second pane, then draws them.
    /// Hand <see cref="PresentedResults{TResult}.Post"/> to <c>Infer</c>.
    /// </summary>
    public PresentedResults<IReadOnlyList<Detection>> Detections { get; }

    /// <summary>Where the panes log how many objects the second pane shows, at most once a second.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Loads YOLOv8 through <paramref name="inference"/> and says in the second pane's title how it
    /// went. Null when the model cannot load: the other panes run on without it. The caller owns
    /// the detector.
    /// </summary>
    public async Task<Yolov8Detector?> LoadDetectorAsync(
        IInferenceSessionFactory inference,
        ILoggerFactory loggerFactory,
        CancellationToken ct
    )
    {
        SetDetectionStatus("loading YOLOv8");
        try
        {
            var detector = await Yolov8Detector.CreateAsync(inference, ct: ct, loggerFactory: loggerFactory);
            SetDetectionStatus(inference.ActiveProvider is { } provider ? $"YOLOv8 on {provider}" : "YOLOv8");
            return detector;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A download that times out is a failure here too, not a cancellation.
            Logger.LogWarning(ex, "YOLOv8 did not load; the second pane shows the picture without boxes.");
            SetDetectionStatus("YOLOv8 unavailable");
            return null;
        }
    }

    private void SetDetectionStatus(string status) => DetectionTitle.Text = $"Object detection: {status}";

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Detections.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        // Each pane takes its own reference to the frame it shows.
        using (frame)
        {
            await _direct.PresentAsync(frame.AddRef(), ct);
            await _detection.PresentAsync(frame.AddRef(), ct);

            using var filtered = ColorFilters.Apply(frame, (ColorFilter)Volatile.Read(ref _filter));
            await _filtered.PresentAsync(filtered.AddRef(), ct);
        }
    }
}
