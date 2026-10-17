namespace FrameFlow.Player.Tests;

/// <summary>
/// A pass's software decoder runs on the threads <see cref="IPassBuilder.WithDecoderThreads"/>
/// asks for, and still decodes every frame (#788).
/// </summary>
public sealed class PassDecoderThreadsTests
{
    private const string Clip = PassDecodeDiscardTests.Clip;

    [RequiresFfmpegAndCorpusFact]
    public async Task TheRequestedThreadsReachTheDecoder()
    {
        var (threads, decoded) = await RunAsync(pass => pass.WithDecoderThreads(3));

        Assert.Equal(3, threads);
        Assert.Equal(await AllFramesAsync(), decoded);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task OneThreadDecodesOnOne()
    {
        var (threads, decoded) = await RunAsync(pass => pass.WithDecoderThreads(1));

        Assert.Equal(1, threads);
        Assert.Equal(await AllFramesAsync(), decoded);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ByDefaultTheDecoderChoosesFromTheProcessors()
    {
        var (threads, _) = await RunAsync(pass => pass);

        Assert.Equal(Math.Clamp(Environment.ProcessorCount, 1, 8), threads);
    }

    private static async Task<long> AllFramesAsync()
    {
        var timeline = await VideoTimeline.ReadAsync(TestEnvironment.GetCorpusFile(Clip)!);
        return timeline.Frames.Count;
    }

    private static async Task<(int Threads, long Decoded)> RunAsync(Func<IPassBuilder, IPassBuilder> configure)
    {
        var path = TestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);
        var sink = new TimestampSink();
        await using var pass = await configure(
                FrameFlowPass.Create(path!).WithVideoSink(sink).WithHardwareDecode(Media.HardwareDecodeMode.Disabled)
            )
            .BuildAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pass.RunToCompletionAsync(cts.Token);
        var diagnostics = pass.VideoDecoder!.GetDiagnostics();
        return (diagnostics.DecodeThreads, diagnostics.FramesDecoded);
    }
}
