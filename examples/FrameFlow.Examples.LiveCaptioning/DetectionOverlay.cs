using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FrameFlow.Yolo;

namespace FrameFlow.Examples.LiveCaptioning;

/// <summary>
/// Renders YOLOv8 bounding boxes on top of the video surface. The control
/// shares its <see cref="Control.Bounds"/> with the
/// <see cref="FrameFlow.Avalonia.FrameFlowVideoView"/> (both stretched in
/// the same Grid cell), and reapplies the same letterbox math the video
/// view uses so boxes line up with the rendered frame regardless of
/// aspect-ratio.
/// </summary>
/// <remarks>
/// The control is decoupled from the pipeline: the playback pump posts
/// detection state via <see cref="Update"/> on the UI thread, and the
/// control's render tick repaints at display rate. No per-frame
/// dispatcher hop for the box-painting itself.
/// </remarks>
public sealed class DetectionOverlay : Control
{
    private static readonly Color[] ClassColors =
    [
        Color.FromRgb(0xFF, 0x6B, 0x6B),
        Color.FromRgb(0x4E, 0xCD, 0xC4),
        Color.FromRgb(0xFF, 0xE6, 0x6D),
        Color.FromRgb(0x95, 0xE1, 0xD3),
        Color.FromRgb(0xFA, 0xC8, 0x8F),
        Color.FromRgb(0xC5, 0x9F, 0xFC),
        Color.FromRgb(0x73, 0xC6, 0xEF),
        Color.FromRgb(0xB6, 0xE3, 0x88),
    ];

    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(16);

    private IReadOnlyList<Detection> _detections = [];
    private int _sourceWidth;
    private int _sourceHeight;
    private DispatcherTimer? _renderTimer;

    public DetectionOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>
    /// Sets the detection state to render on the next paint. Safe to call
    /// from the UI thread only — the control assumes single-threaded
    /// mutation.
    /// </summary>
    public void Update(IReadOnlyList<Detection> detections, int sourceWidth, int sourceHeight)
    {
        _detections = detections;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderTimer = new DispatcherTimer(
            RenderInterval,
            DispatcherPriority.Render,
            (_, _) => InvalidateVisual()
        );
        _renderTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _renderTimer?.Stop();
        _renderTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_sourceWidth <= 0 || _sourceHeight <= 0 || _detections.Count == 0)
            return;

        var dest = ComputeLetterbox(_sourceWidth, _sourceHeight, Bounds.Width, Bounds.Height);
        if (dest.Width <= 0 || dest.Height <= 0)
            return;

        var sx = dest.Width / _sourceWidth;
        var sy = dest.Height / _sourceHeight;

        foreach (var det in _detections)
        {
            var color = ClassColors[det.ClassId % ClassColors.Length];
            var boxBrush = new SolidColorBrush(color);
            var pen = new Pen(boxBrush, thickness: 2);

            var rect = new Rect(
                dest.X + (det.X * sx),
                dest.Y + (det.Y * sy),
                det.Width * sx,
                det.Height * sy
            );
            context.DrawRectangle(null, pen, rect);

            var label = $"{det.ClassName} {det.Confidence:F2}";
            var formatted = new FormattedText(
                label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                12,
                Brushes.Black
            );
            var labelBg = new Rect(
                rect.X,
                Math.Max(dest.Y, rect.Y - formatted.Height - 4),
                formatted.Width + 8,
                formatted.Height + 4
            );
            context.FillRectangle(boxBrush, labelBg);
            context.DrawText(formatted, new Point(labelBg.X + 4, labelBg.Y + 2));
        }
    }

    private static Rect ComputeLetterbox(double srcW, double srcH, double dstW, double dstH)
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
            return default;

        var scale = Math.Min(dstW / srcW, dstH / srcH);
        var w = srcW * scale;
        var h = srcH * scale;
        var x = (dstW - w) / 2;
        var y = (dstH - h) / 2;
        return new Rect(x, y, w, h);
    }
}
