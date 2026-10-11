using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FrameFlow.Face;
using FrameFlow.Inference;
using FrameFlow.Yolo;

namespace FrameFlow.Examples.Camera.Inference.Dml;

/// <summary>
/// Draws detections over the <c>FrameFlowVideoView</c> beneath it: a box and a label for each
/// object, and a box and its six landmarks for each face.
/// </summary>
public sealed class DetectionOverlay : Control
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x4E, 0xCD, 0xC4));
    private static readonly IPen BoxPen = new Pen(Accent, 2);

    private sealed record Shape(Rect Box, string Label, Point[] Points);

    private Shape[] _shapes = [];
    private Size _frame;

    public void Show(InferenceResult<IReadOnlyList<Detection>> result) =>
        Show(result.Width, result.Height, result.Result.Select(d =>
            new Shape(new Rect(d.X, d.Y, d.Width, d.Height), $"{d.ClassName} {d.Confidence:F2}", [])));

    public void Show(InferenceResult<IReadOnlyList<FaceDetection>> result) =>
        Show(result.Width, result.Height, result.Result.Select(f =>
            new Shape(
                new Rect(f.X, f.Y, f.Width, f.Height),
                $"face {f.Confidence:F2}",
                f.Keypoints.Select(k => new Point(k.X, k.Y)).ToArray())));

    // Called on the thread that presented the frame; drawn on the UI thread.
    private void Show(int width, int height, IEnumerable<Shape> shapes)
    {
        var frame = new Size(width, height);
        var drawn = shapes.ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            (_frame, _shapes) = (frame, drawn);
            InvalidateVisual();
        });
    }

    public override void Render(DrawingContext context)
    {
        if (_frame.Width <= 0 || _frame.Height <= 0)
            return;

        // The view fits the frame inside its bounds, centred; FrameFlow does not expose where it
        // drew, so this fits the same way to map frame pixels onto the picture.
        double scale = Math.Min(Bounds.Width / _frame.Width, Bounds.Height / _frame.Height);
        var origin = new Point(
            (Bounds.Width - (_frame.Width * scale)) / 2,
            (Bounds.Height - (_frame.Height * scale)) / 2);
        Point Map(Point p) => origin + (p * scale);

        foreach (var shape in _shapes)
        {
            var box = new Rect(Map(shape.Box.TopLeft), Map(shape.Box.BottomRight));
            context.DrawRectangle(null, BoxPen, box);
            foreach (var point in shape.Points)
                context.DrawEllipse(Accent, null, Map(point), 3, 3);

            var label = new FormattedText(
                shape.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, Brushes.Black);
            var labelBox = new Rect(
                box.X, Math.Max(origin.Y, box.Y - label.Height - 4), label.Width + 8, label.Height + 4);
            context.FillRectangle(Accent, labelBox);
            context.DrawText(label, labelBox.TopLeft + new Vector(4, 2));
        }
    }
}
