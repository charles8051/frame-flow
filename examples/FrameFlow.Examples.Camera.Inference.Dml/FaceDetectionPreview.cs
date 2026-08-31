using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlow.Face;
using FrameFlow.Media;

namespace FrameFlow.Examples.Camera.Inference.Dml;

/// <summary>
/// BlazeFace preview pane — the face analogue of
/// <see cref="ObjectDetectionPreview"/>. Runs <see cref="BlazeFaceDetector"/>
/// whole-frame and overlays each face's box plus its six
/// <see cref="FaceKeypoint"/> landmarks, so the raw model output is visible
/// on the live camera image.
/// </summary>
/// <remarks>
/// Structure (worker/latch/double-buffer/render-timer) is copied verbatim
/// from <see cref="ObjectDetectionPreview"/>; only the payload type
/// (<see cref="FaceDetection"/>) and the overlay drawing differ. See that
/// type's remarks for the queue-of-one and display-path rationale.
/// </remarks>
public sealed class FaceDetectionPreview : Control, IInferencePreview
{
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(16);

    // One distinct colour per keypoint, indexed by FaceKeypoint, so the
    // six landmarks are individually legible.
    private static readonly Color[] KeypointColors =
    [
        Color.FromRgb(0xFF, 0x6B, 0x6B), // RightEye
        Color.FromRgb(0x4E, 0xCD, 0xC4), // LeftEye
        Color.FromRgb(0xFF, 0xE6, 0x6D), // Nose
        Color.FromRgb(0xC5, 0x9F, 0xFC), // Mouth
        Color.FromRgb(0x73, 0xC6, 0xEF), // RightEarTragion
        Color.FromRgb(0xB6, 0xE3, 0x88), // LeftEarTragion
    ];

    private static readonly Color BoxColor = Color.FromRgb(0x4E, 0xCD, 0xC4);

    private sealed record PendingFaces(IVideoFrame Frame, IReadOnlyList<FaceDetection> Faces);

    private PendingFaces? _pending;
    private IVideoFrame? _nextFrame;

    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private int _bitmapWidth;
    private int _bitmapHeight;

    private IReadOnlyList<FaceDetection> _frontFaces = [];

    private DispatcherTimer? _renderTimer;
    private long _droppedWhileBusyCount;
    private long _renderedFrameCount;
    private int _detectionInProgress;
    private string _statusText = "Waiting for detector…";

    private BlazeFaceDetector? _detector;

    private double _tInferMs;
    private double _tTotalMs;

    public long DroppedWhileBusyCount => Interlocked.Read(ref _droppedWhileBusyCount);
    public long RenderedFrameCount => Interlocked.Read(ref _renderedFrameCount);
    public string StatusText => Volatile.Read(ref _statusText);

    public string TimingBreakdown =>
        $"total {Volatile.Read(ref _tTotalMs):F1}  infer {Volatile.Read(ref _tInferMs):F1}";

    public FaceDetectionPreview()
    {
        ClipToBounds = true;
    }

    public void SetDetector(BlazeFaceDetector detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        Volatile.Write(ref _statusText, "Detector ready");
    }

