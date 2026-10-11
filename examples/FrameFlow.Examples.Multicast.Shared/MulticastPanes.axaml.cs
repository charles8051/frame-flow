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

    public MulticastPanes()
    {
        InitializeComponent();

        _direct = DirectView.EnsureSink();
        _detection = DetectionView.EnsureSink();
        _filtered = FilteredView.EnsureSink();

        // The first two panes keep up to two of the frames they are given; the third keeps copies.
        Sink = new DelegatingVideoSink(PresentAsync, maxHeldFrames: 4);

        Detections = new PresentedResults<IReadOnlyList<Detection>>(_detection);
        Detections.Presented += (_, shown) =>
        {
            Overlay.Show(shown);
            Logger.LogDebug("Pane 2 shows {Count} object(s) at {Timestamp}.", shown.Result.Count, shown.Timestamp);
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
    /// Presents each frame it is given in all three panes. Give it to a player, or end a graph at it.
    /// It takes BGRA frames in CPU memory.
    /// </summary>
    public IVideoSink Sink { get; }

    /// <summary>
    /// Holds YOLOv8 results until their frame is on screen in the second pane, then draws them.
    /// Hand <see cref="PresentedResults{TResult}.Post"/> to <c>Infer</c>.
    /// </summary>
    public PresentedResults<IReadOnlyList<Detection>> Detections { get; }

    /// <summary>Where the panes log what the second pane shows.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Says in the second pane's title how the detector is doing.</summary>
    public void SetDetectionStatus(string status) => DetectionTitle.Text = $"Object detection: {status}";

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
