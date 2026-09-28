using System.Reflection;
using System.Runtime.InteropServices;
using FrameFlow.Decoding;
using FrameFlow.Media;
using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Inference.D3D12.Tests;

/// <summary>The repo's FFmpeg natives and corpus, and one FFmpeg bootstrap for the process.</summary>
internal static class TestEnvironment
{
    private static readonly Lazy<string> RepoRoot = new(() =>
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "FrameFlow.slnx")))
                return dir;
        }

        return AppContext.BaseDirectory;
    });

    private static readonly Lazy<HardwareDecodeCapabilities?> Bootstrap = new(() =>
    {
        string native = Path.Combine(RepoRoot.Value, "runtimes", "win-x64", "native");
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(native, "avutil-61.dll")))
            return null;
        var result = new FrameFlowBootstrapper(new FrameFlowNativeOptions { CustomFfmpegPath = native }, NullLoggerFactory.Instance)
            .Initialize();
        return result.IsSuccess ? result.Capabilities : null;
    });

    /// <summary>What the FFmpeg bootstrap found, or null when FFmpeg is not available.</summary>
    public static HardwareDecodeCapabilities? Capabilities => Bootstrap.Value;

    public static string? CorpusFile(string name)
    {
        string path = Path.Combine(RepoRoot.Value, "tests", "corpus", "files", name);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>
/// Skipped unless <c>backend</c> decodes the first frame of <c>clip</c> on hardware here. It
/// decodes rather than asking whether a config exists, because a device that initialises can
/// still decode in software. CI has no GPU, so these tests skip there.
/// </summary>
internal sealed class RequiresHardwareDecodeFactAttribute : FactAttribute
{
    private static readonly Dictionary<(HardwareDecodeBackendKind, string), string?> Reasons = [];

    public RequiresHardwareDecodeFactAttribute(HardwareDecodeBackendKind backend, string clip)
    {
        lock (Reasons)
        {
            if (!Reasons.TryGetValue((backend, clip), out var reason))
            {
                reason = WhyItCannotDecode(backend, clip);
                Reasons[(backend, clip)] = reason;
            }

            Skip = reason;
        }
    }

    private static string? WhyItCannotDecode(HardwareDecodeBackendKind backend, string clip)
    {
        if (TestEnvironment.Capabilities is null)
            return "FFmpeg is not available.";
        if (TestEnvironment.CorpusFile(clip) is null)
            return $"Corpus file '{clip}' is not present. Run scripts/generate-test-corpus.cs.";
        if (!TestEnvironment.Capabilities.Available.Any(b => b.Kind == backend && b.Initialized))
            return $"No {backend} device initialised here.";

        try
        {
            var frames = Decode.FramesAsync(clip, backend, yieldHardware: true, count: 1).GetAwaiter().GetResult();
            bool engaged = frames.Count == 1 && frames[0] is GpuVideoFrame gpu && gpu.Backend == backend;
            foreach (var frame in frames)
                frame.Dispose();
            return engaged ? null : $"A {backend} device opened but did not decode {clip} on hardware.";
        }
        catch (HardwareDecodeUnavailableException)
        {
            return $"No decoder for {clip} binds to {backend} here.";
        }
    }
}

/// <summary>Decodes corpus clips with a chosen backend, as the decoding tests do.</summary>
internal static class Decode
{
    [DllImport("avcodec-63", ExactSpelling = true)]
    private static extern nint av_packet_alloc();

    [DllImport("avcodec-63", ExactSpelling = true)]
    private static extern void av_packet_free(ref nint packet);

    [DllImport("avcodec-63", ExactSpelling = true)]
    private static extern int av_packet_ref(nint destination, nint source);

    [DllImport("avcodec-63", ExactSpelling = true)]
    private static extern void av_packet_unref(nint packet);

    [DllImport("avformat-63", ExactSpelling = true)]
    private static extern int av_read_frame(nint formatContext, nint packet);

    /// <summary>
    /// The first <paramref name="count"/> frames of <paramref name="clip"/>: hardware frames when
    /// <paramref name="yieldHardware"/>, else the same frames read back to the CPU as Bgra32.
    /// </summary>
    public static async Task<List<IVideoFrame>> FramesAsync(
        string clip, HardwareDecodeBackendKind backend, bool yieldHardware, int count)
    {
        await using var demux = (DemuxSession)await new DemuxSessionFactory().OpenAsync(
            MediaSource.FromFile(TestEnvironment.CorpusFile(clip)!));
        int stream = demux.MediaInfo.VideoStreams[0].StreamIndex;
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            stream,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Required, PreferredBackends = [backend] },
            TestEnvironment.Capabilities,
            loggerFactory: null);
        decoder.YieldHardwareFrames = yieldHardware;

        var frames = new List<IVideoFrame>();
        var consume = Task.Run(async () =>
        {
            await foreach (var frame in decoder.DecodeAsync())
            {
                if (frames.Count < count)
                    frames.Add(frame);
                else
                    frame.Dispose();
            }
        });

        nint read = av_packet_alloc();
        try
        {
            while (av_read_frame(demux.FormatContextPtr, read) >= 0)
            {
                if (StreamIndex(read) == stream)
                {
                    nint clone = av_packet_alloc();
                    av_packet_ref(clone, read);
                    await decoder.SendPacketAsync(clone);
                }

                av_packet_unref(read);
            }
        }
        finally
        {
            av_packet_free(ref read);
        }

        decoder.CompletePacketQueue();
        await consume;
        return frames;
    }

    /// <summary><c>AVPacket.stream_index</c>: buf, pts, dts, data and size precede it.</summary>
    private static unsafe int StreamIndex(nint packet) => *(int*)(packet + 36);
}

