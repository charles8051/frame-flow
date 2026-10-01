using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;

namespace VulkanPresenterProbe;

/// <summary>Hosts a compositor surface that shows the converter's ring images.</summary>
internal sealed class VulkanVideoView : Control
{
    private readonly Action<string> _log;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private ICompositionGpuInterop? _interop;

    public VulkanVideoView(Action<string> log) => _log = log;

    public TaskCompletionSource<ICompositionGpuInterop?> Ready { get; } = new();

    public long Shown, Completed, Faulted;

    protected override async void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        var compositor = ElementComposition.GetElementVisual(this)!.Compositor;
        _interop = await compositor.TryGetCompositionGpuInterop();
        if (_interop is null)
        {
            _log("ICompositionGpuInterop: not available on this backend");
            Ready.SetResult(null);
            return;
        }

        _log($"image handle types: [{string.Join(", ", _interop.SupportedImageHandleTypes)}]");
        _log($"semaphore types: [{string.Join(", ", _interop.SupportedSemaphoreTypes)}]");
        foreach (var type in _interop.SupportedImageHandleTypes)
            _log($"sync for {type}: {_interop.GetSynchronizationCapabilities(type)}");
        _log($"compositor device uuid: {(_interop.DeviceUuid is { } u ? Convert.ToHexString(u) : "none")}");

        _surface = compositor.CreateDrawingSurface();
        _visual = compositor.CreateSurfaceVisual();
        _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        _visual.Surface = _surface;
        ElementComposition.SetElementChildVisual(this, _visual);
        Ready.SetResult(_interop);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual is not null)
            _visual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
    }

    /// <summary>UI thread: imports the slot once, then asks the compositor to show it.</summary>
    public void Show(VulkanConverter converter, RingSlot slot)
    {
        var interop = _interop!;
        var image = (ICompositionImportedGpuImage)(slot.ImportedImage ??= interop.ImportImage(
            new PlatformHandle(converter.ExportMemoryFd(slot), KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor),
            new PlatformGraphicsExternalImageProperties
            {
                Width = converter.Width,
                Height = converter.Height,
                Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                MemorySize = slot.MemorySize,
                // Vulkan images are top-left origin; the default is GL's bottom-left.
                TopLeftOrigin = true,
            }));
        var renderDone = (ICompositionImportedGpuSemaphore)(slot.ImportedRenderDone ??= interop.ImportSemaphore(
            new PlatformHandle(converter.ExportSemaphoreFd(slot.RenderDone), KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor)));
        var compositorDone = (ICompositionImportedGpuSemaphore)(slot.ImportedCompositorDone ??= interop.ImportSemaphore(
            new PlatformHandle(converter.ExportSemaphoreFd(slot.CompositorDone), KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor)));

        var present = _surface!.UpdateWithSemaphoresAsync(image, renderDone, compositorDone);
        slot.LastPresent = present;
        slot.CompositorSignalPending = true;
        Volatile.Write(ref slot.State, 2);
        Interlocked.Increment(ref Shown);
        present.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                if (Interlocked.Increment(ref Faulted) <= 3)
                    _log($"present faulted: {t.Exception!.GetBaseException().Message}");
            }
            else
            {
                Interlocked.Increment(ref Completed);
            }
        }, TaskScheduler.Default);
    }
}

/// <summary>Takes Vulkan frames, converts them on the GPU, and hands them to the view.</summary>
internal sealed class VulkanPresenterSink(VulkanVideoView view, Action<string> log) : IVideoSink
{
    private VulkanConverter? _converter;
    private int _next;
    private bool _loggedFirst;

    public long Submitted, Dropped, NotVulkan, WrongFormat;

    public FrameMemoryDomains AcceptedDomains => FrameMemoryDomains.Any;

    // The ring holds a frame per slot until the GPU has read it.
    public int? MaxHeldFrames => VulkanConverter.RingSize;

    public unsafe ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        if (frame is not GpuVideoFrame gpu || !FfmpegVulkan.TryGet(gpu, out var refs))
        {
            Interlocked.Increment(ref NotVulkan);
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        if (refs.SwFormat != (int)FFmpeg.AutoGen.Abstractions.AVPixelFormat.AV_PIX_FMT_NV12)
        {
            if (Interlocked.Increment(ref WrongFormat) == 1)
                log($"sw_format {(FFmpeg.AutoGen.Abstractions.AVPixelFormat)refs.SwFormat} is not NV12; this spike converts NV12 only");
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        if (_converter is null)
        {
            _converter = new VulkanConverter(refs, log);
            _converter.CreateRing(frame.Width, frame.Height);
        }

        if (!_loggedFirst)
        {
            _loggedFirst = true;
            var vkf = refs.Frame;
            log($"first frame: {frame.Width}x{frame.Height}, layout {(Silk.NET.Vulkan.ImageLayout)vkf->layout[0]}, "
                + $"bt709 {refs.Bt709}, full range {refs.FullRange}, access 0x{vkf->access[0]:x}, sem_value {vkf->sem_value[0]}, queue_family 0x{vkf->queue_family[0]:x}, "
                + $"img_flags 0x{refs.VulkanFrames->img_flags:x}, usage 0x{refs.VulkanFrames->usage:x}, tiling {refs.VulkanFrames->tiling}");
        }

        RingSlot? slot = null;
        for (int i = 0; i < VulkanConverter.RingSize; i++)
        {
            var candidate = _converter.Slots[(_next + i) % VulkanConverter.RingSize];
            if (_converter.IsReusable(candidate))
            {
                slot = candidate;
                _next = (_next + i + 1) % VulkanConverter.RingSize;
                break;
            }
        }

        if (slot is null)
        {
            Interlocked.Increment(ref Dropped);
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        // The slot keeps the frame until its fence signals.
        _converter.Convert(gpu, refs, slot);
        Interlocked.Increment(ref Submitted);
        var converter = _converter;
        Dispatcher.UIThread.Post(() => view.Show(converter, slot));
        return ValueTask.CompletedTask;
    }

    public IEnumerable<string> DescribeSlots()
    {
        if (_converter is null)
            yield break;
        for (int i = 0; i < VulkanConverter.RingSize; i++)
            yield return $"slot {i}: {_converter.Describe(_converter.Slots[i])}";
    }

    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

    public VideoSinkDiagnosticsSnapshot GetDiagnostics() => new(Submitted, Dropped, null, null);

    public ValueTask DisposeAsync()
    {
        _converter?.Dispose();
        _converter = null;
        return ValueTask.CompletedTask;
    }
}
