using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FrameFlow.Inference;
using FrameFlow.Yolo;

namespace FrameFlow.Examples.LiveCaptioning;

/// <summary>
/// Draws YOLOv8 boxes over the video. It shares the video view's grid cell and fits the frame
/// into its bounds with the aspect ratio kept, as the view does for square-pixel, upright video.
/// </summary>
public sealed class DetectionOverlay : Control
{
    private static readonly IBrush[] ClassBrushes =
    [
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
        new ImmutableSolidColorBrush(Color.FromRgb(0x4E, 0xCD, 0xC4)),
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xE6, 0x6D)),
        new ImmutableSolidColorBrush(Color.FromRgb(0x95, 0xE1, 0xD3)),
        new ImmutableSolidColorBrush(Color.FromRgb(0xFA, 0xC8, 0x8F)),
        new ImmutableSolidColorBrush(Color.FromRgb(0xC5, 0x9F, 0xFC)),
        new ImmutableSolidColorBrush(Color.FromRgb(0x73, 0xC6, 0xEF)),
        new ImmutableSolidColorBrush(Color.FromRgb(0xB6, 0xE3, 0x88)),
    ];

    private InferenceResult<IReadOnlyList<Detection>>? _result;

    public DetectionOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Draws the boxes in <paramref name="result"/>, or none when it is null. UI thread only.</summary>
    public void Show(InferenceResult<IReadOnlyList<Detection>>? result)
    {
        _result = result;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_result is not { Width: > 0, Height: > 0 } result)
            return;

        var scale = Math.Min(Bounds.Width / result.Width, Bounds.Height / result.Height);
        var left = (Bounds.Width - (result.Width * scale)) / 2;
        var top = (Bounds.Height - (result.Height * scale)) / 2;

        foreach (var det in result.Result)
        {
            var brush = ClassBrushes[det.ClassId % ClassBrushes.Length];
            var box = new Rect(
                left + (det.X * scale),
                top + (det.Y * scale),
                det.Width * scale,
                det.Height * scale
            );
            context.DrawRectangle(null, new Pen(brush, thickness: 2), box);

            var label = new FormattedText(
                $"{det.ClassName} {det.Confidence:F2}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                12,
                Brushes.Black
            );
            var labelBox = new Rect(
                box.X,
                Math.Max(top, box.Y - label.Height - 4),
                label.Width + 8,
                label.Height + 4
            );
            context.FillRectangle(brush, labelBox);
            context.DrawText(label, new Point(labelBox.X + 4, labelBox.Y + 2));
        }
    }
}
