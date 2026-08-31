using System.Buffers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlow.Graph;
using FrameFlow.Media;

namespace FrameFlow.Examples.Multicast.Dml;

/// <summary>
/// Pane 3 — selectable color filter. Receives <see cref="IVideoFrame"/>
/// instances from a Crossbar broadcast branch, applies a per-pixel
/// transform (Identity / Grayscale / Sepia) into a host buffer on the
/// worker thread, and the render pass blits that buffer into a
/// double-buffered <see cref="WriteableBitmap"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Queue-of-one worker.</b> While the filter is in flight, incoming
/// frames go into a single-slot latch via
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/>. Newer frames
/// displace older latched frames; the worker drains the latch after
/// each completed filter pass. Same shape as
/// <c>ObjectDetectionPreview</c> and
/// <c>FrameFlow.Avalonia.AvaloniaVideoSink.PresentAsync</c> — see
/// crossbar's per-branch-edge-policies exploration for the design
/// rationale.
/// </para>
/// <para>
/// <b>Buffer lifecycle.</b> The worker rents a byte buffer from
/// <see cref="ArrayPool{Byte}.Shared"/>, fills it (copy + filter in one
/// pass), and publishes a <see cref="FilteredPayload"/> via
/// <see cref="Interlocked.Exchange"/>. The render pass takes the
/// payload, blits its pixels into the back <see cref="WriteableBitmap"/>,
/// swaps front/back, and returns the buffer to the pool.
/// </para>
/// </remarks>
public sealed class FilteredPreview : Control, IAsyncDisposable
{
    public enum FilterMode
    {
        Identity,
        Grayscale,
        Sepia,
    }

    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(16);

    public static readonly StyledProperty<FilterMode> FilterProperty = AvaloniaProperty.Register<
        FilteredPreview,
        FilterMode
    >(nameof(Filter), FilterMode.Identity);

    public FilterMode Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    private sealed record FilteredPayload(byte[] Pixels, int Width, int Height);

    public FilteredPreview()
    {
        ClipToBounds = true;
    }

    private FilteredPayload? _pending;
    // Single-slot latch — see ObjectDetectionPreview for the protocol.
    private IVideoFrame? _nextFrame;

    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private int _bitmapWidth;
    private int _bitmapHeight;

    private DispatcherTimer? _renderTimer;
    private long _droppedFrameCount;
    private long _renderedFrameCount;
    private int _activeFilter = (int)FilterMode.Identity;
    private string _statusText = "Idle";

