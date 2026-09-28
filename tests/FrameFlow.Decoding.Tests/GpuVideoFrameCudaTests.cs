using System.Runtime.InteropServices;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// <see cref="GpuVideoFrame.TryGetCudaPlanes"/> surfaces an NVDEC frame's device memory (#289). The
/// planes are read back through the CUDA driver and compared, byte for byte, with FFmpeg's own
/// download of the same frame.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class GpuVideoFrameCudaTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Fixture = "test-video-h264-yuv420p.mp4";

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Cuda, Fixture)]
    public async Task ACudaFrame_SurfacesPlanesThatHoldItsSamples()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.Cuda);
        await QueueAllAsync(demux, decoder);
        await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        using var frame = Assert.IsType<GpuVideoFrame>(frames.Current);
        Assert.Equal(PixelFormat.Nv12, frame.Format);

        Assert.True(frame.TryGetCudaPlanes(out var planes));
        Assert.NotEqual(nint.Zero, planes.Context);
        Assert.True(planes.LumaPitch >= frame.Width, $"a {planes.LumaPitch}-byte pitch cannot hold {frame.Width} samples");
        Assert.True(planes.ChromaPitch >= frame.Width);

        int chromaHeight = (frame.Height + 1) / 2;
        byte[] luma = Cuda.Read(planes, planes.Luma, planes.LumaPitch, frame.Height);
        byte[] chroma = Cuda.Read(planes, planes.Chroma, planes.ChromaPitch, chromaHeight);
        var (expectedLuma, expectedChroma) = Download(frame);

        for (int y = 0; y < frame.Height; y++)
        {
            Assert.True(
                luma.AsSpan(y * planes.LumaPitch, frame.Width).SequenceEqual(expectedLuma[y]),
                $"luma row {y} differs from FFmpeg's download");
        }

        for (int y = 0; y < chromaHeight; y++)
        {
            Assert.True(
                chroma.AsSpan(y * planes.ChromaPitch, frame.Width).SequenceEqual(expectedChroma[y]),
                $"chroma row {y} differs from FFmpeg's download");
        }
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Cuda, Fixture)]
    public async Task ADisposedFrame_SurfacesNothing()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.Cuda);
        await QueueAllAsync(demux, decoder);
        await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        var frame = Assert.IsType<GpuVideoFrame>(frames.Current);

        frame.Dispose();

        Assert.False(frame.TryGetCudaPlanes(out var planes));
        Assert.Equal(default, planes);
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, Fixture)]
    public async Task AD3D11VaFrame_HasNoCudaPlanes()
    {
        await using var demux = await OpenAsync();
        await using var decoder = OpenHardware(demux, HardwareDecodeBackendKind.D3D11Va);
        await QueueAllAsync(demux, decoder);
        await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        using var frame = Assert.IsType<GpuVideoFrame>(frames.Current);

        Assert.False(frame.TryGetCudaPlanes(out var planes));
        Assert.Equal(default, planes);
    }

    /// <summary>FFmpeg's download of <paramref name="frame"/>, as rows of <c>Width</c> bytes per plane.</summary>
    private static unsafe (byte[][] Luma, byte[][] Chroma) Download(GpuVideoFrame frame)
    {
        nint cpu = FFAvUtil.av_frame_alloc();
        try
        {
            Assert.True(FFAvUtil.av_hwframe_transfer_data(cpu, frame.NativeAvFrame, 0) >= 0);
            var accessor = new AvFrameAccessor(cpu);
            var luma = new byte[frame.Height][];
            for (int y = 0; y < luma.Length; y++)
                luma[y] = new ReadOnlySpan<byte>(accessor.GetDataPointer(0) + y * accessor.GetLineSize(0), frame.Width).ToArray();
            var chroma = new byte[(frame.Height + 1) / 2][];
            for (int y = 0; y < chroma.Length; y++)
                chroma[y] = new ReadOnlySpan<byte>(accessor.GetDataPointer(1) + y * accessor.GetLineSize(1), frame.Width).ToArray();
            return (luma, chroma);
        }
        finally
        {
            FFAvUtil.av_frame_free(ref cpu);
        }
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

    /// <summary>The CUDA driver calls the test reads device memory with. <c>nvcuda</c> ships with the driver.</summary>
    private static unsafe class Cuda
    {
        [DllImport("nvcuda", EntryPoint = "cuCtxPushCurrent_v2", ExactSpelling = true)]
        private static extern int cuCtxPushCurrent(nint context);

        [DllImport("nvcuda", EntryPoint = "cuCtxPopCurrent_v2", ExactSpelling = true)]
        private static extern int cuCtxPopCurrent(out nint context);

        [DllImport("nvcuda", EntryPoint = "cuStreamSynchronize", ExactSpelling = true)]
        private static extern int cuStreamSynchronize(nint stream);

        [DllImport("nvcuda", EntryPoint = "cuMemcpyDtoH_v2", ExactSpelling = true)]
        private static extern int cuMemcpyDtoH(void* destination, ulong source, nuint bytes);

        /// <summary>
        /// Copies <paramref name="rows"/> rows of <paramref name="pitch"/> bytes from
        /// <paramref name="plane"/>, after the decoder's copy on the frame's stream has finished.
        /// </summary>
        public static byte[] Read(CudaFramePlanes planes, nint plane, int pitch, int rows)
        {
            var host = new byte[pitch * rows];
            Check(cuCtxPushCurrent(planes.Context), "cuCtxPushCurrent");
            try
            {
                Check(cuStreamSynchronize(planes.Stream), "cuStreamSynchronize");
                fixed (byte* destination = host)
                    Check(cuMemcpyDtoH(destination, (ulong)plane, (nuint)host.Length), "cuMemcpyDtoH");
            }
            finally
            {
                cuCtxPopCurrent(out _);
            }

            return host;
        }

        private static void Check(int result, string call) =>
            Assert.True(result == 0, $"{call} returned CUDA error {result}");
    }
}
