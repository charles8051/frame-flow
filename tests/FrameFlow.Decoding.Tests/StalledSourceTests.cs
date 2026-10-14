using FrameFlow.Tests.Shared;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A cancellation reaches a demuxer call that waits on a stalled network source (#709): the open
/// and its probe, a demux pump's read, and through a seek. The source is a server on 127.0.0.1
/// that stops answering (<see cref="StalledHttpSource"/>).
/// </summary>
/// <remarks>
/// <para>
/// Each test cancels only once the server has read the request the call is waiting on, so the
/// call is inside FFmpeg and only an interrupt ends it. A regression leaves it waiting there: the
/// bound turns that into a named failure, and disposing the server then ends the call, so the
/// test host is not left with a thread blocked in native code.
/// </para>
/// <para>
/// A read the interrupt gives up ends the pump as a cancellation does: it returns, with no fault
/// and no end of stream. A read afterwards asks the source again, and so does a seek, after which
/// a run reads to the end of the source.
/// </para>
/// </remarks>
public sealed class StalledSourceTests : IClassFixture<FfmpegBootstrapFixture>
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public StalledSourceTests(FfmpegBootstrapFixture _) { }

    [RequiresFfmpegFact]
    public async Task AnOpenWaitingOnASourceThatNeverAnswers_GivesUpWhenItsTokenIsCancelled()
    {
        await using var server = StalledHttpSource.NeverAnswering();
        using var cancel = new CancellationTokenSource();

        // The open runs on the calling thread, so it gets a thread of its own here.
        var open = Task.Run(() => new DemuxSessionFactory().OpenAsync(server.Source, cancel.Token).AsTask());
        await server.FirstRequest.WaitAsync(Bound);
        await cancel.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => open.WaitAsync(Bound));

        Assert.False(thrown is TimeoutException, "The open was still waiting on the source after its token was cancelled.");
        var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal(cancel.Token, cancelled.CancellationToken);
    }

    [RequiresFfmpegFact]
    public async Task APumpWhoseReadWaitsOnAStalledSource_EndsWhenItsTokenIsCancelled()
    {
        await using var rig = await StalledPump.OpenAsync();
        using var run = new CancellationTokenSource();

        var pump = rig.Start(run.Token);
        await rig.Server.Reconnected.WaitAsync(Bound); // the pump's read waits for the rest
        await run.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => pump.WaitAsync(Bound));

        Assert.False(thrown is TimeoutException, "The pump was still waiting on the source after its token was cancelled.");
        Assert.Null(thrown); // a cancellation, not a fault
        var read = rig.Demux.GetDiagnostics();
        Assert.Equal(1, read.PacketsRead); // the packet the server sent; the interrupted read returned none
        Assert.False(read.EndOfStreamReached, "The interrupted read was taken for the end of the source.");
    }

    [RequiresFfmpegFact]
    public async Task AReadAfterAnInterruptedRead_ReadsOnFromTheSource()
    {
        await using var rig = await StalledPump.OpenAsync();
        using (var run = new CancellationTokenSource())
        {
            var pump = rig.Start(run.Token);
            await rig.Server.Reconnected.WaitAsync(Bound);
            await run.CancelAsync();
            Assert.Null(await Record.ExceptionAsync(() => pump.WaitAsync(Bound)));
        }

        // No seek: the read asks the source again from where the interrupted one stopped, and this
        // time the server answers. The interrupt is not taken for a failure or an end of this read.
        var packet = await rig.Demux.ReadPacketAsync().AsTask().WaitAsync(Bound);

        Assert.NotNull(packet);
        Assert.Equal(StalledHttpSource.OpeningBytes, packet!.Data.Length); // the second packet, as long as the first
    }

    [RequiresFfmpegFact]
    public async Task ARunAfterASeekPastAnInterruptedRead_ReadsToTheEndOfTheSource()
    {
        await using var rig = await StalledPump.OpenAsync();
        using (var run = new CancellationTokenSource())
        {
            var pump = rig.Start(run.Token);
            await rig.Server.Reconnected.WaitAsync(Bound);
            await run.CancelAsync();
            Assert.Null(await Record.ExceptionAsync(() => pump.WaitAsync(Bound)));
        }

        // The seek observes its own token, and reopens the source where it lands. The interrupted
        // read's error does not outlive it.
        await rig.Demux.SeekAsync(TimeSpan.Zero).AsTask().WaitAsync(Bound);
        rig.Pipeline.ResetForSeek();
        var next = rig.Start(CancellationToken.None);

        var thrown = await Record.ExceptionAsync(() => next.WaitAsync(Bound));

        Assert.Null(thrown);
        var read = rig.Demux.GetDiagnostics();
        Assert.True(read.EndOfStreamReached, "The run after the seek did not reach the end of the source.");
        Assert.True(read.BytesRead >= StalledHttpSource.Length, $"The run read {read.BytesRead} bytes of {StalledHttpSource.Length}.");
    }

    /// <summary>
    /// A demux session over <see cref="StalledHttpSource.StallingAfterTheHeader"/> and a pipeline
    /// with no decoder, whose pump reads every packet and keeps none.
    /// </summary>
    private sealed class StalledPump : IAsyncDisposable
    {
        private readonly List<Task> _pumps = [];

        private StalledPump(StalledHttpSource server, DemuxSession demux)
        {
            Server = server;
            Demux = demux;
            Pipeline = new DecodingPipeline(demux, videoDecoder: null, audioDecoder: null);
        }

        public StalledHttpSource Server { get; }

        public DemuxSession Demux { get; }

        public DecodingPipeline Pipeline { get; }

        public static async Task<StalledPump> OpenAsync()
        {
            var server = StalledHttpSource.StallingAfterTheHeader();
            try
            {
                var demux = await new DemuxSessionFactory().OpenAsync(server.Source).AsTask().WaitAsync(Bound);
                return new StalledPump(server, (DemuxSession)demux);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        /// <summary>Runs the pump on a thread of its own.</summary>
        public Task Start(CancellationToken cancellationToken)
        {
            var pump = Task.Run(() => Pipeline.RunDemuxPumpAsync(cancellationToken));
            _pumps.Add(pump);
            return pump;
        }

        public async ValueTask DisposeAsync()
        {
            // Closing the server ends a read nothing interrupted, so nothing is freed under it.
            await Server.DisposeAsync();
            try
            {
                await Task.WhenAll(_pumps).WaitAsync(Bound);
            }
            catch (Exception) when (_pumps.All(p => p.IsCompleted)) { }

            await Pipeline.DisposeAsync();
            await Demux.DisposeAsync();
        }
    }
}