    private int _filterInProgress;

    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);
    public long RenderedFrameCount => Interlocked.Read(ref _renderedFrameCount);
    public string StatusText => Volatile.Read(ref _statusText);

    static FilteredPreview()
    {
        FilterProperty.Changed.AddClassHandler<FilteredPreview>(
            (control, args) =>
            {
                if (args.NewValue is FilterMode mode)
                    Interlocked.Exchange(ref control._activeFilter, (int)mode);
            }
        );
    }

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (Interlocked.CompareExchange(ref _filterInProgress, 1, 0) == 0)
        {
            var stale = Interlocked.Exchange(ref _nextFrame, null);
            if (stale is not null)
            {
                Interlocked.Increment(ref _droppedFrameCount);
                stale.Dispose();
            }
            _ = Task.Run(() => RunFilter(frame), CancellationToken.None);
            return ValueTask.CompletedTask;
        }

        var displaced = Interlocked.Exchange(ref _nextFrame, frame);
        if (displaced is not null)
        {
            Interlocked.Increment(ref _droppedFrameCount);
            displaced.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private void RunFilter(IVideoFrame initialFrame)
    {
        var frame = initialFrame;
        try
        {
            while (frame is not null)
            {
                ProcessOneFrame(frame);
                frame = Interlocked.Exchange(ref _nextFrame, null);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _filterInProgress, 0);
        }
    }

    private void ProcessOneFrame(IVideoFrame frame)
    {
        byte[]? rented = null;
        try
        {
            var cpu = frame.AsCpu();
            if (cpu is null)
            {
                Volatile.Write(ref _statusText, "Frame has no CPU view");
                return;
            }

            var data = cpu.Value;
            var widthBytes = data.Width * 4;
            var totalBytes = checked(widthBytes * data.Height);

            rented = ArrayPool<byte>.Shared.Rent(totalBytes);
            var activeFilter = (FilterMode)Volatile.Read(ref _activeFilter);
            CopyAndFilter(
                src: data.PlaneY.Span,
                srcStride: data.StrideY,
                dst: rented,
                dstStride: widthBytes,
                width: data.Width,
                height: data.Height,
                filter: activeFilter
            );

            var payload = new FilteredPayload(rented, data.Width, data.Height);
            var stale = Interlocked.Exchange(ref _pending, payload);
            rented = null; // ownership transferred to _pending
            if (stale is not null)
                ArrayPool<byte>.Shared.Return(stale.Pixels);

            Volatile.Write(ref _statusText, $"Filter: {activeFilter}");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _statusText, $"Filter error: {ex.GetType().Name}");
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
            frame.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        var stalePending = Interlocked.Exchange(ref _pending, null);
        if (stalePending is not null)
            ArrayPool<byte>.Shared.Return(stalePending.Pixels);
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
                EnsureBackBuffer(newest.Width, newest.Height);
                if (_back is not null)
                {
                    using var fb = _back.Lock();
                    var srcStride = newest.Width * 4;
                    CopyBuffer(newest.Pixels, srcStride, newest.Height, fb);
                }
                (_front, _back) = (_back, _front);
                Interlocked.Increment(ref _renderedFrameCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(newest.Pixels);
            }
        }

        var front = _front;
        if (front is null)
            return;

        var srcW = front.PixelSize.Width;
        var srcH = front.PixelSize.Height;
        var dest = ComputeLetterbox(srcW, srcH, Bounds.Width, Bounds.Height);
        if (dest.Width <= 0 || dest.Height <= 0)
            return;

        var srcRect = new Rect(0, 0, srcW, srcH);
        context.DrawImage(front, srcRect, dest);
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

    private static unsafe void CopyAndFilter(
        ReadOnlySpan<byte> src,
        int srcStride,
        byte[] dst,
        int dstStride,
        int width,
        int height,
        FilterMode filter
    )
    {
        fixed (byte* srcBase = src)
        fixed (byte* dstBase = dst)
        {
            for (int y = 0; y < height; y++)
            {
                byte* srcRow = srcBase + (long)y * srcStride;
                byte* dstRow = dstBase + (long)y * dstStride;
                for (int x = 0; x < width; x++)
                {
                    byte b = srcRow[0];
                    byte g = srcRow[1];
                    byte r = srcRow[2];

                    switch (filter)
                    {
                        case FilterMode.Grayscale:
                        {
                            var gray = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                            dstRow[0] = dstRow[1] = dstRow[2] = gray;
                            break;
                        }
                        case FilterMode.Sepia:
                        {
                            var newR = (r * 393 + g * 769 + b * 189) / 1000;
                            var newG = (r * 349 + g * 686 + b * 168) / 1000;
                            var newB = (r * 272 + g * 534 + b * 131) / 1000;
                            dstRow[2] = (byte)(newR > 255 ? 255 : newR);
                            dstRow[1] = (byte)(newG > 255 ? 255 : newG);
                            dstRow[0] = (byte)(newB > 255 ? 255 : newB);
                            break;
                        }
                        default:
                        {
                            dstRow[0] = b;
                            dstRow[1] = g;
                            dstRow[2] = r;
                            break;
                        }
                    }
                    dstRow[3] = 255; // force opaque alpha
                    srcRow += 4;
                    dstRow += 4;
                }
            }
        }
    }

    private static unsafe void CopyBuffer(
        byte[] src,
        int srcStride,
        int height,
        ILockedFramebuffer fb
    )
    {
        int dstStride = fb.RowBytes;
        int copyBytes = Math.Min(srcStride, dstStride);

        fixed (byte* srcBase = src)
        {
            byte* dstBase = (byte*)fb.Address.ToPointer();
            if (srcStride == dstStride)
            {
                Buffer.MemoryCopy(
                    srcBase,
                    dstBase,
                    (long)dstStride * height,
                    (long)dstStride * height
                );
            }
            else
            {
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
