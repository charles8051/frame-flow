namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Decoding-layer coverage for the video-starvation deadlock and its fix
/// (ADR-0059). The root cause is structural: a single demux pump
/// (<see cref="DecodingPipeline.RunDemuxPumpAsync"/>) feeds every decoder's
/// bounded packet queue and blocks once any queue fills. A stream that is
/// decoded but never drained — e.g. an audio stream with no consumer — fills
/// its queue, blocks the shared pump, and starves the streams that ARE
/// consumed. The fix is to discard such streams at the demuxer
/// (<see cref="DemuxSession.DiscardStream"/>) so their packets never enter the
/// pump.
/// </summary>
public sealed class NoConsumerStreamDiscardTests : IClassFixture<FfmpegBootstrapFixture>
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public NoConsumerStreamDiscardTests(FfmpegBootstrapFixture _, Xunit.Abstractions.ITestOutputHelper output) =>
        _output = output;

    private const string AvFixture = "test-av-h264-aac.mp4";

    // A long A/V clip (h264+aac). The precondition the 3s fixtures cannot meet is
    // that the video stream holds far more packets than the test consumes plus the
    // decoder's 512-packet queue: this one is 45s at 60fps, so ~2700, and the test
    // drains 600 frames and then lets the queue fill. The pump therefore cannot
    // legitimately reach end-of-stream, which is what makes a reported EOF a real
    // failure.
    //
    // Opt-in fixture: generate-test-corpus.cs writes it only under
    // --include-benchmarks. The attribute below turns its absence into a reported
    // SKIP rather than the repo's usual silent early return, so a green default
    // suite does not read as covering this regression.
    //
    // The default corpus cannot host it. test-1080p60-h264-aac.mp4 is the longest
    // non-benchmark fixture and is still too short: it has 601 packets, fewer than
    // the drain alone consumes, so the pump would reach EOF for a reason that has
    // nothing to do with the bug.
    private const string LongAvFixture = "bench-1080p60-h264-aac.mp4";

    /// <summary>
    /// Reproduces the signage freeze at the decoding layer: a long A/V clip with
    /// the audio stream discarded (no consumer, per ADR-0059) and the video
    /// stream drained at ~real-time. The demux pump must NOT report end-of-stream
    /// a few seconds into a 45s file. If it does, the video pipeline starves
    /// after the pre-buffered packets drain and playback freezes while the
    /// wallclock keeps running — the exact field symptom.
    /// </summary>
    [RequiresCorpusFileFact(
        LongAvFixture,
        "Run: dotnet run scripts/generate-test-corpus.cs -- --include-benchmarks"
    )]
    public async Task RunDemuxPump_RealVideoDecoder_AudioDiscarded_LongClip_DoesNotPrematurelyEof()
    {
        var file = TestEnvironment.GetCorpusFile(LongAvFixture)!;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;

        var videoIdx = demux.MediaInfo.VideoStreams[0].StreamIndex;
        var audioIdx = demux.MediaInfo.AudioStreams[0].StreamIndex;

        // ADR-0059: audio has no consumer -> discard it at the demuxer.
        demux.DiscardStream(audioIdx);

        await using var videoDecoder = VideoDecoder.Open(demux.FormatContextPtr, videoIdx);
        await using var pipeline = new DecodingPipeline(demux, videoDecoder, audioDecoder: null);

        using var cts = new CancellationTokenSource();
        var pump = pipeline.RunDemuxPumpAsync(cts.Token);

        // Drain more than one queue's worth of frames, as fast as they decode, so the pump has
        // blocked on the full queue and been released at least once. Then stop.
        //
        // This used to drain at ~25 fps with a 40 ms delay and poll the EOF flag every 250 ms
        // for two seconds. The pacing was never the property: what matters is that the consumer
        // is slower than an unthrottled pump, and a consumer that stops is the slowest there is.
        const int drainFrames = 600;
        var drained = 0;
        await foreach (var f in videoDecoder.DecodeAsync(cts.Token).ConfigureAwait(false))
        {
            f.Dispose();
            if (++drained == drainFrames)
                break;
        }

        // The decoder pulls nothing more, so nothing will drain the queue again. A pump that
        // honours backpressure is now blocked on it, or about to be, and stays blocked. The
        // regression reads on at IO speed, sheds through the drop path, and reaches EOF.
        var parked = pipeline.WaitUntilParkedAsync();
        var first = await Task.WhenAny(pump, parked).WaitAsync(TimeSpan.FromSeconds(30));

        var d = demux.GetDiagnostics();
        _output.WriteLine(
            $"drained={drained}  PacketsRead={d.PacketsRead}  EndOfStream={d.EndOfStreamReached}  pumpDone={pump.IsCompleted}"
        );

        cts.Cancel();
        try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { }

        Assert.True(
            first == parked && !d.EndOfStreamReached,
            $"Demux pump reported EOF after {d.PacketsRead} packets with the consumer stopped at "
                + $"{drained} frames, a few seconds into a 45s file. The audio-discard path "
                + "prematurely ends the pump, so video starves (plays the pre-buffered ~512 "
                + "frames, then freezes while the clock runs on)."
        );
    }

    /// <summary>
    /// Reproduces the deadlock mechanism directly: with an undrained audio
    /// decoder behind a small bounded queue, the demux pump blocks once the
    /// queue fills and never reaches EOF — exactly what froze video in the
    /// field (just triggered in milliseconds instead of ~10 s by shrinking the
    /// queue from its 512 default).
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task RunDemuxPumpAsync_AudioDecoderUndrained_BlocksOnceBoundedQueueFills()
    {
        var file = TestEnvironment.GetCorpusFile(AvFixture);
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;

        Assert.NotEmpty(demux.MediaInfo.AudioStreams);
        var audioStreamIndex = demux.MediaInfo.AudioStreams[0].StreamIndex;

        // Tiny queue so the pump backpressures after a handful of audio packets
        // instead of buffering ~512 (~10 s) first. Same mechanism, faster.
        const int tinyQueue = 8;
        await using var audioDecoder = new AudioDecoder(
            demux.FormatContextPtr,
            audioStreamIndex,
            new AudioDecoderOptions { PacketQueueCapacity = tinyQueue }
        );

        // videoDecoder: null so the pump drops video packets and only the audio
        // side — which nothing drains — exerts backpressure.
        await using var pipeline = new DecodingPipeline(demux, videoDecoder: null, audioDecoder);

        using var cts = new CancellationTokenSource();
        var pump = pipeline.RunDemuxPumpAsync(cts.Token);

        // Nothing drains the audio decoder, so a pump that honours backpressure suspends on the
        // ninth audio packet and stays suspended. One that does not reads to EOF and completes.
        // This used to wait two seconds and assert the pump had not finished, which says nothing
        // about why; the park signal is the pump saying it is blocked on the full queue.
        var parked = pipeline.WaitUntilParkedAsync();
        var completedFirst = await Task.WhenAny(pump, parked).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            completedFirst == parked,
            "Demux pump completed instead of blocking on the undrained bounded audio "
                + "queue. The video-starvation backpressure was not reproduced."
        );

        // Unblock the pump (cancellation aborts the pending queue write) and drain
        // it so native packet buffers are released before disposal.
        cts.Cancel();
        try
        {
            await pump;
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// The fix mechanism: discarding the audio stream at the demuxer drops
    /// (nearly) all of its packets from the read loop while leaving video
    /// untouched. A handful of packets the probe
    /// (<c>avformat_find_stream_info</c>) had already buffered before the discard
    /// flag was set can still leak through — that is why this asserts "nearly
    /// all", not strictly zero. Those few leaked packets are harmless: an
    /// unconsumed stream also has its decoder skipped, so a leaked packet has no
    /// queue to fill (see
    /// <see cref="RunDemuxPumpAsync_AudioStreamDiscarded_RunsToEofWithoutBlocking"/>).
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task DiscardStream_AudioStream_DropsNearlyAllAudioPackets()
    {
        var file = TestEnvironment.GetCorpusFile(AvFixture);
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();

        // Baseline: the un-discarded packet counts for the same file.
        var baseline = await CountPacketsAsync(factory, file, discardAudio: false);
        Assert.True(
            baseline.Audio > 50,
            $"Fixture should carry many audio packets to make the discard meaningful; got {baseline.Audio}."
        );
        Assert.True(baseline.Video > 0);

        // With the audio stream discarded, nearly all of those audio packets
        // vanish from the read loop while video is unaffected.
        var discarded = await CountPacketsAsync(factory, file, discardAudio: true);
        Assert.True(
            discarded.Video > 0,
            "Video packets must still be delivered after discarding audio."
        );
        Assert.True(
            discarded.Audio < baseline.Audio / 10,
            $"Discard left {discarded.Audio} of {baseline.Audio} audio packets; expected nearly all dropped."
        );
    }

    private static async Task<(int Video, int Audio)> CountPacketsAsync(
        DemuxSessionFactory factory,
        string file,
        bool discardAudio
    )
    {
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;

        var videoStreamIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;
        var audioStreamIndex = demux.MediaInfo.AudioStreams[0].StreamIndex;

        if (discardAudio)
            demux.DiscardStream(audioStreamIndex);

        int video = 0;
        int audio = 0;
        DemuxPacket? packet;
        while ((packet = await demux.ReadPacketAsync()) is not null)
        {
            if (packet.StreamIndex == audioStreamIndex)
                audio++;
            else if (packet.StreamIndex == videoStreamIndex)
                video++;
        }

        return (video, audio);
    }

    /// <summary>
    /// Ties the two together at the pipeline level: discarding the unconsumed
    /// audio stream lets the same small-queue, undrained-audio pump that blocked
    /// in <see cref="RunDemuxPumpAsync_AudioDecoderUndrained_BlocksOnceBoundedQueueFills"/>
    /// run cleanly to EOF.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task RunDemuxPumpAsync_AudioStreamDiscarded_RunsToEofWithoutBlocking()
    {
        var file = TestEnvironment.GetCorpusFile(AvFixture);
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;

        var audioStreamIndex = demux.MediaInfo.AudioStreams[0].StreamIndex;
        demux.DiscardStream(audioStreamIndex);

        // Same tiny, undrained audio decoder as the deadlock repro — proving the
        // discard, not a larger queue, is what unblocks the pump.
        await using var audioDecoder = new AudioDecoder(
            demux.FormatContextPtr,
            audioStreamIndex,
            new AudioDecoderOptions { PacketQueueCapacity = 8 }
        );
        await using var pipeline = new DecodingPipeline(demux, videoDecoder: null, audioDecoder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipeline.RunDemuxPumpAsync(cts.Token);

        Assert.True(
            demux.GetDiagnostics().EndOfStreamReached,
            "Demux pump did not reach EOF — the discarded audio stream should not "
                + "have backpressured it."
        );
        Assert.True(demux.GetDiagnostics().PacketsRead > 0, "Expected video packets to be read.");
    }

    /// <summary>
    /// End-to-end delivery guard for the no-consumer signage path (ADR-0059/0060): with
    /// the audio stream discarded and the video send in BLOCK mode (the no-audio default,
    /// <see cref="VideoDecoder.DropNewestWhenQueueFull"/> = <see langword="false"/>), every
    /// coded video frame must reach the consumer even when the clip has far more frames than
    /// the packet queue is deep. That is the invariant the field freeze violated — an
    /// unthrottled pump that shed video after roughly one queue's worth, leaving the rest of
    /// the file unplayed. Block mode never drops, so a full queue only paces the pump.
    /// </summary>
    /// <remarks>
    /// The multi-refill condition is forced deterministically with a tiny
    /// <see cref="VideoDecoderOptions.PacketQueueCapacity"/> (8 ≪ the clip's frame count)
    /// via the new knob, so this asserts full end-to-end frame delivery in milliseconds
    /// rather than needing a long fixture paced at real time. Unlike the demux-layer
    /// EOF check above, this counts decoded frames out the back of the decoder.
    /// </remarks>
    [RequiresFfmpegAndCorpusFact]
    public async Task RunDemuxPump_AudioDiscarded_BlockMode_DeliversEveryVideoFrame_DespiteTinyQueue()
    {
        var file = TestEnvironment.GetCorpusFile(AvFixture);
        if (file is null)
            return;

        var factory = new DemuxSessionFactory();

        // Baseline: how many video frames does the clip actually contain? One demuxed
        // video packet is one coded frame, so the video packet count is the frame count.
        var baseline = await CountPacketsAsync(factory, file, discardAudio: false);
        Assert.True(
            baseline.Video > 8,
            $"Fixture must carry more video frames than the tiny queue depth to exercise "
                + $"multiple refills; got {baseline.Video}."
        );

        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;

        var videoIdx = demux.MediaInfo.VideoStreams[0].StreamIndex;
        var audioIdx = demux.MediaInfo.AudioStreams[0].StreamIndex;

        // ADR-0059: audio has no consumer -> discard it at the demuxer.
        demux.DiscardStream(audioIdx);

        // Tiny BLOCK-mode queue (8 ≪ frame count). A full-queue send blocks and paces the
        // pump; it must never shed a packet, so the decoder still yields every frame.
        await using var videoDecoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            videoIdx,
            new VideoDecoderOptions { PacketQueueCapacity = 8 }
        );
        Assert.False(
            videoDecoder.DropNewestWhenQueueFull,
            "VideoDecoder must default to BLOCK mode (no audio sharing the pump)."
        );

        await using var pipeline = new DecodingPipeline(demux, videoDecoder, audioDecoder: null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Mirror SubstrateSession's lifecycle: pump to EOF, then finalize so the decoder
        // queue completes and DecodeAsync can terminate.
        var pump = Task.Run(
            async () =>
            {
                await pipeline.RunDemuxPumpAsync(cts.Token).ConfigureAwait(false);
                await pipeline
                    .FinalizeDecodersAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            },
            cts.Token
        );

        var frames = 0;
        await foreach (
            var f in videoDecoder.DecodeAsync(cts.Token).ConfigureAwait(false)
        )
        {
            f.Dispose();
            frames++;
        }

        await pump.ConfigureAwait(false);

        Assert.Equal(baseline.Video, frames);
    }
}
