using FrameFlow.Media;

namespace FrameFlow.Examples.Camera.Inference.Dml;

/// <summary>
/// The surface <c>MainWindow</c> drives regardless of which model is
/// running — the YOLO object pane or the BlazeFace pane. Lets the camera
/// graph's sink and the stats tick stay model-agnostic; the concrete
/// <c>SetDetector</c> (which takes a model-specific detector) stays on
/// each pane.
/// </summary>
public interface IInferencePreview : IAsyncDisposable
{
    /// <summary>Hands a frame to the pane's inference worker (queue-of-one, latest-wins).</summary>
    ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct);

    /// <summary>Puts the pane into an "unavailable" state (model failed to load).</summary>
    void SetUnavailable(string reason);

    /// <summary>Frames rendered so far — the detect-fps numerator.</summary>
    long RenderedFrameCount { get; }

    /// <summary>Frames dropped because a worker was busy.</summary>
    long DroppedWhileBusyCount { get; }

    /// <summary>Human-readable pane status (last detection count / error).</summary>
    string StatusText { get; }

    /// <summary>Per-stage timing string for the status bar.</summary>
    string TimingBreakdown { get; }
}
