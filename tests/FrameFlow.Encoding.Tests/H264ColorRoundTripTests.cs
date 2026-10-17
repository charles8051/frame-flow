using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Decoding;

namespace FrameFlow.Encoding.Tests;

/// <summary>
/// A colour the H.264 encoder is given decodes back as that colour. The decoder converts with the
/// matrix a stream signals, and with BT.709 when it signals none (#796). The encoder used to convert
/// with swscale's default BT.601 and signal nothing, so its own clips decoded through the wrong
/// matrix: on 75% bars, green came back 32 levels dark. It now converts with BT.709 and signals
/// BT.709.
/// </summary>
public sealed class H264ColorRoundTripTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const int BarWidth = 40;
    private const int Height = 240;
    private const int Fps = 30;
    private const int FrameCount = 5;

    /// <summary>
    /// What a channel may differ by after the round trip. libopenh264 lands within 3 levels, which
    /// is what 8-bit limited-range YUV costs on its own; another encoder may lose a little more.
    /// With BT.601 every bar but white and black is further off than this, green by 32.
    /// </summary>
    private const int Tolerance = 6;

    /// <summary>75% colour bars, in blue, green, red.</summary>
    private static readonly byte[][] Bars =
    [
        [191, 191, 191],
        [0, 191, 191],
        [191, 191, 0],
        [0, 191, 0],
        [191, 0, 191],
        [0, 0, 191],
        [191, 0, 0],
        [0, 0, 0],
    ];

    private static int Width => BarWidth * Bars.Length;

    [RequiresFfmpegFact]
    public async Task ColourBarsDecodeAsTheyWereEncoded()
    {
        Assert.True(fixture.IsBootstrapped);
        var path = TempMp4Path();
        try
        {
            await WriteBarsAsync(path);

            var decoded = await DecodeBarCentresAsync(path);

            Assert.Equal(FrameCount, decoded.Count);
            Assert.All(
                decoded,
                frame => Assert.Equal((PixelFormat.Bgra32, Width, Height), (frame.Format, frame.Width, frame.Height))
            );
            var off = new List<string>();
            for (int frame = 0; frame < decoded.Count; frame++)
            {
                foreach (var (row, bars) in decoded[frame].Rows)
                {
                    for (int bar = 0; bar < Bars.Length; bar++)
                    {
                        if (Enumerable.Range(0, 3).Any(c => Math.Abs(Bars[bar][c] - bars[bar][c]) > Tolerance))
                        {
                            off.Add(
                                $"frame {frame}, row {row}, bar {bar}: wrote BGR "
                                    + $"[{string.Join(", ", Bars[bar])}], decoded "
                                    + $"[{string.Join(", ", bars[bar])}]"
                            );
                        }
                    }
                }
            }

            Assert.True(
                off.Count == 0,
                $"{off.Count} bar samples are more than {Tolerance} levels off:\n"
                    + string.Join("\n", off.Take(Bars.Length * 3))
            );
        }
        finally
        {
            TryDelete(path);
        }
    }

    [RequiresFfmpegFact]
    public async Task TheStreamSignalsBt709LimitedRange()
    {
        Assert.True(fixture.IsBootstrapped);
        var path = TempMp4Path();
        try
        {
            await WriteBarsAsync(path);

            await using var demux = (DemuxSession)
                await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(path));
            var colour = StreamColour(demux, demux.MediaInfo.VideoStreams[0].StreamIndex);

            Assert.Equal(AVColorSpace.AVCOL_SPC_BT709, colour.Matrix);
            Assert.Equal(AVColorPrimaries.AVCOL_PRI_BT709, colour.Primaries);
            Assert.Equal(AVColorTransferCharacteristic.AVCOL_TRC_BT709, colour.Transfer);
            Assert.Equal(AVColorRange.AVCOL_RANGE_MPEG, colour.Range);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static async Task WriteBarsAsync(string path)
    {
        var options = new H264EncoderOptions
        {
            FrameRateNumerator = Fps,
            FrameRateDenominator = 1,
            BitRate = 1_000_000,
            GopSize = Fps,
        };
        await using var writer = Mp4VideoWriter.Create(path, options);
        for (int i = 0; i < FrameCount; i++)
            await writer.WriteAsync(BarsFrame(i));
        await writer.CompleteAsync();
    }

    private static IVideoFrame BarsFrame(int frameIndex) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            Width,
            Height,
            TimeSpan.FromSeconds(frameIndex / (double)Fps),
            TimeSpan.FromSeconds(1.0 / Fps),
            0,
            static (planes, _) =>
            {
                var px = planes.Y;
                for (int y = 0; y < planes.Height; y++)
                {
                    for (int x = 0; x < planes.Width; x++)
                    {
                        int i = y * planes.StrideY + x * 4;
                        var bar = Bars[x / BarWidth];
                        px[i + 0] = bar[0];
                        px[i + 1] = bar[1];
                        px[i + 2] = bar[2];
                        px[i + 3] = 0xFF;
                    }
                }
            }
        );

    /// <summary>
    /// Decodes <paramref name="path"/> as playback does, through the demuxer, the default software
    /// decoder and a <see cref="DecodingPipeline"/>, and takes the BGR at the centre of every bar
    /// on the first, middle and last rows of each frame.
    /// </summary>
    private static async Task<List<DecodedFrame>> DecodeBarCentresAsync(string path)
    {
        await using var demux = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(path));
        await using var decoder =
            DecoderFactories.Video(demux) ?? throw new InvalidOperationException("No video stream.");
        await using var pipeline = new DecodingPipeline(
            (DemuxSession)demux,
            (VideoDecoder)decoder,
            audioDecoder: null
        );

        // Sampled and released as they come, so the decoder never waits on a frame the test holds,
        // and asserted afterwards, so a failed assertion cannot leave the pump blocked. A frame the
        // bars cannot be read from is recorded with no rows rather than thrown on here.
        var collect = Task.Run(async () =>
        {
            var frames = new List<DecodedFrame>();
            await foreach (var frame in decoder.DecodeAsync())
            {
                using (frame)
                {
                    var rows = new List<(int Row, byte[][] Bars)>();
                    frames.Add(new DecodedFrame(frame.Format, frame.Width, frame.Height, rows));
                    if (
                        frame.Format != PixelFormat.Bgra32
                        || frame.Width != Width
                        || frame.Height != Height
                        || frame.AsCpu() is not { } cpu
                    )
                        continue;

                    foreach (int row in new[] { 0, Height / 2, Height - 1 })
                    {
                        var bars = new byte[Bars.Length][];
                        for (int bar = 0; bar < Bars.Length; bar++)
                        {
                            int offset = row * cpu.StrideY + (bar * BarWidth + BarWidth / 2) * 4;
                            bars[bar] = cpu.PlaneY.Span.Slice(offset, 3).ToArray();
                        }

                        rows.Add((row, bars));
                    }
                }
            }

            return frames;
        });

        await pipeline.RunDemuxPumpAsync(CancellationToken.None);
        await pipeline.FinalizeDecodersAsync();
        return await collect;
    }

    /// <summary>A decoded frame's format and size, and the bars read from it if it is the expected <c>Bgra32</c>.</summary>
    private sealed record DecodedFrame(PixelFormat Format, int Width, int Height, List<(int Row, byte[][] Bars)> Rows);

    private static unsafe (
        AVColorSpace Matrix,
        AVColorPrimaries Primaries,
        AVColorTransferCharacteristic Transfer,
        AVColorRange Range
    ) StreamColour(DemuxSession demux, int streamIndex)
    {
        var formatContext = (AVFormatContext*)demux.FormatContextPtr;
        AVCodecParameters* parameters = formatContext->streams[streamIndex]->codecpar;
        return (
            parameters->color_space,
            parameters->color_primaries,
            parameters->color_trc,
            parameters->color_range
        );
    }

    private static string TempMp4Path() =>
        Path.Combine(Path.GetTempPath(), $"frameflow-enc-{Guid.NewGuid():N}.mp4");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
