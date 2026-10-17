using FrameFlow.Decoding;
using FrameFlow.Media;

namespace FrameFlow.Player.Tests;

/// <summary>
/// A pass's frames convert with the matrix <see cref="IPassBuilder.WithColorMatrix"/> picks, and
/// by default an untagged SD stream's with BT.601 (#841).
/// </summary>
public sealed class PassColorMatrixTests
{
    // 720x480 flat 75% bars with no colour tag.
    private const string Clip = "test-video-ffv1-untagged-bars-480p.mkv";

    // What FFmpeg's scale filter converts the yellow bar to, in blue, green, red, with each matrix.
    private static readonly byte[] YellowBt601 = [6, 201, 188];
    private static readonly byte[] YellowBt709 = [0, 188, 190];

    [RequiresFfmpegAndCorpusFact]
    public async Task ByDefaultAnUntaggedSdStreamConvertsWithBt601()
    {
        var (matrix, yellow) = await RunAsync(pass => pass);

        Assert.Equal(ColorMatrix.Auto, matrix);
        AssertNear(YellowBt601, yellow);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheRequestedMatrixReachesTheDecoder()
    {
        var (matrix, yellow) = await RunAsync(pass => pass.WithColorMatrix(ColorMatrix.Bt709));

        Assert.Equal(ColorMatrix.Bt709, matrix);
        AssertNear(YellowBt709, yellow);
    }

    /// <summary>The builder's matrix wins; otherwise the caller's decoder options keep theirs.</summary>
    [Theory]
    [InlineData(ColorMatrix.Auto, ColorMatrix.Bt601, ColorMatrix.Bt601)]
    [InlineData(ColorMatrix.Bt709, ColorMatrix.Bt601, ColorMatrix.Bt709)]
    [InlineData(ColorMatrix.Bt2020, ColorMatrix.Auto, ColorMatrix.Bt2020)]
    [InlineData(ColorMatrix.Auto, ColorMatrix.Auto, ColorMatrix.Auto)]
    public void TheDecoderOptionsCarryTheMatrix(ColorMatrix builder, ColorMatrix options, ColorMatrix expected)
    {
        var decoderOptions = PassBuilder.DecoderOptions(
            new VideoDecoderOptions { ColorMatrix = options }, device: null, budget: null, decoderThreads: 0, builder);

        Assert.Equal(expected, decoderOptions.ColorMatrix);
    }

    private static void AssertNear(byte[] expected, byte[] actual)
    {
        for (int channel = 0; channel < 3; channel++)
            Assert.True(Math.Abs(expected[channel] - actual[channel]) <= 1, $"Channel {channel}: expected {expected[channel]}, got {actual[channel]}.");
    }

    private static async Task<(ColorMatrix Matrix, byte[] Yellow)> RunAsync(Func<IPassBuilder, IPassBuilder> configure)
    {
        var path = TestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        var sink = new FirstFrameSink();
        await using var pass = await configure(
                FrameFlowPass.Create(path!).WithVideoSink(sink).WithHardwareDecode(HardwareDecodeMode.Disabled)
            )
            .BuildAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pass.RunToCompletionAsync(cts.Token);
        Assert.NotNull(sink.Yellow);
        return (pass.VideoDecoder!.GetDiagnostics().ColorMatrix, sink.Yellow!);
    }

    /// <summary>Keeps the first frame's yellow bar, at the middle of the bar and the frame.</summary>
    private sealed class FirstFrameSink : IVideoSink
    {
        public byte[]? Yellow { get; private set; }

        public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            try
            {
                if (Yellow is null && frame.AsCpu() is { } cpu)
                {
                    Assert.Equal(PixelFormat.Bgra32, frame.Format);
                    int barWidth = frame.Width / 8;
                    int at = frame.Height / 2 * cpu.StrideY + (barWidth + barWidth / 2) * 4;
                    Yellow = cpu.PlaneY.Span.Slice(at, 3).ToArray();
                }
            }
            finally
            {
                frame.Dispose();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
