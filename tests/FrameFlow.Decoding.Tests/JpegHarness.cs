using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>Opens a JPEG and decodes it through a <see cref="VideoDecoder"/>, for the tests that need a stream one backend refuses or decodes wrongly.</summary>
internal static class JpegHarness
{
    public static async Task<DemuxSession> OpenAsync(string path, bool still = false) =>
        (DemuxSession)
            await new DemuxSessionFactory()
                .OpenAsync(
                    still
                        ? MediaSource.FromStill(path, TimeSpan.FromSeconds(1))
                        : MediaSource.FromFile(path)
                );

    /// <summary>Queues every video packet, completes the queue, and collects the decoded frames.</summary>
    public static async Task<FrameList> DecodeAllAsync(DemuxSession demux, VideoDecoder decoder)
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

        var frames = new FrameList();
        try
        {
            await foreach (var frame in decoder.DecodeAsync())
                frames.Add(frame);
        }
        catch
        {
            frames.Dispose();
            throw;
        }

        return frames;
    }

    /// <summary>The first frame's Bgra32 byte at <paramref name="column"/> of <paramref name="row"/>, channel <paramref name="channel"/> (0 = blue).</summary>
    public static byte BgraSample(IVideoFrame frame, int column, int row, int channel)
    {
        var cpu = ((CpuVideoFrame)frame).AsCpu()!.Value;
        return cpu.PlaneY.Span[row * cpu.StrideY + column * 4 + channel];
    }

    /// <summary>The decoded frames, released with the test's scope.</summary>
    internal sealed class FrameList : List<IVideoFrame>, IDisposable
    {
        public void Dispose()
        {
            foreach (var frame in this)
                frame.Dispose();
        }
    }

    internal sealed class TempFile : IDisposable
    {
        public string Path { get; }

        private TempFile(string path) => Path = path;

        public static TempFile With(byte[] bytes, string extension = ".jpg")
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"frameflow-{Guid.NewGuid():N}{extension}"
            );
            File.WriteAllBytes(path, bytes);
            return new TempFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
