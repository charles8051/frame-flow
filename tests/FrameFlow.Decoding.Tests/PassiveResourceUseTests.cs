namespace FrameFlow.Decoding.Tests;

#if DEBUG
/// <summary>
/// The passive resources a pump and a graph use, the demux session, the pipeline and the audio
/// decoder, take no lock around their native calls. Their owner ends every use before it disposes
/// them, and a Debug build checks it (ADR-0086, decision 3.5): a dispose made during a use throws
/// and frees nothing, and once the use has ended it frees as usual.
/// </summary>
/// <remarks>
/// The use is held through a seam: a pump parked on a full decoder queue, which nothing drains, and
/// a decode that has handed out a buffer and not been asked for the next. Neither makes a native
/// call while the test disposes, so a dispose that wrongly frees fails the assertion rather than
/// crashing the test host. Release builds compile the check away, so these run in Debug only.
/// </remarks>
public sealed class PassiveResourceUseTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string AvFile = "test-av-h264-aac.mp4";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public PassiveResourceUseTests(FfmpegBootstrapFixture _) { }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheDemuxSession_DisposedWhileAPumpReadsIt_Throws_AndFreesOnceThePumpHasEnded()
    {
        await using var rig = await ParkedPump.StartAsync();

        var thrown = await Record.ExceptionAsync(() => rig.Demux.DisposeAsync().AsTask());

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains(nameof(DemuxSession), thrown!.Message);
        await rig.StopAsync();
        await rig.Demux.DisposeAsync(); // the use has ended, so this frees
        Assert.Throws<ObjectDisposedException>(() => rig.Demux.DiscardStream(0));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ThePipeline_DisposedWhileItsPumpRuns_Throws_AndFreesOnceThePumpHasEnded()
    {
        await using var rig = await ParkedPump.StartAsync();

        var thrown = await Record.ExceptionAsync(() => rig.Pipeline.DisposeAsync().AsTask());

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains(nameof(DecodingPipeline), thrown!.Message);
        await rig.StopAsync();
        Assert.Null(await Record.ExceptionAsync(() => rig.Pipeline.DisposeAsync().AsTask()));
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task TheAudioDecoder_DisposedWhileADecodeIsUnderWay_Throws_AndFreesOnceTheDecodeHasEnded()
    {
        await using var rig = await ParkedPump.StartAsync();

        // The decode has handed out its first buffer and stands at the next step: it is under way
        // until it is disposed, and makes no native call meanwhile.
        var decode = rig.Audio.DecodeAsync().GetAsyncEnumerator();
        Assert.True(await decode.MoveNextAsync().AsTask().WaitAsync(Bound), "The decode produced nothing.");
        decode.Current.Dispose();

        var thrown = await Record.ExceptionAsync(() => rig.Audio.DisposeAsync().AsTask());

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains(nameof(AudioDecoder), thrown!.Message);
        await decode.DisposeAsync();
        await rig.StopAsync();
        await rig.Audio.DisposeAsync(); // the decode has ended, so this frees
        Assert.Throws<ObjectDisposedException>(() => rig.Audio.Flush());
    }

    /// <summary>
    /// A pump over <see cref="AvFile"/> parked on the audio decoder's full queue: a use of the
    /// demux session and of the pipeline that lasts until <see cref="StopAsync"/>.
    /// </summary>
    private sealed class ParkedPump : IAsyncDisposable
    {
        private readonly CancellationTokenSource _run = new();
        private Task? _pump;

        private ParkedPump(DemuxSession demux, AudioDecoder audio)
        {
            Demux = demux;
            Audio = audio;
            Pipeline = new DecodingPipeline(demux, videoDecoder: null, audio);
        }

        public DemuxSession Demux { get; }

        public AudioDecoder Audio { get; }

        public DecodingPipeline Pipeline { get; }

        public static async Task<ParkedPump> StartAsync()
        {
            var file = TestEnvironment.GetCorpusFile(AvFile);
            Assert.NotNull(file);
            var demux = (DemuxSession)await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(file!));
            var audio = new AudioDecoder(
                demux.FormatContextPtr,
                demux.MediaInfo.AudioStreams[0].StreamIndex,
                new AudioDecoderOptions { PacketQueueCapacity = 2 }
            );
            var rig = new ParkedPump(demux, audio);
            rig._pump = rig.Pipeline.RunDemuxPumpAsync(rig._run.Token);
            var parked = rig.Pipeline.WaitUntilParkedAsync();
            Assert.Same(parked, await Task.WhenAny(parked, rig._pump).WaitAsync(Bound));
            return rig;
        }

        /// <summary>Cancels the pump's parked write, and waits for the pump to end.</summary>
        public async Task StopAsync()
        {
            await _run.CancelAsync();
            await _pump!.WaitAsync(Bound);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await Pipeline.DisposeAsync();
            await Audio.DisposeAsync();
            await Demux.DisposeAsync();
            _run.Dispose();
        }
    }
}
#endif