/// <summary>A D3D12VA frame's NV12 samples, read back to the CPU through FFmpeg.</summary>
internal sealed class Nv12Image
{
    private static readonly PropertyInfo NativeAvFrame = typeof(GpuVideoFrame).GetProperty(
        "NativeAvFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [DllImport("avutil-61", ExactSpelling = true)]
    private static extern nint av_frame_alloc();

    [DllImport("avutil-61", ExactSpelling = true)]
    private static extern void av_frame_free(ref nint frame);

    [DllImport("avutil-61", ExactSpelling = true)]
    private static extern int av_hwframe_transfer_data(nint destination, nint source, int flags);

    private Nv12Image(int width, int height, byte[] luma, byte[] chroma)
    {
        Width = width;
        Height = height;
        Luma = luma;
        Chroma = chroma;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Luma, <see cref="Width"/> by <see cref="Height"/>, tightly packed.</summary>
    public byte[] Luma { get; }

    /// <summary>Interleaved Cb and Cr at half resolution in each direction, rounded up, tightly packed.</summary>
    public byte[] Chroma { get; }

    public int ChromaWidth => (Width + 1) / 2;

    public int ChromaHeight => (Height + 1) / 2;

    /// <remarks><c>AVFrame.data[8]</c> is at offset 0 and <c>linesize[8]</c> at 64.</remarks>
    public static unsafe Nv12Image Read(GpuVideoFrame frame)
    {
        nint source = (nint)NativeAvFrame.GetValue(frame)!;
        nint cpu = av_frame_alloc();
        try
        {
            int result = av_hwframe_transfer_data(cpu, source, 0);
            if (result < 0)
                throw new InvalidOperationException($"av_hwframe_transfer_data failed: {result}.");
            byte** data = (byte**)cpu;
            int* linesize = (int*)(cpu + 64);
            int width = frame.Width, height = frame.Height;
            int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
            var luma = new byte[width * height];
            var chroma = new byte[chromaWidth * chromaHeight * 2];
            for (int y = 0; y < height; y++)
                new ReadOnlySpan<byte>(data[0] + y * linesize[0], width).CopyTo(luma.AsSpan(y * width));
            for (int y = 0; y < chromaHeight; y++)
                new ReadOnlySpan<byte>(data[1] + y * linesize[1], chromaWidth * 2).CopyTo(chroma.AsSpan(y * chromaWidth * 2));
            return new Nv12Image(width, height, luma, chroma);
        }
        finally
        {
            av_frame_free(ref cpu);
        }
    }
}
