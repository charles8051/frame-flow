using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using FrameFlow.Inference;
using FrameFlow.Yolo;

namespace FrameFlow.Examples.Multicast.Shared;

/// <summary>Draws YOLOv8's boxes, laid over a video view showing the frame they were found in.</summary>
public sealed class DetectionOverlay : Control
{
    private static readonly IImmutableSolidColorBrush[] Palette =
    [
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
        new ImmutableSolidColorBrush(Color.FromRgb(0x4E, 0xCD, 0xC4)),
        new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xE6, 0x6D)),
        new ImmutableSolidColorBrush(Color.FromRgb(0xC5, 0x9F, 0xFC)),
        new ImmutableSolidColorBrush(Color.FromRgb(0x73, 0xC6, 0xEF)),
        new ImmutableSolidColorBrush(Color.FromRgb(0xB6, 0xE3, 0x88)),
    ];

    private InferenceResult<IReadOnlyList<Detection>>? _shown;

    public DetectionOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Draws <paramref name="result"/>, or nothing when it is null. Callable from any thread.</summary>
    public void Show(InferenceResult<IReadOnlyList<Detection>>? result)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(result));
            return;
        }

        _shown = result;
        InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (_shown is not { Width: > 0, Height: > 0 } shown)
            return;

        // The view letterboxes the frame into the same bounds. Square pixels are assumed: the
        // result does not say how an anamorphic or rotated frame was shown.
        double scale = Math.Min(Bounds.Width / shown.Width, Bounds.Height / shown.Height);
        double left = (Bounds.Width - shown.Width * scale) / 2;
        double top = (Bounds.Height - shown.Height * scale) / 2;

        foreach (var detection in shown.Result)
        {
            var brush = Palette[detection.ClassId % Palette.Length];
            var box = new Rect(
                left + detection.X * scale,
                top + detection.Y * scale,
                detection.Width * scale,
                detection.Height * scale
            );
            context.DrawRectangle(null, new ImmutablePen(brush, 2), box);

            var label = new FormattedText(
                $"{detection.ClassName} {detection.Confidence:F2}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                11,
                Brushes.Black
            );
            var labelBox = new Rect(box.X, Math.Max(top, box.Y - label.Height - 2), label.Width + 6, label.Height + 2);
            context.FillRectangle(brush, labelBox);
            context.DrawText(label, new Point(labelBox.X + 3, labelBox.Y + 1));
        }
    }
}
