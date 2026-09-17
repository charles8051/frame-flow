using FrameFlow.Media;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Coverage for the display interval a decoded frame carries (#249).
/// </summary>
/// <remarks>
/// <para>
/// <c>VideoDecoder</c> read the frame's PTS and left its <see cref="IVideoFrame.Duration"/>
/// at <see cref="TimeSpan.Zero"/>, so every frame the pipeline produced claimed to occupy no
/// time at all. The presenter-side pacer holds the last frame of a run until the clock
/// reaches <c>Pts + Duration</c> before it reports end-of-stream; with the duration always
/// zero that target equalled the PTS the frame was selected on and the hold never engaged.
/// </para>
/// <para>
/// The consequence is invisible at video cadence — one frame is 41 ms at 24 fps — and
/// total for a source whose single frame is meant to stay up for seconds, which is what
/// surfaced it (#248).
/// </para>
/// </remarks>
public sealed class FrameDurationTests : IClassFixture<FfmpegBootstrapFixture>
{
    // Constant frame rate, no B-frames (the pinned LGPL FFmpeg encodes H.264 with
    // libopenh264), so presentation order is decode order and every frame's interval is
    // the same. That makes the expected duration a single number rather than a distribution.
    private const string VideoFixture = "test-video-h264-yuv420p.mp4";

    [RequiresFfmpegAndCorpusFact]
    public async Task DecodedFrames_CarryTheStreamsDisplayInterval()
    {
        var file = TestEnvironment.GetCorpusFile(VideoFixture);
        Assert.True(
            file is not null,
            $"Corpus is present but {VideoFixture} is missing. Re-run scripts/generate-test-corpus.cs."
        );

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file!));
        var demux = (DemuxSession)session;
        var stream = demux.MediaInfo.VideoStreams[0];

        var durations = await DecodeDurationsAsync(demux, stream.StreamIndex);

        Assert.NotEmpty(durations);

        // The gate. A zero here is the whole defect: it is what the pacer adds to the PTS to
        // decide when the last frame has finished being on screen.
        Assert.DoesNotContain(TimeSpan.Zero, durations);

        // Non-zero is necessary but not sufficient — a wrong time base would also be non-zero.
        // The stream declares 24 fps, so each frame occupies 1/24 s. One tick of tolerance
        // absorbs the rescale through microseconds.
        var expected = TimeSpan.FromSeconds(1.0 / stream.FrameRate);
        var tolerance = TimeSpan.FromMilliseconds(1);
        foreach (var d in durations)
        {
            Assert.True(
                (d - expected).Duration() <= tolerance,
                $"frame duration {d} is not the stream's {expected} interval at {stream.FrameRate} fps."
            );
        }
    }

    /// <summary>
    /// Feeds every video packet of the file through the decoder and returns the duration of
    /// each frame it produced. The corpus clip is far shorter than the decoder's packet queue,
    /// so the whole file is queued before the drain rather than pumped alongside it.
    /// </summary>
    private static async Task<List<TimeSpan>> DecodeDurationsAsync(DemuxSession demux, int videoStreamIndex)
    {
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            videoStreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            HardwareDecodeCapabilities.Empty,
            loggerFactory: null
        );

        nint readPacket = FFAvCodec.av_packet_alloc();
        try
        {
            while (FFAvFormat.av_read_frame(demux.FormatContextPtr, readPacket) >= 0)
            {
                if (new AvPacketAccessor(readPacket).StreamIndex == videoStreamIndex)
                {
                    // The decoder queue is async and takes ownership, so it gets its own
                    // reference rather than the buffer the next read will reuse.
                    nint clone = FFAvCodec.av_packet_alloc();
                    FFAvCodec.av_packet_ref(clone, readPacket);
                    await decoder.SendPacketAsync(clone);
                }

                FFAvCodec.av_packet_unref(readPacket);
            }
        }
        finally
        {
            FFAvCodec.av_packet_free(ref readPacket);
        }

        decoder.CompletePacketQueue();

        var durations = new List<TimeSpan>();
        await foreach (var frame in decoder.DecodeAsync())
        {
            durations.Add(frame.Duration);
            frame.Dispose();
        }

        return durations;
    }
}
