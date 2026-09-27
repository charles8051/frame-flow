using System.Runtime.InteropServices;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// <see cref="GpuVideoFrame.TryGetD3D12Texture"/> surfaces a D3D12VA frame's texture and the
/// fence that says when it is written (#289). The pointers are checked as the COM objects they
/// claim to be, and the fence by waiting for the value the accessor reports.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class GpuVideoFrameD3D12Tests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Fixture = "test-video-h264-yuv420p.mp4";
    private const int DxgiFormatNv12 = 103;
    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(30);
    private static readonly Guid IidResource = new("696442be-a72e-4059-bc79-5b5c98040fad");
    private static readonly Guid IidFence = new("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, Fixture)]
    public async Task AD3D12VaFrame_SurfacesItsTextureAndTheFenceThatSaysItIsWritten()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.D3D12Va);
        await QueueAllAsync(demux, decoder);
        await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        using var frame = Assert.IsType<GpuVideoFrame>(frames.Current);

        Assert.True(frame.TryGetD3D12Texture(out nint texture, out int subresource, out nint fence, out ulong fenceValue));
        Assert.Equal(0, subresource);
        Assert.True(fenceValue > 0, "the fence value is zero, which every fence starts at");

        AssertImplements(texture, IidResource);
        AssertImplements(fence, IidFence);
        var desc = ResourceDesc(texture);
        Assert.Equal(DxgiFormatNv12, desc.Format);
        Assert.True(desc.Width >= (ulong)frame.Width && desc.Height >= (uint)frame.Height,
            $"a {desc.Width}x{desc.Height} texture cannot hold a {frame.Width}x{frame.Height} frame");

        // The decoder signals the fence to the reported value once the frame is written.
        using var written = new ManualResetEvent(false);
        Marshal.ThrowExceptionForHR(SetEventOnCompletion(fence, fenceValue, written.SafeWaitHandle.DangerousGetHandle()));
        Assert.True(written.WaitOne(FailureBound), $"the fence never reached {fenceValue}");
        Assert.True(CompletedValue(fence) >= fenceValue);
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, Fixture)]
    public async Task AD3D11VaFrame_HasNoD3D12Texture()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.D3D11Va);
        await QueueAllAsync(demux, decoder);
        await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        using var frame = Assert.IsType<GpuVideoFrame>(frames.Current);

        Assert.True(frame.TryGetD3D11Texture(out _, out _, out _));
        Assert.False(frame.TryGetD3D12Texture(out nint texture, out _, out nint fence, out _));
        Assert.Equal(nint.Zero, texture);
        Assert.Equal(nint.Zero, fence);
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, Fixture)]
    public async Task ADisposedFrame_SurfacesNothing()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.D3D12Va);
        await QueueAllAsync(demux, decoder);
        await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        var frame = Assert.IsType<GpuVideoFrame>(frames.Current);

        frame.Dispose();

        Assert.False(frame.TryGetD3D12Texture(out nint texture, out _, out _, out _));
        Assert.Equal(nint.Zero, texture);
    }

    private VideoDecoder OpenHardware(DemuxSession demux, HardwareDecodeBackendKind backend)
    {
        var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required, PreferredBackends = [backend] },
            fixture.Capabilities,
            loggerFactory: null
        );
        Assert.Equal(backend, decoder.HardwareBackend);
        decoder.YieldHardwareFrames = true;
        return decoder;
    }

    private static void AssertImplements(nint pointer, Guid iid)
    {
        Assert.NotEqual(nint.Zero, pointer);
        Assert.Equal(0, Marshal.QueryInterface(pointer, in iid, out nint result));
        Marshal.Release(result);
    }

    // ID3D12Resource::GetDesc is vtable slot 10 (IUnknown 0-2, ID3D12Object 3-6, GetDevice 7,
    // Map 8, Unmap 9). It returns the struct through a hidden pointer.
    private static unsafe ResourceDescription ResourceDesc(nint resource)
    {
        var getDesc = (delegate* unmanaged<nint, ResourceDescription*, ResourceDescription*>)(*(nint**)resource)[10];
        ResourceDescription desc;
        getDesc(resource, &desc);
        return desc;
    }

    // ID3D12Fence::GetCompletedValue and SetEventOnCompletion are slots 8 and 9.
    private static unsafe ulong CompletedValue(nint fence) =>
        ((delegate* unmanaged<nint, ulong>)(*(nint**)fence)[8])(fence);

    private static unsafe int SetEventOnCompletion(nint fence, ulong value, nint handle) =>
        ((delegate* unmanaged<nint, ulong, nint, int>)(*(nint**)fence)[9])(fence, value, handle);

    /// <summary><c>D3D12_RESOURCE_DESC</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceDescription
    {
        public int Dimension;
        public ulong Alignment;
        public ulong Width;
        public uint Height;
        public ushort DepthOrArraySize;
        public ushort MipLevels;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Layout;
        public int Flags;
    }

    private static async Task<DemuxSession> OpenAsync()
    {
        var file = TestEnvironment.GetCorpusFile(Fixture);
        Assert.True(file is not null, $"Corpus is present but {Fixture} is missing.");
        return (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
    }

    /// <summary>Queues every video packet and completes the queue.</summary>
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
}
