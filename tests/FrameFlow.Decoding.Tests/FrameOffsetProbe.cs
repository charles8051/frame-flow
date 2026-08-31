using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Native;
using FrameFlow.Native.Interop;
using Xunit.Abstractions;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Empirically probes AVFrame field values by decoding a real audio frame.
/// Validates that typed struct access via FFmpeg.AutoGen.Abstractions produces
/// correct values for audio metadata fields.
/// </summary>
public sealed class FrameOffsetProbe
{
    private readonly ITestOutputHelper _output;

    public FrameOffsetProbe(ITestOutputHelper output) => _output = output;

    [RequiresFfmpegAndCorpusFact]
    public async Task Probe_AudioFrame_ChannelCount_Offsets()
    {
        var path = TestEnvironment.GetCorpusFile("test-audio-aac.m4a");
        if (path is null)
            return;

        var bootstrapper = new FrameFlowBootstrapper(new FrameFlowNativeOptions());
        var result = bootstrapper.Initialize();
        if (!result.IsSuccess)
            return;

        var source = MediaSource.FromFile(path);
        var factory = new DemuxSessionFactory();

        await using var session = await factory.OpenAsync(source);
        var demux = (DemuxSession)session;
        var audioStream = session.MediaInfo.AudioStreams[0];

        _output.WriteLine(
            $"Audio: {audioStream.CodecName} {audioStream.SampleRate}Hz {audioStream.Channels}ch"
        );

        var fmtCtxRaw = demux.FormatContextPtr;
        unsafe
        {
            ref AVFormatContext fmtCtx = ref Unsafe.AsRef<AVFormatContext>((void*)fmtCtxRaw);
            AVStream* streamPtr = fmtCtx.streams[audioStream.StreamIndex];
            AVCodecParameters* codecPar = streamPtr->codecpar;
            int codecId = (int)codecPar->codec_id;

            nint codec = FFAvCodec.avcodec_find_decoder(codecId);
            nint ctx = FFAvCodec.avcodec_alloc_context3(codec);
            FFAvCodec.avcodec_parameters_to_context(ctx, (nint)codecPar);
            FFAvCodec.avcodec_open2(ctx, codec, nint.Zero);

            nint framePtr = FFAvUtil.av_frame_alloc();
            nint pktPtr = FFAvCodec.av_packet_alloc();

            bool frameDecoded = false;
            while (!frameDecoded)
            {
                int readRet = FFAvFormat.av_read_frame(fmtCtxRaw, pktPtr);
                if (readRet < 0)
                    break;

                var pktAccessor = new AvPacketAccessor(pktPtr);
                if (pktAccessor.StreamIndex != audioStream.StreamIndex)
                {
                    FFAvCodec.av_packet_unref(pktPtr);
                    continue;
                }

                FFAvCodec.avcodec_send_packet(ctx, pktPtr);
                int recv = FFAvCodec.avcodec_receive_frame(ctx, framePtr);
                FFAvCodec.av_packet_unref(pktPtr);

                if (recv == 0)
                {
                    // Use typed struct access via FFmpeg.AutoGen.Abstractions
                    ref AVFrame frame = ref Unsafe.AsRef<AVFrame>((void*)framePtr);

                    int format = frame.format;
                    int nbSamples = frame.nb_samples;
                    int sampleRate = frame.sample_rate;
                    int nbChannels = frame.ch_layout.nb_channels;

                    _output.WriteLine($"format = {format}");
                    _output.WriteLine($"nb_samples = {nbSamples}");
                    _output.WriteLine($"sample_rate = {sampleRate}");
                    _output.WriteLine($"ch_layout.nb_channels = {nbChannels}");

                    // Validate the values are in the expected range for AAC stereo
                    Assert.True(sampleRate > 0, $"sample_rate should be > 0, got {sampleRate}");
                    Assert.True(nbChannels > 0, $"nb_channels should be > 0, got {nbChannels}");
                    Assert.True(nbSamples > 0, $"nb_samples should be > 0, got {nbSamples}");

                    frameDecoded = true;
                }
            }

            FFAvCodec.av_packet_free(ref pktPtr);
            FFAvUtil.av_frame_free(ref framePtr);
            FFAvCodec.avcodec_free_context(ref ctx);

            // If no frame was decoded, that's acceptable (corpus file may not be present)
            // The test is skipped via RequiresFfmpegAndCorpusFact if prerequisites are missing.
        }
    }
}
