using FrameFlow.Decoding.Core;
using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// How a decoded frame is meant to be shown (#542): the pixel shape and rotation read from the
/// container and the codec, reported on the stream and carried on every frame.
/// </summary>
public sealed class DisplayGeometryTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Anamorphic = "test-video-h264-anamorphic.mp4";
    private const string Rotated = "test-video-h264-rotated.mp4";
    private const string ContainerAspect = "test-video-h264-container-aspect.mp4";
    private const string Plain = "test-video-h264-yuv420p.mp4";

    private readonly FfmpegBootstrapFixture _fixture;

    public DisplayGeometryTests(FfmpegBootstrapFixture fixture) => _fixture = fixture;

    // ── Pure ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, VideoRotation.None)]
    [InlineData(90.0, VideoRotation.Clockwise270)] // ffprobe's rotation=90: a quarter turn counterclockwise
    [InlineData(-90.0, VideoRotation.Clockwise90)] // what a phone held upright usually records
    [InlineData(180.0, VideoRotation.Clockwise180)]
    [InlineData(-180.0, VideoRotation.Clockwise180)]
    [InlineData(270.0, VideoRotation.Clockwise90)]
    [InlineData(89.6, VideoRotation.Clockwise270)] // rounded to a degree first, as ffplay does
    [InlineData(45.0, VideoRotation.None)] // not a quarter turn
    [InlineData(double.NaN, VideoRotation.None)] // no matrix, or a degenerate one
    public void RotationOf_SnapsFFmpegsAngleToTheQuarterTurnItsPlayerApplies(double ffmpegDegrees, VideoRotation expected)
    {
        Assert.Equal(expected, DisplayGeometry.RotationOf(ffmpegDegrees));
    }

    [Theory]
    [InlineData(32, 27, 0, 1, 32, 27)] // the container's wins over an unknown codec value
    [InlineData(32, 27, 8, 9, 32, 27)] // and over a known one
    [InlineData(0, 1, 8, 9, 8, 9)] // the codec's when the container says nothing
    [InlineData(64, 54, 0, 0, 32, 27)] // in lowest terms
    [InlineData(0, 0, 0, 0, 1, 1)] // square when neither says
    [InlineData(-1, 1, 0, 1, 1, 1)] // a negative term is no value
    public void Resolve_PrefersTheContainer_ThenTheFrame_ThenSquare(
        int containerNum, int containerDen, int frameNum, int frameDen, int expectedNum, int expectedDen)
    {
        Assert.Equal(
            new SampleAspectRatio(expectedNum, expectedDen),
            DisplayGeometry.Resolve((containerNum, containerDen), (frameNum, frameDen)));
    }

    [Theory]
    [InlineData(0, 0, 0, 1, 4, 3, 4, 3)] // a frame without one takes the codec parameters'
    [InlineData(0, 0, 8, 9, 4, 3, 8, 9)] // a frame's own wins over them
    [InlineData(3, 2, 8, 9, 4, 3, 3, 2)] // and the container's over both
    public void Resolve_FallsBackToTheCodecParameters_Last(
        int containerNum, int containerDen, int frameNum, int frameDen, int codecNum, int codecDen,
        int expectedNum, int expectedDen)
    {
        Assert.Equal(
            new SampleAspectRatio(expectedNum, expectedDen),
            DisplayGeometry.Resolve((containerNum, containerDen), (frameNum, frameDen), (codecNum, codecDen)));
    }

    // ── The stream ──────────────────────────────────────────────────────────

    [RequiresFfmpegAndCorpusFact]
    public async Task AnAnamorphicStream_ReportsItsPixelShape_AndItsDisplayWidth()
    {
        await using var demux = await OpenAsync(Anamorphic);
        var video = demux.MediaInfo.VideoStreams[0];

        Assert.Equal(new SampleAspectRatio(32, 27), video.SampleAspectRatio);
        Assert.Equal(VideoRotation.None, video.Rotation);
        Assert.Equal((320, 240), (video.Width, video.Height));
        Assert.Equal((379, 240), (video.DisplayWidth, video.DisplayHeight));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ARotatedStream_ReportsItsRotation_AndAPortraitDisplaySize()
    {
        await using var demux = await OpenAsync(Rotated);
        var video = demux.MediaInfo.VideoStreams[0];

        Assert.Equal(VideoRotation.Clockwise270, video.Rotation);
        Assert.True(video.SampleAspectRatio.IsSquare);
        Assert.Equal((240, 320), (video.DisplayWidth, video.DisplayHeight));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task AnOrdinaryStream_IsSquareAndUpright()
    {
        await using var demux = await OpenAsync(Plain);
        var video = demux.MediaInfo.VideoStreams[0];

        Assert.Equal(SampleAspectRatio.Square, video.SampleAspectRatio);
        Assert.Equal(VideoRotation.None, video.Rotation);
    }

    // ── The frames ──────────────────────────────────────────────────────────

    [RequiresFfmpegAndCorpusTheory]
    [InlineData(Anamorphic, 32, 27, VideoRotation.None)]
    [InlineData(Rotated, 1, 1, VideoRotation.Clockwise270)]
    [InlineData(Plain, 1, 1, VideoRotation.None)]
    [InlineData(ContainerAspect, 3, 2, VideoRotation.None)] // the container's, over the bitstream's 32:27
    public async Task SoftwareDecodedFrames_CarryTheGeometry(string clip, int num, int den, VideoRotation rotation)
    {
        await using var demux = await OpenAsync(clip);
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );

        var frames = await DecodeAllAsync(demux, decoder);

        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.Equal((new SampleAspectRatio(num, den), rotation), f));
    }

    [RequiresHardwareDecodeBackendFact(HardwareDecodeBackendKind.D3D11Va, Rotated)]
    public async Task HardwareFramesOnTheGpu_CarryTheGeometry_AndKeepItOnReadback()
    {
        await using var demux = await OpenAsync(Rotated);
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions
            {
                Mode = HardwareDecodeMode.Required,
                PreferredBackends = [HardwareDecodeBackendKind.D3D11Va],
            },
            _fixture.Capabilities,
            loggerFactory: null
        );
        decoder.YieldHardwareFrames = true;

        await QueueAllAsync(demux, decoder);
        await using var frames = decoder.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        using var gpu = Assert.IsType<GpuVideoFrame>(frames.Current);
        while (await frames.MoveNextAsync())
            frames.Current.Dispose();

        Assert.Equal(VideoRotation.Clockwise270, gpu.Rotation);
        using var cpu = gpu.ReadbackToCpuBgra32();
        Assert.Equal(VideoRotation.Clockwise270, cpu.Rotation);
        Assert.Equal(gpu.SampleAspectRatio, cpu.SampleAspectRatio);
    }

    private static async Task<DemuxSession> OpenAsync(string clip)
    {
        var file = TestEnvironment.GetCorpusFile(clip);
        Assert.True(file is not null, $"Corpus is present but {clip} is missing. Re-run scripts/generate-test-corpus.cs.");
        return (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
    }

    private static async Task<List<(SampleAspectRatio, VideoRotation)>> DecodeAllAsync(
        DemuxSession demux, VideoDecoder decoder)
    {
        await QueueAllAsync(demux, decoder);
        var geometry = new List<(SampleAspectRatio, VideoRotation)>();
        await foreach (var frame in decoder.DecodeAsync())
        {
            geometry.Add((frame.SampleAspectRatio, frame.Rotation));
            frame.Dispose();
        }

        return geometry;
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
}
