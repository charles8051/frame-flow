using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// What a format context's interrupt callback answers (#709). It is asked as FFmpeg asks it,
/// through the function pointer and the handle the context is given, and it observes the token of
/// the operation using the context: none between operations, so an operation after a cancelled one
/// is not interrupted by it.
/// </summary>
public sealed class FormatContextInterruptTests : IClassFixture<FfmpegBootstrapFixture>
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public FormatContextInterruptTests(FfmpegBootstrapFixture _) { }

    [Fact]
    public void WithNoOperation_ItDoesNotInterrupt()
    {
        using var interrupt = new FormatContextInterrupt();

        Assert.Equal(0, Ask(interrupt));
    }

    [Fact]
    public void ItInterruptsTheOperationWhoseTokenIsCancelled_AndNothingAfterIt()
    {
        using var interrupt = new FormatContextInterrupt();
        using var run = new CancellationTokenSource();

        var replaced = interrupt.Enter(run.Token);
        Assert.Equal(0, Ask(interrupt));
        run.Cancel();
        Assert.Equal(1, Ask(interrupt));
        interrupt.Exit(replaced);

        // The next operation, a seek after the run, observes its own token.
        using var seek = new CancellationTokenSource();
        var replacedBySeek = interrupt.Enter(seek.Token);
        Assert.Equal(0, Ask(interrupt));
        interrupt.Exit(replacedBySeek);
        Assert.Equal(0, Ask(interrupt));
    }

    [Fact]
    public void OnceClosed_ItInterruptsWhateverIsRunning()
    {
        using var interrupt = new FormatContextInterrupt();
        var replaced = interrupt.Enter(CancellationToken.None);

        interrupt.Close();

        Assert.Equal(1, Ask(interrupt));
        interrupt.Exit(replaced);
        Assert.Equal(1, Ask(interrupt));
    }

    [Fact]
    public unsafe void WithNoArgument_ItDoesNotInterrupt()
    {
        Assert.Equal(0, FormatContextInterrupt.Callback(null));
    }

    /// <summary>
    /// A seek made after a run was cancelled reads the container to find its keyframe. It observes
    /// its own token there, so the run's cancellation does not give it up.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task ASeekAfterARunWasCancelled_IsNotInterruptedByTheRunsToken()
    {
        var file = TestEnvironment.GetCorpusFile("test-1080p-h264-aac.mp4");
        Assert.NotNull(file);
        await using var session = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
        var demux = (DemuxSession)session;

        // Nothing drains the audio decoder's small queue, so the pump parks on it a few packets in,
        // and the run is cancelled there, as a reposition cancels the run before it seeks.
        await using var audio = new AudioDecoder(
            demux.FormatContextPtr,
            demux.MediaInfo.AudioStreams[0].StreamIndex,
            new AudioDecoderOptions { PacketQueueCapacity = 4 }
        );
        await using var pipeline = new DecodingPipeline(demux, videoDecoder: null, audio);
        using (var run = new CancellationTokenSource())
        {
            var pump = pipeline.RunDemuxPumpAsync(run.Token);
            var parked = pipeline.WaitUntilParkedAsync();
            Assert.Same(parked, await Task.WhenAny(parked, pump).WaitAsync(Bound));
            await run.CancelAsync();
            await pump.WaitAsync(Bound);
        }

        // Far from where the pump stopped, so the keyframe search reads the file again.
        var thrown = await Record.ExceptionAsync(() => demux.SeekAsync(TimeSpan.FromSeconds(3)).AsTask());

        Assert.Null(thrown);
        var video = demux.MediaInfo.VideoStreams[0].StreamIndex;
        DemuxPacket? first;
        do
        {
            first = await demux.ReadPacketAsync();
        } while (first is not null && first.StreamIndex != video);
        Assert.NotNull(first);
        Assert.True(first!.IsKeyFrame, $"The first video packet after the seek, at {first.Pts}, is not a keyframe.");
    }

    private static unsafe int Ask(FormatContextInterrupt interrupt) =>
        FormatContextInterrupt.Callback((void*)interrupt.Opaque);
}
