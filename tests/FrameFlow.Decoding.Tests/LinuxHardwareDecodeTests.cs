using FrameFlow.Native;
using FrameFlow.Native.Interop;
using static FrameFlow.Decoding.Tests.JpegHarness;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// NVDEC and VAAPI decode a corpus clip end to end on Linux (#865). Skipped off Linux and
/// wherever the backend does not decode the clip on hardware, so they run only on a Linux host
/// with the GPU and its drivers.
/// </summary>
/// <remarks>
/// <para>
/// What they pin is the probe's promise: a backend it reports initialised decodes. The probe
/// used to report CUDA from <c>libcuda.so.1</c> alone, which is enough to create the device
/// and not enough to decode on it, since NVDEC loads <c>libnvcuvid.so.1</c> separately.
/// </para>
/// <para>
/// The gate decodes the first frame to decide (see
/// <see cref="RequiresHardwareDecodeBackendFactAttribute"/>), so a skip names what was
/// missing rather than reading as a pass.
/// </para>
/// </remarks>
[Collection(DecodePoolCollection.Name)]
public sealed class LinuxHardwareDecodeTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";

    [RequiresLinuxHardwareDecodeBackendFact(HardwareDecodeBackendKind.Cuda, Clip)]
    public Task Nvdec_DecodesTheClipOnHardware() => AssertDecodesOnAsync(HardwareDecodeBackendKind.Cuda);

    [RequiresLinuxHardwareDecodeBackendFact(HardwareDecodeBackendKind.VaApi, Clip)]
    public Task Vaapi_DecodesTheClipOnHardware() => AssertDecodesOnAsync(HardwareDecodeBackendKind.VaApi);

    [RequiresLinuxHardwareDecodeBackendFact(HardwareDecodeBackendKind.Cuda, Clip)]
    public void Nvdec_TheProbeCheckedTheLibraryNvdecLoads() =>
        // A CUDA backend that initialised here passed the gate, and the gate asks for the
        // library NVDEC itself loads, not only the one device creation loads.
        Assert.Contains("libnvcuvid.so.1", HardwareDecodeProbe.DriverLibrariesFor(HardwareDecodeBackendKind.Cuda));

    private async Task AssertDecodesOnAsync(HardwareDecodeBackendKind backend)
    {
        var file = TestEnvironment.GetCorpusFile(Clip);
        Assert.True(file is not null, $"Corpus is present but {Clip} is missing.");

        var software = await DecodeAsync(file!, HardwareDecodeMode.Disabled, HardwareDecodeCapabilities.Empty, [], false);
        Assert.Null(software.Engaged);
        Assert.True(software.Frames > 0, "Software decoded no frames.");

        var only = new HardwareDecodeCapabilities(
            fixture.Capabilities.Available.Where(b => b.Kind == backend && b.Initialized).ToList()
        );
        Assert.NotEmpty(only.Available);

        foreach (bool yieldHardwareFrames in new[] { false, true })
        {
            var run = await DecodeAsync(file!, HardwareDecodeMode.Required, only, [backend], yieldHardwareFrames);

            string where = $"{backend}, hardware frames {yieldHardwareFrames}";
            Assert.True(run.Engaged == backend, $"{where}: engaged {run.Engaged}");
            Assert.True(run.Frames == software.Frames, $"{where}: {run.Frames} frames, software {software.Frames}");
            Assert.True(
                run.GpuFrames == (yieldHardwareFrames ? software.Frames : 0),
                $"{where}: {run.GpuFrames} GPU frames"
            );
        }
    }

    private sealed record Run(int Frames, int GpuFrames, HardwareDecodeBackendKind? Engaged);

    private static async Task<Run> DecodeAsync(
        string file,
        HardwareDecodeMode mode,
        HardwareDecodeCapabilities capabilities,
        IReadOnlyList<HardwareDecodeBackendKind> preferred,
        bool yieldHardwareFrames
    )
    {
        await using var demux = await OpenAsync(file);
        int stream = demux.MediaInfo.VideoStreams[0].StreamIndex;
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            stream,
            new HardwareDecodeOptions { Mode = mode, PreferredBackends = preferred },
            capabilities,
            loggerFactory: null
        );
        decoder.YieldHardwareFrames = yieldHardwareFrames;

        await QueueAllAsync(demux, stream, decoder);

        // Every frame is released as it arrives: holding a hardware pool's frames starves the
        // decoder.
        int frames = 0;
        int gpuFrames = 0;
        await foreach (var frame in decoder.DecodeAsync())
        {
            using (frame)
            {
                frames++;
                if (frame is GpuVideoFrame gpu)
                {
                    Assert.Equal(decoder.HardwareBackend, gpu.Backend);
                    gpuFrames++;
                }
            }
        }

        return new Run(frames, gpuFrames, decoder.HardwareBackend);
    }

    private static async Task QueueAllAsync(DemuxSession demux, int stream, VideoDecoder decoder)
    {
        nint read = FFAvCodec.av_packet_alloc();
        try
        {
            while (FFAvFormat.av_read_frame(demux.FormatContextPtr, read) >= 0)
            {
                if (new AvPacketAccessor(read).StreamIndex == stream)
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

/// <summary>
/// As <see cref="RequiresHardwareDecodeBackendFactAttribute"/>, and skipped off Linux without
/// asking the hardware anything.
/// </summary>
internal sealed class RequiresLinuxHardwareDecodeBackendFactAttribute : FactAttribute
{
    /// <param name="backend">The backend the test decodes on.</param>
    /// <param name="clip">The corpus file the test decodes.</param>
    public RequiresLinuxHardwareDecodeBackendFactAttribute(HardwareDecodeBackendKind backend, string clip)
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = $"{backend} decode on Linux; this host is not Linux.";
            return;
        }

        Skip = new RequiresHardwareDecodeBackendFactAttribute(backend, clip).Skip;
    }
}
