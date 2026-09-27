using System.Runtime.InteropServices;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A <see cref="HardwareDevice"/> that decoders borrow instead of creating their own (#428).
/// D3D12 devices are one per adapter, so two decoders' <c>ID3D12Device</c> pointers match whether
/// they share a device or not; what a borrowed device changes is FFmpeg's device context, and that
/// is what these tests compare.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class HardwareDeviceTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const string H264 = "test-video-h264-yuv420p.mp4";
    private const string Av1 = "test-video-av1-yuv420p.mkv";
    private static readonly Guid IidDevice = new("189819f1-1db6-4b57-be54-1821339b85f7");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, H264)]
    public async Task ADecoderGivenADevice_DecodesOnIt()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);

        await using var demux = await OpenAsync(H264);
        await using var decoder = Open(demux, device, HardwareDecodeMode.Required);
        using var frame = await FirstFrameAsync(demux, decoder);

        Assert.Equal(HardwareDecodeBackendKind.D3D12Va, decoder.HardwareBackend);
        Assert.Equal(device.ContextPointer, frame.HwDeviceContext);
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, H264)]
    public async Task WithoutADevice_EachDecoderMakesItsOwnContext()
    {
        await using var firstDemux = await OpenAsync(H264);
        await using var first = Open(firstDemux, device: null, HardwareDecodeMode.Required);
        using var firstFrame = await FirstFrameAsync(firstDemux, first);
        await using var secondDemux = await OpenAsync(H264);
        await using var second = Open(secondDemux, device: null, HardwareDecodeMode.Required);
        using var secondFrame = await FirstFrameAsync(secondDemux, second);

        Assert.NotEqual(firstFrame.HwDeviceContext, secondFrame.HwDeviceContext);
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, H264)]
    public async Task TheDevice_OutlivesTheDecodersThatBorrowIt()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        Assert.True(device.TryGetD3D12Device(out nint before));

        // One decoder after another, each closed with its frames before the next opens.
        for (int i = 0; i < 2; i++)
        {
            await using var demux = await OpenAsync(H264);
            await using var decoder = Open(demux, device, HardwareDecodeMode.Required);
            using var frame = await FirstFrameAsync(demux, decoder);
            Assert.Equal(device.ContextPointer, frame.HwDeviceContext);
        }

        // The device is still the one it was.
        Assert.True(device.TryGetD3D12Device(out nint after));
        Assert.Equal(before, after);
        Assert.Equal(Identity(before), QueryIdentity(after, IidDevice));
    }

    /// <summary>
    /// Two decoders open at once on one device each get a pool of their own. Both frames stay
    /// alive while the pools are compared, so neither address can have been reused.
    /// </summary>
    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, H264)]
    public async Task DecodersSharingADevice_EachHaveTheirOwnPool()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);

        await using var firstDemux = await OpenAsync(H264);
        await using var first = Open(firstDemux, device, HardwareDecodeMode.Required);
        using var firstFrame = await FirstFrameAsync(firstDemux, first);
        await using var secondDemux = await OpenAsync(H264);
        await using var second = Open(secondDemux, device, HardwareDecodeMode.Required);
        using var secondFrame = await FirstFrameAsync(secondDemux, second);

        Assert.Equal(device.ContextPointer, firstFrame.HwDeviceContext);
        Assert.Equal(device.ContextPointer, secondFrame.HwDeviceContext);
        Assert.NotEqual(firstFrame.HwFramesContext, secondFrame.HwFramesContext);
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, H264)]
    public async Task ACallersD3D12Device_IsLentToTheDecoder()
    {
        nint ours = CreateD3D12Device();
        try
        {
            using var device = HardwareDevice.FromD3D12Device(ours);
            Assert.True(device.TryGetD3D12Device(out nint lent));
            Assert.Equal(ours, lent);

            await using var demux = await OpenAsync(H264);
            await using var decoder = Open(demux, device, HardwareDecodeMode.Required);
            using var frame = await FirstFrameAsync(demux, decoder);

            Assert.Equal(device.ContextPointer, frame.HwDeviceContext);
            Assert.True(frame.TryGetD3D12Texture(out nint texture, out _, out _, out _));
            Assert.Equal(Identity(ours), Identity(DeviceOf(texture)));
        }
        finally
        {
            Marshal.Release(ours);
        }
    }

    /// <summary>
    /// The default AV1 decoder, libdav1d, has no hardware configuration (#417), so a D3D12 device
    /// has nothing to bind: Auto decodes in software, Required fails and says which backend it
    /// tried, and Disabled never uses the device.
    /// </summary>
    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, H264)]
    public async Task ACodecWithNoConfigurationForTheDevice_FallsBackOrFailsAsItsModeSays()
    {
        using var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);

        await using (var demux = await OpenAsync(Av1))
        await using (var auto = Open(demux, device, HardwareDecodeMode.Auto))
            Assert.Null(auto.HardwareBackend);

        await using (var demux = await OpenAsync(Av1))
        await using (var disabled = Open(demux, device, HardwareDecodeMode.Disabled))
            Assert.Null(disabled.HardwareBackend);

        await using (var demux = await OpenAsync(Av1))
        {
            var error = Assert.Throws<HardwareDecodeUnavailableException>(
                () => Open(demux, device, HardwareDecodeMode.Required));
            var attempt = Assert.Single(error.Attempts);
            Assert.Equal(HardwareDecodeBackendKind.D3D12Va, attempt.Backend);
        }
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, H264)]
    public void ADisposedDevice_LendsNothing()
    {
        var device = HardwareDevice.Create(HardwareDecodeBackendKind.D3D12Va);
        device.Dispose();

        Assert.False(device.TryGetD3D12Device(out nint d3d12));
        Assert.Equal(0, d3d12);
        Assert.Throws<ObjectDisposedException>(() => device.Borrow());
        device.Dispose();
    }

    [Fact]
    public void EveryBackend_MapsToTheFfmpegDeviceTypeItIsClassifiedFrom()
    {
        foreach (var kind in Enum.GetValues<HardwareDecodeBackendKind>())
        {
            int? type = HardwareDecodeProbeBridge.AvDeviceTypeOf(kind);
            if (kind == HardwareDecodeBackendKind.Other)
                Assert.Null(type);
            else
                Assert.Equal(kind, HardwareDecodeProbeBridge.ClassifyBackend(Assert.NotNull(type)));
        }
    }

    [Fact]
    public void ABackendFfmpegHasNoDeviceFor_IsRefusedByName()
    {
        var error = Assert.Throws<NotSupportedException>(() => HardwareDevice.Create(HardwareDecodeBackendKind.Other));
        Assert.Contains("Other", error.Message);
    }

    private VideoDecoder Open(DemuxSession demux, HardwareDevice? device, HardwareDecodeMode mode)
    {
        var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = mode, PreferredBackends = [HardwareDecodeBackendKind.D3D12Va] },
            fixture.Capabilities,
            loggerFactory: null,
            device is null ? null : new VideoDecoderOptions { Device = device }
        );
        decoder.YieldHardwareFrames = true;
        return decoder;
    }

    private static async Task<GpuVideoFrame> FirstFrameAsync(DemuxSession demux, VideoDecoder decoder)
    {
        await QueueAllAsync(demux, decoder);
        await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        var frame = Assert.IsType<GpuVideoFrame>(frames.Current);
        // Release the rest so the decoder can close.
        while (await frames.MoveNextAsync())
            frames.Current.Dispose();
        return frame;
    }

    private static async Task<DemuxSession> OpenAsync(string clip)
    {
        var file = TestEnvironment.GetCorpusFile(clip);
        Assert.True(file is not null, $"Corpus is present but {clip} is missing.");
        return (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
    }

    private static async Task QueueAllAsync(DemuxSession demux, VideoDecoder decoder)
    {
        int streamIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;
        nint read = FFAvCodec.av_packet_alloc();
        try
        {
            while (FFAvFormat.av_read_frame(demux.FormatContextPtr, read) >= 0)
            {
                if (new AvPacketAccessor(read).StreamIndex == streamIndex)
                {
                    nint clone = FFAvCodec.av_packet_alloc();
                    FFAvCodec.av_packet_ref(clone, read);
                    await decoder.SendPacketAsync(clone);
                }

                FFAvCodec.av_packet_unref(read);
            }
        }
        finally
        {
            FFAvCodec.av_packet_free(ref read);
        }

        decoder.CompletePacketQueue();
    }

    private static nint CreateD3D12Device()
    {
        const int FeatureLevel11_0 = 0xb000;
        Marshal.ThrowExceptionForHR(D3D12CreateDevice(0, FeatureLevel11_0, IidDevice, out nint device));
        return device;
    }

    /// <summary>The <c>ID3D12Device*</c> a resource lives on, through <c>ID3D12DeviceChild::GetDevice</c>.</summary>
    private static unsafe nint DeviceOf(nint resource)
    {
        var getDevice = (delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)resource)[7];
        nint device = 0;
        Guid iid = IidDevice;
        Marshal.ThrowExceptionForHR(getDevice(resource, &iid, &device));
        Marshal.Release(device);
        return device;
    }

    private static nint Identity(nint unknown) => QueryIdentity(unknown, IidUnknown);

    private static nint QueryIdentity(nint unknown, Guid iid)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint result));
        Marshal.Release(result);
        return iid == IidUnknown ? result : QueryIdentity(result, IidUnknown);
    }

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(nint adapter, int minimumFeatureLevel, in Guid riid, out nint device);
}
