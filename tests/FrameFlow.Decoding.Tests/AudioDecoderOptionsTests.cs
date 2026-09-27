using FrameFlow.Decoding;

namespace FrameFlow.Decoding.Tests;

public sealed class AudioDecoderOptionsTests : IClassFixture<FfmpegBootstrapFixture>
{
    [Fact]
    public void DefaultTargetSampleRate_Is48000()
    {
        var opts = new AudioDecoderOptions();
        Assert.Equal(48_000, opts.TargetSampleRate);
    }

    [Fact]
    public void TargetSampleRate_InitSyntax_StoresValue()
    {
        var opts = new AudioDecoderOptions { TargetSampleRate = 22_050 };
        Assert.Equal(22_050, opts.TargetSampleRate);
    }

    /// <summary>
    /// Every production call site passes no options, so the queue depth a decoder gets from
    /// <c>null</c> is the one playback runs with. It must be the documented default, which
    /// <c>ReadAheadCapacity</c> sizes video's queue against (#221).
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task NullOptions_GiveTheDocumentedQueueDepth()
    {
        var file = TestEnvironment.GetCorpusFile("test-av-h264-aac.mp4");
        if (file is null)
            return;

        await using var session = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;
        var audio = demux.MediaInfo.AudioStreams[0].StreamIndex;

        await using var withNull = new AudioDecoder(demux.FormatContextPtr, audio, options: null);
        await using var withDefaults = new AudioDecoder(demux.FormatContextPtr, audio, new AudioDecoderOptions());

        Assert.Equal(new AudioDecoderOptions().PacketQueueCapacity, withNull.PacketQueueCapacity);
        Assert.Equal(withDefaults.PacketQueueCapacity, withNull.PacketQueueCapacity);
    }
}
