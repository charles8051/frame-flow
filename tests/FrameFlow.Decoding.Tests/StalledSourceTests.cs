using FrameFlow.Tests.Shared;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A cancellation reaches a demuxer call that waits on a stalled network source (#709): the open
/// and its probe, a demux pump's read, a packet read, and a seek. The source is a server on
/// 127.0.0.1 that stops answering (<see cref="StalledHttpSource"/>).
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
/// and no end of stream, and the part of a packet the read brought back goes nowhere. The session
/// then reads nothing until a seek, after which a run reads to the end of the source: the
/// interrupt's error on the I/O is forgotten.
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
        var open = Task.Run(
            () => new DemuxSessionFactory().OpenAsync(server.Source, cancel.Token).AsTask(),
            CancellationToken.None
        );
        await server.FirstRequest.WaitAsync(Bound);
        await cancel.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => open.WaitAsync(Bound));

        Assert.False(thrown is TimeoutException, "The open was still waiting on the source after its token was cancelled.");
        var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal(cancel.Token, cancelled.CancellationToken);
    }

    /// <summary>
    /// The probe has found the stream's parameters in the first half of an MPEG-TS file and reads on
    /// when the source stalls. Interrupted there, FFmpeg's probe still succeeds; the open is
    /// cancelled, so it hands back no session probed partway.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task AnOpenCancelledWhileItsProbeWaitsOnTheSource_ReturnsNoSession()
    {
        var file = TestEnvironment.GetCorpusFile("test-video-h264-start-offset.ts");
        Assert.NotNull(file);
        await using var server = StalledHttpSource.StallingWhileProbed(await File.ReadAllBytesAsync(file!), "mpegts");
        using var cancel = new CancellationTokenSource();

        var open = Task.Run(
            () => new DemuxSessionFactory().OpenAsync(server.Source, cancel.Token).AsTask(),
            CancellationToken.None
        );
        var first = await Task.WhenAny(server.Reconnected, open).WaitAsync(Bound);
        Assert.True(first == server.Reconnected, "The probe did not wait on the source.");
        await cancel.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => open.WaitAsync(Bound));
        if (open.IsCompletedSuccessfully)
            await open.Result.DisposeAsync();

        Assert.False(thrown is TimeoutException, "The open was still waiting on the source after its token was cancelled.");
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
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

    /// <summary>
    /// The server sends a packet and a half. The pump's second read takes the half and waits for
    /// the rest; interrupted, FFmpeg hands the half back as a packet, and the pump drops it rather
    /// than route it.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task APumpWhoseReadIsCutShortPartwayThroughAPacket_RoutesNoPartOfIt()
    {
        await using var rig = await StalledPump.OpenAsync(StalledHttpSource.PcmPacketBytes * 3 / 2);
        using var run = new CancellationTokenSource();

        var pump = rig.Start(run.Token);
        await rig.Server.Reconnected.WaitAsync(Bound);
        await run.CancelAsync();

        Assert.Null(await Record.ExceptionAsync(() => pump.WaitAsync(Bound)));
        Assert.Equal(1, rig.Demux.GetDiagnostics().PacketsRead); // the whole packet, and not the half
    }

    /// <summary>
    /// The same packet and a half, read with <see cref="DemuxSession.ReadPacketAsync"/>: the read
    /// cut short throws, rather than return the half as a packet.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task AReadCutShortPartwayThroughAPacket_ThrowsRatherThanReturnThePart()
    {
        await using var rig = await StalledPump.OpenAsync(StalledHttpSource.PcmPacketBytes * 3 / 2);
        var whole = await rig.Demux.ReadPacketAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(StalledHttpSource.PcmPacketBytes, whole!.Data.Length);
        using var cancel = new CancellationTokenSource();

        // The read runs on the calling thread, so it gets a thread of its own here.
        var read = rig.Track(Task.Run(() => rig.Demux.ReadPacketAsync(cancel.Token).AsTask(), CancellationToken.None));
        await rig.Server.Reconnected.WaitAsync(Bound);
        await cancel.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => read.WaitAsync(Bound));

        Assert.False(thrown is TimeoutException, "The read was still waiting on the source after its token was cancelled.");
        if (read.IsCompletedSuccessfully)
            Assert.Fail($"The read returned a packet of {read.Result?.Data.Length} bytes.");
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
    }

    /// <summary>
    /// A read given up leaves the demuxer's position unreliable, so neither a packet read nor a
    /// run reads on from it until a seek has put the session somewhere known.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task AReadAfterAnInterruptedRead_IsRefusedUntilASeek()
    {
        await using var rig = await StalledPump.OpenAsync();
        await rig.InterruptARunAsync();

        var read = await Record.ExceptionAsync(() => rig.Demux.ReadPacketAsync().AsTask());
        var run = await Record.ExceptionAsync(() => rig.Start(CancellationToken.None));

        Assert.IsType<InvalidOperationException>(read);
        Assert.Contains("Seek before reading on", read!.Message);
        Assert.IsType<InvalidOperationException>(run);

        await rig.Demux.SeekAsync(TimeSpan.Zero).AsTask().WaitAsync(Bound);
        var packet = await rig.Demux.ReadPacketAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(StalledHttpSource.PcmPacketBytes, packet!.Data.Length);
    }

    [RequiresFfmpegFact]
    public async Task ARunAfterASeekPastAnInterruptedRead_ReadsToTheEndOfTheSource()
    {
        await using var rig = await StalledPump.OpenAsync();
        await rig.InterruptARunAsync();

        // The seek observes its own token, and reopens the source where it lands. The interrupted
        // read's error on the I/O does not outlive it.
        await rig.Demux.SeekAsync(TimeSpan.Zero).AsTask().WaitAsync(Bound);
        rig.Pipeline.ResetForSeek();
        var next = rig.Start(CancellationToken.None);

        var thrown = await Record.ExceptionAsync(() => next.WaitAsync(Bound));

        Assert.Null(thrown);
        var read = rig.Demux.GetDiagnostics();
        Assert.True(read.EndOfStreamReached, "The run after the seek did not reach the end of the source.");
        Assert.True(read.BytesRead >= rig.Server.Length, $"The run read {read.BytesRead} bytes of {rig.Server.Length}.");
    }

    /// <summary>
    /// A seek far into the source reopens it there, and the server does not answer. The seek gives
    /// up when its token is cancelled, and having moved the demuxer partway, leaves the session
    /// reading nothing until a seek completes.
    /// </summary>
    [RequiresFfmpegFact]
    public async Task ASeekWhoseReopeningStalls_GivesUpWhenItsTokenIsCancelled()
    {
        await using var rig = await StalledPump.OpenAsync();
        using var cancel = new CancellationTokenSource();

        var seek = rig.Track(
            Task.Run(() => rig.Demux.SeekAsync(TimeSpan.FromSeconds(4), cancel.Token).AsTask(), CancellationToken.None)
        );
        await rig.Server.Reconnected.WaitAsync(Bound);
        await cancel.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => seek.WaitAsync(Bound));

        Assert.False(thrown is TimeoutException, "The seek was still waiting on the source after its token was cancelled.");
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        var read = await Record.ExceptionAsync(() => rig.Demux.ReadPacketAsync().AsTask());
        Assert.IsType<InvalidOperationException>(read);
        Assert.Contains("Seek before reading on", read!.Message);
    }

    /// <summary>
    /// A demux session over <see cref="StalledHttpSource.StallingAfterTheHeader"/> and a pipeline
    /// with no decoder, whose pump reads every packet and keeps none.
    /// </summary>
    private sealed class StalledPump : IAsyncDisposable
    {
        // The pumps, reads and seeks the test started, which the dispose waits for before it frees.
        private readonly List<Task> _operations = [];

        private StalledPump(StalledHttpSource server, DemuxSession demux)
        {
            Server = server;
            Demux = demux;
            Pipeline = new DecodingPipeline(demux, videoDecoder: null, audioDecoder: null);
        }

        public StalledHttpSource Server { get; }

        public DemuxSession Demux { get; }

        public DecodingPipeline Pipeline { get; }

        public static async Task<StalledPump> OpenAsync(int openingBytes = StalledHttpSource.PcmPacketBytes)
        {
            var server = StalledHttpSource.StallingAfterTheHeader(openingBytes);
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
            // Not the pump's token: a run cancelled before its task starts would not run at all.
            return Track(Task.Run(() => Pipeline.RunDemuxPumpAsync(cancellationToken), CancellationToken.None));
        }

        /// <summary>Holds an operation on the session, so the dispose frees nothing under it.</summary>
        public T Track<T>(T operation)
            where T : Task
        {
            _operations.Add(operation);
            return operation;
        }

        /// <summary>Runs the pump until its read waits on the source, and cancels it there.</summary>
        public async Task InterruptARunAsync()
        {
            using var run = new CancellationTokenSource();
            var pump = Start(run.Token);
            await Server.Reconnected.WaitAsync(Bound);
            await run.CancelAsync();
            Assert.Null(await Record.ExceptionAsync(() => pump.WaitAsync(Bound)));
        }

        public async ValueTask DisposeAsync()
        {
            // Closing the server ends a read nothing interrupted, so nothing is freed under it.
            await Server.DisposeAsync();
            try
            {
                await Task.WhenAll(_operations).WaitAsync(Bound);
            }
            catch (Exception) when (_operations.All(o => o.IsCompleted)) { }

            await Pipeline.DisposeAsync();
            await Demux.DisposeAsync();
        }
    }
}
