using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Media;
using FrameFlow.Native.Interop;
using Xunit.Abstractions;
using static FrameFlow.Decoding.Tests.JpegHarness;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A stream decoded on hardware converts to the same Bgra32 as the same stream decoded in software
/// (#792), both when the decoder reads frames back itself and when a caller reads back a
/// <see cref="GpuVideoFrame"/>. Skipped where no backend decodes the codec.
/// </summary>
/// <remarks>
/// The decoders' 4:2:0 output is bit-exact for these codecs, so any difference is the conversion's.
/// Every frame is released as it arrives: holding a hardware pool's frames starves the decoder.
/// </remarks>
[Collection(DecodePoolCollection.Name)]
public sealed class HardwareSoftwareBgraParityTests(FfmpegBootstrapFixture fixture, ITestOutputHelper output)
    : IClassFixture<FfmpegBootstrapFixture>
{
    [RequiresHardwareDecodeFact((int)AVCodecID.AV_CODEC_ID_H264)]
    public Task H264ConvertsAlikeOnHardwareAndInSoftware() => AssertParityAsync("test-video-h264-yuv420p.mp4");

    [RequiresHardwareDecodeFact((int)AVCodecID.AV_CODEC_ID_VP9)]
    public Task Vp9ConvertsAlikeOnHardwareAndInSoftware() => AssertParityAsync("test-video-vp9-yuv420p.webm");

    /// <summary>10-bit: the frame reads back as P010 and the software decoder gives yuv420p10le.</summary>
    [RequiresHardwareDecodeFact((int)AVCodecID.AV_CODEC_ID_VP9)]
    public Task TenBitVp9ConvertsAlikeOnHardwareAndInSoftware() =>
        AssertParityAsync("test-video-vp9-yuv420p10.webm");

    /// <summary>
    /// Full range, which the transfer off the GPU does not carry over: the readback takes it from
    /// the hardware frame.
    /// </summary>
    [RequiresHardwareDecodeFact((int)AVCodecID.AV_CODEC_ID_VP9)]
    public Task FullRangeVp9ConvertsAlikeOnHardwareAndInSoftware() =>
        AssertParityAsync("test-video-vp9-yuv420p-fullrange.webm");

    [RequiresHardwareDecodeFact((int)AVCodecID.AV_CODEC_ID_HEVC)]
    public Task HevcConvertsAlikeOnHardwareAndInSoftware() => AssertParityAsync("test-video-h265-yuv420p.mp4");

    private async Task AssertParityAsync(string clip)
    {
        var file = TestEnvironment.GetCorpusFile(clip);
        Assert.True(file is not null, $"Corpus is present but {clip} is missing.");

        var software = await DecodeAsync(file!, HardwareDecodeMode.Disabled, yieldHardwareFrames: false);
        Assert.Null(software.Engaged);
        Assert.NotEmpty(software.Frames);

        // Every backend that initialised here and decodes the codec, not only the first one bound.
        var backends = fixture.Capabilities.Available.Where(b => b.Initialized).Select(b => b.Kind).Distinct();
        var compared = new List<HardwareDecodeBackendKind>();
        foreach (var backend in backends)
        {
            foreach (bool yieldHardwareFrames in new[] { false, true })
            {
                var hardware = await TryDecodeOnAsync(file!, backend, yieldHardwareFrames);
                if (hardware is null)
                    break;

                Assert.Equal(backend, hardware.Engaged);
                Assert.Equal(software.Frames.Count, hardware.Frames.Count);
                for (int i = 0; i < software.Frames.Count; i++)
                    AssertSame(software.Frames[i], hardware.Frames[i], $"{clip} frame {i} on {backend} (GPU frames read back: {yieldHardwareFrames})");
                if (yieldHardwareFrames)
                    compared.Add(backend);
            }
        }

        Assert.NotEmpty(compared);
        output.WriteLine($"{clip}: {software.Frames.Count} frames identical on {string.Join(", ", compared)}.");
    }

    private static void AssertSame(byte[] expected, byte[] actual, string what)
    {
        if (expected.AsSpan().SequenceEqual(actual))
            return;

        int worst = 0;
        for (int b = 0; b < Math.Min(expected.Length, actual.Length); b++)
            worst = Math.Max(worst, Math.Abs(expected[b] - actual[b]));
        Assert.Fail($"{what} differs from software by up to {worst} levels.");
    }

    // Null when the backend does not decode this stream on hardware here.
    private async Task<Run?> TryDecodeOnAsync(string file, HardwareDecodeBackendKind backend, bool yieldHardwareFrames)
    {
        var only = new HardwareDecodeCapabilities(
            fixture.Capabilities.Available.Where(b => b.Kind == backend && b.Initialized).ToList()
        );
        try
        {
            var run = await DecodeAsync(file, HardwareDecodeMode.Required, yieldHardwareFrames, only, [backend]);
            return run.Engaged is null ? null : run;
        }
        catch (HardwareDecodeUnavailableException)
        {
            return null;
        }
        catch (MediaException)
        {
            return null;
        }
    }

    private sealed record Run(List<byte[]> Frames, HardwareDecodeBackendKind? Engaged);

    private async Task<Run> DecodeAsync(
        string file,
        HardwareDecodeMode mode,
        bool yieldHardwareFrames,
        HardwareDecodeCapabilities? capabilities = null,
        IReadOnlyList<HardwareDecodeBackendKind>? preferred = null
    )
    {
        await using var demux = await OpenAsync(file);
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = mode, PreferredBackends = preferred ?? [] },
            capabilities ?? HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );
        decoder.YieldHardwareFrames = yieldHardwareFrames;

        var frames = new List<byte[]>();
        await QueueAllAsync(demux, decoder);
        await foreach (var frame in decoder.DecodeAsync())
        {
            using (frame)
            {
                if (frame is GpuVideoFrame gpu)
                {
                    using var read = gpu.ReadbackToCpuBgra32();
                    frames.Add(Pixels(read));
                }
                else
                {
                    frames.Add(Pixels((CpuVideoFrame)frame));
                }
            }
        }

        return new Run(frames, decoder.HardwareBackend);
    }

    private static byte[] Pixels(CpuVideoFrame frame)
    {
        var data = frame.AsCpu()!.Value;
        Assert.Equal(PixelFormat.Bgra32, frame.Format);
        return data.PlaneY.Span[..(data.StrideY * data.Height)].ToArray();
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
