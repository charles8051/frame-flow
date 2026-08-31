using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Yolo;
using FrameFlow.Inference.Cuda;

namespace FrameFlow.Examples.Multicast;

/// <summary>
/// Pane 2 — YOLOv8 object detection. Receives <see cref="IVideoFrame"/>
/// instances from a Crossbar broadcast branch, runs inference via
/// <see cref="Yolov8Detector"/>, and overlays labelled bounding boxes
/// during the render pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Host-owned detector.</b> The pane does not construct the
/// <see cref="Yolov8Detector"/> itself — the host (<c>MainWindow</c>)
/// builds it once at startup and injects it via <see cref="SetDetector"/>.
/// </para>
/// <para>
/// <b>Queue-of-one worker.</b> While inference is in flight, incoming
/// frames go into a single-slot latch (<see cref="_nextFrame"/>) via
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/>. A newer frame
/// displaces an older latched frame (the displaced frame is disposed
/// and counted as a drop). When the worker finishes the current
/// detection, it drains the latch and continues processing — so the
/// upstream pacing-jitter pattern (bursts of two frames within ~14 ms
/// at 25 fps source rate) no longer drops the second-of-burst.
/// Mirrors <c>FrameFlow.Avalonia.AvaloniaVideoSink.PresentAsync</c>'s
/// established latest-wins pattern. See the crossbar exploration
/// per-branch-edge-policies-for-broadcast-fanout.md for the design
/// rationale and what an EdgeOptions.LatestWins(1)-based version would
/// require.
/// </para>
/// <para>
/// <b>Display path.</b> Worker does inference only — no bitmap encode,
/// no off-UI-thread WriteableBitmap construction. The render pass
/// pulls the pending payload, blits pixels into a double-buffered
/// <see cref="WriteableBitmap"/> via <see cref="WriteableBitmap.Lock"/>,
/// and draws. Mirrors <c>FrameFlow.Avalonia.FrameFlowVideoView</c>'s
/// pattern: WriteableBitmap is touched only on the UI thread.
/// </para>
/// </remarks>
public sealed class ObjectDetectionPreview : Control, IAsyncDisposable
{
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(16);

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

    private sealed record PendingDetection(IVideoFrame Frame, IReadOnlyList<Detection> Detections);

    private PendingDetection? _pending;
    // Single-slot latch for the next frame to process while a worker
    // is in flight. Writers are PresentAsync calls; reader is the
    // worker loop. Race-safe via Interlocked.Exchange — newer frames
    // displace older latched frames (latest-wins).
    private IVideoFrame? _nextFrame;

    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private int _bitmapWidth;
    private int _bitmapHeight;

    private IReadOnlyList<Detection> _frontDetections = [];

    private DispatcherTimer? _renderTimer;
    private long _droppedWhileBusyCount;
    private long _renderedFrameCount;
    // 0 = no worker running; 1 = worker in flight. PresentAsync
    // CAS-acquires this; the worker clears it on clean exit.
    private int _detectionInProgress;
    private string _statusText = "Waiting for detector…";

    private Yolov8Detector? _detector;

    private double _tInferMs;
    private double _tTotalMs;

    public long DroppedWhileBusyCount => Interlocked.Read(ref _droppedWhileBusyCount);
    public long RenderedFrameCount => Interlocked.Read(ref _renderedFrameCount);
    public string StatusText => Volatile.Read(ref _statusText);

    public string TimingBreakdown =>
        $"total {Volatile.Read(ref _tTotalMs):F1}  infer {Volatile.Read(ref _tInferMs):F1}";

    public ObjectDetectionPreview()
    {
        ClipToBounds = true;
    }

    public void SetDetector(Yolov8Detector detector)
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

        // Acquire the worker slot. If we win, drain any stale latched
        // frame (a frame that raced in between a prior worker's last
        // peek and its busy-clear), then fire a fresh worker. If we
        // lose, latch this frame for the in-flight worker to pick up —
        // displacing any older latched frame.
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
                    _frontDetections = newest.Detections;
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

        var detections = _frontDetections;
        if (detections.Count == 0 || srcW <= 0 || srcH <= 0)
            return;

        var sx = dest.Width / srcW;
        var sy = dest.Height / srcH;

        foreach (var det in detections)
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
                        // Detector went away mid-flight (SetUnavailable
                        // race). Dispose this frame and exit the worker —
                        // PresentAsync silently drops subsequent frames
                        // while _detector stays null.
                        frame.Dispose();
                        frame = null!;
                        break;
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var detections = detector.Detect(frame);
                    sw.Stop();
                    Volatile.Write(ref _tInferMs, sw.Elapsed.TotalMilliseconds);

                    var payload = new PendingDetection(frame, detections);
                    var stale = Interlocked.Exchange(ref _pending, payload);
                    publishedOwnership = true;
                    stale?.Frame.Dispose();

                    Volatile.Write(
                        ref _statusText,
                        detections.Count == 0
                            ? "No objects detected"
                            : $"Objects: {detections.Count}"
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

                // Drain the single-slot latch for the next iteration.
                // If empty, the loop exits and we clear the busy flag
                // in the outer finally.
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
