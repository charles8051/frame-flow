using FrameFlow.Graph;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using FrameFlow.Tests.Shared;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// The upload and the readback build a new frame from each input, so each carries the input's
/// timeline, pixel shape and rotation over (ADR-0084, decision 3). Each runs on a device of the
/// backend it names, and is skipped without one.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class GpuDerivedItemConformanceTests(FfmpegBootstrapFixture fixture) : IClassFixture<FfmpegBootstrapFixture>
{
    // Only a gate: a device of the backend initialises and decodes here.
    private const string Fixture = "test-video-h264-yuv420p.mp4";

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, Fixture)]
    public void OnD3D11Va_AnUploadAndItsReadback_CarryTheLineage() =>
        UploadAndReadback_CarryTheLineage(HardwareDecodeBackendKind.D3D11Va);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, Fixture)]
    public void OnD3D12Va_AnUploadAndItsReadback_CarryTheLineage() =>
        UploadAndReadback_CarryTheLineage(HardwareDecodeBackendKind.D3D12Va);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.Cuda, Fixture)]
    public void OnCuda_AnUploadAndItsReadback_CarryTheLineage() =>
        UploadAndReadback_CarryTheLineage(HardwareDecodeBackendKind.Cuda);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, Fixture)]
    public Task OnD3D11Va_AHardwareFrame_CarriesTheDecodersTimeline() =>
        HardwareFrames_CarryTheDecodersTimeline(HardwareDecodeBackendKind.D3D11Va);

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D12Va, Fixture)]
    public Task OnD3D12Va_AHardwareFrame_CarriesTheDecodersTimeline() =>
        HardwareFrames_CarryTheDecodersTimeline(HardwareDecodeBackendKind.D3D12Va);

    /// <summary>
    /// A decoder that hands out frames on the GPU stamps them as one that downloads them does
    /// (ADR-0084, decision 2).
    /// </summary>
    private async Task HardwareFrames_CarryTheDecodersTimeline(HardwareDecodeBackendKind backend)
    {
        var file = TestEnvironment.GetCorpusFile(Fixture);
        Assert.True(file is not null, $"Corpus is present but {Fixture} is missing.");
        await using var demux = (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
        var streamIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            streamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required, PreferredBackends = [backend] },
            fixture.Capabilities,
            loggerFactory: null
        );
        Assert.Equal(backend, decoder.HardwareBackend);
        decoder.YieldHardwareFrames = true;
        var timeline = TimelineId.Next();
        decoder.Timeline = timeline;

        nint read = FFAvCodec.av_packet_alloc();
        try
        {
            // Four packets: a frame or two out is all this needs, and the pool stays far from full.
            for (int sent = 0; sent < 4 && FFAvFormat.av_read_frame(demux.FormatContextPtr, read) >= 0; )
            {
                if (new AvPacketAccessor(read).StreamIndex == streamIndex)
                {
                    nint clone = FFAvCodec.av_packet_alloc();
                    FFAvCodec.av_packet_ref(clone, read);
                    await decoder.SendPacketAsync(clone);
                    sent++;
                }

                FFAvCodec.av_packet_unref(read);
            }
        }
        finally
        {
            FFAvCodec.av_packet_free(ref read);
        }
        decoder.CompletePacketQueue();

        var frames = 0;
        await foreach (var frame in decoder.DecodeAsync())
        {
            using (frame)
            {
                Assert.IsType<GpuVideoFrame>(frame);
                Assert.Equal(timeline, frame.Timeline);
                frames++;
            }
        }
        Assert.True(frames > 0, "the decoder produced no frame");
    }

    private void UploadAndReadback_CarryTheLineage(HardwareDecodeBackendKind backend)
    {
        _ = fixture;
        using var device = HardwareDevice.Create(backend);
        var upload = new GpuFrameUpload(device);
        using var input = DerivedItemConformance.VideoInput(PixelFormat.Bgra32, width: 64, height: 48);

        using var uploaded = upload.Upload(input);
        using var readBack = uploaded.ReadbackToCpuBgra32();

        DerivedItemConformance.AssertCarriesLineage(input, uploaded);
        DerivedItemConformance.AssertCarriesLineage(uploaded, readBack);
    }
}