    public void SetUnavailable(string reason)
    {
        _detector = null;
        Volatile.Write(ref _statusText, $"Unavailable — {reason}");
    }

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_detector is null)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        if (Interlocked.CompareExchange(ref _detectionInProgress, 1, 0) == 0)
        {
            var stale = Interlocked.Exchange(ref _nextFrame, null);
            if (stale is not null)
            {
                Interlocked.Increment(ref _droppedWhileBusyCount);
                stale.Dispose();
            }
            _ = Task.Run(() => RunDetectionAsync(frame), CancellationToken.None);
            return ValueTask.CompletedTask;
        }

        var displaced = Interlocked.Exchange(ref _nextFrame, frame);
        if (displaced is not null)
        {
            Interlocked.Increment(ref _droppedWhileBusyCount);
            displaced.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        var stalePending = Interlocked.Exchange(ref _pending, null);
        stalePending?.Frame.Dispose();
        var staleNext = Interlocked.Exchange(ref _nextFrame, null);
        staleNext?.Dispose();
        _front?.Dispose();
        _front = null;
        _back?.Dispose();
        _back = null;
        return ValueTask.CompletedTask;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var newest = Interlocked.Exchange(ref _pending, null);
        if (newest is not null)
        {
            try
            {
                var cpu = newest.Frame.AsCpu();
                if (cpu is null)
                {
                    Volatile.Write(ref _statusText, "Frame has no CPU view");
                }
                else
                {
                    var data = cpu.Value;
                    EnsureBackBuffer(data.Width, data.Height);
                    if (_back is not null)
                    {
                        using var fb = _back.Lock();
                        CopyPixels(data.PlaneY.Span, data.StrideY, fb);
                    }
                    (_front, _back) = (_back, _front);
                    _frontFaces = newest.Faces;
                    Interlocked.Increment(ref _renderedFrameCount);
                }
            }
            finally
            {
                newest.Frame.Dispose();
            }
        }

        var front = _front;
        if (front is null)
            return;

        var srcW = front.PixelSize.Width;
        var srcH = front.PixelSize.Height;
        var bounds = Bounds;
        var dest = ComputeLetterbox(srcW, srcH, bounds.Width, bounds.Height);
        if (dest.Width <= 0 || dest.Height <= 0)
            return;

        var srcRect = new Rect(0, 0, srcW, srcH);
        context.DrawImage(front, srcRect, dest);

        var faces = _frontFaces;
        if (faces.Count == 0 || srcW <= 0 || srcH <= 0)
            return;

        var sx = dest.Width / srcW;
        var sy = dest.Height / srcH;

        var boxBrush = new SolidColorBrush(BoxColor);
        var boxPen = new Pen(boxBrush, thickness: 2);

        foreach (var face in faces)
        {
            var rect = new Rect(
                dest.X + (face.X * sx),
                dest.Y + (face.Y * sy),
                face.Width * sx,
                face.Height * sy
            );
            context.DrawRectangle(null, boxPen, rect);

            // Six landmark dots, each its own colour.
            for (int k = 0; k < face.Keypoints.Count; k++)
            {
                var kp = face.Keypoints[k];
                var center = new Point(dest.X + (kp.X * sx), dest.Y + (kp.Y * sy));
                var dotBrush = new SolidColorBrush(KeypointColors[k % KeypointColors.Length]);
                context.DrawEllipse(dotBrush, null, center, 3, 3);
            }

            var label = $"face {face.Confidence:F2}";
            var formatted = new FormattedText(
                label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                11,
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
        _ = DisposeAsync().AsTask();
        base.OnDetachedFromVisualTree(e);
    }

    private async Task RunDetectionAsync(IVideoFrame initialFrame)
    {
        var frame = initialFrame;
        try
        {
            while (frame is not null)
            {
                var swTotal = System.Diagnostics.Stopwatch.StartNew();
                var publishedOwnership = false;
                try
                {
                    var detector = _detector;
                    if (detector is null)
                    {
                        frame.Dispose();
                        frame = null!;
                        break;
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var faces = detector.Detect(frame);
                    sw.Stop();
                    Volatile.Write(ref _tInferMs, sw.Elapsed.TotalMilliseconds);

                    var payload = new PendingFaces(frame, faces);
                    var stale = Interlocked.Exchange(ref _pending, payload);
                    publishedOwnership = true;
                    stale?.Frame.Dispose();

                    Volatile.Write(
                        ref _statusText,
                        faces.Count == 0 ? "No faces detected" : $"Faces: {faces.Count}"
                    );
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _statusText, $"Inference error: {ex.GetType().Name}");
                }
                finally
                {
                    if (!publishedOwnership && frame is not null)
                        frame.Dispose();
                    swTotal.Stop();
                    Volatile.Write(ref _tTotalMs, swTotal.Elapsed.TotalMilliseconds);
                }

                frame = Interlocked.Exchange(ref _nextFrame, null);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _detectionInProgress, 0);
        }

        await Task.CompletedTask;
    }

    private void EnsureBackBuffer(int width, int height)
    {
        if (_bitmapWidth == width && _bitmapHeight == height && _back is not null)
            return;

        _back?.Dispose();
        _front?.Dispose();

        _back = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            global::Avalonia.Platform.PixelFormat.Bgra8888,
            AlphaFormat.Premul
        );
        _front = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            global::Avalonia.Platform.PixelFormat.Bgra8888,
            AlphaFormat.Premul
        );

        _bitmapWidth = width;
        _bitmapHeight = height;
    }

    private static unsafe void CopyPixels(
        ReadOnlySpan<byte> src,
        int srcStride,
        ILockedFramebuffer fb
    )
    {
        int height = fb.Size.Height;
        int dstStride = fb.RowBytes;
        int copyBytes = Math.Min(srcStride, dstStride);

        if (srcStride == dstStride && src.Length >= (long)dstStride * height)
        {
            fixed (byte* srcPtr = src)
            {
                Buffer.MemoryCopy(
                    srcPtr,
                    fb.Address.ToPointer(),
                    (long)dstStride * height,
                    (long)dstStride * height
                );
            }
        }
        else
        {
            fixed (byte* srcBase = src)
            {
                byte* dstBase = (byte*)fb.Address.ToPointer();
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        srcBase + (long)y * srcStride,
                        dstBase + (long)y * dstStride,
                        dstStride,
                        copyBytes
                    );
                }
            }
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
